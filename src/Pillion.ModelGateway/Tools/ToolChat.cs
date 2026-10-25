using System.Text.Json;
using Microsoft.Extensions.AI;
using Pillion.Contracts;
using ChatMessage = Microsoft.Extensions.AI.ChatMessage;

namespace Pillion.ModelGateway;

/// <summary>
/// One turn of a tool-calling conversation for the agent runtime (work item 3.23). The messages are the
/// Microsoft.Extensions.AI shapes: <see cref="TextContent"/> for text, <see cref="FunctionCallContent"/> in an assistant
/// message for the calls the model made earlier, and <see cref="FunctionResultContent"/> in a message with the
/// <c>tool</c> role for the results the runtime produced. The gateway never invokes a tool: it carries the declarations to the
/// provider and the calls back, and the runtime decides under its permission tiers whether a call runs.
/// </summary>
public sealed record ToolChatRequest
{
    /// <summary>Gets the caller-assigned id, for the Outbound log and progress notices.</summary>
    public required string RequestId { get; init; }

    /// <summary>Gets the conversation so far, oldest first. A system message is added from the templates when there is none.</summary>
    public required IReadOnlyList<ChatMessage> Messages { get; init; }

    /// <summary>
    /// Gets the tools offered to the model: <see cref="AIFunctionDeclaration"/>s (name, description, JSON schema), for
    /// example from <see cref="ToolDeclarations.Create"/> or <see cref="AIFunctionFactory.CreateDeclaration"/>. Other
    /// kinds of <see cref="AITool"/> are ignored. Empty offers none.
    /// </summary>
    public IReadOnlyList<AITool> Tools { get; init; } = [];

    /// <summary>Gets the task class the turn is routed as (<see cref="TaskClass.Agent"/> for an agent step).</summary>
    public TaskClass TaskClass { get; init; } = TaskClass.Agent;

    /// <summary>Gets the profile, or <see cref="Profile.Auto"/> to let the task-class setting and the Auto policy decide.</summary>
    public Profile Profile { get; init; } = Profile.Auto;

    /// <summary>Gets an explicit model id (<c>providerId/modelId</c> or bare), which wins over Auto; the runtime pins the model it planned for.</summary>
    public string? ModelId { get; init; }

    /// <summary>Gets the most tokens to generate, or <see langword="null"/> for the adapter's default.</summary>
    public int? MaxOutputTokens { get; init; }

    /// <summary>Gets how the model may use the tools (<see cref="ChatToolMode.Auto"/> when <see langword="null"/>).</summary>
    public ChatToolMode? ToolMode { get; init; }
}

/// <summary>How the tools of a <see cref="ToolChatRequest"/> reached the model.</summary>
public enum ToolTransport
{
    /// <summary>The declarations were sent in the provider's native tool-calling shape and the calls decoded from it.</summary>
    Native,

    /// <summary>
    /// No declarations were sent: the model's capability descriptor says <see cref="ToolCallingSupport.None"/> (or the request
    /// offered no tools). The runtime drives such a model with the text protocol (design section 8).
    /// </summary>
    None,

    /// <summary>
    /// The descriptor said native but the adapter or the server refused the tools on this turn. The descriptor of the model
    /// has been downgraded to <see cref="ToolCallingSupport.None"/> and the turn ended with an error; the runtime retries with
    /// the text protocol.
    /// </summary>
    Downgraded,
}

/// <summary>
/// One update of a tool-calling turn: a text delta, a complete tool call, or the final update with the finish reason, the
/// token counts and any error. Every update names the provider and model that answered.
/// </summary>
public sealed record ToolChatUpdate
{
    /// <summary>Gets the request id.</summary>
    public required string RequestId { get; init; }

    /// <summary>Gets the provider that answered.</summary>
    public required string ProviderId { get; init; }

    /// <summary>Gets the provider's model id.</summary>
    public required string ModelId { get; init; }

    /// <summary>Gets how the tools reached the model.</summary>
    public required ToolTransport Transport { get; init; }

    /// <summary>Gets a text delta, or <see langword="null"/> when this update carries none.</summary>
    public string? Text { get; init; }

    /// <summary>
    /// Gets a complete tool call (its id, name and parsed arguments, each value a <see cref="JsonElement"/>), or
    /// <see langword="null"/>. Streamed argument deltas are assembled by the adapter before the call is yielded.
    /// </summary>
    public FunctionCallContent? ToolCall { get; init; }

    /// <summary>Gets a value indicating whether this is the last update of the turn.</summary>
    public bool IsFinal { get; init; }

    /// <summary>Gets why the model stopped (<see cref="ChatFinishReason.ToolCalls"/> after calls), on the final update when the provider reported one.</summary>
    public ChatFinishReason? FinishReason { get; init; }

    /// <summary>Gets the prompt tokens (provider-reported, else estimated), on the final update.</summary>
    public int PromptTokens { get; init; }

    /// <summary>Gets the generated tokens (provider-reported, else estimated), on the final update.</summary>
    public int CompletionTokens { get; init; }

    /// <summary>
    /// Gets the error that ended the turn after it started (a provider failure mid-stream, tool-call arguments that were not
    /// valid JSON, a tools downgrade), on the final update; <see langword="null"/> when the turn succeeded. A failure before
    /// the turn started is reported by the call throwing <see cref="PillionRpcException"/>.
    /// </summary>
    public PillionError? Error { get; init; }

    /// <summary>Gets a value indicating whether <see cref="Error"/> is transient and the same turn may be sent again.</summary>
    public bool ErrorIsRetryable { get; init; }
}

/// <summary>Where a <see cref="ToolChatRequest"/> would be sent and whether tools go natively, so the runtime can pick its prompt variant before the turn.</summary>
/// <param name="ProviderId">The provider.</param>
/// <param name="ModelId">The provider's model id.</param>
/// <param name="IsLocal">Whether the provider is local.</param>
/// <param name="ToolCalling">The model's tool-calling support after any run-time downgrade.</param>
/// <param name="Reason">The router's explanation.</param>
public sealed record ToolRoute(string ProviderId, string ModelId, bool IsLocal, ToolCallingSupport ToolCalling, string Reason)
{
    /// <summary>Gets the qualified model id <c>providerId/modelId</c>.</summary>
    public string QualifiedId => QualifiedModelId.Format(ProviderId, ModelId);
}

/// <summary>Builds tool declarations without a delegate: the gateway describes tools to the model and never invokes them.</summary>
public static class ToolDeclarations
{
    /// <summary>Creates a declaration.</summary>
    /// <param name="name">The tool name (letters, digits, underscores and hyphens; what the model calls it by).</param>
    /// <param name="description">What the tool does, for the model.</param>
    /// <param name="parametersSchemaJson">The JSON schema of the arguments object, for example <c>{"type":"object","properties":{...},"required":[...]}</c>.</param>
    /// <returns>The declaration, to put in <see cref="ToolChatRequest.Tools"/>.</returns>
    public static AIFunctionDeclaration Create(string name, string description, string parametersSchemaJson)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(description);
        ArgumentException.ThrowIfNullOrWhiteSpace(parametersSchemaJson);
        using var schema = JsonDocument.Parse(parametersSchemaJson);
        return AIFunctionFactory.CreateDeclaration(name, description, schema.RootElement.Clone(), null);
    }
}

using System.Runtime.CompilerServices;
using System.Text.Json.Nodes;
using Microsoft.Extensions.AI;
using Pillion.Common.Settings;
using Pillion.ModelGateway.Providers.OpenAICompatible;
using Pillion.ModelGateway.Providers.Tools;
using ChatMessage = Microsoft.Extensions.AI.ChatMessage;

namespace Pillion.ModelGateway.Providers.AzureOpenAI;

/// <summary>
/// Chat over <c>POST /openai/deployments/{deployment}/chat/completions</c> with <c>stream: true</c>. The body and the
/// chunk parser are the OpenAI adapter's (official-API rules: <c>max_completion_tokens</c>, no sampling parameters for
/// reasoning models; tools, streamed <c>tool_calls</c> and <c>tool</c> messages as there); the deployment is named in the
/// URL, so the body carries no <c>model</c>. Azure's content-filter
/// chunks (an empty <c>choices</c> array with filter results) carry no text and are skipped.
/// </summary>
internal sealed class AzureOpenAIChatClient : IChatClient
{
    private readonly AzureOpenAIProvider _provider;
    private readonly string _deployment;

    public AzureOpenAIChatClient(AzureOpenAIProvider provider, string deployment)
    {
        _provider = provider;
        _deployment = deployment;
    }

    public async Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) =>
        await GetStreamingResponseAsync(messages, options, cancellationToken).ToChatResponseAsync(cancellationToken).ConfigureAwait(false);

    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(messages);

        var deployment = string.IsNullOrWhiteSpace(options?.ModelId) ? _deployment : options.ModelId;
        var body = BuildBody(deployment, messages, options);
        var responseId = Guid.NewGuid().ToString("N");
        var calls = new StreamedToolCalls(_provider.DisplayName, responseId);

        var stream = ToolJson.GuardTools(_provider.Http.PostStreamAsync(_provider.ChatPath(deployment), body, deployment, cancellationToken), body.ContainsKey("tools"), deployment, cancellationToken);
        await foreach (var chunk in stream.ConfigureAwait(false))
        {
            using (chunk)
            {
                var update = OpenAIChatClient.ToUpdate(chunk.Data.RootElement, deployment, responseId, calls);
                if (update is not null)
                {
                    yield return update;
                }
            }
        }

        if (calls.HasCalls)
        {
            yield return OpenAIChatClient.CallsUpdate(calls, deployment, responseId);
        }
    }

    public object? GetService(Type serviceType, object? serviceKey = null)
    {
        ArgumentNullException.ThrowIfNull(serviceType);
        if (serviceKey is not null)
        {
            return null;
        }

        if (serviceType == typeof(ChatClientMetadata))
        {
            return new ChatClientMetadata(_provider.Id, _provider.Endpoint, _deployment);
        }

        return serviceType.IsInstanceOfType(this) ? this : null;
    }

    public void Dispose()
    {
        // The provider owns the HTTP client.
    }

    /// <summary>Builds the request body: the official OpenAI body without <c>model</c>.</summary>
    internal static JsonObject BuildBody(string deployment, IEnumerable<ChatMessage> messages, ChatOptions? options)
    {
        var body = OpenAIChatClient.BuildBody(ProviderKind.OpenAi, deployment, messages, options);
        body.Remove("model");
        return body;
    }
}

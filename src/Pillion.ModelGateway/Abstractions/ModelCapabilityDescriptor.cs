using Pillion.Contracts;

namespace Pillion.ModelGateway;

/// <summary>How a model supports tool (function) calling.</summary>
public enum ToolCallingSupport
{
    /// <summary>No tool calling; the agent falls back to the text protocol.</summary>
    None,

    /// <summary>The provider's native tool-calling API.</summary>
    Native,
}

/// <summary>What one model can do and what it costs (design section 6, Provider abstraction).</summary>
public sealed record ModelCapabilityDescriptor
{
    /// <summary>A descriptor for a model nothing is known about: chat and streaming only, a 4,096-token window.</summary>
    public static ModelCapabilityDescriptor Unknown { get; } = new();

    /// <summary>Gets the model's context window in tokens.</summary>
    public int ContextWindow { get; init; } = 4096;

    /// <summary>Gets the most tokens the model generates in one response.</summary>
    public int MaxOutputTokens { get; init; } = 2048;

    /// <summary>Gets a value indicating whether the model supports chat.</summary>
    public bool Chat { get; init; } = true;

    /// <summary>Gets how the model supports tool calling.</summary>
    public ToolCallingSupport ToolCalling { get; init; }

    /// <summary>Gets a value indicating whether the model accepts images.</summary>
    public bool Vision { get; init; }

    /// <summary>Gets a value indicating whether the model supports fill-in-the-middle completion.</summary>
    public bool Fim { get; init; }

    /// <summary>Gets a value indicating whether the model generates embeddings.</summary>
    public bool Embeddings { get; init; }

    /// <summary>Gets a value indicating whether responses can be streamed.</summary>
    public bool Streaming { get; init; } = true;

    /// <summary>Gets a value indicating whether the provider caches long stable prompt prefixes.</summary>
    public bool PromptCaching { get; init; }

    /// <summary>
    /// Gets a value indicating whether the model has a reasoning-effort control the adapter can set (Anthropic's
    /// <c>output_config.effort</c>, Gemini's <c>thinkingLevel</c>).
    /// </summary>
    public bool ReasoningEffort { get; init; }

    /// <summary>Gets the cost in US dollars per million input tokens (0 for local models).</summary>
    public decimal InputCostPerMillionTokens { get; init; }

    /// <summary>Gets the cost in US dollars per million output tokens (0 for local models).</summary>
    public decimal OutputCostPerMillionTokens { get; init; }

    /// <summary>Gets a value indicating whether the model runs on this machine.</summary>
    public bool IsLocal { get; init; }

    /// <summary>Gets the capabilities as the contract's flags.</summary>
    /// <returns>The flags.</returns>
    public ModelCapabilities ToFlags()
    {
        var flags = ModelCapabilities.None;
        if (Chat)
        {
            flags |= ModelCapabilities.Chat;
        }

        if (Fim)
        {
            flags |= ModelCapabilities.Fim;
        }

        if (Vision)
        {
            flags |= ModelCapabilities.Vision;
        }

        if (ToolCalling != ToolCallingSupport.None)
        {
            flags |= ModelCapabilities.Tools;
        }

        if (Embeddings)
        {
            flags |= ModelCapabilities.Embeddings;
        }

        return flags;
    }
}

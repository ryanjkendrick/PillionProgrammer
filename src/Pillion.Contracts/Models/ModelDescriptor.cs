using System.Runtime.Serialization;

namespace Pillion.Contracts;

/// <summary>Describes a model that the host can use.</summary>
[DataContract]
public sealed class ModelDescriptor
{
    /// <summary>Gets or sets the model id, unique across providers.</summary>
    [DataMember(Order = 0)]
    public string Id { get; set; } = string.Empty;

    /// <summary>Gets or sets the name shown to the user.</summary>
    [DataMember(Order = 1)]
    public string DisplayName { get; set; } = string.Empty;

    /// <summary>Gets or sets the id of the provider that serves the model.</summary>
    [DataMember(Order = 2)]
    public string ProviderId { get; set; } = string.Empty;

    /// <summary>Gets or sets a value indicating whether the model runs on this machine.</summary>
    [DataMember(Order = 3)]
    public bool IsLocal { get; set; }

    /// <summary>Gets or sets what the model can do.</summary>
    [DataMember(Order = 4)]
    public ModelCapabilities Capabilities { get; set; }

    /// <summary>Gets or sets the provider's own id for the model (for example <c>qwen2.5-coder:7b</c>); <see cref="Id"/> is unique across providers.</summary>
    [DataMember(Order = 5)]
    public string ModelId { get; set; } = string.Empty;

    /// <summary>Gets or sets a value indicating whether the model can be used now (policy allows it, its provider is reachable and it is installed).</summary>
    [DataMember(Order = 6)]
    public bool IsAvailable { get; set; }

    /// <summary>Gets or sets the model's context window in tokens (0 when unknown).</summary>
    [DataMember(Order = 7)]
    public int ContextWindow { get; set; }

    /// <summary>Gets or sets the model's content digest when the provider reports one (Ollama does), for the status bar.</summary>
    [DataMember(Order = 8)]
    public string? Digest { get; set; }
}
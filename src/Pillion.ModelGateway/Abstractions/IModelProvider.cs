using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Pillion.Common.Settings;
using Pillion.Contracts;

namespace Pillion.ModelGateway;

/// <summary>
/// One configured model provider (an Ollama server, an OpenAI-compatible endpoint, a cloud API). Created from an
/// entry in the <c>providers</c> setting by the <see cref="IModelProviderFactory"/> for its <see cref="Kind"/>.
/// </summary>
public interface IModelProvider : IDisposable
{
    /// <summary>Gets the provider id from settings; also the name of its secret.</summary>
    string Id { get; }

    /// <summary>Gets the provider kind; matches <see cref="ProviderSettings.Kind"/>.</summary>
    ProviderKind Kind { get; }

    /// <summary>Gets the name shown to the user.</summary>
    string DisplayName { get; }

    /// <summary>Gets a value indicating whether the provider runs on this machine (no cloud boundary is crossed).</summary>
    bool IsLocal { get; }

    /// <summary>Lists the models the provider can serve now.</summary>
    /// <param name="cancellationToken">A token that cancels the call.</param>
    /// <returns>The models, each with its capabilities.</returns>
    /// <exception cref="ProviderException">The provider could not be asked.</exception>
    Task<IReadOnlyList<ProviderModel>> ListModelsAsync(CancellationToken cancellationToken);

    /// <summary>Gets a chat client for one model.</summary>
    /// <param name="modelId">The provider's model id.</param>
    /// <returns>The client. Callers do not dispose it; the provider owns it.</returns>
    IChatClient GetChatClient(string modelId);

    /// <summary>Gets a fill-in-the-middle client for one model.</summary>
    /// <param name="modelId">The provider's model id.</param>
    /// <returns>The client, or <see langword="null"/> when the provider or the model does not support FIM.</returns>
    IFimClient? GetFimClient(string modelId);

    /// <summary>Checks that the provider can be reached (for Test connection). Never throws for an unreachable provider.</summary>
    /// <param name="cancellationToken">A token that cancels the probe.</param>
    /// <returns>The result.</returns>
    Task<ProviderProbeResult> ProbeAsync(CancellationToken cancellationToken);
}

/// <summary>Creates <see cref="IModelProvider"/>s of one <see cref="ProviderKind"/> from settings.</summary>
public interface IModelProviderFactory
{
    /// <summary>Gets the kind this factory creates.</summary>
    ProviderKind Kind { get; }

    /// <summary>Determines whether a provider needs an API key in the secret store before it can be routed to.</summary>
    /// <param name="settings">The provider's settings.</param>
    /// <returns><see langword="true"/> when a key is required.</returns>
    bool RequiresApiKey(ProviderSettings settings);

    /// <summary>Creates a provider. Must not perform network I/O.</summary>
    /// <param name="settings">The provider's settings.</param>
    /// <param name="context">Services the provider may use.</param>
    /// <returns>The provider.</returns>
    IModelProvider Create(ProviderSettings settings, ProviderCreationContext context);
}

/// <summary>What a provider factory receives besides the provider's settings.</summary>
/// <param name="ApiKey">The API key from the secret store, or <see langword="null"/>. Never log it.</param>
/// <param name="Catalog">The model catalog.</param>
/// <param name="LoggerFactory">Creates loggers.</param>
/// <param name="AutocompleteModelId">The model routed to autocomplete for this provider, if known; local runtimes keep it resident.</param>
public sealed record ProviderCreationContext(
    string? ApiKey,
    ModelCatalog Catalog,
    ILoggerFactory LoggerFactory,
    string? AutocompleteModelId);

/// <summary>A model a provider can serve.</summary>
/// <param name="ProviderId">The provider id.</param>
/// <param name="ModelId">The provider's model id (for Ollama, for example <c>qwen2.5-coder:7b</c>).</param>
/// <param name="DisplayName">The name shown to the user.</param>
/// <param name="Capabilities">What the model can do.</param>
/// <param name="Digest">The model's content digest when the provider reports one (Ollama does), for the status bar.</param>
public sealed record ProviderModel(
    string ProviderId,
    string ModelId,
    string DisplayName,
    ModelCapabilityDescriptor Capabilities,
    string? Digest = null)
{
    /// <summary>Converts the model to the contract DTO.</summary>
    /// <param name="isAvailable">Whether the model can be used now.</param>
    /// <returns>The descriptor.</returns>
    public ModelDescriptor ToDescriptor(bool isAvailable) => new()
    {
        Id = QualifiedModelId.Format(ProviderId, ModelId),
        DisplayName = DisplayName,
        ProviderId = ProviderId,
        IsLocal = Capabilities.IsLocal,
        Capabilities = Capabilities.ToFlags(),
        ModelId = ModelId,
        IsAvailable = isAvailable,
        ContextWindow = Capabilities.ContextWindow,
        Digest = Digest,
    };
}

/// <summary>The result of <see cref="IModelProvider.ProbeAsync"/>.</summary>
/// <param name="IsReachable">Whether the provider answered and can be used.</param>
/// <param name="Version">The server version when the provider reports one.</param>
/// <param name="Message">A user-facing description of the result.</param>
public sealed record ProviderProbeResult(bool IsReachable, string? Version, string Message);

/// <summary>Builds and parses the gateway-wide model id <c>providerId/modelId</c>.</summary>
public static class QualifiedModelId
{
    /// <summary>Formats a qualified model id.</summary>
    /// <param name="providerId">The provider id.</param>
    /// <param name="modelId">The provider's model id.</param>
    /// <returns><c>providerId/modelId</c>.</returns>
    public static string Format(string providerId, string modelId) => providerId + "/" + modelId;

    /// <summary>
    /// Splits <paramref name="value"/> into a provider id and model id when it starts with the id of a known provider
    /// followed by <c>/</c>. Model ids may themselves contain <c>/</c> (<c>hf.co/org/model</c>), so only a known provider
    /// prefix is split off.
    /// </summary>
    /// <param name="value">A qualified or bare model id.</param>
    /// <param name="providerIds">The ids of the configured providers.</param>
    /// <returns>The provider id (or <see langword="null"/> for a bare id) and the model id.</returns>
    public static (string? ProviderId, string ModelId) Parse(string value, IEnumerable<string> providerIds)
    {
        ArgumentNullException.ThrowIfNull(value);
        ArgumentNullException.ThrowIfNull(providerIds);

        var slash = value.IndexOf('/', StringComparison.Ordinal);
        if (slash > 0)
        {
            var prefix = value[..slash];
            foreach (var id in providerIds)
            {
                if (string.Equals(id, prefix, StringComparison.OrdinalIgnoreCase))
                {
                    return (id, value[(slash + 1)..]);
                }
            }
        }

        return (null, value);
    }
}

using Pillion.Contracts;

namespace Pillion.ModelGateway;

/// <summary>
/// Whether a provider is reachable now. The connectivity work item (section 11) supplies the real implementation;
/// until then <see cref="AlwaysReachable"/> answers yes for every provider.
/// </summary>
public interface IProviderReachability
{
    /// <summary>Determines whether a provider is reachable.</summary>
    /// <param name="providerId">The provider id.</param>
    /// <returns><see langword="true"/> when requests may be sent to it.</returns>
    bool IsReachable(string providerId);
}

/// <summary>An <see cref="IProviderReachability"/> that reports every provider reachable.</summary>
public sealed class AlwaysReachable : IProviderReachability
{
    /// <inheritdoc/>
    public bool IsReachable(string providerId) => true;
}

/// <summary>
/// Spend caps per provider (section 6, Cost and rate governance). Phase 0 defines the interface only and
/// <see cref="NoCostCaps"/> always passes.
/// </summary>
public interface ICostGovernor
{
    /// <summary>Determines whether a provider is within its spend caps.</summary>
    /// <param name="providerId">The provider id.</param>
    /// <returns><see langword="true"/> when requests may be sent to it.</returns>
    bool IsWithinCap(string providerId);
}

/// <summary>An <see cref="ICostGovernor"/> that never blocks.</summary>
public sealed class NoCostCaps : ICostGovernor
{
    /// <inheritdoc/>
    public bool IsWithinCap(string providerId) => true;
}

/// <summary>What the router needs to know about a request.</summary>
public sealed record RoutingRequest
{
    /// <summary>Gets the task class.</summary>
    public TaskClass TaskClass { get; init; }

    /// <summary>Gets the profile the request asks for; <see cref="Profile.Auto"/> lets the task-class setting and the Auto policy decide.</summary>
    public Profile Profile { get; init; } = Profile.Auto;

    /// <summary>Gets an explicit model id (<c>providerId/modelId</c> or a bare model id) that overrides routing.</summary>
    public string? ModelId { get; init; }

    /// <summary>Gets the estimated prompt size in tokens, for the context-size check.</summary>
    public int EstimatedPromptTokens { get; init; }
}

/// <summary>A routing decision.</summary>
/// <param name="ProviderId">The provider that serves the request.</param>
/// <param name="ModelId">The provider's model id.</param>
/// <param name="Profile">The profile the model was chosen for.</param>
/// <param name="IsLocal">Whether the provider is local.</param>
/// <param name="Reason">A human-readable explanation, for logs and the status bar tooltip.</param>
public sealed record RoutedModel(string ProviderId, string ModelId, Profile Profile, bool IsLocal, string Reason)
{
    /// <summary>Gets the models to try, in order, when this one fails before producing output.</summary>
    public IReadOnlyList<RoutedModel> Fallbacks { get; init; } = [];

    /// <summary>Gets the qualified model id <c>providerId/modelId</c>.</summary>
    public string QualifiedId => QualifiedModelId.Format(ProviderId, ModelId);
}

/// <summary>
/// Chooses a provider and model for a request (design section 6). Rules apply in this order: policy (Local-only),
/// per-task-class cloud switches, connectivity, capability, cost caps, then the request's explicit model id, which
/// always wins over Auto.
/// </summary>
public interface IModelRouter
{
    /// <summary>Routes a request.</summary>
    /// <param name="request">The request.</param>
    /// <returns>The chosen model with its fallback chain.</returns>
    /// <exception cref="PillionRpcException">No model can serve the request (the error code says why).</exception>
    RoutedModel Resolve(RoutingRequest request);

    /// <summary>Routes a chat request.</summary>
    /// <param name="taskClass">The task class.</param>
    /// <param name="request">The chat request.</param>
    /// <returns>The chosen model with its fallback chain.</returns>
    RoutedModel Resolve(TaskClass taskClass, ChatRequest request);

    /// <summary>Routes a fill-in-the-middle request.</summary>
    /// <param name="taskClass">The task class (normally <see cref="TaskClass.Autocomplete"/>).</param>
    /// <param name="request">The FIM request.</param>
    /// <returns>The chosen model with its fallback chain.</returns>
    RoutedModel Resolve(TaskClass taskClass, FimRequest request);
}

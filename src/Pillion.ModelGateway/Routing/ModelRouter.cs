using System.Globalization;
using Pillion.Common.Settings;
using Pillion.Contracts;

namespace Pillion.ModelGateway;

/// <summary>
/// The router (design section 6, Task classes, profiles and the Auto policy). For each request it picks a profile
/// (the request's, else the task class's setting, else the Auto policy), expands it into a fallback chain (the next
/// profile in the same locality, then local), turns each profile into concrete provider and model candidates
/// (<c>routing.profiles</c>, else the catalog, else the provider's allowed or installed models), and filters the
/// candidates by the rules in this order: policy, cloud switches, connectivity, capability, cost caps. An explicit
/// model id on the request wins over all of that, subject only to policy and the cloud switches.
/// </summary>
public sealed class ModelRouter : IModelRouter
{
    /// <summary>How a candidate was chosen when <c>routing.profiles</c> names it.</summary>
    private const string ConfiguredHow = "routing.profiles";

    private static readonly Profile[] CloudProfiles = [Profile.Frontier, Profile.Balanced, Profile.Fast];

    private readonly IProviderRegistry _registry;
    private readonly ModelCatalog _catalog;
    private readonly ModelInventory _inventory;
    private readonly IProviderReachability _reachability;
    private readonly CircuitBreakerRegistry _breakers;
    private readonly ICostGovernor _costs;

    /// <summary>Initializes a new instance of the <see cref="ModelRouter"/> class.</summary>
    /// <param name="registry">The configured providers.</param>
    /// <param name="catalog">The model catalog.</param>
    /// <param name="inventory">The models providers last reported.</param>
    /// <param name="reachability">Connectivity per provider.</param>
    /// <param name="breakers">The circuit breakers (an open circuit counts as unreachable).</param>
    /// <param name="costs">The spend caps.</param>
    public ModelRouter(
        IProviderRegistry registry,
        ModelCatalog catalog,
        ModelInventory inventory,
        IProviderReachability reachability,
        CircuitBreakerRegistry breakers,
        ICostGovernor costs)
    {
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(inventory);
        ArgumentNullException.ThrowIfNull(reachability);
        ArgumentNullException.ThrowIfNull(breakers);
        ArgumentNullException.ThrowIfNull(costs);

        _registry = registry;
        _catalog = catalog;
        _inventory = inventory;
        _reachability = reachability;
        _breakers = breakers;
        _costs = costs;
    }

    /// <summary>Whether a profile names a cloud model.</summary>
    /// <param name="profile">The profile.</param>
    /// <returns><see langword="true"/> for Fast, Balanced and Frontier.</returns>
    public static bool IsCloudProfile(Profile profile) => profile is Profile.Fast or Profile.Balanced or Profile.Frontier;

    /// <summary>Gets the per-task-class cloud switch.</summary>
    /// <param name="switches">The switches.</param>
    /// <param name="taskClass">The task class.</param>
    /// <returns>Whether the task class may use a cloud provider. Embedding may only when <c>cloudSwitches.embedding</c> is on, which it is not by default.</returns>
    public static bool IsCloudAllowed(CloudSwitchSettings switches, TaskClass taskClass)
    {
        ArgumentNullException.ThrowIfNull(switches);
        return taskClass switch
        {
            TaskClass.Autocomplete => switches.Autocomplete,
            TaskClass.InlineEdit => switches.InlineEdit,
            TaskClass.Chat => switches.Chat,
            TaskClass.Summarise => switches.Chat,
            TaskClass.Agent => switches.Agent,
            TaskClass.Vision => switches.Vision,
            TaskClass.Embedding => switches.Embedding,
            _ => false,
        };
    }

    /// <summary>Gets the fallback chain for a profile: the profile, the next profile in the same locality, then local.</summary>
    /// <param name="profile">The primary profile.</param>
    /// <param name="taskClass">The task class (autocomplete prefers the fast local model).</param>
    /// <returns>The profiles to try, in order.</returns>
    public static IReadOnlyList<Profile> FallbackChain(Profile profile, TaskClass taskClass)
    {
        Profile[] local = taskClass == TaskClass.Autocomplete || profile == Profile.LocalFast
            ? [Profile.LocalFast, Profile.LocalStrong]
            : [Profile.LocalStrong, Profile.LocalFast];

        return profile switch
        {
            Profile.Frontier => [Profile.Frontier, Profile.Balanced, .. local],
            Profile.Balanced => [Profile.Balanced, Profile.Fast, .. local],
            Profile.Fast => [Profile.Fast, Profile.Balanced, .. local],
            _ => local,
        };
    }

    /// <inheritdoc/>
    public RoutedModel Resolve(TaskClass taskClass, ChatRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        return Resolve(new RoutingRequest
        {
            TaskClass = taskClass,
            Profile = request.Profile,
            ModelId = request.ModelId,
            EstimatedPromptTokens = TokenEstimator.Estimate(request.Messages.Select(m => m.Content)),
        });
    }

    /// <inheritdoc/>
    public RoutedModel Resolve(TaskClass taskClass, FimRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        return Resolve(new RoutingRequest
        {
            TaskClass = taskClass,
            Profile = request.Profile ?? Profile.Auto,
            ModelId = request.ModelId,
            EstimatedPromptTokens = TokenEstimator.Estimate(request.Prefix) + TokenEstimator.Estimate(request.Suffix),
        });
    }

    /// <inheritdoc/>
    public RoutedModel Resolve(RoutingRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        var snapshot = _registry.GetSnapshot();
        var settings = snapshot.Settings.Settings;
        var rejections = new List<Rejection>();

        // 1-2. Policy and cloud switches decide whether cloud providers are in play at all.
        var cloudBlockedReason = CloudBlockedReason(snapshot.Settings, request.TaskClass);

        var (profile, profileReason) = ChooseProfile(request, snapshot, cloudBlockedReason);
        var survivors = new List<RoutedModel>();
        foreach (var chainProfile in FallbackChain(profile, request.TaskClass))
        {
            foreach (var candidate in Candidates(chainProfile, request.TaskClass, snapshot))
            {
                var rejection = Check(candidate, request, cloudBlockedReason);
                if (rejection is not null)
                {
                    rejections.Add(rejection);
                    continue;
                }

                if (!survivors.Any(s => SameModel(s, candidate)))
                {
                    var reason = chainProfile == profile
                        ? $"{request.TaskClass}: {profileReason}"
                        : $"{request.TaskClass}: {profileReason}; fell back to {chainProfile}";
                    survivors.Add(candidate with { Reason = AppendRejections(reason, rejections) });
                }
            }
        }

        // 6. The request's explicit model id wins over Auto (still subject to policy and the cloud switches).
        if (!string.IsNullOrWhiteSpace(request.ModelId))
        {
            var chosen = ResolveExplicit(request, snapshot, cloudBlockedReason, profile);
            return chosen with { Fallbacks = survivors.Where(s => !SameModel(s, chosen)).ToList() };
        }

        if (survivors.Count == 0)
        {
            throw NoRoute(request, profile, rejections, snapshot, cloudBlockedReason);
        }

        return survivors[0] with { Fallbacks = survivors.Skip(1).ToList() };
    }

    private static bool SameModel(RoutedModel a, RoutedModel b) =>
        string.Equals(a.ProviderId, b.ProviderId, StringComparison.OrdinalIgnoreCase) && ModelInventory.ModelIdsEqual(a.ModelId, b.ModelId);

    private static string? CloudBlockedReason(EffectiveSettings settings, TaskClass taskClass)
    {
        if (settings.LocalOnly)
        {
            return "Local-only mode is on";
        }

        return IsCloudAllowed(settings.Settings.CloudSwitches, taskClass)
            ? null
            : $"the cloud switch for {taskClass} is off";
    }

    private (Profile Profile, string Reason) ChooseProfile(RoutingRequest request, ProviderRegistrySnapshot snapshot, string? cloudBlockedReason)
    {
        if (request.Profile != Profile.Auto)
        {
            return (request.Profile, $"{request.Profile} (requested)");
        }

        if (snapshot.Settings.Settings.Routing.Tasks.TryGetValue(request.TaskClass, out var configured) && configured != Profile.Auto)
        {
            return (configured, $"{configured} (task setting)");
        }

        var online = cloudBlockedReason is null && snapshot.Providers.Any(p => !p.IsLocal && IsReachable(p.Id) && _costs.IsWithinCap(p.Id));
        var state = online ? "online" : "offline";
        switch (request.TaskClass)
        {
            case TaskClass.Autocomplete:
                if (HasLocalFimModel(snapshot))
                {
                    return (Profile.LocalFast, "Auto: LocalFast (a local FIM model is installed)");
                }

                return online
                    ? (Profile.Fast, "Auto: Fast (no local FIM model; cloud autocomplete is on)")
                    : (Profile.LocalFast, $"Auto: LocalFast ({state})");

            case TaskClass.Chat:
            case TaskClass.InlineEdit:
                return online ? (Profile.Balanced, "Auto: Balanced (online)") : (Profile.LocalStrong, $"Auto: LocalStrong ({state})");

            case TaskClass.Agent:
            case TaskClass.Vision:
                return online ? (Profile.Frontier, "Auto: Frontier (online)") : (Profile.LocalStrong, $"Auto: LocalStrong ({state})");

            case TaskClass.Embedding:
                // Embeddings stay local under Auto even when the cloud switch is on: the switch only allows a cloud
                // embedding model that routing.tasks.embedding, a requested profile or an explicit model id names.
                return (Profile.LocalFast, "Auto: local (embeddings stay local unless a cloud model is requested and the Embedding cloud switch is on)");

            case TaskClass.Summarise:
                return online ? (Profile.Fast, "Auto: Fast (online)") : (Profile.LocalFast, $"Auto: LocalFast ({state})");

            default:
                return online ? (Profile.Balanced, "Auto: Balanced (online)") : (Profile.LocalStrong, $"Auto: LocalStrong ({state})");
        }
    }

    private bool HasLocalFimModel(ProviderRegistrySnapshot snapshot)
    {
        foreach (var candidate in Candidates(Profile.LocalFast, TaskClass.Autocomplete, snapshot))
        {
            if (IsReachable(candidate.ProviderId) &&
                Capabilities(candidate.ProviderId, candidate.ModelId, isLocal: true).Fim &&
                IsInstalled(candidate.ProviderId, candidate.ModelId) != false)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Turns a profile into concrete candidates, one per provider of the profile's locality, in settings order. For the
    /// Embedding task class a model named by <c>routing.profiles</c> comes first, then the provider kinds the catalog's
    /// <c>embeddingProviderOrder</c> lists (the bundled model, for the default experience), then the rest.
    /// </summary>
    private List<RoutedModel> Candidates(Profile profile, TaskClass taskClass, ProviderRegistrySnapshot snapshot)
    {
        var candidates = CandidatesInSettingsOrder(profile, taskClass, snapshot).ToList();
        if (taskClass != TaskClass.Embedding || candidates.Count < 2)
        {
            return candidates;
        }

        return [.. candidates
            .OrderBy(c => c.Reason == ConfiguredHow ? 0 : 1)
            .ThenBy(c => snapshot.Find(c.ProviderId) is { } provider ? _catalog.GetEmbeddingProviderPriority(provider.Kind) : int.MaxValue)];
    }



    private IEnumerable<RoutedModel> CandidatesInSettingsOrder(Profile profile, TaskClass taskClass, ProviderRegistrySnapshot snapshot)
    {
        var settings = snapshot.Settings.Settings;
        var wantLocal = !IsCloudProfile(profile);
        settings.Routing.Profiles.TryGetValue(profile, out var configured);
        var configuredTarget = string.IsNullOrWhiteSpace(configured)
            ? ((string? ProviderId, string ModelId)?)null
            : QualifiedModelId.Parse(configured, snapshot.ProviderIds);

        foreach (var provider in snapshot.Providers)
        {
            if (provider.IsLocal != wantLocal)
            {
                continue;
            }

            var providerSettings = snapshot.FindSettings(provider.Id)!;
            string? model;
            string how;
            // routing.profiles names chat and completion models, so it does not apply to embeddings unless it names an embedding model.
            var configuredApplies = configuredTarget is { } named &&
                (taskClass != TaskClass.Embedding ||
                    ((named.ProviderId is null || string.Equals(named.ProviderId, provider.Id, StringComparison.OrdinalIgnoreCase)) &&
                        Capabilities(provider.Id, named.ModelId, provider.IsLocal).Embeddings));
            if (configuredApplies && configuredTarget is { } target &&
                (target.ProviderId is null || string.Equals(target.ProviderId, provider.Id, StringComparison.OrdinalIgnoreCase)))
            {
                model = target.ModelId;
                how = ConfiguredHow;
            }
            else if (configuredApplies && configuredTarget is { ProviderId: not null })
            {
                continue; // The profile is pinned to another provider.
            }
            else
            {
                (model, how) = DefaultModel(provider, providerSettings, profile, taskClass);
            }

            if (model is null)
            {
                continue;
            }

            yield return new RoutedModel(provider.Id, model, profile, provider.IsLocal, how);
        }
    }

    private (string? Model, string How) DefaultModel(IModelProvider provider, ProviderSettings settings, Profile profile, TaskClass taskClass)
    {
        var allowed = settings.Models;
        string? catalogModel;
        if (taskClass == TaskClass.Embedding)
        {
            // The catalog lists the preferred embedding models per provider kind; the first one that is allowed and installed wins.
            var preferred = _catalog.GetDefaultEmbeddingModels(provider.Kind);
            catalogModel = preferred.FirstOrDefault(m =>
                    (allowed.Count == 0 || allowed.Any(a => ModelInventory.ModelIdsEqual(a, m))) && IsInstalled(provider.Id, m) != false)
                ?? (preferred.Count > 0 ? preferred[0] : null);
        }
        else
        {
            catalogModel = _catalog.GetDefaultModel(provider.Kind, profile);
        }

        if (catalogModel is not null &&
            (allowed.Count == 0 || allowed.Any(m => ModelInventory.ModelIdsEqual(m, catalogModel))) &&
            IsInstalled(provider.Id, catalogModel) != false &&
            MeetsRequirement(Capabilities(provider.Id, catalogModel, provider.IsLocal), taskClass))
        {
            return (catalogModel, "catalog");
        }

        // The catalog's model is not allowed or not installed: use the first allowed or installed model that can do the job.
        var pool = allowed.Count > 0
            ? allowed
            : _inventory.TryGet(provider.Id, out var installed) ? installed.Select(m => m.ModelId).ToList() : [];
        foreach (var model in pool)
        {
            if (IsInstalled(provider.Id, model) != false && MeetsRequirement(Capabilities(provider.Id, model, provider.IsLocal), taskClass))
            {
                return (model, catalogModel is null ? "provider models" : $"provider models (catalog model {catalogModel} not available)");
            }
        }

        return (catalogModel, "catalog");
    }

    private static bool MeetsRequirement(ModelCapabilityDescriptor capabilities, TaskClass taskClass) => taskClass switch
    {
        TaskClass.Autocomplete => capabilities.Fim,
        TaskClass.Vision => capabilities.Vision,
        TaskClass.Embedding => capabilities.Embeddings,
        _ => capabilities.Chat,
    };

    private Rejection? Check(RoutedModel candidate, RoutingRequest request, string? cloudBlockedReason)
    {
        // 1-2. Policy and cloud switches.
        if (!candidate.IsLocal && cloudBlockedReason is not null)
        {
            return new Rejection(candidate, RejectionKind.Policy, cloudBlockedReason);
        }

        // 3. Connectivity (an open circuit counts as unreachable).
        if (!IsReachable(candidate.ProviderId))
        {
            return new Rejection(candidate, RejectionKind.Unreachable, "unreachable");
        }

        // 4. Capability: FIM for autocomplete, vision for vision, embeddings for embedding, context size; installed.
        var capabilities = Capabilities(candidate.ProviderId, candidate.ModelId, candidate.IsLocal);
        if (IsInstalled(candidate.ProviderId, candidate.ModelId) == false)
        {
            return new Rejection(candidate, RejectionKind.NotInstalled, "not installed");
        }

        if (!MeetsRequirement(capabilities, request.TaskClass))
        {
            var missing = request.TaskClass switch
            {
                TaskClass.Autocomplete => "fill-in-the-middle",
                TaskClass.Vision => "vision",
                TaskClass.Embedding => "embeddings",
                _ => "chat",
            };
            return new Rejection(candidate, RejectionKind.Capability, $"no {missing} support");
        }

        if (request.EstimatedPromptTokens > capabilities.ContextWindow)
        {
            return new Rejection(
                candidate,
                RejectionKind.ContextSize,
                string.Create(CultureInfo.InvariantCulture, $"prompt of ~{request.EstimatedPromptTokens} tokens exceeds its {capabilities.ContextWindow}-token context"));
        }

        // 5. Cost caps.
        if (!_costs.IsWithinCap(candidate.ProviderId))
        {
            return new Rejection(candidate, RejectionKind.Cost, "over its spend cap");
        }

        return null;
    }

    private RoutedModel ResolveExplicit(RoutingRequest request, ProviderRegistrySnapshot snapshot, string? cloudBlockedReason, Profile autoProfile)
    {
        var settings = snapshot.Settings.Settings;
        var (providerId, modelId) = QualifiedModelId.Parse(request.ModelId!.Trim(), snapshot.ProviderIds);

        IModelProvider? provider;
        if (providerId is not null)
        {
            provider = snapshot.Find(providerId);
            if (provider is null)
            {
                var hidden = snapshot.Hidden.FirstOrDefault(h => string.Equals(h.Settings.Id, providerId, StringComparison.OrdinalIgnoreCase));
                throw new PillionRpcException(new PillionError
                {
                    Code = PillionErrorCode.ProviderUnavailable,
                    Message = $"Model provider '{providerId}' is not available: {hidden?.Reason ?? "it is not configured"}",
                });
            }
        }
        else
        {
            provider = snapshot.Providers.FirstOrDefault(p => _inventory.Find(p.Id, modelId) is not null)
                ?? snapshot.Providers.FirstOrDefault(p => snapshot.FindSettings(p.Id)!.Models.Any(m => ModelInventory.ModelIdsEqual(m, modelId)))
                ?? snapshot.Providers.FirstOrDefault(p => p.IsLocal)
                ?? (snapshot.Providers.Count > 0 ? snapshot.Providers[0] : null)
                ?? throw new PillionRpcException(new PillionError
                {
                    Code = PillionErrorCode.ProviderUnavailable,
                    Message = $"No model provider is configured to serve '{modelId}'.",
                });
        }

        if (!provider.IsLocal && cloudBlockedReason is not null)
        {
            throw new PillionRpcException(new PillionError
            {
                Code = PillionErrorCode.PolicyDenied,
                Message = $"'{QualifiedModelId.Format(provider.Id, modelId)}' is a cloud model and {cloudBlockedReason}.",
            });
        }

        var profile = request.Profile != Profile.Auto
            ? request.Profile
            : PinnedProfile(settings, snapshot.ProviderIds, provider, modelId) ?? (provider.IsLocal
                ? (IsCloudProfile(autoProfile) ? (request.TaskClass == TaskClass.Autocomplete ? Profile.LocalFast : Profile.LocalStrong) : autoProfile)
                : (IsCloudProfile(autoProfile) ? autoProfile : Profile.Balanced));

        return new RoutedModel(provider.Id, modelId, profile, provider.IsLocal, $"{request.TaskClass}: explicit model {QualifiedModelId.Format(provider.Id, modelId)}");
    }

    private Profile? PinnedProfile(PillionSettings settings, IEnumerable<string> providerIds, IModelProvider provider, string modelId)
    {
        foreach (var (profile, configured) in settings.Routing.Profiles)
        {
            if (string.IsNullOrWhiteSpace(configured))
            {
                continue;
            }

            var (pinnedProvider, pinnedModel) = QualifiedModelId.Parse(configured, providerIds);
            if ((pinnedProvider is null || string.Equals(pinnedProvider, provider.Id, StringComparison.OrdinalIgnoreCase)) &&
                ModelInventory.ModelIdsEqual(pinnedModel, modelId) &&
                IsCloudProfile(profile) != provider.IsLocal)
            {
                return profile;
            }
        }

        foreach (var profile in provider.IsLocal ? [Profile.LocalFast, Profile.LocalStrong] : CloudProfiles)
        {
            if (_catalog.GetDefaultModel(provider.Kind, profile) is { } model && ModelInventory.ModelIdsEqual(model, modelId))
            {
                return profile;
            }
        }

        return null;
    }

    private bool IsReachable(string providerId) => _reachability.IsReachable(providerId) && _breakers.IsAvailable(providerId);

    /// <summary>Whether the provider reported the model: <see langword="null"/> when the provider has not been listed yet.</summary>
    private bool? IsInstalled(string providerId, string modelId) =>
        _inventory.TryGet(providerId, out _) ? _inventory.Find(providerId, modelId) is not null : null;

    private ModelCapabilityDescriptor Capabilities(string providerId, string modelId, bool isLocal) =>
        _inventory.Find(providerId, modelId)?.Capabilities ?? _catalog.GetCapabilities(modelId, isLocal);

    private static string AppendRejections(string reason, List<Rejection> rejections) =>
        rejections.Count == 0
            ? reason
            : reason + "; skipped " + string.Join(", ", rejections.Select(r => $"{r.Candidate.QualifiedId} ({r.Reason})"));

    private static PillionRpcException NoRoute(RoutingRequest request, Profile profile, List<Rejection> rejections, ProviderRegistrySnapshot snapshot, string? cloudBlockedReason)
    {
        PillionErrorCode code;
        string message;
        if (cloudBlockedReason is not null && snapshot.Providers.Any(p => !p.IsLocal) && rejections.All(r => r.Kind == RejectionKind.Policy))
        {
            code = PillionErrorCode.PolicyDenied;
            message = $"No local model can serve this {request.TaskClass} request, and cloud providers are not used because {cloudBlockedReason}.";
        }
        else if (rejections.Count == 0)
        {
            code = PillionErrorCode.ProviderUnavailable;
            message = snapshot.Providers.Count == 0
                ? "No model provider is configured. Add one under Options, Pillion, Providers."
                : $"No configured provider offers a model for {profile} or its fallbacks.";
        }
        else
        {
            code = rejections.All(r => r.Kind == RejectionKind.Policy) ? PillionErrorCode.PolicyDenied
                : rejections.Any(r => r.Kind == RejectionKind.NotInstalled) && rejections.All(r => r.Kind is RejectionKind.NotInstalled or RejectionKind.Policy or RejectionKind.Capability)
                    ? PillionErrorCode.ModelNotInstalled
                : rejections.All(r => r.Kind is RejectionKind.ContextSize or RejectionKind.Policy) ? PillionErrorCode.ContextOverflow
                : PillionErrorCode.ProviderUnavailable;
            message = $"No model can serve this {request.TaskClass} request: " +
                string.Join("; ", rejections.Select(r => $"{r.Candidate.QualifiedId} {r.Reason}")) + ".";
        }

        return new PillionRpcException(new PillionError { Code = code, Message = message });
    }

    private enum RejectionKind
    {
        Policy,
        Unreachable,
        NotInstalled,
        Capability,
        ContextSize,
        Cost,
    }

    private sealed record Rejection(RoutedModel Candidate, RejectionKind Kind, string Reason);
}

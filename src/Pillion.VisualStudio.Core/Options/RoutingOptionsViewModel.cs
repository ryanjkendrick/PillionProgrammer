using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.Threading;
using Newtonsoft.Json.Linq;
using Pillion.Contracts;

namespace Pillion.VisualStudio.Core;

/// <summary>
/// The Tools &gt; Options &gt; Pillion Programmer &gt; Models and routing page (design doc sections 5 and 6): the profile
/// and the cloud switch of every task class (<c>routing.tasks.*</c>, <c>cloudSwitches.*</c>), an optional model id per
/// profile (<c>routing.profiles.*</c>), the "Think harder by default" control (<c>routing.thinkHarder</c>) and the GPU
/// tier override (<c>models.gpuTier</c>). Only the keys the user changed are written, one <c>SetAsync</c> each, so a
/// save never overwrites a value another page or an editor changed meanwhile.
/// </summary>
public sealed class RoutingOptionsViewModel : OptionsPageViewModel
{
    /// <summary>The key of the routing section.</summary>
    public const string RoutingKey = "routing";

    /// <summary>The key of the "Think harder by default" setting.</summary>
    public const string ThinkHarderKey = "routing.thinkHarder";

    /// <summary>The key of the GPU tier setting.</summary>
    public const string GpuTierKey = "models.gpuTier";

    /// <summary>The text of the line explaining the GPU tier.</summary>
    public const string GpuTierExplanation =
        "The tier picks which local models stay loaded and how much code context autocomplete sends. Auto detects it from the graphics adapter's dedicated memory; override it if detection is wrong.";

    private static readonly TimeSpan ModelListTimeout = TimeSpan.FromSeconds(5);

    private bool _thinkHarder;
    private bool _loadedThinkHarder;
    private bool _localOnly;
    private OptionChoice _selectedGpuTier = RoutingChoices.GpuTiers[0];
    private string _loadedGpuTier = "auto";

    /// <summary>Initializes a new instance of the <see cref="RoutingOptionsViewModel"/> class.</summary>
    /// <param name="connection">The connection to Pillion Host.</param>
    /// <param name="joinableTaskFactory">The package's joinable task factory.</param>
    /// <param name="log">The logger, or <see langword="null"/>.</param>
    public RoutingOptionsViewModel(IHostConnection connection, JoinableTaskFactory joinableTaskFactory, PillionLog? log = null)
        : base(connection, joinableTaskFactory, log)
    {
        TaskRoutes =
        [
            new(TaskClass.Autocomplete, "Autocomplete", "Ghost-text suggestions as you type. Needs a fill-in-the-middle model.", "autocomplete", cloudSwitchDefault: false,
                "Off by default: suggestions use the local completion model. Turn on to let them use a cloud model."),
            new(TaskClass.InlineEdit, "Inline edit", "Edits to a selection or the code around the caret.", "inlineEdit", cloudSwitchDefault: true,
                "Inline chat and refactoring proposals may use a cloud model."),
            new(TaskClass.Chat, "Chat", "Conversations in the Pillion chat window.", "chat", cloudSwitchDefault: true,
                "On once a cloud key is added. A cloud key enables chat only; the other classes need their own switch."),
            new(TaskClass.Agent, "Agent", "Planning and steps of autonomous agent sessions.", "agent", cloudSwitchDefault: false,
                "Off by default: the agent needs its own switch. Fixes for failed builds and tests (self-healing) have theirs under Privacy."),
            new(TaskClass.Embedding, "Embedding", "Indexing and search over your code.", cloudSwitchKey: null, cloudSwitchDefault: false,
                "Always runs on this machine, so there is no cloud switch."),
            new(TaskClass.Vision, "Vision", "Requests that include screenshots or images.", "vision", cloudSwitchDefault: true,
                "Browser screenshots may be sent to a cloud vision model."),
        ];

        ProfileOverrides =
        [
            new(Profile.Fast, "Fast", isLocal: false),
            new(Profile.Balanced, "Balanced", isLocal: false),
            new(Profile.Frontier, "Frontier", isLocal: false),
            new(Profile.LocalFast, "Local-fast", isLocal: true),
            new(Profile.LocalStrong, "Local-strong", isLocal: true),
        ];

        foreach (var route in TaskRoutes)
        {
            route.Changed += OnItemChanged;
        }

        foreach (var profile in ProfileOverrides)
        {
            profile.Changed += OnItemChanged;
        }
    }

    /// <summary>Gets the task class rows, one per <see cref="TaskClass"/>.</summary>
    public ObservableCollection<TaskRouteViewModel> TaskRoutes { get; }

    /// <summary>Gets the profile rows with their optional model id.</summary>
    public ObservableCollection<ProfileOverrideViewModel> ProfileOverrides { get; }

    /// <summary>Gets the GPU tier choices (an instance member so the view can bind to it).</summary>
#pragma warning disable CA1822 // Bound from XAML.
    public IReadOnlyList<OptionChoice> GpuTierChoices => RoutingChoices.GpuTiers;

    /// <summary>Gets the line explaining the GPU tier (an instance member so the view can bind to it).</summary>
    public string GpuTierHelp => GpuTierExplanation;

    /// <summary>Gets the explanation of the "Think harder" control (an instance member so the view can bind to it).</summary>
    public string ThinkHarderHelp =>
        "A placeholder for now: Pillion records the choice, and Auto will use it to prefer a more capable model with more reasoning effort where a provider supports it.";
#pragma warning restore CA1822

    /// <summary>Gets or sets a value indicating whether "Think harder" is on by default (<c>routing.thinkHarder</c>).</summary>
    public bool ThinkHarder
    {
        get => _thinkHarder;
        set
        {
            if (SetProperty(ref _thinkHarder, value))
            {
                IsDirty = true;
            }
        }
    }

    /// <summary>Gets or sets the GPU tier override (<c>models.gpuTier</c>).</summary>
    public OptionChoice SelectedGpuTier
    {
        get => _selectedGpuTier;
        set
        {
            if (value is not null && SetProperty(ref _selectedGpuTier, value))
            {
                OnPropertyChanged(nameof(GpuTierText));
                IsDirty = true;
            }
        }
    }

    /// <summary>Gets the read-only line that says which GPU tier is in effect.</summary>
    public string GpuTierText => string.Equals(_loadedGpuTier, "auto", StringComparison.Ordinal)
        ? "GPU tier: Auto (detected by Pillion Host)."
        : "GPU tier: " + (RoutingChoices.Find(RoutingChoices.GpuTiers, _loadedGpuTier)?.Label ?? _loadedGpuTier) + " (set here, not detected).";

    /// <summary>Gets a value indicating whether Local-only mode is on, which overrides every cloud switch.</summary>
    public bool LocalOnly
    {
        get => _localOnly;
        private set
        {
            if (SetProperty(ref _localOnly, value))
            {
                OnPropertyChanged(nameof(LocalOnlyNote));
                foreach (var route in TaskRoutes)
                {
                    route.CloudSwitchEnabled = !value;
                }
            }
        }
    }

    /// <summary>Gets the note shown while Local-only mode is on, or an empty string.</summary>
    public string LocalOnlyNote => LocalOnly
        ? "Local-only mode is on (Privacy): nothing is sent to a cloud model, whatever these switches say."
        : string.Empty;

    /// <inheritdoc/>
    protected override IReadOnlyList<string> WatchedSections { get; } = [RoutingKey, "models", SettingsJsonText.CloudSwitchesKey, "privacy", SettingsJsonText.ProvidersKey];

    /// <inheritdoc/>
    protected override async Task<Action<OptionsPageViewModel>> LoadCoreAsync(ISettingsService settings, CancellationToken cancellationToken)
    {
        var routing = SettingsJsonText.ReadObject(await settings.GetAsync(RoutingKey, cancellationToken).ConfigureAwait(false));
        var switches = SettingsJsonText.ReadObject(await settings.GetAsync(SettingsJsonText.CloudSwitchesKey, cancellationToken).ConfigureAwait(false));
        var gpuTier = SettingsJsonText.Parse(await settings.GetAsync(GpuTierKey, cancellationToken).ConfigureAwait(false)) is JValue { Type: JTokenType.String } tier
            ? (string?)tier
            : null;
        var localOnly = SettingsJsonText.ReadBool(await settings.GetAsync(SettingsJsonText.LocalOnlyKey, cancellationToken).ConfigureAwait(false), fallback: false);
        var models = await ListModelsAsync(cancellationToken).ConfigureAwait(false);

        return page => ((RoutingOptionsViewModel)page).ApplyLoaded(routing, switches, gpuTier, localOnly, models);
    }

    /// <inheritdoc/>
    protected override string? Validate()
    {
        foreach (var profile in ProfileOverrides)
        {
            if (profile.Validate() is { } problem)
            {
                return problem;
            }
        }

        return null;
    }

    /// <inheritdoc/>
    protected override Func<ISettingsService, CancellationToken, Task<Action<OptionsPageViewModel>?>> PrepareSave()
    {
        var writes = new List<(string Key, string Json)>();
        var routes = TaskRoutes.ToList();
        var profiles = ProfileOverrides.ToList();
        var thinkHarder = ThinkHarder;
        var gpuTier = SelectedGpuTier.Value;

        foreach (var route in routes)
        {
            if (!string.Equals(route.SelectedProfile.Value, route.LoadedProfile, StringComparison.Ordinal))
            {
                writes.Add(("routing.tasks." + route.Key, SettingsJsonText.Write(new JValue(route.SelectedProfile.Value))));
            }

            if (route.HasCloudSwitch && route.CloudSwitchOn != route.LoadedCloudSwitch)
            {
                writes.Add((SettingsJsonText.CloudSwitchesKey + "." + route.CloudSwitchKey, SettingsJsonText.Write(route.CloudSwitchOn)));
            }
        }

        foreach (var profile in profiles)
        {
            var id = profile.ModelId.Trim();
            if (!string.Equals(id, profile.LoadedModelId, StringComparison.Ordinal))
            {
                writes.Add(("routing.profiles." + profile.Key, SettingsJsonText.Write(new JValue(id))));
            }
        }

        if (thinkHarder != _loadedThinkHarder)
        {
            writes.Add((ThinkHarderKey, SettingsJsonText.Write(thinkHarder)));
        }

        if (!string.Equals(gpuTier, _loadedGpuTier, StringComparison.Ordinal))
        {
            writes.Add((GpuTierKey, SettingsJsonText.Write(new JValue(gpuTier))));
        }

        return async (settings, cancellationToken) =>
        {
            foreach (var (key, json) in writes)
            {
                await settings.SetAsync(key, json, cancellationToken).ConfigureAwait(false);
            }

            return page => ((RoutingOptionsViewModel)page).ApplySaved(routes, profiles, thinkHarder, gpuTier);
        };
    }

    /// <summary>Lists the models the host's providers offer, for the suggestions. A failure just means no suggestions.</summary>
    private async Task<IReadOnlyList<ModelDescriptor>> ListModelsAsync(CancellationToken cancellationToken)
    {
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(ModelListTimeout);
            var models = await Connection.GetProxy<IModelService>().ListModelsAsync(timeout.Token).ConfigureAwait(false);
            return models ?? [];
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
#pragma warning disable CA1031 // The list is only a convenience; the page works without it.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            Log?.Debug("options.routing.models_unavailable", "Could not list models for the suggestions.", ("error", ex.GetType().Name));
            return [];
        }
    }

    private void ApplyLoaded(JObject routing, JObject switches, string? gpuTier, bool localOnly, IReadOnlyList<ModelDescriptor> models)
    {
        var tasks = routing["tasks"] as JObject ?? [];
        var profiles = routing["profiles"] as JObject ?? [];
        foreach (var route in TaskRoutes)
        {
            var profile = tasks[route.Key] is JValue { Type: JTokenType.String } p ? (string?)p : null;
            var on = route.HasCloudSwitch && switches[route.CloudSwitchKey!] is JValue { Type: JTokenType.Boolean } b ? (bool)b : route.CloudSwitchDefault;
            route.Load(profile, on);
        }

        foreach (var item in ProfileOverrides)
        {
            item.Load(profiles[item.Key] is JValue { Type: JTokenType.String } m ? (string?)m : null);
            item.SetSuggestions(
                models
                    .Where(model => model is not null && model.IsLocal == item.IsLocal && !string.IsNullOrWhiteSpace(model.Id))
                    .Select(model => model.Id)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .OrderBy(id => id, StringComparer.OrdinalIgnoreCase));
        }

        _loadedThinkHarder = routing["thinkHarder"] is JValue { Type: JTokenType.Boolean } t && (bool)t;
        _thinkHarder = _loadedThinkHarder;
        OnPropertyChanged(nameof(ThinkHarder));

        var choice = RoutingChoices.Find(RoutingChoices.GpuTiers, gpuTier) ?? RoutingChoices.GpuTiers[0];
        _loadedGpuTier = choice.Value;
        _selectedGpuTier = choice;
        OnPropertyChanged(nameof(SelectedGpuTier));
        OnPropertyChanged(nameof(GpuTierText));

        LocalOnly = localOnly;
        foreach (var route in TaskRoutes)
        {
            route.CloudSwitchEnabled = !localOnly;
        }
    }

    private void ApplySaved(List<TaskRouteViewModel> routes, List<ProfileOverrideViewModel> profiles, bool thinkHarder, string gpuTier)
    {
        foreach (var route in routes)
        {
            route.MarkSaved();
        }

        foreach (var profile in profiles)
        {
            profile.MarkSaved();
        }

        _loadedThinkHarder = thinkHarder;
        _loadedGpuTier = gpuTier;
        OnPropertyChanged(nameof(GpuTierText));
    }

    private void OnItemChanged(object? sender, EventArgs e) => IsDirty = true;
}

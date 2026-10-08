using System;
using System.Collections.ObjectModel;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.Threading;
using Newtonsoft.Json.Linq;
using Pillion.Contracts;

namespace Pillion.VisualStudio.Core;

/// <summary>One cloud switch on the Privacy page: whether a task class may use cloud providers.</summary>
public sealed class CloudSwitchViewModel : ObservableObject
{
    private bool _isOn;
    private bool _isEnabled = true;

    /// <summary>Initializes a new instance of the <see cref="CloudSwitchViewModel"/> class.</summary>
    /// <param name="key">The property name under <c>cloudSwitches</c> in settings.json.</param>
    /// <param name="label">The label.</param>
    /// <param name="description">The one-line explanation.</param>
    /// <param name="defaultValue">The value when the setting is missing (the host's default).</param>
    public CloudSwitchViewModel(string key, string label, string description, bool defaultValue)
    {
        Key = key;
        Label = label;
        Description = description;
        DefaultValue = defaultValue;
        _isOn = defaultValue;
    }

    /// <summary>Raised when <see cref="IsOn"/> changes.</summary>
    public event EventHandler? Changed;

    /// <summary>Gets the property name under <c>cloudSwitches</c>.</summary>
    public string Key { get; }

    /// <summary>Gets the label.</summary>
    public string Label { get; }

    /// <summary>Gets the one-line explanation.</summary>
    public string Description { get; }

    /// <summary>Gets the value used when the setting is missing.</summary>
    public bool DefaultValue { get; }

    /// <summary>Gets or sets a value indicating whether the task class may use cloud providers.</summary>
    public bool IsOn
    {
        get => _isOn;
        set
        {
            if (SetProperty(ref _isOn, value))
            {
                Changed?.Invoke(this, EventArgs.Empty);
            }
        }
    }

    /// <summary>Gets a value indicating whether the switch can be changed (not while Local-only mode is on).</summary>
    public bool IsEnabled
    {
        get => _isEnabled;
        internal set => SetProperty(ref _isEnabled, value);
    }

    /// <summary>Sets the value without raising <see cref="Changed"/>.</summary>
    /// <param name="value">The value read from settings.</param>
    internal void Load(bool value)
    {
        _isOn = value;
        OnPropertyChanged(nameof(IsOn));
    }
}

/// <summary>
/// The Tools &gt; Options &gt; Pillion Programmer &gt; Privacy page: the Local-only switch (<c>privacy.localOnly</c>) and one
/// cloud switch per task class (<c>cloudSwitches.*</c>), saved through <see cref="ISettingsService.SetAsync"/>.
/// </summary>
public sealed class PrivacyOptionsViewModel : OptionsPageViewModel
{
    /// <summary>The one-line explanation of Local-only mode.</summary>
    public const string LocalOnlyDescription =
        "Disables every cloud provider: nothing is sent to a model outside this machine and no connectivity probes run.";

    private bool _localOnly;
    private JObject _loadedSwitches = [];

    /// <summary>Initializes a new instance of the <see cref="PrivacyOptionsViewModel"/> class.</summary>
    /// <param name="connection">The connection to Pillion Host.</param>
    /// <param name="joinableTaskFactory">The package's joinable task factory.</param>
    /// <param name="log">The logger, or <see langword="null"/>.</param>
    public PrivacyOptionsViewModel(IHostConnection connection, JoinableTaskFactory joinableTaskFactory, PillionLog? log = null)
        : base(connection, joinableTaskFactory, log)
    {
        CloudSwitches =
        [
            new("chat", "Chat", "Chat conversations may use a cloud model. A cloud key enables chat only; the other task classes need their own switch.", defaultValue: true),
            new("inlineEdit", "Inline edit", "Inline chat and refactoring proposals on a selection may use a cloud model.", defaultValue: true),
            new("autocomplete", "Autocomplete", "Ghost-text suggestions may use a cloud model; otherwise they use the local completion model.", defaultValue: false),
            new("agent", "Agent", "Agent planning and steps may use a cloud model.", defaultValue: false),
            new("selfHealing", "Self-healing", "Fixes for failed builds and tests may use a cloud model.", defaultValue: false),
            new("vision", "Vision", "Browser screenshots may be sent to a cloud vision model.", defaultValue: true),
        ];

        foreach (var item in CloudSwitches)
        {
            item.Changed += (_, _) => IsDirty = true;
        }
    }

    /// <summary>Gets or sets a value indicating whether Local-only mode is on.</summary>
    public bool LocalOnly
    {
        get => _localOnly;
        set
        {
            if (SetProperty(ref _localOnly, value))
            {
                IsDirty = true;
                UpdateSwitchesEnabled();
            }
        }
    }

    /// <summary>Gets the one-line explanation of Local-only mode (an instance member so the view can bind to it).</summary>
#pragma warning disable CA1822 // Bound from XAML.
    public string LocalOnlyText => LocalOnlyDescription;
#pragma warning restore CA1822

    /// <summary>Gets the cloud switches, one per task class.</summary>
    public ObservableCollection<CloudSwitchViewModel> CloudSwitches { get; }

    /// <inheritdoc/>
    protected override async Task<Action<OptionsPageViewModel>> LoadCoreAsync(ISettingsService settings, CancellationToken cancellationToken)
    {
        var localOnly = SettingsJsonText.ReadBool(await settings.GetAsync(SettingsJsonText.LocalOnlyKey, cancellationToken).ConfigureAwait(false), fallback: false);
        var switches = SettingsJsonText.ReadObject(await settings.GetAsync(SettingsJsonText.CloudSwitchesKey, cancellationToken).ConfigureAwait(false));
        return page => ((PrivacyOptionsViewModel)page).ApplyLoaded(localOnly, switches);
    }

    /// <inheritdoc/>
    protected override Func<ISettingsService, CancellationToken, Task<Action<OptionsPageViewModel>?>> PrepareSave()
    {
        var localOnly = SettingsJsonText.Write(LocalOnly);

        // Start from the object that was read, so switches this version does not show are kept.
        var switches = (JObject)_loadedSwitches.DeepClone();
        foreach (var item in CloudSwitches)
        {
            switches[item.Key] = item.IsOn;
        }

        var switchesJson = SettingsJsonText.Write(switches);
        return async (settings, cancellationToken) =>
        {
            await settings.SetAsync(SettingsJsonText.LocalOnlyKey, localOnly, cancellationToken).ConfigureAwait(false);
            await settings.SetAsync(SettingsJsonText.CloudSwitchesKey, switchesJson, cancellationToken).ConfigureAwait(false);
            return page => ((PrivacyOptionsViewModel)page)._loadedSwitches = switches;
        };
    }

    private void ApplyLoaded(bool localOnly, JObject switches)
    {
        _loadedSwitches = switches;
        _localOnly = localOnly;
        OnPropertyChanged(nameof(LocalOnly));
        foreach (var item in CloudSwitches)
        {
            item.Load(switches[item.Key] is JValue { Type: JTokenType.Boolean } value ? (bool)value : item.DefaultValue);
        }

        UpdateSwitchesEnabled();
    }

    private void UpdateSwitchesEnabled()
    {
        foreach (var item in CloudSwitches)
        {
            item.IsEnabled = !LocalOnly;
        }
    }
}

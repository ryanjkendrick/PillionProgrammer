using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
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
/// cloud switch per task class (<c>cloudSwitches.*</c>), saved through <see cref="ISettingsService.SetAsync"/>; and Delete all
/// conversations (work item 2.2), which asks the host to delete every saved conversation, message and pin and the whole
/// Outbound log, after a confirmation.
/// </summary>
public sealed class PrivacyOptionsViewModel : OptionsPageViewModel
{
    /// <summary>The one-line explanation of Local-only mode.</summary>
    public const string LocalOnlyDescription =
        "Disables every cloud provider: nothing is sent to a model outside this machine and no connectivity probes run.";

    private readonly ConversationEvents? _conversationEvents;
    /// <summary>The text of the confirmation asked before Delete all.</summary>
    public const string DeleteAllConfirmation =
        "Delete every saved conversation, with its messages and pinned context, and the whole Outbound log? "
        + "This cannot be undone. Settings and the code index are not touched.";

    /// <summary>The one-line explanation beside the Delete all button.</summary>
    public const string DeleteAllDescription =
        "Conversations and the Outbound log are kept on this machine, encrypted, until you delete them. This removes all of them at once.";

    private bool _localOnly;
    private JObject _loadedSwitches = [];
    private bool _isDeletingAll;
    private string _deleteAllStatus = string.Empty;
    private Func<string, bool>? _confirmDeleteAll;

    /// <summary>Initializes a new instance of the <see cref="PrivacyOptionsViewModel"/> class.</summary>
    /// <param name="connection">The connection to Pillion Host.</param>
    /// <param name="joinableTaskFactory">The package's joinable task factory.</param>
    /// <param name="log">The logger, or <see langword="null"/>.</param>
    /// <param name="conversationEvents">Told when Delete all succeeded, so an open Chat window forgets its conversations; <see langword="null"/> tells no one.</param>
    public PrivacyOptionsViewModel(IHostConnection connection, JoinableTaskFactory joinableTaskFactory, PillionLog? log = null, ConversationEvents? conversationEvents = null)
        : base(connection, joinableTaskFactory, log)
    {
        _conversationEvents = conversationEvents;
        DeleteAllConversationsCommand = new DelegateCommand(() => Run(DeleteAllConversationsAsync), () => IsLoaded && !IsDeletingAll && ConfirmDeleteAll is not null);
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

    /// <summary>
    /// Gets or sets the confirmation asked before Delete all: it receives <see cref="DeleteAllConfirmation"/> and returns
    /// <see langword="true"/> to go ahead. Delete all is unavailable without it.
    /// </summary>
    public Func<string, bool>? ConfirmDeleteAll
    {
        get => _confirmDeleteAll;
        set
        {
            _confirmDeleteAll = value;
            DeleteAllConversationsCommand.RaiseCanExecuteChanged();
        }
    }

    /// <summary>Gets the Delete all conversations command.</summary>
    public DelegateCommand DeleteAllConversationsCommand { get; }

    /// <summary>Gets a value indicating whether Delete all is running.</summary>
    public bool IsDeletingAll
    {
        get => _isDeletingAll;
        private set
        {
            if (SetProperty(ref _isDeletingAll, value))
            {
                DeleteAllConversationsCommand.RaiseCanExecuteChanged();
            }
        }
    }

    /// <summary>Gets the result of the last Delete all, or why it failed; empty before one.</summary>
    public string DeleteAllStatus
    {
        get => _deleteAllStatus;
        private set
        {
            if (SetProperty(ref _deleteAllStatus, value ?? string.Empty))
            {
                OnPropertyChanged(nameof(HasDeleteAllStatus));
            }
        }
    }

    /// <summary>Gets a value indicating whether <see cref="DeleteAllStatus"/> has text.</summary>
    public bool HasDeleteAllStatus => _deleteAllStatus.Length > 0;

    /// <summary>Gets the one-line explanation beside the Delete all button (an instance member so the view can bind to it).</summary>
#pragma warning disable CA1822 // Bound from XAML.
    public string DeleteAllText => DeleteAllDescription;
#pragma warning restore CA1822

    /// <summary>
    /// Asks the host to delete every saved conversation, message and pin and the whole Outbound log, after
    /// <see cref="ConfirmDeleteAll"/>. The result, or the reason it failed, is in <see cref="DeleteAllStatus"/>. Call on the UI thread.
    /// </summary>
    /// <returns>A task that completes when the host has answered or the developer declined.</returns>
    public async Task DeleteAllConversationsAsync()
    {
        if (!IsLoaded || IsDeletingAll || ConfirmDeleteAll is not { } confirm || !confirm(DeleteAllConfirmation))
        {
            return;
        }

        IsDeletingAll = true;
        DeleteAllStatus = "Deleting...";
        await TaskScheduler.Default;
        DeleteAllConversationsResult? result = null;
        RpcFailure? failure = null;
        try
        {
            // The host compacts the database after the delete, which takes a moment on a large one.
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(DisposalToken);
            timeout.CancelAfter(TimeSpan.FromMinutes(2));
            result = await Connection.GetProxy<IConversationService>().DeleteAllConversationsAsync(timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (DisposalToken.IsCancellationRequested)
        {
            return;
        }
#pragma warning disable CA1031 // Shown on the page.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            failure = RpcFailure.Describe(ex);
            Log?.Warning("options.privacy.deleteall.failed", "Deleting all conversations failed.", ("error", ex.GetType().Name), ("code", failure.Code?.ToString()));
        }

        await JoinableTaskFactory.SwitchToMainThreadAsync(DisposalToken);
        IsDeletingAll = false;
        if (result is not null)
        {
            DeleteAllStatus = string.Format(
                CultureInfo.CurrentCulture,
                "Deleted {0:N0} conversations, {1:N0} messages, {2:N0} pins and {3:N0} Outbound log entries.",
                result.Conversations,
                result.Messages,
                result.Pins,
                result.OutboundEntries);
            _conversationEvents?.RaiseAllDeleted();
        }
        else
        {
            DeleteAllStatus = failure!.HostLost ? HostNotReadyText + "; nothing was deleted." : "Could not delete: " + failure.Message;
        }
    }

    /// <summary>Gets the cloud switches, one per task class.</summary>
    public ObservableCollection<CloudSwitchViewModel> CloudSwitches { get; }

    /// <inheritdoc/>
    protected override IReadOnlyList<string> WatchedSections { get; } = ["privacy", SettingsJsonText.CloudSwitchesKey];

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

    /// <inheritdoc/>
    protected override void RaiseCommands()
    {
        base.RaiseCommands();
        DeleteAllConversationsCommand?.RaiseCanExecuteChanged();
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

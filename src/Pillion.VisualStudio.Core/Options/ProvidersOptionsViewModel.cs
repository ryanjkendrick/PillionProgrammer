using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.Threading;
using Newtonsoft.Json.Linq;
using Pillion.Contracts;

namespace Pillion.VisualStudio.Core;

/// <summary>
/// The Tools &gt; Options &gt; Pillion Programmer &gt; Providers and keys page: the <c>providers</c> setting with add, edit
/// and remove, an API key per provider stored in the Windows Credential Manager (never in settings), a "Key stored"
/// indicator that checks presence only, and Test connection through <see cref="ISettingsService.TestProviderAsync"/>.
/// </summary>
public sealed class ProvidersOptionsViewModel : OptionsPageViewModel
{
    private readonly ICredentialStore _credentials;
    private List<string> _savedIds = [];
    private ProviderItemViewModel? _selectedProvider;

    /// <summary>Initializes a new instance of the <see cref="ProvidersOptionsViewModel"/> class.</summary>
    /// <param name="connection">The connection to Pillion Host.</param>
    /// <param name="joinableTaskFactory">The package's joinable task factory.</param>
    /// <param name="credentials">Where keys are stored.</param>
    /// <param name="log">The logger, or <see langword="null"/>. Keys are never logged.</param>
    public ProvidersOptionsViewModel(IHostConnection connection, JoinableTaskFactory joinableTaskFactory, ICredentialStore credentials, PillionLog? log = null)
        : base(connection, joinableTaskFactory, log)
    {
        _credentials = credentials ?? throw new ArgumentNullException(nameof(credentials));
        AddCommand = new DelegateCommand(Add, () => IsLoaded && !IsBusy);
        RemoveCommand = new DelegateCommand(Remove, () => IsLoaded && !IsBusy && SelectedProvider is not null);
        TestCommand = new DelegateCommand(() => Run(TestSelectedAsync), () => IsLoaded && !IsBusy && SelectedProvider is not null);
    }

    /// <summary>Gets the provider kinds for the Kind dropdown (an instance member so the view can bind to it).</summary>
#pragma warning disable CA1822 // Bound from XAML.
    public IReadOnlyList<string> Kinds => ProviderItemViewModel.Kinds;
#pragma warning restore CA1822

    /// <summary>Gets the providers.</summary>
    public ObservableCollection<ProviderItemViewModel> Providers { get; } = [];

    /// <summary>Gets or sets the provider being edited.</summary>
    public ProviderItemViewModel? SelectedProvider
    {
        get => _selectedProvider;
        set
        {
            if (SetProperty(ref _selectedProvider, value))
            {
                OnPropertyChanged(nameof(HasSelection));
                RaiseCommands();
            }
        }
    }

    /// <summary>Gets a value indicating whether a provider is selected.</summary>
    public bool HasSelection => SelectedProvider is not null;

    /// <summary>Gets the Add command.</summary>
    public DelegateCommand AddCommand { get; }

    /// <summary>Gets the Remove command; the provider's key is deleted when the page is saved.</summary>
    public DelegateCommand RemoveCommand { get; }

    /// <summary>Gets the Test connection command for the selected provider.</summary>
    public DelegateCommand TestCommand { get; }

    /// <summary>Adds a new provider and selects it. Call on the UI thread.</summary>
    public void Add()
    {
        if (!IsLoaded)
        {
            return;
        }

        var number = 1;
        string id;
        do
        {
            id = "provider-" + number.ToString(CultureInfo.InvariantCulture);
            number++;
        }
        while (Providers.Any(p => string.Equals(p.Id, id, StringComparison.OrdinalIgnoreCase)));

        var item = ProviderItemViewModel.CreateNew(id);
        Attach(item);
        Providers.Add(item);
        SelectedProvider = item;
        IsDirty = true;
    }

    /// <summary>Removes the selected provider. Its key is deleted when the page is saved. Call on the UI thread.</summary>
    public void Remove()
    {
        if (SelectedProvider is not { } item)
        {
            return;
        }

        var index = Providers.IndexOf(item);
        item.Changed -= OnItemChanged;
        Providers.Remove(item);
        SelectedProvider = Providers.Count == 0 ? null : Providers[Math.Min(index, Providers.Count - 1)];
        IsDirty = true;
    }

    /// <summary>
    /// Tests the selected provider with the host's saved settings and key. A new or changed provider must be saved
    /// first. Call on the UI thread.
    /// </summary>
    /// <returns>A task that completes when the result is shown.</returns>
    public async Task TestSelectedAsync()
    {
        if (SelectedProvider is not { } item || !IsLoaded || IsBusy)
        {
            return;
        }

        if (item.IsNew || IsDirty)
        {
            item.TestResult = "Save first: Test connection uses the saved settings and key.";
            return;
        }

        var id = item.Id;
        SetBusy(true);
        item.TestResult = "Testing...";

        await TaskScheduler.Default;
        string text;
        try
        {
            using var timeout = CreateCallTimeout();
            var result = await Connection.GetProxy<ISettingsService>().TestProviderAsync(id, timeout.Token).ConfigureAwait(false);
            var message = string.IsNullOrWhiteSpace(result?.Message) ? string.Empty : " " + result!.Message;
            text = result is { IsSuccess: true } ? "Connection succeeded." + message : "Connection failed." + message;
        }
        catch (OperationCanceledException) when (DisposalToken.IsCancellationRequested)
        {
            return;
        }
#pragma warning disable CA1031 // Shown beside the button.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            text = "Test failed: " + RpcFailure.Describe(ex).Message;
            Log?.Warning("options.test.failed", "Test connection failed.", ("provider", id), ("error", ex.GetType().Name));
        }

        await JoinableTaskFactory.SwitchToMainThreadAsync(DisposalToken);
        SetBusy(false);
        item.TestResult = text;
    }

    /// <inheritdoc/>
    protected override IReadOnlyList<string> WatchedSections { get; } = [SettingsJsonText.ProvidersKey];

    /// <inheritdoc/>
    protected override async Task<Action<OptionsPageViewModel>> LoadCoreAsync(ISettingsService settings, CancellationToken cancellationToken)
    {
        var json = await settings.GetAsync(SettingsJsonText.ProvidersKey, cancellationToken).ConfigureAwait(false);
        var providers = SettingsJsonText.ReadProviders(json);
        var stored = providers
            .Select(p => SettingsJsonText.GetString(p, "id"))
            .Select(HasKey)
            .ToList();

        return page => ((ProvidersOptionsViewModel)page).ApplyLoaded(providers, stored);
    }

    /// <inheritdoc/>
    protected override string? Validate()
    {
        foreach (var item in Providers)
        {
            if (item.Validate() is { } problem)
            {
                SelectedProvider = item;
                return problem;
            }
        }

        var duplicate = Providers.GroupBy(p => p.Id, StringComparer.OrdinalIgnoreCase).FirstOrDefault(g => g.Count() > 1);
        return duplicate is null ? null : "Two providers have the id '" + duplicate.Key + "'; ids must be unique.";
    }

    /// <inheritdoc/>
    protected override Func<ISettingsService, CancellationToken, Task<Action<OptionsPageViewModel>?>> PrepareSave()
    {
        var items = Providers.ToList();
        var array = new JArray(items.Select(i => i.ToSettings()).Cast<object>().ToArray());
        var json = SettingsJsonText.Write(array);
        var ids = items.Select(i => i.Id).ToList();
        var keys = items.Where(i => i.HasPendingKey).Select(i => (i.Id, Key: i.ApiKey)).ToList();
        var removed = _savedIds.Where(id => !ids.Contains(id, StringComparer.OrdinalIgnoreCase)).ToList();

        return async (settings, cancellationToken) =>
        {
            // Settings first: a key is only stored for a provider that is in the settings file.
            await settings.SetAsync(SettingsJsonText.ProvidersKey, json, cancellationToken).ConfigureAwait(false);
            foreach (var (id, key) in keys)
            {
                _credentials.Write(id, key);
            }

            foreach (var id in removed)
            {
                if (CredentialManagerStore.IsValidName(id))
                {
                    _credentials.Delete(id);
                }
            }

            var present = ids.Select(HasKey).ToList();
            return page => ((ProvidersOptionsViewModel)page).ApplySaved(items, ids, present);
        };
    }

    /// <inheritdoc/>
    protected override void RaiseCommands()
    {
        base.RaiseCommands();
        AddCommand?.RaiseCanExecuteChanged();
        RemoveCommand?.RaiseCanExecuteChanged();
        TestCommand?.RaiseCanExecuteChanged();
    }

    private bool HasKey(string id)
    {
        if (!CredentialManagerStore.IsValidName(id))
        {
            return false;
        }

        try
        {
            return _credentials.Exists(id);
        }
#pragma warning disable CA1031 // The indicator falls back to "No key stored".
        catch (Exception ex)
#pragma warning restore CA1031
        {
            Log?.Warning("options.key.check_failed", "Checking for a stored key failed.", ("provider", id), ("error", ex.GetType().Name));
            return false;
        }
    }

    private void ApplyLoaded(IReadOnlyList<JObject> providers, List<bool> stored)
    {
        var selectedId = SelectedProvider?.Id;
        foreach (var old in Providers)
        {
            old.Changed -= OnItemChanged;
        }

        Providers.Clear();
        for (var i = 0; i < providers.Count; i++)
        {
            var item = ProviderItemViewModel.FromSettings(providers[i]);
            item.HasStoredKey = stored[i];
            Attach(item);
            Providers.Add(item);
        }

        _savedIds = Providers.Select(p => p.Id).ToList();
        SelectedProvider = Providers.FirstOrDefault(p => string.Equals(p.Id, selectedId, StringComparison.Ordinal)) ?? Providers.FirstOrDefault();
    }

    private void ApplySaved(List<ProviderItemViewModel> items, List<string> ids, List<bool> present)
    {
        for (var i = 0; i < items.Count; i++)
        {
            items[i].MarkSaved();
            items[i].HasStoredKey = present[i];
        }

        _savedIds = [.. ids];
    }

    private void Attach(ProviderItemViewModel item) => item.Changed += OnItemChanged;

    private void OnItemChanged(object? sender, EventArgs e) => IsDirty = true;
}

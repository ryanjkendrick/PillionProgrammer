using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.Threading;
using Pillion.Contracts;

namespace Pillion.VisualStudio.Core;

/// <summary>Where an Options page is in loading the host's settings.</summary>
public enum OptionsLoadState
{
    /// <summary>Pillion Host is not ready; the page shows "Host not ready" and loads once it is.</summary>
    HostNotReady,

    /// <summary>The settings are being read.</summary>
    Loading,

    /// <summary>The settings are shown and can be edited.</summary>
    Loaded,

    /// <summary>Reading the settings failed; <see cref="OptionsPageViewModel.StatusMessage"/> says why.</summary>
    Failed,
}

/// <summary>
/// Base class for the Tools &gt; Options &gt; Pillion Programmer pages. The pages are a view over the host's settings file
/// (design doc section 5): they load through <see cref="ISettingsService"/> when opened, show "Host not ready" while the
/// connection is not Ready, and never block the UI thread: RPC calls run on the thread pool inside the package's
/// <see cref="JoinableTaskFactory"/>, and only the property updates run on the main thread.
/// </summary>
public abstract class OptionsPageViewModel : ObservableObject, IDisposable
{
    /// <summary>The text shown while the host is not ready.</summary>
    public const string HostNotReadyText = "Host not ready";

    private static readonly TimeSpan CallTimeout = TimeSpan.FromSeconds(30);

    private readonly CancellationTokenSource _disposal = new();
    private OptionsLoadState _loadState = OptionsLoadState.HostNotReady;
    private string _statusMessage = HostNotReadyText;
    private bool _isBusy;
    private bool _isDirty;
    private bool _subscribed;
    private int _loadVersion;
    private bool _disposed;

    /// <summary>Initializes a new instance of the <see cref="OptionsPageViewModel"/> class.</summary>
    /// <param name="connection">The connection to Pillion Host.</param>
    /// <param name="joinableTaskFactory">The package's joinable task factory.</param>
    /// <param name="log">The logger, or <see langword="null"/>.</param>
    protected OptionsPageViewModel(IHostConnection connection, JoinableTaskFactory joinableTaskFactory, PillionLog? log)
    {
        Connection = connection ?? throw new ArgumentNullException(nameof(connection));
        JoinableTaskFactory = joinableTaskFactory ?? throw new ArgumentNullException(nameof(joinableTaskFactory));
        Log = log;
        ReloadCommand = new DelegateCommand(() => Run(LoadAsync), () => !IsBusy);
        SaveCommand = new DelegateCommand(() => Run(SaveAsync), () => IsLoaded && !IsBusy);
    }

    /// <summary>Gets the load state.</summary>
    public OptionsLoadState LoadState
    {
        get => _loadState;
        private set
        {
            if (SetProperty(ref _loadState, value))
            {
                OnPropertyChanged(nameof(IsLoaded));
                OnPropertyChanged(nameof(IsHostNotReady));
                RaiseCommands();
            }
        }
    }

    /// <summary>Gets a value indicating whether the settings are shown and editable.</summary>
    public bool IsLoaded => LoadState == OptionsLoadState.Loaded;

    /// <summary>Gets a value indicating whether the page shows "Host not ready".</summary>
    public bool IsHostNotReady => LoadState == OptionsLoadState.HostNotReady;

    /// <summary>Gets the page's status message ("Host not ready", "Saved.", or an error).</summary>
    public string StatusMessage
    {
        get => _statusMessage;
        protected set => SetProperty(ref _statusMessage, value ?? string.Empty);
    }

    /// <summary>Gets a value indicating whether a load, save or test is running.</summary>
    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (SetProperty(ref _isBusy, value))
            {
                RaiseCommands();
            }
        }
    }

    /// <summary>Gets or sets a value indicating whether there are unsaved changes.</summary>
    public bool IsDirty
    {
        get => _isDirty;
        protected set => SetProperty(ref _isDirty, value);
    }

    /// <summary>Gets the command that reads the settings again.</summary>
    public DelegateCommand ReloadCommand { get; }

    /// <summary>Gets the command that saves the page.</summary>
    public DelegateCommand SaveCommand { get; }

    /// <summary>Gets the connection to Pillion Host.</summary>
    protected IHostConnection Connection { get; }

    /// <summary>Gets the package's joinable task factory.</summary>
    protected JoinableTaskFactory JoinableTaskFactory { get; }

    /// <summary>Gets the logger.</summary>
    protected PillionLog? Log { get; }

    /// <summary>Gets a token that is cancelled when the page is disposed.</summary>
    protected CancellationToken DisposalToken => _disposal.Token;

    /// <summary>
    /// Loads the page from the host's settings, or shows "Host not ready" and loads once the host is ready.
    /// Call on the UI thread (the page's activation); returns without waiting for the host when it is not ready.
    /// </summary>
    /// <returns>A task that completes when the page shows the settings, an error or "Host not ready".</returns>
    public async Task LoadAsync()
    {
        if (_disposed)
        {
            return;
        }

        if (!_subscribed)
        {
            _subscribed = true;
            Connection.StateChanged += OnHostStateChanged;
        }

        var version = Interlocked.Increment(ref _loadVersion);
        await JoinableTaskFactory.SwitchToMainThreadAsync(DisposalToken);
        if (!IsUsable(Connection.State))
        {
            LoadState = OptionsLoadState.HostNotReady;
            StatusMessage = NotReadyMessage(Connection.State);
            return;
        }

        LoadState = OptionsLoadState.Loading;
        StatusMessage = "Loading...";
        IsBusy = true;

        await TaskScheduler.Default;
        Action<OptionsPageViewModel>? apply = null;
        RpcFailure? failure = null;
        try
        {
            using var timeout = CreateCallTimeout();
            var settings = Connection.GetProxy<ISettingsService>();
            apply = await LoadCoreAsync(settings, timeout.Token).ConfigureAwait(false);
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
            Log?.Warning("options.load.failed", "Loading an Options page failed.", ("page", GetType().Name), ("error", ex.GetType().Name));
        }

        await JoinableTaskFactory.SwitchToMainThreadAsync(DisposalToken);
        if (version != Volatile.Read(ref _loadVersion))
        {
            return;
        }

        IsBusy = false;
        if (apply is not null)
        {
            apply(this);
            IsDirty = false;
            LoadState = OptionsLoadState.Loaded;
            StatusMessage = string.Empty;
        }
        else if (failure!.HostLost)
        {
            LoadState = OptionsLoadState.HostNotReady;
            StatusMessage = HostNotReadyText + ". This page loads when Pillion Host is ready.";
        }
        else
        {
            LoadState = OptionsLoadState.Failed;
            StatusMessage = "Could not read the settings: " + failure.Message;
        }
    }

    /// <summary>Saves the page to the host's settings (and, for keys, to the Credential Manager). Call on the UI thread.</summary>
    /// <returns>A task that completes when the save has finished; <see cref="StatusMessage"/> reports the result.</returns>
    public async Task SaveAsync()
    {
        if (!IsLoaded || IsBusy || _disposed)
        {
            return;
        }

        var validation = Validate();
        if (validation is not null)
        {
            StatusMessage = validation;
            return;
        }

        var save = PrepareSave();
        IsBusy = true;
        StatusMessage = "Saving...";

        await TaskScheduler.Default;
        Action<OptionsPageViewModel>? apply = null;
        string? error = null;
        try
        {
            using var timeout = CreateCallTimeout();
            apply = await save(Connection.GetProxy<ISettingsService>(), timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (DisposalToken.IsCancellationRequested)
        {
            return;
        }
#pragma warning disable CA1031 // Shown on the page.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            var failure = RpcFailure.Describe(ex);
            error = failure.HostLost ? HostNotReadyText + "; nothing was saved." : "Could not save: " + failure.Message;
            Log?.Warning("options.save.failed", "Saving an Options page failed.", ("page", GetType().Name), ("error", ex.GetType().Name));
        }

        await JoinableTaskFactory.SwitchToMainThreadAsync(DisposalToken);
        IsBusy = false;
        if (error is null)
        {
            apply?.Invoke(this);
            IsDirty = false;
            StatusMessage = "Saved.";
        }
        else
        {
            StatusMessage = error;
        }
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        Dispose(disposing: true);
        GC.SuppressFinalize(this);
    }

    /// <summary>Stops following the host state.</summary>
    /// <param name="disposing">Whether this is a call to <see cref="Dispose()"/>.</param>
    protected virtual void Dispose(bool disposing)
    {
        if (_disposed || !disposing)
        {
            return;
        }

        _disposed = true;
        if (_subscribed)
        {
            Connection.StateChanged -= OnHostStateChanged;
        }

        _disposal.Cancel();
    }

    /// <summary>
    /// Reads the page's settings on a background thread and returns the update to apply on the UI thread.
    /// </summary>
    /// <param name="settings">The settings proxy.</param>
    /// <param name="cancellationToken">A token that cancels the read.</param>
    /// <returns>The update, applied on the UI thread.</returns>
    protected abstract Task<Action<OptionsPageViewModel>> LoadCoreAsync(ISettingsService settings, CancellationToken cancellationToken);

    /// <summary>Checks the page before saving. Called on the UI thread.</summary>
    /// <returns>A message describing the problem, or <see langword="null"/> when the page can be saved.</returns>
    protected virtual string? Validate() => null;

    /// <summary>
    /// Captures what to save (on the UI thread) and returns the work that writes it (run on a background thread), which
    /// in turn returns an optional update to apply on the UI thread afterwards.
    /// </summary>
    /// <returns>The save work.</returns>
    protected abstract Func<ISettingsService, CancellationToken, Task<Action<OptionsPageViewModel>?>> PrepareSave();

    /// <summary>Runs page work in the background; failures are logged.</summary>
    /// <param name="work">The work.</param>
    protected void Run(Func<Task> work)
    {
        _ = JoinableTaskFactory.RunAsync(async () =>
        {
            try
            {
                await work().ConfigureAwait(true);
            }
            catch (OperationCanceledException) when (DisposalToken.IsCancellationRequested)
            {
                // The page is closing.
            }
#pragma warning disable CA1031 // Fire-and-forget UI work must not crash Visual Studio.
            catch (Exception ex)
#pragma warning restore CA1031
            {
                Log?.Error("options.unexpected", "Unexpected Options page failure.", ("page", GetType().Name), ("error", ex.GetType().Name));
            }
        });
    }

    /// <summary>Creates a call timeout linked to the page's lifetime.</summary>
    /// <returns>The token source; dispose it.</returns>
    protected CancellationTokenSource CreateCallTimeout()
    {
        var source = CancellationTokenSource.CreateLinkedTokenSource(DisposalToken);
        source.CancelAfter(CallTimeout);
        return source;
    }

    /// <summary>Raises the commands' CanExecuteChanged. Call on the UI thread.</summary>
    protected virtual void RaiseCommands()
    {
        ReloadCommand.RaiseCanExecuteChanged();
        SaveCommand.RaiseCanExecuteChanged();
    }

    /// <summary>Marks the page busy or idle (for subclass operations such as Test connection). Call on the UI thread.</summary>
    /// <param name="busy">Whether the page is busy.</param>
    protected void SetBusy(bool busy) => IsBusy = busy;

    private static bool IsUsable(HostState state) => state is HostState.Ready or HostState.Degraded;

    private static string NotReadyMessage(HostState state) => state switch
    {
        HostState.Starting => HostNotReadyText + ": Pillion Host is starting. This page loads when it is ready.",
        HostState.Restarting => HostNotReadyText + ": Pillion Host is restarting. This page loads when it is ready.",
        HostState.Failed => HostNotReadyText + ": Pillion Host failed. Use Tools > Pillion: Retry; this page loads when the host is ready.",
        _ => HostNotReadyText + ".",
    };

    /// <summary>Raised on a background thread: loads the page once the host becomes ready while it shows "Host not ready".</summary>
    private void OnHostStateChanged(object? sender, HostStateChangedEventArgs e)
    {
        if (IsUsable(e.State) && !IsUsable(e.PreviousState))
        {
            Run(async () =>
            {
                await JoinableTaskFactory.SwitchToMainThreadAsync(DisposalToken);
                if (LoadState is OptionsLoadState.HostNotReady or OptionsLoadState.Failed)
                {
                    await LoadAsync().ConfigureAwait(true);
                }
            });
        }
        else if (!IsUsable(e.State))
        {
            Run(async () =>
            {
                await JoinableTaskFactory.SwitchToMainThreadAsync(DisposalToken);
                if (LoadState != OptionsLoadState.Loaded)
                {
                    LoadState = OptionsLoadState.HostNotReady;
                    StatusMessage = NotReadyMessage(e.State);
                }
            });
        }
    }
}

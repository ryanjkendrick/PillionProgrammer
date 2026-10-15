using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.Threading;
using Pillion.Contracts;

namespace Pillion.VisualStudio.Core;

/// <summary>What <see cref="IndexWindowViewModel"/> needs from Visual Studio, supplied by the Index tool window.</summary>
public sealed class IndexWindowOptions
{
    /// <summary>Gets or sets the logger, or <see langword="null"/>.</summary>
    public PillionLog? Log { get; set; }

    /// <summary>Gets or sets the feature flags: the window shows "disabled by settings or policy" while <c>indexWindow</c> is off. <see langword="null"/> means always available.</summary>
    public ChatAvailability? Availability { get; set; }

    /// <summary>Gets or sets what puts text on the clipboard (Copy path); Copy path is unavailable without it.</summary>
    public Action<string>? CopyText { get; set; }

    /// <summary>Gets or sets what asks the developer to confirm a delete (called on the UI thread with the question); without it nothing is deleted.</summary>
    public Func<string, bool>? Confirm { get; set; }

    /// <summary>Gets or sets what opens a root's <c>.pillionignore</c> for editing (called on the UI thread with the root); the command is unavailable without it.</summary>
    public Action<string>? OpenPillionIgnore { get; set; }

    /// <summary>Gets or sets how many excluded files one page holds. The default is 200, the host's default.</summary>
    public int ExcludedPageSize { get; set; } = 200;

    /// <summary>Gets or sets how long status notifications are gathered before they are applied on the UI thread, so a burst costs one visit.</summary>
    public TimeSpan ApplyDelay { get; set; } = TimeSpan.FromMilliseconds(100);
}

/// <summary>
/// The Pillion Index window (work item 2.15; design sections 5, 7, 10 and 13): the state of the workspace index with its
/// progress, one row per repository root with Rebuild and Delete, Pause and Resume, the files the index excludes
/// (<see cref="Excluded"/>) and every index folder on the machine (<see cref="Folders"/>).
/// </summary>
/// <remarks>
/// Updates arrive as the host's <c>indexStatusChanged</c> notification (<see cref="IHostConnection.IndexStatusChanged"/>, at
/// most twice a second): the handler only stores the latest status and schedules one visit to the UI thread, which applies it
/// in place. There is no polling: the window reads the status, the index folders and the first root's exclusions once when it
/// opens (and again when the host becomes ready), and again after a user action. Every RPC call runs on the thread pool.
/// Public members are used on the UI thread.
/// </remarks>
public sealed class IndexWindowViewModel : ObservableObject, IDisposable
{
    private readonly IHostConnection _connection;
    private readonly JoinableTaskFactory _joinableTaskFactory;
    private readonly IndexWindowOptions _options;
    private readonly PillionLog? _log;
    private readonly CancellationTokenSource _disposal = new();
    private readonly IndexServiceCaller _caller;
    private IndexStatus? _status;
    private IndexStatus? _pending;
    private int _applyScheduled;
    private int _notifications;
    private bool _started;
    private bool _disposed;
    private bool _isBusy;
    private bool _isHostReady;
    private bool _isWindowEnabled = true;
    private string _actionMessage = string.Empty;
    private string _loadMessage = string.Empty;
    private string? _rootsSignature;
    private bool _wasActive;

    /// <summary>Initializes a new instance of the <see cref="IndexWindowViewModel"/> class. Call <see cref="Start"/> next.</summary>
    /// <param name="connection">The connection to Pillion Host.</param>
    /// <param name="joinableTaskFactory">The package's joinable task factory.</param>
    /// <param name="options">What the window supplies, or <see langword="null"/> for none.</param>
    public IndexWindowViewModel(IHostConnection connection, JoinableTaskFactory joinableTaskFactory, IndexWindowOptions? options = null)
    {
        _connection = connection ?? throw new ArgumentNullException(nameof(connection));
        _joinableTaskFactory = joinableTaskFactory ?? throw new ArgumentNullException(nameof(joinableTaskFactory));
        _options = options ?? new IndexWindowOptions();
        _log = _options.Log;
        _caller = new IndexServiceCaller(connection, joinableTaskFactory, _log, _disposal.Token);
        _isHostReady = _caller.IsHostReady;
        _isWindowEnabled = _options.Availability?.IsIndexWindowEnabled ?? true;
        Excluded = new ExcludedFilesViewModel(this, _caller, _options.ExcludedPageSize);
        Folders = new IndexFoldersViewModel(this, _caller);
        PauseCommand = new DelegateCommand(() => RunUserAction(PauseAsync), () => CanPause);
        ResumeCommand = new DelegateCommand(() => RunUserAction(ResumeAsync), () => CanResume);
        RefreshCommand = new DelegateCommand(() => RunUserAction(RefreshAllAsync), () => CanAct);
    }

    /// <summary>Gets the repository roots of the open solution or folder, one row each.</summary>
    public ObservableCollection<IndexRootRowViewModel> Roots { get; } = [];

    /// <summary>Gets the Excluded files tab.</summary>
    public ExcludedFilesViewModel Excluded { get; }

    /// <summary>Gets the Indexes tab.</summary>
    public IndexFoldersViewModel Folders { get; }

    /// <summary>Gets the Pause command.</summary>
    public DelegateCommand PauseCommand { get; }

    /// <summary>Gets the Resume command.</summary>
    public DelegateCommand ResumeCommand { get; }

    /// <summary>Gets the command that reads the status, the index folders and the exclusions again.</summary>
    public DelegateCommand RefreshCommand { get; }

    /// <summary>Gets the latest status the host reported, or <see langword="null"/> before the first.</summary>
    public IndexStatus? Status => _status;

    /// <summary>Gets a value indicating whether a status has been received.</summary>
    public bool HasStatus => _status is not null;

    /// <summary>Gets the step the indexer is in, as text ("Embedding chunks", "Idle (up to date)", "Paused").</summary>
    public string PhaseText => _status is null ? IndexStrings.NoStatusYet : IndexFormat.Phase(_status.Phase);

    /// <summary>Gets the whole status as one sentence, which is also what a screen reader announces for the header.</summary>
    public string SummaryText => IndexFormat.Summary(_status);

    /// <summary>Gets a value indicating whether the indexer is working.</summary>
    public bool IsIndexing => _status is not null && IndexFormat.IsActive(_status);

    /// <summary>Gets a value indicating whether indexing is paused, for any reason.</summary>
    public bool IsPaused => _status is not null && IndexFormat.IsPaused(_status);

    /// <summary>Gets why indexing is paused ("Paused: a build is running"), or empty when it is not.</summary>
    public string PauseReasonText
    {
        get
        {
            if (!IsPaused)
            {
                return string.Empty;
            }

            var reason = IndexFormat.PauseReason(_status!.PauseReason);
            return reason.Length == 0 ? IndexStrings.PausedLine : IndexStrings.Format(IndexStrings.PausedLineFormat, reason);
        }
    }

    /// <summary>Gets a value indicating whether <see cref="PauseReasonText"/> has text.</summary>
    public bool HasPauseReason => IsPaused;

    /// <summary>Gets a value indicating whether the progress bar is shown: while a run is in progress.</summary>
    public bool IsProgressVisible => _status?.Progress is not null && (IsIndexing || IsPaused);

    /// <summary>Gets how far the run has got, from 0 to 100, for the progress bar.</summary>
    public double ProgressPercent => (_status is null ? null : IndexFormat.Fraction(_status)) is { } fraction ? fraction * 100.0 : 0.0;

    /// <summary>Gets a value indicating whether the progress bar has no measure yet (nothing to index so far).</summary>
    public bool IsProgressIndeterminate => IsProgressVisible && (_status is null || IndexFormat.Fraction(_status) is null);

    /// <summary>Gets the progress as text: files done of total and chunks stored, for example "30 of 40 files, 160 chunks".</summary>
    public string ProgressText
    {
        get
        {
            if (_status?.Progress is not { } progress)
            {
                return string.Empty;
            }

            return IndexStrings.Format(
                IndexStrings.ProgressFormat,
                IndexFormat.Count(progress.FilesDone),
                IndexFormat.Count(progress.FilesTotal),
                IndexFormat.Count(progress.ChunksDone));
        }
    }

    /// <summary>Gets the estimated time left ("About 2 min remaining"), or empty when there is no estimate.</summary>
    public string RemainingText => _status?.Progress is { } progress ? IndexFormat.Remaining(progress.EstimatedRemainingSeconds) : string.Empty;

    /// <summary>Gets the last error the indexer met, or empty.</summary>
    public string LastError => _status?.LastError ?? string.Empty;

    /// <summary>Gets a value indicating whether the indexer reported an error.</summary>
    public bool HasLastError => !string.IsNullOrWhiteSpace(_status?.LastError);

    /// <summary>Gets what the index holds in total, for example "40 files in 1 root, 320 chunks, 480 KB on disk".</summary>
    public string TotalsText
    {
        get
        {
            if (_status is null || _status.Roots.Length == 0)
            {
                return string.Empty;
            }

            return IndexStrings.Format(
                IndexStrings.TotalsFormat,
                IndexFormat.Count(_status.Roots.Sum(r => r.IndexedFileCount)),
                IndexFormat.Count(_status.Roots.Length),
                IndexFormat.Count(_status.Roots.Sum(r => r.ChunkCount)),
                IndexFormat.Bytes(_status.Roots.Sum(r => r.DiskBytes)),
                IndexFormat.Count(_status.ExcludedSecretFileCount));
        }
    }

    /// <summary>Gets when the index was last updated.</summary>
    public string LastUpdatedText => _status?.LastUpdatedUtc is { } updated ? IndexStrings.Format(IndexStrings.LastUpdatedFormat, IndexFormat.Moment(updated)) : string.Empty;

    /// <summary>Gets a value indicating whether there is a root to show.</summary>
    public bool HasRoots => Roots.Count > 0;

    /// <summary>Gets the line shown instead of the root rows while the host reports none.</summary>
    public string NoRootsText => HasStatus && Roots.Count == 0 ? IndexStrings.NoRoots : string.Empty;

    /// <summary>Gets a value indicating whether the window is available: <see langword="false"/> while the <c>indexWindow</c> flag is off.</summary>
    public bool IsWindowEnabled
    {
        get => _isWindowEnabled;
        private set
        {
            if (SetProperty(ref _isWindowEnabled, value))
            {
                OnPropertyChanged(nameof(IsWindowDisabled));
                OnPropertyChanged(nameof(DisabledMessage));
                RaiseCommands();
            }
        }
    }

    /// <summary>Gets a value indicating whether the window shows "disabled by settings or policy" instead of the index.</summary>
    public bool IsWindowDisabled => !_isWindowEnabled;

    /// <summary>Gets the text shown while the window is disabled.</summary>
    public string DisabledMessage => IsWindowDisabled ? IndexStrings.DisabledMessage : string.Empty;

    /// <summary>Gets a value indicating whether Pillion Host is connected.</summary>
    public bool IsHostReady
    {
        get => _isHostReady;
        private set
        {
            if (SetProperty(ref _isHostReady, value))
            {
                OnPropertyChanged(nameof(HostMessage));
                OnPropertyChanged(nameof(HasHostMessage));
                RaiseCommands();
            }
        }
    }

    /// <summary>Gets "Host not ready" while Pillion Host is not connected; the numbers shown are the last known ones.</summary>
    public string HostMessage => _isHostReady ? string.Empty : IndexStrings.HostNotReady;

    /// <summary>Gets a value indicating whether <see cref="HostMessage"/> has text.</summary>
    public bool HasHostMessage => !_isHostReady;

    /// <summary>Gets why the status could not be read, or empty.</summary>
    public string LoadMessage
    {
        get => _loadMessage;
        private set
        {
            if (SetProperty(ref _loadMessage, value ?? string.Empty))
            {
                OnPropertyChanged(nameof(HasLoadMessage));
            }
        }
    }

    /// <summary>Gets a value indicating whether <see cref="LoadMessage"/> has text.</summary>
    public bool HasLoadMessage => _loadMessage.Length > 0;

    /// <summary>Gets the result of the last action (a started rebuild, a delete, a refusal), or empty.</summary>
    public string ActionMessage
    {
        get => _actionMessage;
        private set
        {
            if (SetProperty(ref _actionMessage, value ?? string.Empty))
            {
                OnPropertyChanged(nameof(HasActionMessage));
            }
        }
    }

    /// <summary>Gets a value indicating whether <see cref="ActionMessage"/> has text.</summary>
    public bool HasActionMessage => _actionMessage.Length > 0;

    /// <summary>Gets a value indicating whether an action (pause, resume, rebuild, delete) is running.</summary>
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

    internal bool CanAct => _isHostReady && _isWindowEnabled && !_isBusy && !_disposed;

    internal bool CanCopy => _options.CopyText is not null;

    internal bool CanOpenPillionIgnore => _options.OpenPillionIgnore is not null;

    private bool CanPause => CanAct && _status is not null && !(IsPaused && _status.PauseReason == IndexPauseReason.User);

    private bool CanResume => CanAct && IsPaused;

    /// <summary>Starts following the host: subscribes to its notifications and does the one refresh a window makes when it opens. Call on the UI thread.</summary>
    public void Start()
    {
        if (_started || _disposed)
        {
            return;
        }

        _started = true;
        _connection.IndexStatusChanged += OnStatusNotification;
        _connection.StateChanged += OnHostStateChanged;
        if (_options.Availability is { } availability)
        {
            availability.Changed += OnAvailabilityChanged;
        }

        RunBackground(RefreshAllAsync, "open");
    }

    /// <summary>Reads the status, the index folders and the first page of the chosen root's exclusions. Call on the UI thread.</summary>
    /// <returns>A task that completes when everything is shown (or the reasons it could not be read).</returns>
    public async Task RefreshAllAsync()
    {
        IsHostReady = _caller.IsHostReady;
        if (!_caller.IsHostReady)
        {
            return;
        }

        // Choosing the first root (the status brings the roots) already reads its exclusions; read them again only when the
        // same root was chosen before.
        var chosen = Excluded.SelectedRoot;
        await RefreshStatusAsync().ConfigureAwait(true);
        await Folders.LoadAsync().ConfigureAwait(true);
        if (chosen is not null && Excluded.SelectedRoot is not null)
        {
            await Excluded.LoadAsync().ConfigureAwait(true);
        }
    }

    /// <summary>Reads the status once with <see cref="IContextService.GetIndexStatusAsync"/> (a notification that arrives meanwhile wins).</summary>
    /// <returns>A task that completes when the status is applied. Call on the UI thread.</returns>
    public async Task RefreshStatusAsync()
    {
        if (!_caller.IsHostReady)
        {
            return;
        }

        var generation = Volatile.Read(ref _notifications);
        var result = await _caller.CallAsync("GetIndexStatusAsync", (service, token) => service.GetIndexStatusAsync(token)).ConfigureAwait(false);
        await _joinableTaskFactory.SwitchToMainThreadAsync(_disposal.Token);
        if (!result.Succeeded || result.Value is null)
        {
            LoadMessage = IndexStrings.Format(IndexStrings.StatusFailedFormat, result.Failure?.Message ?? IndexStrings.Unknown);
            return;
        }

        LoadMessage = string.Empty;
        if (generation == Volatile.Read(ref _notifications))
        {
            Apply(result.Value);
        }
    }

    /// <summary>Pauses indexing (by the user). Call on the UI thread.</summary>
    /// <returns>A task that completes when the host has answered.</returns>
    public Task PauseAsync() => SetPausedAsync(pause: true);

    /// <summary>Resumes indexing. Call on the UI thread.</summary>
    /// <returns>A task that completes when the host has answered.</returns>
    public Task ResumeAsync() => SetPausedAsync(pause: false);

    /// <summary>Starts a rebuild of a root's index in the background; its progress arrives as status notifications. Call on the UI thread.</summary>
    /// <param name="row">The root.</param>
    /// <returns>A task that completes when the host has started the rebuild (or refused).</returns>
    public async Task RebuildAsync(IndexRootRowViewModel row)
    {
        if (row is null)
        {
            throw new ArgumentNullException(nameof(row));
        }

        var path = row.RootPath;
        var result = await _caller.CallAsync("RebuildIndexAsync", async (service, token) =>
        {
            await service.RebuildIndexAsync(path, token).ConfigureAwait(false);
            return true;
        }).ConfigureAwait(false);
        await _joinableTaskFactory.SwitchToMainThreadAsync(_disposal.Token);
        ActionMessage = result.Succeeded
            ? IndexStrings.Format(IndexStrings.RebuildStartedFormat, path)
            : IndexStrings.Format(IndexStrings.RebuildFailedFormat, path, result.Failure!.Message);
        await RefreshStatusAsync().ConfigureAwait(true);
    }

    /// <summary>Asks to confirm, then deletes the index of a root. Call on the UI thread.</summary>
    /// <param name="row">The root.</param>
    /// <returns>A task that completes when the delete (or the refusal, or the developer's No) is reported.</returns>
    public async Task DeleteAsync(IndexRootRowViewModel row)
    {
        if (row is null)
        {
            throw new ArgumentNullException(nameof(row));
        }

        if (string.IsNullOrEmpty(row.IndexId))
        {
            ActionMessage = IndexStrings.Format(IndexStrings.NothingToDeleteFormat, row.RootPath);
            return;
        }

        if (!Confirm(IndexStrings.Format(IndexStrings.DeleteRootConfirmFormat, row.RootPath, row.DiskText)))
        {
            return;
        }

        await DeleteIndexFolderAsync(row.IndexId, row.RootPath).ConfigureAwait(true);
        await RefreshStatusAsync().ConfigureAwait(true);
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        if (_started)
        {
            _connection.IndexStatusChanged -= OnStatusNotification;
            _connection.StateChanged -= OnHostStateChanged;
            if (_options.Availability is { } availability)
            {
                availability.Changed -= OnAvailabilityChanged;
            }
        }

        _disposal.Cancel(); // Not disposed: work still on its way reads the token, and a disposed source throws from Token.
    }

    internal bool CanRebuild(IndexRootRowViewModel row) => CanAct && !IsIndexing && !string.IsNullOrEmpty(row.RootPath);

    internal bool CanDelete(IndexRootRowViewModel row) => CanAct && !IsIndexing && !string.IsNullOrEmpty(row.IndexId);

    internal bool Confirm(string question) => _options.Confirm?.Invoke(question) ?? false;

    internal void Copy(string text) => _options.CopyText?.Invoke(text);

    internal void OpenPillionIgnore(string rootPath)
    {
        if (_options.OpenPillionIgnore is not { } open)
        {
            return;
        }

        try
        {
            open(rootPath);
        }
        catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            _log?.Warning("index.window.pillionignore.failed", "Opening .pillionignore failed.", ("error", ex.GetType().Name));
            ActionMessage = IndexStrings.Format(IndexStrings.PillionIgnoreFailedFormat, rootPath, ex.Message);
        }
    }

    /// <summary>Deletes an index folder by id and reports the result in <see cref="ActionMessage"/>.</summary>
    internal async Task DeleteIndexFolderAsync(string indexId, string display)
    {
        var result = await _caller.CallAsync("DeleteIndexAsync", (service, token) => service.DeleteIndexAsync(indexId, token)).ConfigureAwait(false);
        await _joinableTaskFactory.SwitchToMainThreadAsync(_disposal.Token);
        if (!result.Succeeded)
        {
            ActionMessage = IndexStrings.Format(IndexStrings.DeleteFailedFormat, display, result.Failure!.Message);
        }
        else if (result.Value)
        {
            ActionMessage = IndexStrings.Format(IndexStrings.DeletedFormat, display);
        }
        else
        {
            ActionMessage = IndexStrings.Format(IndexStrings.NothingToDeleteFormat, display);
        }
    }

    /// <summary>Runs an action the developer started: one at a time, with the buttons disabled meanwhile, and failures shown.</summary>
    internal void RunUserAction(Func<Task> action)
    {
        if (_isBusy || _disposed)
        {
            return;
        }

        IsBusy = true;
        _ = _joinableTaskFactory.RunAsync(async () =>
        {
            try
            {
                await action().ConfigureAwait(true);
            }
            catch (OperationCanceledException) when (_disposal.IsCancellationRequested)
            {
                return;
            }
#pragma warning disable CA1031 // A failure of a button must not take Visual Studio down; it is shown and logged.
            catch (Exception ex)
#pragma warning restore CA1031
            {
                _log?.Error("index.window.action.failed", "An Index window action failed.", ("error", ex.GetType().Name));
                await _joinableTaskFactory.SwitchToMainThreadAsync(CancellationToken.None);
                ActionMessage = RpcFailure.Describe(ex).Message;
            }
            finally
            {
                if (!_disposed)
                {
                    await _joinableTaskFactory.SwitchToMainThreadAsync(CancellationToken.None);
                    IsBusy = false;
                }
            }
        });
    }

    /// <summary>Runs work the window started itself (a page load, the refresh on open); failures are logged, never thrown.</summary>
    internal void RunBackground(Func<Task> work, string name)
    {
        if (_disposed)
        {
            return;
        }

        _ = _joinableTaskFactory.RunAsync(async () =>
        {
            try
            {
                await work().ConfigureAwait(true);
            }
            catch (OperationCanceledException) when (_disposal.IsCancellationRequested)
            {
                // The window is closing.
            }
#pragma warning disable CA1031 // Background work must not take Visual Studio down; it is logged.
            catch (Exception ex)
#pragma warning restore CA1031
            {
                _log?.Error("index.window.background.failed", "Index window work failed.", ("work", name), ("error", ex.GetType().Name));
            }
        });
    }

    private async Task SetPausedAsync(bool pause)
    {
        var result = await _caller.CallAsync("PauseIndexingAsync", async (service, token) =>
        {
            await service.PauseIndexingAsync(pause, token).ConfigureAwait(false);
            return true;
        }).ConfigureAwait(false);
        await _joinableTaskFactory.SwitchToMainThreadAsync(_disposal.Token);
        ActionMessage = result.Succeeded
            ? string.Empty
            : IndexStrings.Format(pause ? IndexStrings.PauseFailedFormat : IndexStrings.ResumeFailedFormat, result.Failure!.Message);
        if (result.Succeeded)
        {
            await RefreshStatusAsync().ConfigureAwait(true);
        }
    }

    /// <summary>Raised on a background thread: keeps the latest status and schedules one visit to the UI thread.</summary>
    private void OnStatusNotification(object? sender, HostIndexStatusChangedEventArgs e)
    {
        if (_disposed)
        {
            return;
        }

        Interlocked.Increment(ref _notifications);
        Volatile.Write(ref _pending, e.Status);
        if (Interlocked.Exchange(ref _applyScheduled, 1) != 0)
        {
            return;
        }

        _ = _joinableTaskFactory.RunAsync(ApplyPendingAsync);
    }

    private async Task ApplyPendingAsync()
    {
        try
        {
            await TaskScheduler.Default;
            if (_options.ApplyDelay > TimeSpan.Zero)
            {
                await Task.Delay(_options.ApplyDelay, _disposal.Token).ConfigureAwait(false);
            }

            await _joinableTaskFactory.SwitchToMainThreadAsync(_disposal.Token);
            Interlocked.Exchange(ref _applyScheduled, 0);
            if (Interlocked.Exchange(ref _pending, null) is { } status)
            {
                Apply(status);
            }
        }
        catch (OperationCanceledException)
        {
            // The window is closing.
        }
        catch (ObjectDisposedException)
        {
            // The window closed while a notification was waiting.
        }
    }

    /// <summary>Raised on a background thread when the host connects, restarts or fails.</summary>
    private void OnHostStateChanged(object? sender, HostStateChangedEventArgs e)
    {
        var ready = e.State is HostState.Ready or HostState.Degraded;
        var becameReady = ready && e.PreviousState is not (HostState.Ready or HostState.Degraded);
        _ = _joinableTaskFactory.RunAsync(async () =>
        {
            try
            {
                await _joinableTaskFactory.SwitchToMainThreadAsync(_disposal.Token);
                IsHostReady = ready;
                if (becameReady)
                {
                    await RefreshAllAsync().ConfigureAwait(true);
                }
            }
            catch (OperationCanceledException)
            {
                // The window is closing.
            }
#pragma warning disable CA1031 // Background work must not take Visual Studio down; it is logged.
            catch (Exception ex)
#pragma warning restore CA1031
            {
                _log?.Error("index.window.state.failed", "Index window could not follow the host state.", ("error", ex.GetType().Name));
            }
        });
    }

    private void OnAvailabilityChanged(object? sender, EventArgs e)
    {
        var enabled = _options.Availability?.IsIndexWindowEnabled ?? true;
        _ = _joinableTaskFactory.RunAsync(async () =>
        {
            try
            {
                await _joinableTaskFactory.SwitchToMainThreadAsync(_disposal.Token);
                IsWindowEnabled = enabled;
            }
            catch (OperationCanceledException)
            {
                // The window is closing.
            }
        });
    }

    /// <summary>Shows a status. Runs on the UI thread, once per batch of notifications.</summary>
    private void Apply(IndexStatus status)
    {
        using var timing = UiThreadTimings.Measure("index.window.apply");
        _status = status;
        status.Roots ??= [];
        ReconcileRoots(status.Roots);

        OnPropertyChanged(nameof(Status));
        OnPropertyChanged(nameof(HasStatus));
        OnPropertyChanged(nameof(PhaseText));
        OnPropertyChanged(nameof(SummaryText));
        OnPropertyChanged(nameof(IsIndexing));
        OnPropertyChanged(nameof(IsPaused));
        OnPropertyChanged(nameof(PauseReasonText));
        OnPropertyChanged(nameof(HasPauseReason));
        OnPropertyChanged(nameof(IsProgressVisible));
        OnPropertyChanged(nameof(ProgressPercent));
        OnPropertyChanged(nameof(IsProgressIndeterminate));
        OnPropertyChanged(nameof(ProgressText));
        OnPropertyChanged(nameof(RemainingText));
        OnPropertyChanged(nameof(LastError));
        OnPropertyChanged(nameof(HasLastError));
        OnPropertyChanged(nameof(TotalsText));
        OnPropertyChanged(nameof(LastUpdatedText));
        OnPropertyChanged(nameof(HasRoots));
        OnPropertyChanged(nameof(NoRootsText));
        RaiseCommands();
        Excluded.OnRootsChanged();

        // The index folders change when a root gets an index (a rebuild finished, one was opened or deleted).
        var signature = string.Join("|", status.Roots.Select(r => r.IndexId));
        var active = IndexFormat.IsActive(status);
        var finished = _wasActive && !active;
        _wasActive = active;
        if (_started && (_rootsSignature is null || !string.Equals(signature, _rootsSignature, StringComparison.Ordinal) || finished))
        {
            // The first status needs no extra read: opening the window reads the index folders itself.
            var first = _rootsSignature is null;
            _rootsSignature = signature;
            if (!first && _caller.IsHostReady)
            {
                RunBackground(Folders.LoadAsync, "indexes");
            }
        }
    }

    private void ReconcileRoots(IndexRootStatus[] roots)
    {
        var samePaths = roots.Length == Roots.Count;
        for (var i = 0; samePaths && i < roots.Length; i++)
        {
            samePaths = string.Equals(roots[i].RootPath, Roots[i].RootPath, StringComparison.OrdinalIgnoreCase);
        }

        if (samePaths)
        {
            for (var i = 0; i < roots.Length; i++)
            {
                Roots[i].Update(roots[i]);
            }

            return;
        }

        var existing = Roots.ToDictionary(r => r.RootPath, StringComparer.OrdinalIgnoreCase);
        Roots.Clear();
        foreach (var root in roots)
        {
            if (existing.TryGetValue(root.RootPath, out var row))
            {
                row.Update(root);
            }
            else
            {
                row = new IndexRootRowViewModel(this, root);
            }

            Roots.Add(row);
        }
    }

    private void RaiseCommands()
    {
        PauseCommand.RaiseCanExecuteChanged();
        ResumeCommand.RaiseCanExecuteChanged();
        RefreshCommand.RaiseCanExecuteChanged();
        foreach (var row in Roots)
        {
            row.RaiseCommands();
        }

        Excluded.RaiseCommands();
        Folders.RaiseCommands();
    }
}

using System;
using System.ComponentModel.Design;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using Microsoft.VisualStudio.Threading;
using Pillion.Contracts;
using Pillion.VisualStudio.Autocomplete;
using Pillion.VisualStudio.Core;
using Pillion.VisualStudio.Core.Autocomplete;
using Pillion.VisualStudio.Core.Signals;
using Pillion.VisualStudio.Core.WorkspaceQueries;
using Pillion.VisualStudio.InlineChat;
using Pillion.VisualStudio.Options;
using Pillion.VisualStudio.Signals;
using Pillion.VisualStudio.Statusbar;
using Pillion.VisualStudio.ToolWindows;
using Pillion.VisualStudio.WorkspaceQueries;
using Task = System.Threading.Tasks.Task;

namespace Pillion.VisualStudio;

/// <summary>
/// The Pillion Programmer package. Loads in the background when a solution or folder is opened, starts and supervises
/// Pillion Host (one per devenv.exe), shows its state in the status bar, registers <see cref="IHostConnection"/> as a
/// service, and adds the Tools menu commands "Pillion: Open Chat", "Pillion: Show Status", "Pillion: Retry" and
/// "Pillion: Open Logs". Provides the Pillion Chat tool window (also under View &gt; Other Windows), the Pillion Index tool
/// window and the status bar's indexing indicator (behind the <c>indexWindow</c> flag), and the
/// Tools &gt; Options &gt; Pillion Programmer pages "Providers and keys", "Privacy", "Models and routing", "Autocomplete" and "Indexing".
/// </summary>
[PackageRegistration(UseManagedResourcesOnly = true, AllowsBackgroundLoading = true)]
[ProvideMenuResource("Menus.ctmenu", 1)]
[ProvideAutoLoad(VSConstants.UICONTEXT.SolutionExists_string, PackageAutoLoadFlags.BackgroundLoad)]
[ProvideAutoLoad(VSConstants.UICONTEXT.FolderOpened_string, PackageAutoLoadFlags.BackgroundLoad)]
[ProvideService(typeof(IHostConnection), IsAsyncQueryable = true)]
[ProvideService(typeof(IIdeSignals), IsAsyncQueryable = true)]
[ProvideToolWindow(typeof(ChatToolWindow), Style = VsDockStyle.Tabbed, Window = SolutionExplorerWindow, Orientation = ToolWindowOrientation.Right)]
[ProvideToolWindow(typeof(IndexToolWindow), Style = VsDockStyle.Tabbed, Window = SolutionExplorerWindow, Orientation = ToolWindowOrientation.Right)]
[ProvideOptionPage(typeof(ProvidersOptionPage), ProductName, "Providers and keys", 0, 0, supportsAutomation: false)]
[ProvideOptionPage(typeof(PrivacyOptionPage), ProductName, "Privacy", 0, 0, supportsAutomation: false)]
[ProvideOptionPage(typeof(RoutingOptionPage), ProductName, "Models and routing", 0, 0, supportsAutomation: false)]
[ProvideOptionPage(typeof(AutocompleteOptionPage), ProductName, "Autocomplete", 0, 0, supportsAutomation: false)]
[ProvideOptionPage(typeof(IndexingOptionPage), ProductName, "Indexing", 0, 0, supportsAutomation: false)]
[Guid(PackageGuids.PackageString)]
public sealed class PillionPackage : AsyncPackage
{
    private const string ProductName = "Pillion Programmer";

    /// <summary>The Solution Explorer tool window GUID (EnvDTE.Constants.vsWindowKindSolutionExplorer): Pillion Chat docks beside it.</summary>
    private const string SolutionExplorerWindow = "3ae79031-e1bc-11d0-8f78-00a0c9110057";

    /// <summary>How long the host gets to exit cleanly when Visual Studio closes.</summary>
    private static readonly TimeSpan HostShutdownTimeout = TimeSpan.FromSeconds(2);

    /// <summary>The longest the Output pane and the Error List wait for the host before the package's start-up counts as over.</summary>
    private static readonly TimeSpan StartupSettleTimeout = TimeSpan.FromSeconds(10);

    /// <summary>How long after the host settles the Output pane and the Error List are created.</summary>
    private static readonly TimeSpan StartupSettleGrace = TimeSpan.FromMilliseconds(250);

    private PillionLog? _log;
    private HostSupervisor? _supervisor;
    private ChatAvailability? _chatAvailability;
    private IndexStatusIndicator? _indexIndicator;
    private IndexStatusBarItem? _indexStatusItem;
    private OutputPaneWriter? _output;
    private IVsStatusbar? _statusBar;
    private int _statusBarRefreshQueued;
    private AutocompleteFeature? _autocomplete;
    private InlineChatFeature? _inlineChat;
    private IdeSignalsFeature? _signals;
    private IndexingRelayFeature? _indexing;
    private HostFeatureFlag? _workspaceQueriesFlag;
    private RoslynWorkspaceQueryEngine? _workspaceQueryEngine;
    private WorkspaceQueryService? _workspaceQueries;
    private IntelliCodeStatus _intelliCodeReported = IntelliCodeStatus.NotRequired;
    private int _intelliCodeCheckRunning;

    /// <summary>Gets the extension's version (the assembly informational version).</summary>
    internal static string ExtensionVersion { get; } = ReadVersion();

    /// <summary>Gets the loaded package, once <see cref="InitializeAsync"/> has created the supervisor (for the Options pages).</summary>
    internal static PillionPackage? Instance { get; private set; }

    /// <summary>
    /// Gets the events shared by the Chat window and the Privacy options page (Delete all conversations tells the open window to
    /// forget the conversations it listed).
    /// </summary>
    internal ConversationEvents ConversationEvents { get; } = new();

    /// <summary>Gets the connection to Pillion Host, once initialization has created it.</summary>
    internal IHostConnection? HostConnection => _supervisor;

    /// <summary>Gets the host's effective feature flags (chat, mentions, Outbound log view, Index window), once initialization has created them.</summary>
    internal ChatAvailability? Availability => _chatAvailability;

    /// <summary>Gets the extension log, once initialization has created it.</summary>
    internal PillionLog? Log => _log;

    /// <summary>Gets the IDE signals (builds, documents, diagnostics, Output panes) service, once initialization has created it.</summary>
    internal IIdeSignals? IdeSignals => _signals?.Hub;

    /// <summary>Gets ghost-text autocomplete (engine, settings, IntelliCode state), once initialization has created it.</summary>
    internal AutocompleteFeature? Autocomplete => _autocomplete;

    /// <summary>Gets inline chat (feature flag, recent prompts, the command), once initialization has created it.</summary>
    internal InlineChatFeature? InlineChat => _inlineChat;

    /// <summary>Gets the workspace query service the host calls (work item 2.14), once initialization has created it.</summary>
    internal WorkspaceQueryService? WorkspaceQueries => _workspaceQueries;

    /// <inheritdoc/>
    /// <remarks>
    /// Start-up cost (design section 13: 0 ms blocking, under 50 ms of UI-thread CPU). The first statement moves the work
    /// off the caller's thread: a background load already runs on a pool thread, but a synchronous load (a service or
    /// command that reaches the package first) enters on the UI thread, which would otherwise build the whole object graph
    /// and JIT it there. What the UI thread must do is one short visit (command registration and the first status bar
    /// text); the features start on the thread pool afterwards, so no JoinableTask is created, and no file is touched, on
    /// the UI thread. <see cref="UiThreadTimings"/> (a debug switch) times every stretch, and the Gate 1 test reads them.
    /// </remarks>
    protected override async Task InitializeAsync(CancellationToken cancellationToken, IProgress<ServiceProgressData> progress)
    {
        var preAwait = UiThreadTimings.Measure("package.init.preawait");
        await TaskScheduler.Default;
        preAwait.Dispose();

        var background = UiThreadTimings.Measure("package.init.background");
        await base.InitializeAsync(cancellationToken, progress).ConfigureAwait(true);

        // Background thread: nothing here touches the UI or waits for the host.
        var log = new PillionLog(PillionLog.DefaultDirectory, writeInBackground: true);
        _log = log;
        var supervisor = CreateSupervisor(log);
        _supervisor = supervisor;
        var workspaceQueriesFlag = new HostFeatureFlag(supervisor, WorkspaceQueryService.Flag, log);
        _workspaceQueriesFlag = workspaceQueriesFlag;
        var chatAvailability = new ChatAvailability(supervisor, log);
        _chatAvailability = chatAvailability;
        var indexIndicator = new IndexStatusIndicator(supervisor, chatAvailability, log);
        _indexIndicator = indexIndicator;
        _indexStatusItem = new IndexStatusBarItem(JoinableTaskFactory, OpenIndexFromStatusBar, QueueStatusBarRefresh, log);
        var autocomplete = new AutocompleteFeature(supervisor, log);
        _autocomplete = autocomplete;
        var inlineChat = new InlineChatFeature(this, supervisor, log, output: null);
        _inlineChat = inlineChat;
        AddService(typeof(IHostConnection), (container, token, type) => Task.FromResult<object?>(supervisor), promote: true);
        var signals = new IdeSignalsFeature(JoinableTaskFactory, log);
        _signals = signals;
        AddService(typeof(IIdeSignals), (container, token, type) => Task.FromResult<object?>(signals.Hub), promote: true);
        var indexing = new IndexingRelayFeature(JoinableTaskFactory, supervisor, signals.Hub, log); // Work item 2.9: what to index, when to pause.
        _indexing = indexing;
        Instance = this;

        // The Output pane is created on its first write (on the UI thread, by the writer), not here. Until then lines wait
        // in the writer's queue, so the sink can be connected now.
        Func<Task> startupOver = () => StartupSettle.WhenSettledAsync(supervisor, StartupSettleTimeout, StartupSettleGrace, DisposalToken);
        var output = new OutputPaneWriter(CreateOutputPane, startupOver, JoinableTaskFactory, DisposalToken);
        _output = output;
        log.Sink = line => output.WriteLine(line);
        inlineChat.Output = line => output.WriteLine(line);
        output.WriteLine($"{ProductName} {ExtensionVersion} loaded (contract {ContractVersion.Text}).");
        background.Dispose();

        // The COM status bar and command service must be used on the UI thread: one visit.
        await JoinableTaskFactory.SwitchToMainThreadAsync(cancellationToken);

        // One visit (package.init.main) made of three parts (a name with "/" is a part of the visit before it).
        var visit = UiThreadTimings.Measure("package.init.main");
        var services = UiThreadTimings.Measure("package.init.main/services");
        var commandService = await GetServiceAsync(typeof(IMenuCommandService)).ConfigureAwait(true) as OleMenuCommandService;
        _statusBar = await GetServiceAsync(typeof(SVsStatusbar)).ConfigureAwait(true) as IVsStatusbar;
        services.Dispose();

        using (UiThreadTimings.Measure("package.init.main/commands"))
        {
            if (commandService is not null)
            {
                commandService.AddCommand(new MenuCommand(OnShowStatus, new CommandID(PackageGuids.CommandSet, PackageIds.ShowStatusCommand)));
                commandService.AddCommand(new MenuCommand(OnRetryHost, new CommandID(PackageGuids.CommandSet, PackageIds.RetryHostCommand)));
                commandService.AddCommand(new MenuCommand(OnOpenLogs, new CommandID(PackageGuids.CommandSet, PackageIds.OpenLogsCommand)));
                commandService.AddCommand(CreateOpenChatCommand(PackageIds.OpenChatCommand));
                commandService.AddCommand(CreateOpenChatCommand(PackageIds.ViewChatCommand));
                commandService.AddCommand(CreateOpenIndexCommand(PackageIds.OpenIndexCommand));
                commandService.AddCommand(CreateOpenIndexCommand(PackageIds.ViewIndexCommand));
                commandService.AddCommand(inlineChat.CreateCommand());
            }
        }

        using (UiThreadTimings.Measure("package.init.main/statusbar"))
        {
            SetStatusBarText(StatusBarText(supervisor));
        }

        visit.Dispose();

        // Back to the thread pool for the rest: subscribing and starting create JoinableTasks, and the IntelliCode check
        // reads a process's version, none of which the UI thread should pay for. Everything the handlers use (the log
        // sink, the Output writer, the status bar) is in place before the host starts.
        await TaskScheduler.Default;
        var start = UiThreadTimings.Measure("package.init.start.background");
        supervisor.StateChanged += OnHostStateChanged;
        supervisor.ConnectivityChanged += OnConnectivityChanged;
        autocomplete.StatusChanged += (_, _) => QueueStatusBarRefresh();
        indexIndicator.Changed += (_, _) => QueueStatusBarRefresh();

        // Returns at once: launching, connecting and the handshake run on the thread pool. Availability follows the
        // host's effective feature flags: it reads them whenever the host becomes ready.
        chatAvailability.Start();
        indexIndicator.Start();
        autocomplete.Start();
        inlineChat.Start();
        _ = JoinableTaskFactory.RunAsync(() => signals.StartAsync(this, DisposalToken, startupOver));
        _ = JoinableTaskFactory.RunAsync(() => indexing.StartAsync(this, DisposalToken, startupOver));
        workspaceQueriesFlag.Start();
        _ = JoinableTaskFactory.RunAsync(() => WarmUpWorkspaceQueriesAsync(startupOver));
        _ = JoinableTaskFactory.RunAsync(() => CheckIntelliCodeAsync(DisposalToken));
        supervisor.Start();
        start.Dispose();
    }

    /// <inheritdoc/>
    /// <remarks>Only Pillion Chat and Pillion Index are created asynchronously; the base implementation returns <see langword="null"/> too.</remarks>
    public override IVsAsyncToolWindowFactory? GetAsyncToolWindowFactory(Guid toolWindowType) =>
        toolWindowType == typeof(ChatToolWindow).GUID || toolWindowType == typeof(IndexToolWindow).GUID ? this : null;

    /// <inheritdoc/>
    protected override string? GetToolWindowTitle(Type toolWindowType, int id) =>
        toolWindowType == typeof(ChatToolWindow) ? ChatToolWindow.Title
        : toolWindowType == typeof(IndexToolWindow) ? IndexToolWindow.Title
        : base.GetToolWindowTitle(toolWindowType, id);

    /// <inheritdoc/>
    protected override System.Threading.Tasks.Task<object> InitializeToolWindowAsync(Type toolWindowType, int id, CancellationToken cancellationToken)
    {
        if (toolWindowType == typeof(IndexToolWindow))
        {
            // The view model starts on the UI thread, in the window.
            var indexViewModel = new IndexWindowViewModel(
                _supervisor!,
                JoinableTaskFactory,
                new IndexWindowOptions
                {
                    Log = _log,
                    Availability = _chatAvailability,
                    CopyText = ChatToolWindow.CopyToClipboard,
                    Confirm = IndexToolWindow.Confirm,
                    OpenPillionIgnore = IndexToolWindow.OpenPillionIgnore,
                });
            return System.Threading.Tasks.Task.FromResult<object>(new IndexToolWindowState(indexViewModel));
        }

        if (toolWindowType != typeof(ChatToolWindow))
        {
            return base.InitializeToolWindowAsync(toolWindowType, id, cancellationToken);
        }

        // Runs after InitializeAsync, so the supervisor exists. The view model starts on the UI thread, in the window.
        var files = new WorkspaceFileIndex(JoinableTaskFactory, _log); // Mentions (work item 1.9): the solution or folder file list.
        var viewModel = new ChatViewModel(
            _supervisor!,
            JoinableTaskFactory,
            new ChatViewModelOptions
            {
                OpenLogs = OpenLogFolder,
                Log = _log,
                Availability = _chatAvailability,
                WorkspaceFiles = files,
                EditorContext = new EditorChatContext(JoinableTaskFactory, () => _signals?.Hub, _log),
                CopyText = ChatToolWindow.CopyToClipboard,
                ConversationEvents = ConversationEvents,
                OpenCitation = ChatToolWindow.OpenCitation,
                OpenCitationSpan = ChatToolWindow.OpenCitationSpan,
            });
        return System.Threading.Tasks.Task.FromResult<object>(new ChatToolWindowState(viewModel, files));
    }

    /// <inheritdoc/>
    protected override void Dispose(bool disposing)
    {
        if (disposing && ReferenceEquals(Instance, this))
        {
            Instance = null;
        }

        if (disposing)
        {
            _chatAvailability?.Dispose();
            _chatAvailability = null;
            _indexIndicator?.Dispose();
            _indexIndicator = null;
            _indexStatusItem = null;
            _autocomplete?.Dispose();
            _autocomplete = null;
            _inlineChat?.Dispose();
            _inlineChat = null;
            _indexing?.Dispose(); // Before the signals: the relay listens to the hub's build state.
            _indexing = null;
            _signals?.Dispose();
            _signals = null;
            _workspaceQueriesFlag?.Dispose();
            _workspaceQueriesFlag = null;
        }

        if (disposing && _supervisor is { } supervisor)
        {
            _supervisor = null;
            supervisor.StateChanged -= OnHostStateChanged;
            supervisor.ConnectivityChanged -= OnConnectivityChanged;
            try
            {
                // Visual Studio is closing: give the host up to 2 s to exit, then kill its process tree.
                JoinableTaskFactory.Run(() => supervisor.ShutdownAsync(HostShutdownTimeout));
            }
#pragma warning disable CA1031 // Shutdown must not throw out of Dispose; the job object still kills the host.
            catch (Exception ex)
#pragma warning restore CA1031
            {
                _log?.Error("package.dispose.failed", "Shutting down Pillion Host failed.", ("error", ex.GetType().Name), ("detail", ex.Message));
            }

            supervisor.Dispose();
        }

        if (disposing)
        {
            _ = _log?.Flush(TimeSpan.FromSeconds(1)); // The log writes in the background: let the last lines reach the file.
        }

        base.Dispose(disposing);
    }

    private static string ReadVersion()
    {
        var assembly = typeof(PillionPackage).Assembly;
        return assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
            ?? assembly.GetName().Version?.ToString()
            ?? "0.0.0";
    }

    /// <summary>Creates the "Pillion Programmer" Output pane. Called by the writer on the UI thread, when the first line is written.</summary>
    private static IVsOutputWindowPane? CreateOutputPane()
    {
        ThreadHelper.ThrowIfNotOnUIThread();

        if (Package.GetGlobalService(typeof(SVsOutputWindow)) is not IVsOutputWindow outputWindow)
        {
            return null;
        }

        var paneGuid = PackageGuids.OutputPane;
        if (ErrorHandler.Failed(outputWindow.CreatePane(ref paneGuid, ProductName, fInitVisible: 1, fClearWithSolution: 0))
            || ErrorHandler.Failed(outputWindow.GetPane(ref paneGuid, out var pane)))
        {
            return null;
        }

        return pane;
    }

    private HostSupervisor CreateSupervisor(PillionLog log)
    {
        var extensionAssembly = typeof(PillionPackage).Assembly.Location;
        // Workspace queries (work item 2.14): the host's find_references and callers_of, answered from the live Roslyn
        // workspace on the thread pool. The flag object is created right after the supervisor; until then it reads as on.
        var engine = new RoslynWorkspaceQueryEngine(log);
        _workspaceQueryEngine = engine;
        _workspaceQueries = new WorkspaceQueryService(
            engine,
            () => _workspaceQueriesFlag?.IsEnabled ?? true,
            log,
            new WorkspaceQueryOptions { IsUiThread = ThreadHelper.CheckAccess });
        var targets = new HostLocalTargets(new EditorService(this), new NotImplementedApprovalService(), _workspaceQueries);
        var options = new HostLauncherOptions(HostLauncherOptions.DefaultHostPath(extensionAssembly), extensionAssembly, targets)
        {
            LogDirectory = PillionLog.DefaultDirectory,
        };

        return new HostSupervisor(new HostLauncherSelector(options, log), JoinableTaskFactory, log); // The selector lets the IDE tests swap in the fake host.
    }

    /// <summary>
    /// Looks up the Roslyn workspace for the workspace queries once the package's start-up is over, on the thread pool, so
    /// no query pays for the lookup (the component model may visit the UI thread once to create itself).
    /// </summary>
    private async Task WarmUpWorkspaceQueriesAsync(Func<Task> startupOver)
    {
        try
        {
            await startupOver().ConfigureAwait(false);
            await TaskScheduler.Default;
            if (_workspaceQueryEngine is { } engine)
            {
                await engine.WarmUpAsync().ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // Visual Studio is shutting down.
        }
#pragma warning disable CA1031 // The first query looks again; a failed warm-up must not break the package.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            _log?.Warning("workspace.query.warmup.failed", "Looking up the Roslyn workspace failed.", ("error", ex.GetType().Name), ("detail", ex.Message));
        }
    }

    /// <summary>Raised on a background thread; switches to the UI thread only to update the status bar and Output pane.</summary>
    private void OnHostStateChanged(object? sender, HostStateChangedEventArgs e)
    {
        if (e.State == HostState.Failed)
        {
            _output?.WriteLine(
                $"Pillion Host failed: {e.Reason}{Environment.NewLine}"
                + $"Use Tools > Pillion: Retry to start it again, or Tools > Pillion: Open Logs for details ({PillionLog.DefaultDirectory}).");
        }

        QueueStatusBarRefresh();
    }

    /// <summary>Raised on a background thread when the host reports a new connectivity state.</summary>
    private void OnConnectivityChanged(object? sender, HostConnectivityChangedEventArgs e) => QueueStatusBarRefresh();

    private string StatusBarText(HostSupervisor supervisor) =>
        HostStatusText.Format(supervisor.State, supervisor.RestartAttempt, supervisor.MaxRestartAttempts, supervisor.Connectivity)
        + (supervisor.State is HostState.Ready or HostState.Degraded ? _autocomplete?.StatusBarSuffix : null)
        + (supervisor.State is HostState.Ready or HostState.Degraded ? IndexStatusBarSuffix() : null)
        + (_intelliCodeReported is IntelliCodeStatus.Missing or IntelliCodeStatus.CompletionsOff ? ". " + IntelliCodeRequirement.Message : string.Empty);

    /// <summary>
    /// What the status bar text adds for the indexing indicator: nothing when it is idle or when the clickable item is in
    /// the status bar (it shows the indicator itself); otherwise the indicator's words after a comma (the fallback, also
    /// used until the item is attached). The words only: the glyphs are not in Visual Studio's interface font, and text that
    /// needs a fallback font costs the UI thread a composite font lookup (see <see cref="IndexFormat.StatusBarParts"/>).
    /// </summary>
    private string? IndexStatusBarSuffix() =>
        _indexIndicator is { Label.Length: > 0 } indicator && _indexStatusItem is not { IsAttached: true } ? ", " + indicator.Label : null;

    /// <summary>
    /// Posts one status bar refresh to the UI thread, unless one is already waiting there: a host restart raises a state change,
    /// a connectivity change and an indexing indicator change within moments, and they share one short visit. The refresh reads
    /// the state on the UI thread, so the latest change always wins.
    /// </summary>
    private void QueueStatusBarRefresh()
    {
        if (Interlocked.Exchange(ref _statusBarRefreshQueued, 1) == 0)
        {
            _ = JoinableTaskFactory.RunAsync(RefreshStatusBarAsync);
        }
    }

    /// <summary>Shows the supervisor's current state and connectivity, read on the UI thread so the latest change always wins.</summary>
    private async Task RefreshStatusBarAsync()
    {
        try
        {
            await JoinableTaskFactory.SwitchToMainThreadAsync(DisposalToken);
            Volatile.Write(ref _statusBarRefreshQueued, 0); // A change from now on posts again; this visit reads what came before.
            using var timing = UiThreadTimings.Measure("package.statusbar");
            UpdateIndexStatusItem();
            if (_supervisor is { } supervisor)
            {
                SetStatusBarText(StatusBarText(supervisor));
            }
        }
        catch (OperationCanceledException)
        {
            // Visual Studio is shutting down.
        }
    }

    /// <summary>
    /// Shows the indexing indicator in the clickable status bar item. Costs nothing until there is something to show; then
    /// the item is asked, once, to attach itself when the UI thread is idle (it searches the status bar there, within a few
    /// milliseconds, or keeps to the status bar text). Until then, and if it never attaches, the status bar text shows the
    /// indicator (<see cref="IndexStatusBarSuffix"/>).
    /// </summary>
    private void UpdateIndexStatusItem()
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        if (_indexIndicator is not { } indicator || _indexStatusItem is not { } item)
        {
            return;
        }

        using var timing = UiThreadTimings.Measure("package.statusbar.index");
        var label = indicator.Label;
        if (label.Length > 0 && !item.IsAttached && !item.HasFailed)
        {
            item.RequestAttach();
        }

        item.Update(indicator.Glyph, label, indicator.ToolTip);
    }

    private void SetStatusBarText(string text)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        if (_statusBar is null)
        {
            return;
        }

        if (ErrorHandler.Succeeded(_statusBar.IsFrozen(out var frozen)) && frozen == 0)
        {
            _ = _statusBar.SetText(text);
        }
    }

    private void OnShowStatus(object sender, EventArgs e)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        JoinableTaskFactory.RunAsync(ShowStatusAsync).FileAndForget("pillion/showstatus");
    }

    /// <summary>
    /// Shows the status dialog. The autocomplete section needs the host's counters, so the text is built off the UI
    /// thread (the host gets two seconds to answer) and the dialog is shown on it.
    /// </summary>
    private async Task ShowStatusAsync()
    {
        try
        {
            var autocompleteText = _autocomplete is { } autocomplete
                ? await Task.Run(() => autocomplete.GetStatusTextAsync(DisposalToken), DisposalToken).ConfigureAwait(true)
                : null;

            await JoinableTaskFactory.SwitchToMainThreadAsync(DisposalToken);
            var host = _supervisor is { } supervisor
                ? StatusBarText(supervisor)
                    + (supervisor.HostProcessId is { } pid ? $" (pid {pid})" : string.Empty)
                    + (supervisor.FailureReason is { } reason ? Environment.NewLine + reason : string.Empty)
                : "Pillion: not started";
            if (autocompleteText is not null)
            {
                host += Environment.NewLine + Environment.NewLine + autocompleteText;
                _output?.WriteLine(autocompleteText);
            }

            if (_signals is { } signals)
            {
                host += Environment.NewLine + Environment.NewLine + signals.Hub.StatusText;
            }

            VsShellUtilities.ShowMessageBox(
                this,
                $"{ProductName} {ExtensionVersion}{Environment.NewLine}Contract version {ContractVersion.Text}{Environment.NewLine}{Environment.NewLine}{host}",
                ProductName,
                OLEMSGICON.OLEMSGICON_INFO,
                OLEMSGBUTTON.OLEMSGBUTTON_OK,
                OLEMSGDEFBUTTON.OLEMSGDEFBUTTON_FIRST);
        }
        catch (OperationCanceledException)
        {
            // Visual Studio is shutting down.
        }
    }

    private void OnRetryHost(object sender, EventArgs e)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        if (_supervisor is not { } supervisor)
        {
            return;
        }

        _ = JoinableTaskFactory.RunAsync(async () =>
        {
            try
            {
                await supervisor.RetryAsync().ConfigureAwait(false);
            }
#pragma warning disable CA1031 // A failed retry is reported through the supervisor's state and the log.
            catch (Exception ex)
#pragma warning restore CA1031
            {
                _log?.Error("package.retry.failed", "Retry failed.", ("error", ex.GetType().Name), ("detail", ex.Message));
            }
        });
    }

    /// <summary>
    /// Creates an Open Chat command that is disabled while the chat feature flag is off (settings or repository policy).
    /// Opening the Tools menu also asks the host again, so a change shows up without restarting Visual Studio.
    /// </summary>
    private OleMenuCommand CreateOpenChatCommand(int commandId)
    {
        var command = new OleMenuCommand(OnOpenChat, new CommandID(PackageGuids.CommandSet, commandId));
        command.BeforeQueryStatus += (sender, args) =>
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            using var timing = UiThreadTimings.Measure("package.querystatus.openchat");
            var availability = _chatAvailability;
            if (sender is OleMenuCommand menuCommand)
            {
                menuCommand.Enabled = availability?.IsEnabled ?? true;
                menuCommand.Visible = true;
            }

            if (availability is not null)
            {
                _ = JoinableTaskFactory.RunAsync(() => availability.RefreshAsync(DisposalToken));
            }

            // The Tools menu is where users go after changing IntelliCode's options: check again (and re-read the
            // autocomplete settings) whenever it opens.
            if (commandId == PackageIds.OpenChatCommand && _autocomplete is { } autocomplete)
            {
                _ = JoinableTaskFactory.RunAsync(() => autocomplete.Availability.RefreshAsync(DisposalToken));
                _ = JoinableTaskFactory.RunAsync(() => CheckIntelliCodeAsync(DisposalToken));
            }
        };
        return command;
    }

    private void OnOpenChat(object sender, EventArgs e)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        _ = JoinableTaskFactory.RunAsync(async () =>
        {
            try
            {
                if (_chatAvailability is { } availability)
                {
                    await availability.RefreshAsync(DisposalToken).ConfigureAwait(true);
                    if (!availability.IsEnabled)
                    {
                        _output?.WriteLine(ChatAvailability.DisabledMessage + ".");
                        return;
                    }
                }

                var window = await ShowToolWindowAsync(typeof(ChatToolWindow), 0, create: true, DisposalToken).ConfigureAwait(true);
                if (window?.Frame is null)
                {
                    _log?.Error("package.open_chat.failed", "Could not create the Pillion Chat window.");
                }
            }
            catch (OperationCanceledException)
            {
                // Visual Studio is shutting down.
            }
        });
    }

    /// <summary>
    /// Creates the command that opens the Index window. It is hidden (and disabled) while the <c>indexWindow</c> flag is off;
    /// opening the Tools or View menu also asks the host again, so a change shows up without restarting Visual Studio.
    /// </summary>
    private OleMenuCommand CreateOpenIndexCommand(int commandId)
    {
        var command = new OleMenuCommand(OnOpenIndex, new CommandID(PackageGuids.CommandSet, commandId));
        command.BeforeQueryStatus += (sender, args) =>
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            using var timing = UiThreadTimings.Measure("package.querystatus.openindex");
            var availability = _chatAvailability;
            if (sender is OleMenuCommand menuCommand)
            {
                var enabled = availability?.IsIndexWindowEnabled ?? true;
                menuCommand.Visible = enabled;
                menuCommand.Enabled = enabled;
            }

            if (availability is not null)
            {
                _ = JoinableTaskFactory.RunAsync(() => availability.RefreshAsync(DisposalToken));
            }
        };
        return command;
    }

    private void OnOpenIndex(object sender, EventArgs e)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        _ = JoinableTaskFactory.RunAsync(async () =>
        {
            try
            {
                if (_chatAvailability is { } availability)
                {
                    await availability.RefreshAsync(DisposalToken).ConfigureAwait(true);
                    if (!availability.IsIndexWindowEnabled)
                    {
                        _output?.WriteLine(IndexStrings.DisabledMessage + ".");
                        return;
                    }
                }

                var window = await ShowToolWindowAsync(typeof(IndexToolWindow), 0, create: true, DisposalToken).ConfigureAwait(true);
                if (window?.Frame is null)
                {
                    _log?.Error("package.open_index.failed", "Could not create the Pillion Index window.");
                }
            }
            catch (OperationCanceledException)
            {
                // Visual Studio is shutting down.
            }
        });
    }

    /// <summary>A click on the status bar's indexing indicator (UI thread).</summary>
    private void OpenIndexFromStatusBar()
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        OnOpenIndex(this, EventArgs.Empty);
    }

    private void OnOpenLogs(object sender, EventArgs e) => OpenLogFolder();

    /// <summary>
    /// The IntelliCode run-time check on Visual Studio 2022: when IntelliCode is missing or its automatic code
    /// completions are off, says so in the status bar and the Output pane (once per change). Nothing on 18.x.
    /// </summary>
    private async Task CheckIntelliCodeAsync(CancellationToken cancellationToken)
    {
        if (_autocomplete is not { } autocomplete || Interlocked.Exchange(ref _intelliCodeCheckRunning, 1) != 0)
        {
            return;
        }

        try
        {
            // Started from the UI thread (the Tools menu opening) this would run on it until the first await; the check
            // reads the devenv.exe version resource, so leave the UI thread first. RunAsync visits it only where it must.
            await TaskScheduler.Default;
            var (status, detail) = await IntelliCodeCheck.RunAsync(this, cancellationToken).ConfigureAwait(true);
            autocomplete.IntelliCode = status;
            var previous = _intelliCodeReported;
            _intelliCodeReported = status;
            if (status == previous)
            {
                return;
            }

            _log?.Info("autocomplete.intellicode", "IntelliCode check: " + status + ".", ("detail", detail));
            if (IntelliCodeRequirement.Describe(status) is { } message)
            {
                _output?.WriteLine(message);
            }
            else if (previous is IntelliCodeStatus.Missing or IntelliCodeStatus.CompletionsOff)
            {
                _output?.WriteLine("IntelliCode's automatic code completions are on: inline suggestions are available.");
            }

            // The status bar text carries the IntelliCode part only while it is missing or its completions are off: when neither
            // the old nor the new status shows it the text is unchanged, so the UI thread is not visited at all.
            if (!ShowsInStatusBar(status) && !ShowsInStatusBar(previous))
            {
                return;
            }

            await JoinableTaskFactory.SwitchToMainThreadAsync(cancellationToken);
            using var timing = UiThreadTimings.Measure("package.intellicode.statusbar");
            if (_supervisor is { } supervisor)
            {
                SetStatusBarText(StatusBarText(supervisor));
            }
        }
        catch (OperationCanceledException)
        {
            // Visual Studio is shutting down.
        }
#pragma warning disable CA1031 // The check must never break the package; ghost text simply stays as it is.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            _log?.Warning("autocomplete.intellicode.failed", "The IntelliCode check failed.", ("error", ex.GetType().Name), ("detail", ex.Message));
        }
        finally
        {
            Interlocked.Exchange(ref _intelliCodeCheckRunning, 0);
        }
    }

    private static bool ShowsInStatusBar(IntelliCodeStatus status) => status is IntelliCodeStatus.Missing or IntelliCodeStatus.CompletionsOff;

    /// <summary>Opens the log folder in File Explorer (Tools &gt; Pillion: Open Logs, and the chat window's status line).</summary>
    private void OpenLogFolder()
    {
        var directory = PillionLog.DefaultDirectory;
        try
        {
            Directory.CreateDirectory(directory);
            using var explorer = Process.Start(new ProcessStartInfo("explorer.exe", "\"" + directory + "\"") { UseShellExecute = true });
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
        {
            _log?.Error("package.open_logs.failed", "Could not open the log folder.", ("path", directory), ("detail", ex.Message));
        }
    }
}

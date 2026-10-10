using System;
using System.ComponentModel.Design;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using Microsoft.VisualStudio;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using Pillion.Contracts;
using Pillion.VisualStudio.Autocomplete;
using Pillion.VisualStudio.Core;
using Pillion.VisualStudio.Core.Autocomplete;
using Pillion.VisualStudio.Core.Signals;
using Pillion.VisualStudio.InlineChat;
using Pillion.VisualStudio.Options;
using Pillion.VisualStudio.Signals;
using Pillion.VisualStudio.ToolWindows;
using Task = System.Threading.Tasks.Task;

namespace Pillion.VisualStudio;

/// <summary>
/// The Pillion Programmer package. Loads in the background when a solution or folder is opened, starts and supervises
/// Pillion Host (one per devenv.exe), shows its state in the status bar, registers <see cref="IHostConnection"/> as a
/// service, and adds the Tools menu commands "Pillion: Open Chat", "Pillion: Show Status", "Pillion: Retry" and
/// "Pillion: Open Logs". Provides the Pillion Chat tool window (also under View &gt; Other Windows) and the
/// Tools &gt; Options &gt; Pillion Programmer pages "Providers and keys" and "Privacy".
/// </summary>
[PackageRegistration(UseManagedResourcesOnly = true, AllowsBackgroundLoading = true)]
[ProvideMenuResource("Menus.ctmenu", 1)]
[ProvideAutoLoad(VSConstants.UICONTEXT.SolutionExists_string, PackageAutoLoadFlags.BackgroundLoad)]
[ProvideAutoLoad(VSConstants.UICONTEXT.FolderOpened_string, PackageAutoLoadFlags.BackgroundLoad)]
[ProvideService(typeof(IHostConnection), IsAsyncQueryable = true)]
[ProvideService(typeof(IIdeSignals), IsAsyncQueryable = true)]
[ProvideToolWindow(typeof(ChatToolWindow), Style = VsDockStyle.Tabbed, Window = SolutionExplorerWindow, Orientation = ToolWindowOrientation.Right)]
[ProvideOptionPage(typeof(ProvidersOptionPage), ProductName, "Providers and keys", 0, 0, supportsAutomation: false)]
[ProvideOptionPage(typeof(PrivacyOptionPage), ProductName, "Privacy", 0, 0, supportsAutomation: false)]
[ProvideOptionPage(typeof(RoutingOptionPage), ProductName, "Models and routing", 0, 0, supportsAutomation: false)]
[ProvideOptionPage(typeof(AutocompleteOptionPage), ProductName, "Autocomplete", 0, 0, supportsAutomation: false)]
[Guid(PackageGuids.PackageString)]
public sealed class PillionPackage : AsyncPackage
{
    private const string ProductName = "Pillion Programmer";

    /// <summary>The Solution Explorer tool window GUID (EnvDTE.Constants.vsWindowKindSolutionExplorer): Pillion Chat docks beside it.</summary>
    private const string SolutionExplorerWindow = "3ae79031-e1bc-11d0-8f78-00a0c9110057";

    /// <summary>How long the host gets to exit cleanly when Visual Studio closes.</summary>
    private static readonly TimeSpan HostShutdownTimeout = TimeSpan.FromSeconds(2);

    private PillionLog? _log;
    private HostSupervisor? _supervisor;
    private ChatAvailability? _chatAvailability;
    private OutputPaneWriter? _output;
    private IVsStatusbar? _statusBar;
    private AutocompleteFeature? _autocomplete;
    private InlineChatFeature? _inlineChat;
    private IdeSignalsFeature? _signals;
    private IntelliCodeStatus _intelliCodeReported = IntelliCodeStatus.NotRequired;
    private int _intelliCodeCheckRunning;

    /// <summary>Gets the extension's version (the assembly informational version).</summary>
    internal static string ExtensionVersion { get; } = ReadVersion();

    /// <summary>Gets the loaded package, once <see cref="InitializeAsync"/> has created the supervisor (for the Options pages).</summary>
    internal static PillionPackage? Instance { get; private set; }

    /// <summary>Gets the connection to Pillion Host, once initialization has created it.</summary>
    internal IHostConnection? HostConnection => _supervisor;

    /// <summary>Gets the extension log, once initialization has created it.</summary>
    internal PillionLog? Log => _log;

    /// <summary>Gets the IDE signals (builds, documents, diagnostics, Output panes) service, once initialization has created it.</summary>
    internal IIdeSignals? IdeSignals => _signals?.Hub;

    /// <summary>Gets ghost-text autocomplete (engine, settings, IntelliCode state), once initialization has created it.</summary>
    internal AutocompleteFeature? Autocomplete => _autocomplete;

    /// <summary>Gets inline chat (feature flag, recent prompts, the command), once initialization has created it.</summary>
    internal InlineChatFeature? InlineChat => _inlineChat;

    /// <inheritdoc/>
    protected override async Task InitializeAsync(CancellationToken cancellationToken, IProgress<ServiceProgressData> progress)
    {
        await base.InitializeAsync(cancellationToken, progress).ConfigureAwait(true);

        // Background thread: nothing here touches the UI or waits for the host.
        var log = new PillionLog(PillionLog.DefaultDirectory);
        _log = log;
        var supervisor = CreateSupervisor(log);
        _supervisor = supervisor;
        var chatAvailability = new ChatAvailability(supervisor, log);
        _chatAvailability = chatAvailability;
        var autocomplete = new AutocompleteFeature(supervisor, log);
        _autocomplete = autocomplete;
        var inlineChat = new InlineChatFeature(this, supervisor, log, output: null);
        _inlineChat = inlineChat;
        AddService(typeof(IHostConnection), (container, token, type) => Task.FromResult<object?>(supervisor), promote: true);
        var signals = new IdeSignalsFeature(JoinableTaskFactory, log);
        _signals = signals;
        AddService(typeof(IIdeSignals), (container, token, type) => Task.FromResult<object?>(signals.Hub), promote: true);
        Instance = this;

        // The COM output window, status bar and command service must be used on the UI thread.
        await JoinableTaskFactory.SwitchToMainThreadAsync(cancellationToken);

        var outputWindow = await GetServiceAsync(typeof(SVsOutputWindow)).ConfigureAwait(true) as IVsOutputWindow;
        var commandService = await GetServiceAsync(typeof(IMenuCommandService)).ConfigureAwait(true) as OleMenuCommandService;
        _statusBar = await GetServiceAsync(typeof(SVsStatusbar)).ConfigureAwait(true) as IVsStatusbar;

        var pane = CreateOutputPane(outputWindow);
        if (pane is not null)
        {
            pane.OutputStringThreadSafe($"{ProductName} {ExtensionVersion} loaded (contract {ContractVersion.Text}).{Environment.NewLine}");
            var output = new OutputPaneWriter(pane, JoinableTaskFactory, DisposalToken);
            _output = output;
            log.Sink = line => output.WriteLine(line);
            inlineChat.Output = line => output.WriteLine(line);
        }

        if (commandService is not null)
        {
            commandService.AddCommand(new MenuCommand(OnShowStatus, new CommandID(PackageGuids.CommandSet, PackageIds.ShowStatusCommand)));
            commandService.AddCommand(new MenuCommand(OnRetryHost, new CommandID(PackageGuids.CommandSet, PackageIds.RetryHostCommand)));
            commandService.AddCommand(new MenuCommand(OnOpenLogs, new CommandID(PackageGuids.CommandSet, PackageIds.OpenLogsCommand)));
            commandService.AddCommand(CreateOpenChatCommand(PackageIds.OpenChatCommand));
            commandService.AddCommand(CreateOpenChatCommand(PackageIds.ViewChatCommand));
            commandService.AddCommand(inlineChat.CreateCommand());
        }

        supervisor.StateChanged += OnHostStateChanged;
        supervisor.ConnectivityChanged += OnConnectivityChanged;
        autocomplete.StatusChanged += (_, _) => _ = JoinableTaskFactory.RunAsync(RefreshStatusBarAsync);
        SetStatusBarText(StatusBarText(supervisor));

        // Returns at once: launching, connecting and the handshake run on the thread pool. Availability follows the
        // host's effective feature flags: it reads them whenever the host becomes ready.
        chatAvailability.Start();
        autocomplete.Start();
        inlineChat.Start();
        _ = JoinableTaskFactory.RunAsync(() => signals.StartAsync(this, DisposalToken));
        _ = JoinableTaskFactory.RunAsync(() => CheckIntelliCodeAsync(DisposalToken));
        supervisor.Start();
    }

    /// <inheritdoc/>
    /// <remarks>Only Pillion Chat is created asynchronously; the base implementation returns <see langword="null"/> too.</remarks>
    public override IVsAsyncToolWindowFactory? GetAsyncToolWindowFactory(Guid toolWindowType) =>
        toolWindowType == typeof(ChatToolWindow).GUID ? this : null;

    /// <inheritdoc/>
    protected override string? GetToolWindowTitle(Type toolWindowType, int id) =>
        toolWindowType == typeof(ChatToolWindow) ? ChatToolWindow.Title : base.GetToolWindowTitle(toolWindowType, id);

    /// <inheritdoc/>
    protected override System.Threading.Tasks.Task<object> InitializeToolWindowAsync(Type toolWindowType, int id, CancellationToken cancellationToken)
    {
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
            _autocomplete?.Dispose();
            _autocomplete = null;
            _inlineChat?.Dispose();
            _inlineChat = null;
            _signals?.Dispose();
            _signals = null;
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

        base.Dispose(disposing);
    }

    private static string ReadVersion()
    {
        var assembly = typeof(PillionPackage).Assembly;
        return assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
            ?? assembly.GetName().Version?.ToString()
            ?? "0.0.0";
    }

    private static IVsOutputWindowPane? CreateOutputPane(IVsOutputWindow? outputWindow)
    {
        ThreadHelper.ThrowIfNotOnUIThread();

        if (outputWindow is null)
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
        var targets = new HostLocalTargets(new EditorService(this), new NotImplementedApprovalService(), new NotImplementedWorkspaceQueryService());
        var options = new HostLauncherOptions(HostLauncherOptions.DefaultHostPath(extensionAssembly), extensionAssembly, targets)
        {
            LogDirectory = PillionLog.DefaultDirectory,
        };

        return new HostSupervisor(new HostLauncherSelector(options, log), JoinableTaskFactory, log); // The selector lets the IDE tests swap in the fake host.
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

        _ = JoinableTaskFactory.RunAsync(RefreshStatusBarAsync);
    }

    /// <summary>Raised on a background thread when the host reports a new connectivity state.</summary>
    private void OnConnectivityChanged(object? sender, HostConnectivityChangedEventArgs e) =>
        _ = JoinableTaskFactory.RunAsync(RefreshStatusBarAsync);

    private string StatusBarText(HostSupervisor supervisor) =>
        HostStatusText.Format(supervisor.State, supervisor.RestartAttempt, supervisor.MaxRestartAttempts, supervisor.Connectivity)
        + (supervisor.State is HostState.Ready or HostState.Degraded ? _autocomplete?.StatusBarSuffix : null)
        + (_intelliCodeReported is IntelliCodeStatus.Missing or IntelliCodeStatus.CompletionsOff ? ". " + IntelliCodeRequirement.Message : string.Empty);

    /// <summary>Shows the supervisor's current state and connectivity, read on the UI thread so the latest change always wins.</summary>
    private async Task RefreshStatusBarAsync()
    {
        try
        {
            await JoinableTaskFactory.SwitchToMainThreadAsync(DisposalToken);
            using var timing = UiThreadTimings.Measure("package.statusbar");
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

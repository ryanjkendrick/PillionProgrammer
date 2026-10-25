using System;
using System.Threading;
using System.Threading.Tasks;
using EnvDTE;
using Microsoft.VisualStudio;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using Microsoft.VisualStudio.Shell.TableManager;
using Microsoft.VisualStudio.Threading;
using Pillion.VisualStudio.Core;
using Pillion.VisualStudio.Core.Signals;
using Task = System.Threading.Tasks.Task;

namespace Pillion.VisualStudio.Signals;

/// <summary>
/// The package-owned IDE signals: creates the <see cref="IdeSignalsHub"/> that is registered as the
/// <see cref="IIdeSignals"/> service and connects it to Visual Studio. <see cref="StartAsync"/> never blocks package load:
/// it runs after initialization, finds the services on a background thread and visits the UI thread in short batches only
/// for the COM calls that need it. It connects the build events, the Running Document Table and Roslyn at once; the Error
/// List and the Output window are connected lazily, on their first use (issue #49, decided 2026-10-24): asking Visual
/// Studio for <c>SVsErrorList</c> makes it create the Error List's table control (51 to 79 ms of UI-thread time on 17.14,
/// 117 ms before Ready on a CI runner) and <c>SVsOutputWindow</c> initialises the Output window (22 to 26 ms), so the hub
/// runs the connectors when diagnostics or a pane are first asked for (or through
/// <see cref="IIdeSignals.EnsureDiagnosticsConnectedAsync"/>). Until then <see cref="IIdeSignals.DiagnosticsChanged"/>
/// does not fire and Roslyn answers the active document's diagnostics.
/// </summary>
internal sealed class IdeSignalsFeature : IDisposable
{
    private const uint NoCookie = 0;

    private readonly JoinableTaskFactory _joinableTaskFactory;
    private readonly PillionLog _log;
    private readonly DisconnectSteps _disconnect = new();
    private int _stopped;

    /// <summary>Initializes a new instance of the <see cref="IdeSignalsFeature"/> class.</summary>
    /// <param name="joinableTaskFactory">Switches to the UI thread.</param>
    /// <param name="log">The extension log.</param>
    public IdeSignalsFeature(JoinableTaskFactory joinableTaskFactory, PillionLog log)
    {
        _joinableTaskFactory = joinableTaskFactory;
        _log = log;
        Hub = new IdeSignalsHub(SystemClock.Instance, log);
    }

    /// <summary>Gets the hub: the object registered as the <see cref="IIdeSignals"/> service.</summary>
    public IdeSignalsHub Hub { get; }

    /// <summary>
    /// Connects the hub to Visual Studio; each part that fails is logged and left out, the others keep working. The
    /// Error List and the Output window are not connected here: the hub gets their connectors and runs each once, on the
    /// first diagnostics or pane query (or <see cref="IIdeSignals.EnsureDiagnosticsConnectedAsync"/>).
    /// </summary>
    /// <param name="services">The package's async service provider.</param>
    /// <param name="cancellationToken">Cancelled when Visual Studio shuts down.</param>
    /// <returns>A task that completes when the eager connections are made.</returns>
    public async Task StartAsync(IAsyncServiceProvider services, CancellationToken cancellationToken)
    {
        await TaskScheduler.Default;

        await RunStepAsync("build events", () => ConnectBuildEventsAsync(services, cancellationToken)).ConfigureAwait(false);
        await RunStepAsync("document events", () => ConnectDocumentEventsAsync(services, cancellationToken)).ConfigureAwait(false);
        Hub.Roslyn = new RoslynDiagnosticsSource(_log);
        Hub.OutputConnector = token => RunStepAsync("output panes", () => ConnectOutputPanesAsync(services, Linked(token, cancellationToken)));
        Hub.ErrorListConnector = token => RunStepAsync("Error List", () => ConnectErrorListAsync(services, Linked(token, cancellationToken)));
        _log.Info("signals.started", "IDE signals started.", ("diagnostics", Hub.DiagnosticsNote));
    }

    /// <summary>The caller's token, or the shutdown token when the caller's cannot be cancelled (the connection still stops at shutdown).</summary>
    private static CancellationToken Linked(CancellationToken caller, CancellationToken shutdown) =>
        caller.CanBeCanceled ? caller : shutdown;

    /// <inheritdoc/>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _stopped, 1) != 0)
        {
            return;
        }

        // Order: stop the sources of events first (on the UI thread, where the COM advises were made), then the hub that
        // receives them, so no sink calls into a disposed hub. Each disconnect is isolated (see DisconnectSteps); the hub
        // is disposed even when the switch to the UI thread itself fails.
        try
        {
            _joinableTaskFactory.Run(async () =>
            {
                await _joinableTaskFactory.SwitchToMainThreadAsync();
                _ = _disconnect.RunAll(_log);
            });
        }
#pragma warning disable CA1031 // Shutting down: nothing here may throw out of Dispose.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            _log.Warning("signals.dispose.failed", "Disconnecting the IDE signals failed.", ("error", ex.GetType().Name), ("detail", ex.Message));
        }
        finally
        {
            Hub.Dispose();
        }
    }

    private async Task RunStepAsync(string name, Func<Task> step)
    {
        try
        {
            await step().ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
#pragma warning disable CA1031 // One failing connection must not take the others (or the package) down.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            _log.Warning("signals.connect.failed", "Connecting the IDE signals to " + name + " failed.", ("error", ex.GetType().Name), ("detail", ex.Message));
        }
    }

    private async Task ConnectBuildEventsAsync(IAsyncServiceProvider services, CancellationToken cancellationToken)
    {
        await _joinableTaskFactory.SwitchToMainThreadAsync(cancellationToken);
        using var timing = UiThreadTimings.Measure("signals.connect.build");
        var buildManager = await services.GetServiceAsync(typeof(SVsSolutionBuildManager)).ConfigureAwait(true) as IVsSolutionBuildManager2;
        if (buildManager is null)
        {
            _log.Warning("signals.build.unavailable", "The solution build manager is not available; build events are off.");
            return;
        }

        if (Volatile.Read(ref _stopped) == 0 && ErrorHandler.Succeeded(buildManager.AdviseUpdateSolutionEvents(new SolutionBuildSink(Hub), out var cookie)) && cookie != NoCookie)
        {
            _disconnect.Add("build events", () =>
            {
                ThreadHelper.ThrowIfNotOnUIThread();
                _ = buildManager.UnadviseUpdateSolutionEvents(cookie);
            });
        }
    }

    private async Task ConnectDocumentEventsAsync(IAsyncServiceProvider services, CancellationToken cancellationToken)
    {
        await _joinableTaskFactory.SwitchToMainThreadAsync(cancellationToken);
        using var timing = UiThreadTimings.Measure("signals.connect.documents");
        var table = await services.GetServiceAsync(typeof(SVsRunningDocumentTable)).ConfigureAwait(true) as IVsRunningDocumentTable;
        if (table is not IVsRunningDocumentTable4 table4)
        {
            _log.Warning("signals.documents.unavailable", "The Running Document Table is not available; document events are off.");
            return;
        }

        if (Volatile.Read(ref _stopped) == 0 && ErrorHandler.Succeeded(table.AdviseRunningDocTableEvents(new RunningDocumentSink(Hub, table4), out var cookie)) && cookie != NoCookie)
        {
            _disconnect.Add("document events", () =>
            {
                ThreadHelper.ThrowIfNotOnUIThread();
                _ = table.UnadviseRunningDocTableEvents(cookie);
            });
        }
    }

    private async Task ConnectOutputPanesAsync(IAsyncServiceProvider services, CancellationToken cancellationToken)
    {
        await _joinableTaskFactory.SwitchToMainThreadAsync(cancellationToken);
        using var timing = UiThreadTimings.Measure("signals.connect.output");
        var outputWindow = await services.GetServiceAsync(typeof(SVsOutputWindow)).ConfigureAwait(true) as IVsOutputWindow;
        var dte = await services.GetServiceAsync(typeof(SDTE)).ConfigureAwait(true) as DTE;
        if (outputWindow is null || dte is null)
        {
            _log.Warning("signals.output.unavailable", "The Output window is not available; Output pane queries return nothing.");
            return;
        }

        Hub.Output = new OutputPaneSource(outputWindow, dte, _joinableTaskFactory);
    }

    private async Task ConnectErrorListAsync(IAsyncServiceProvider services, CancellationToken cancellationToken)
    {
        await _joinableTaskFactory.SwitchToMainThreadAsync(cancellationToken);
        using var timing = UiThreadTimings.Measure("signals.connect.errorlist");
        _log.Info("signals.errorlist.connecting", "Connecting the Error List on its first use.");
        var errorList = await services.GetServiceAsync(typeof(SVsErrorList)).ConfigureAwait(true) as IErrorList;
        if (errorList is null)
        {
            _log.Warning("signals.errorlist.unavailable", "The Error List is not available; diagnostics come from Roslyn only.");
            return;
        }

        if (Volatile.Read(ref _stopped) != 0)
        {
            return;
        }

        // Keep the table control itself: at shutdown Visual Studio has already closed the Error List and
        // errorList.TableControl is null by then, which made Dispose throw a NullReferenceException every time
        // (logged as signals.dispose.failed). Unsubscribing from the captured control is harmless either way.
        var tableControl = errorList.TableControl;
        if (tableControl is null)
        {
            _log.Warning("signals.errorlist.unavailable", "The Error List has no table control yet; diagnostics come from Roslyn only.");
            return;
        }

        CheckTableKeys();
        tableControl.EntriesChanged += OnErrorListChanged;
        _disconnect.Add("the Error List", () => tableControl.EntriesChanged -= OnErrorListChanged);
        Hub.ErrorList = new ErrorListSource(errorList, _joinableTaskFactory);
    }

    /// <summary>The Core mapper reads columns by name; say so loudly if Visual Studio's names ever differ.</summary>
    private void CheckTableKeys()
    {
        var pairs = new (string Expected, string Actual)[]
        {
            (TableKeys.DocumentName, StandardTableKeyNames.DocumentName),
            (TableKeys.Line, StandardTableKeyNames.Line),
            (TableKeys.Column, StandardTableKeyNames.Column),
            (TableKeys.ErrorSeverity, StandardTableKeyNames.ErrorSeverity),
            (TableKeys.ErrorCode, StandardTableKeyNames.ErrorCode),
            (TableKeys.Text, StandardTableKeyNames.Text),
            (TableKeys.ProjectName, StandardTableKeyNames.ProjectName),
        };
        foreach (var (expected, actual) in pairs)
        {
            if (!string.Equals(expected, actual, StringComparison.Ordinal))
            {
                _log.Error("signals.tablekeys.mismatch", "An Error List column key differs from Core's constant.", ("core", expected), ("visualStudio", actual));
            }
        }
    }

    private void OnErrorListChanged(object? sender, EventArgs e) => Hub.NotifyDiagnosticsMayHaveChanged();
}

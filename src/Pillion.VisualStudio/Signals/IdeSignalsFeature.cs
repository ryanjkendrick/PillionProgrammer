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
/// <see cref="IIdeSignals"/> service and connects it to Visual Studio (build events, Running Document Table, Error List,
/// Roslyn workspace, Output window). <see cref="StartAsync"/> never blocks package load: it runs after initialization,
/// finds the services on a background thread and visits the UI thread in short batches only for the COM calls that need it.
/// </summary>
internal sealed class IdeSignalsFeature : IDisposable
{
    private const uint NoCookie = 0;

    private readonly JoinableTaskFactory _joinableTaskFactory;
    private readonly PillionLog _log;
    private IVsSolutionBuildManager2? _buildManager;
    private uint _buildCookie = NoCookie;
    private IVsRunningDocumentTable? _runningDocumentTable;
    private uint _documentCookie = NoCookie;
    private IErrorList? _errorList;
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

    /// <summary>Connects the hub to Visual Studio; each part that fails is logged and left out, the others keep working.</summary>
    /// <param name="services">The package's async service provider.</param>
    /// <param name="cancellationToken">Cancelled when Visual Studio shuts down.</param>
    /// <returns>A task that completes when the connections are made.</returns>
    public async Task StartAsync(IAsyncServiceProvider services, CancellationToken cancellationToken)
    {
        await TaskScheduler.Default;

        await RunStepAsync("build events", () => ConnectBuildEventsAsync(services, cancellationToken)).ConfigureAwait(false);
        await RunStepAsync("document events", () => ConnectDocumentEventsAsync(services, cancellationToken)).ConfigureAwait(false);
        await RunStepAsync("output panes", () => ConnectOutputPanesAsync(services, cancellationToken)).ConfigureAwait(false);
        Hub.Roslyn = new RoslynDiagnosticsSource(_log);
        await RunStepAsync("Error List", () => ConnectErrorListAsync(services, cancellationToken)).ConfigureAwait(false);
        _log.Info("signals.started", "IDE signals started.", ("diagnostics", Hub.DiagnosticsNote));
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _stopped, 1) != 0)
        {
            return;
        }

        Hub.Dispose();
        try
        {
            _joinableTaskFactory.Run(async () =>
            {
                await _joinableTaskFactory.SwitchToMainThreadAsync();
                if (_buildManager is not null && _buildCookie != NoCookie)
                {
                    _ = _buildManager.UnadviseUpdateSolutionEvents(_buildCookie);
                    _buildCookie = NoCookie;
                }

                if (_runningDocumentTable is not null && _documentCookie != NoCookie)
                {
                    _ = _runningDocumentTable.UnadviseRunningDocTableEvents(_documentCookie);
                    _documentCookie = NoCookie;
                }

                if (_errorList is not null)
                {
                    _errorList.TableControl.EntriesChanged -= OnErrorListChanged;
                    _errorList = null;
                }
            });
        }
#pragma warning disable CA1031 // Shutting down: nothing here may throw out of Dispose.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            _log.Warning("signals.dispose.failed", "Disconnecting the IDE signals failed.", ("error", ex.GetType().Name), ("detail", ex.Message));
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
        var buildManager = await services.GetServiceAsync(typeof(SVsSolutionBuildManager)).ConfigureAwait(true) as IVsSolutionBuildManager2;
        if (buildManager is null)
        {
            _log.Warning("signals.build.unavailable", "The solution build manager is not available; build events are off.");
            return;
        }

        if (Volatile.Read(ref _stopped) == 0 && ErrorHandler.Succeeded(buildManager.AdviseUpdateSolutionEvents(new SolutionBuildSink(Hub), out var cookie)))
        {
            _buildManager = buildManager;
            _buildCookie = cookie;
        }
    }

    private async Task ConnectDocumentEventsAsync(IAsyncServiceProvider services, CancellationToken cancellationToken)
    {
        await _joinableTaskFactory.SwitchToMainThreadAsync(cancellationToken);
        var table = await services.GetServiceAsync(typeof(SVsRunningDocumentTable)).ConfigureAwait(true) as IVsRunningDocumentTable;
        if (table is not IVsRunningDocumentTable4 table4)
        {
            _log.Warning("signals.documents.unavailable", "The Running Document Table is not available; document events are off.");
            return;
        }

        if (Volatile.Read(ref _stopped) == 0 && ErrorHandler.Succeeded(table.AdviseRunningDocTableEvents(new RunningDocumentSink(Hub, table4), out var cookie)))
        {
            _runningDocumentTable = table;
            _documentCookie = cookie;
        }
    }

    private async Task ConnectOutputPanesAsync(IAsyncServiceProvider services, CancellationToken cancellationToken)
    {
        await _joinableTaskFactory.SwitchToMainThreadAsync(cancellationToken);
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

        CheckTableKeys();
        errorList.TableControl.EntriesChanged += OnErrorListChanged;
        _errorList = errorList;
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

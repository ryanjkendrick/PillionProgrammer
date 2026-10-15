using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using Microsoft.VisualStudio.Threading;
using Pillion.VisualStudio.Core;
using Pillion.VisualStudio.Core.Signals;
using Task = System.Threading.Tasks.Task;

namespace Pillion.VisualStudio.Signals;

/// <summary>
/// The package-owned indexing relay (work item 2.9): connects the open solution or folder (<see cref="SolutionWorkspaceSource"/>),
/// the debugger (<see cref="DebuggerActivitySource"/>) and the build state (<see cref="IIdeSignals"/>) to an
/// <see cref="IndexingRelay"/>, which tells the host what to index and when to pause. Behind no flag of its own: the host
/// decides with <c>features.indexing</c>, and nothing is sent before it is ready. <see cref="StartAsync"/> never blocks package
/// load: it waits until start-up is over, then visits the UI thread once to advise the two event sources.
/// </summary>
internal sealed class IndexingRelayFeature : IDisposable
{
    private const uint NoCookie = 0;

    private readonly JoinableTaskFactory _joinableTaskFactory;
    private readonly IHostConnection _connection;
    private readonly IIdeSignals _signals;
    private readonly PillionLog _log;
    private readonly DisconnectSteps _disconnect = new();
    private readonly DebuggerActivitySource _debugger;
    private IndexingRelay? _relay;
    private int _stopped;

    /// <summary>Initializes a new instance of the <see cref="IndexingRelayFeature"/> class.</summary>
    /// <param name="joinableTaskFactory">Switches to the UI thread.</param>
    /// <param name="connection">The connection to Pillion Host.</param>
    /// <param name="signals">The IDE signals (the build state).</param>
    /// <param name="log">The extension log.</param>
    public IndexingRelayFeature(JoinableTaskFactory joinableTaskFactory, IHostConnection connection, IIdeSignals signals, PillionLog log)
    {
        _joinableTaskFactory = joinableTaskFactory;
        _connection = connection;
        _signals = signals;
        _log = log;
        _debugger = new DebuggerActivitySource(log);
    }

    /// <summary>Connects the event sources and starts the relay; a part that fails is logged and left out.</summary>
    /// <param name="services">The package's async service provider.</param>
    /// <param name="cancellationToken">Cancelled when Visual Studio shuts down.</param>
    /// <param name="startupOver">Returns a task that completes when the package's start-up is over, or <see langword="null"/> to start at once.</param>
    /// <returns>A task that completes when the relay has started.</returns>
    public async Task StartAsync(IAsyncServiceProvider services, CancellationToken cancellationToken, Func<Task>? startupOver = null)
    {
        await TaskScheduler.Default;
        if (startupOver is not null)
        {
            await startupOver().ConfigureAwait(false);
        }

        try
        {
            await _joinableTaskFactory.SwitchToMainThreadAsync(cancellationToken);
            IVsSolution? solution;
            using (UiThreadTimings.Measure("indexing.connect"))
            {
                solution = await services.GetServiceAsync(typeof(SVsSolution)).ConfigureAwait(true) as IVsSolution;
                var debugger = await services.GetServiceAsync(typeof(SVsShellDebugger)).ConfigureAwait(true) as IVsDebugger;
                if (Volatile.Read(ref _stopped) != 0)
                {
                    return;
                }

                ConnectDebugger(debugger);
                if (solution is null)
                {
                    _log.Warning("indexing.relay.unavailable", "The solution service is not available; the host is not told what to index.");
                    return;
                }
            }

            var workspace = new SolutionWorkspaceSource(_joinableTaskFactory, solution, _log);
            using (UiThreadTimings.Measure("indexing.connect.solution"))
            {
                if (ErrorHandler.Succeeded(solution.AdviseSolutionEvents(workspace, out var cookie)) && cookie != NoCookie)
                {
                    _disconnect.Add("solution events", () =>
                    {
                        ThreadHelper.ThrowIfNotOnUIThread();
                        _ = solution.UnadviseSolutionEvents(cookie);
                    });
                }
                else
                {
                    _log.Warning("indexing.solution_events.unavailable", "Could not follow solution events; the workspace is sent on each connection only.");
                }
            }

            // Off the UI thread again: starting the relay may send at once when the host is already ready.
            await TaskScheduler.Default;
            var relay = new IndexingRelay(_connection, _signals, workspace, _debugger, SystemClock.Instance, _log);
            if (Interlocked.CompareExchange(ref _relay, relay, null) is not null || Volatile.Read(ref _stopped) != 0)
            {
                relay.Dispose();
                return;
            }

            relay.Start();
            _log.Info("indexing.relay.started", "The indexing relay started.");
        }
        catch (OperationCanceledException)
        {
            // Visual Studio is shutting down.
        }
#pragma warning disable CA1031 // The relay must never take the package down; the host simply does not index.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            _log.Warning("indexing.relay.start_failed", "Starting the indexing relay failed.", ("error", ex.GetType().Name), ("detail", ex.Message));
        }
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _stopped, 1) != 0)
        {
            return;
        }

        // Stop sending first, then disconnect the COM event sources on the UI thread, where they were advised.
        Interlocked.Exchange(ref _relay, null)?.Dispose();
        if (_disconnect.Count == 0)
        {
            return;
        }

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
            _log.Warning("indexing.relay.dispose_failed", "Disconnecting the indexing relay failed.", ("error", ex.GetType().Name), ("detail", ex.Message));
        }
    }

    private void ConnectDebugger(IVsDebugger? debugger)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        if (debugger is null)
        {
            _log.Warning("indexing.debugger.unavailable", "The debugger service is not available; indexing does not pause while debugging.");
            return;
        }

        if (ErrorHandler.Succeeded(debugger.AdviseDebuggerEvents(_debugger, out var cookie)) && cookie != NoCookie)
        {
            _disconnect.Add("debugger events", () =>
            {
                ThreadHelper.ThrowIfNotOnUIThread();
                _ = debugger.UnadviseDebuggerEvents(cookie);
            });
            _debugger.Seed(debugger);
        }
    }
}

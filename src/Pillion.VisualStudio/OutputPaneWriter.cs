using System;
using System.Collections.Concurrent;
using System.Text;
using System.Threading;
using Microsoft.VisualStudio.Shell.Interop;
using Microsoft.VisualStudio.Threading;
using Pillion.VisualStudio.Core;

namespace Pillion.VisualStudio;

/// <summary>
/// Writes lines to the "Pillion Programmer" Output pane from any thread. Lines are queued and flushed in batches on the
/// UI thread, so background work (the supervisor, the logger) never blocks on or calls into the UI thread directly. The
/// pane itself is created by the first flush, so a package that never writes does not pay for it.
/// </summary>
internal sealed class OutputPaneWriter
{
    private readonly Func<IVsOutputWindowPane?> _createPane;
    private readonly Func<System.Threading.Tasks.Task>? _firstFlushGate;
    private IVsOutputWindowPane? _pane;
    private volatile bool _paneCreated;
    private readonly JoinableTaskFactory _joinableTaskFactory;
    private readonly CancellationToken _disposalToken;
    private readonly ConcurrentQueue<string> _pending = new();
    private int _flushScheduled;

    /// <summary>Initializes a new instance of the <see cref="OutputPaneWriter"/> class.</summary>
    /// <param name="createPane">Creates the Output pane; called on the UI thread, once, by the first flush. May return <see langword="null"/> (the lines are then dropped).</param>
    /// <param name="firstFlushGate">Returns a task that completes when the pane may be created (the package's start-up is over); lines wait in the queue until then. <see langword="null"/> creates it at once.</param>
    /// <param name="joinableTaskFactory">The package's joinable task factory.</param>
    /// <param name="disposalToken">Cancelled when the package is disposed.</param>
    public OutputPaneWriter(Func<IVsOutputWindowPane?> createPane, Func<System.Threading.Tasks.Task>? firstFlushGate, JoinableTaskFactory joinableTaskFactory, CancellationToken disposalToken)
    {
        _createPane = createPane;
        _firstFlushGate = firstFlushGate;
        _joinableTaskFactory = joinableTaskFactory;
        _disposalToken = disposalToken;
    }

    /// <summary>Queues a line for the pane.</summary>
    /// <param name="line">The text, without a trailing newline.</param>
    public void WriteLine(string line)
    {
        _pending.Enqueue(line);
        if (Interlocked.Exchange(ref _flushScheduled, 1) == 0)
        {
            _ = _joinableTaskFactory.RunAsync(FlushAsync);
        }
    }

    private async System.Threading.Tasks.Task FlushAsync()
    {
        try
        {
            if (_firstFlushGate is not null && !_paneCreated)
            {
                // Creating the pane makes Visual Studio initialize the Output window: not while the package is loading.
                // Lines written meanwhile stay queued (the flush stays scheduled), and go out in one batch.
                await System.Threading.Tasks.TaskScheduler.Default;
                await _firstFlushGate().ConfigureAwait(false);
            }

            await _joinableTaskFactory.SwitchToMainThreadAsync(_disposalToken);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        using var timing = UiThreadTimings.Measure("output.flush");

        // Reset before draining: a line queued after this point schedules another flush.
        Volatile.Write(ref _flushScheduled, 0);
        var text = new StringBuilder();
        while (_pending.TryDequeue(out var line))
        {
            text.Append(line).Append(Environment.NewLine);
        }

        if (text.Length == 0)
        {
            return;
        }

        if (!_paneCreated)
        {
            _paneCreated = true;
            _pane = _createPane();
        }

        _ = _pane?.OutputStringThreadSafe(text.ToString());
    }
}

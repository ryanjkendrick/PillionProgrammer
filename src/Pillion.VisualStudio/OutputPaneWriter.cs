using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using Microsoft.VisualStudio.Shell.Interop;
using Microsoft.VisualStudio.Threading;
using Pillion.VisualStudio.Core;

namespace Pillion.VisualStudio;

/// <summary>
/// Writes lines to the "Pillion Programmer" Output pane from any thread. Lines are queued and flushed in batches on the
/// UI thread, so background work (the supervisor, the logger) never blocks on or calls into the UI thread directly. The
/// pane is created only by the first line worth showing (issue #49, decided 2026-10-24): a warning or above, or a notice
/// the user asked for. Routine lines (the loaded line, the log's info lines, the five-minute autocomplete summary) wait
/// in a bounded buffer until then and go out with it, so a session in which nothing goes wrong never makes Visual
/// Studio initialise the Output window for Pillion (22 to 26 ms of UI-thread time on 17.14, right after Ready).
/// </summary>
internal sealed class OutputPaneWriter
{
    /// <summary>The most routine lines kept while the pane does not exist yet; older ones are dropped (the log file has them all).</summary>
    public const int BufferedLines = 500;

    private readonly Func<IVsOutputWindowPane?> _createPane;
    private readonly Func<System.Threading.Tasks.Task>? _firstFlushGate;
    private readonly JoinableTaskFactory _joinableTaskFactory;
    private readonly CancellationToken _disposalToken;
    private readonly object _gate = new();
    private readonly Queue<string> _pending = new();
    private IVsOutputWindowPane? _pane;
    private bool _paneCreated;
    private bool _showPane;
    private bool _flushScheduled;

    /// <summary>Initializes a new instance of the <see cref="OutputPaneWriter"/> class.</summary>
    /// <param name="createPane">Creates the Output pane; called on the UI thread, once, by the first flush that shows it. May return <see langword="null"/> (the lines are then dropped).</param>
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

    /// <summary>Gets a value indicating whether the pane has been created (for tests and Show Status).</summary>
    public bool PaneCreated
    {
        get
        {
            lock (_gate)
            {
                return _paneCreated;
            }
        }
    }

    /// <summary>Queues a line for the pane.</summary>
    /// <param name="line">The text, without a trailing newline.</param>
    /// <param name="showPane">
    /// Whether the line is worth creating the pane for: a warning or above, or a notice the user asked for. With
    /// <see langword="false"/> the line waits in the buffer until something else creates the pane (it is written at once
    /// when the pane already exists).
    /// </param>
    public void WriteLine(string line, bool showPane = true)
    {
        bool schedule;
        lock (_gate)
        {
            _pending.Enqueue(line);
            if (showPane)
            {
                _showPane = true;
            }

            if (!_paneCreated && !_showPane)
            {
                while (_pending.Count > BufferedLines)
                {
                    _pending.Dequeue();
                }

                return; // Nothing to show yet: the pane is not created for a routine line.
            }

            schedule = !_flushScheduled;
            _flushScheduled = true;
        }

        if (schedule)
        {
            _ = _joinableTaskFactory.RunAsync(FlushAsync);
        }
    }

    private async System.Threading.Tasks.Task FlushAsync()
    {
        try
        {
            if (_firstFlushGate is not null && !PaneCreated)
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
        var text = new StringBuilder();
        lock (_gate)
        {
            _flushScheduled = false;
            while (_pending.Count > 0)
            {
                text.Append(_pending.Dequeue()).Append(Environment.NewLine);
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
        }

        _ = _pane?.OutputStringThreadSafe(text.ToString());
    }
}

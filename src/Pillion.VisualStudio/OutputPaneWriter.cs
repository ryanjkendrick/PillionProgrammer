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
/// UI thread, so background work (the supervisor, the logger) never blocks on or calls into the UI thread directly.
/// </summary>
internal sealed class OutputPaneWriter
{
    private readonly IVsOutputWindowPane _pane;
    private readonly JoinableTaskFactory _joinableTaskFactory;
    private readonly CancellationToken _disposalToken;
    private readonly ConcurrentQueue<string> _pending = new();
    private int _flushScheduled;

    /// <summary>Initializes a new instance of the <see cref="OutputPaneWriter"/> class.</summary>
    /// <param name="pane">The Output pane.</param>
    /// <param name="joinableTaskFactory">The package's joinable task factory.</param>
    /// <param name="disposalToken">Cancelled when the package is disposed.</param>
    public OutputPaneWriter(IVsOutputWindowPane pane, JoinableTaskFactory joinableTaskFactory, CancellationToken disposalToken)
    {
        _pane = pane;
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

        if (text.Length > 0)
        {
            _pane.OutputStringThreadSafe(text.ToString());
        }
    }
}

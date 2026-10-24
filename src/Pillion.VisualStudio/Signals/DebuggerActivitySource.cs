using System;
using System.Threading;
using Microsoft.VisualStudio;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using Pillion.Contracts;
using Pillion.VisualStudio.Core;
using Pillion.VisualStudio.Core.Signals;

namespace Pillion.VisualStudio.Signals;

/// <summary>
/// The debugger's part of the IDE activity set (work item 2.9): <see cref="IdeActivity.Debug"/> while the debugger is not in
/// design mode (running or at a break, including Edit and Continue), from <see cref="IVsDebuggerEvents.OnModeChange"/>.
/// Called on the UI thread; it only stores the mode and notifies the relay, which sends from the thread pool.
/// </summary>
internal sealed class DebuggerActivitySource : IIdeActivitySource, IVsDebuggerEvents
{
    private readonly PillionLog? _log;
    private int _current;

    /// <summary>Initializes a new instance of the <see cref="DebuggerActivitySource"/> class.</summary>
    /// <param name="log">The extension log, or <see langword="null"/>.</param>
    public DebuggerActivitySource(PillionLog? log) => _log = log;

    /// <inheritdoc/>
    public event EventHandler? Changed;

    /// <inheritdoc/>
    public IdeActivity Current => (IdeActivity)Volatile.Read(ref _current);

    /// <summary>Reads the debugger's mode now (when the events are connected, a session may already be running).</summary>
    /// <param name="debugger">The shell debugger.</param>
    public void Seed(IVsDebugger debugger)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        var mode = new DBGMODE[1];
        if (ErrorHandler.Succeeded(debugger.GetMode(mode)))
        {
            _ = OnModeChange(mode[0]);
        }
    }

    /// <inheritdoc/>
    public int OnModeChange(DBGMODE dbgmodeNew)
    {
        var activity = (dbgmodeNew & ~DBGMODE.DBGMODE_EncMask) == DBGMODE.DBGMODE_Design ? IdeActivity.None : IdeActivity.Debug;
        if (Interlocked.Exchange(ref _current, (int)activity) == (int)activity)
        {
            return VSConstants.S_OK;
        }

        var handlers = Changed;
        if (handlers is not null)
        {
            foreach (EventHandler handler in handlers.GetInvocationList())
            {
                try
                {
                    handler(this, EventArgs.Empty);
                }
#pragma warning disable CA1031 // Never throw back into the debugger's event source.
                catch (Exception ex)
#pragma warning restore CA1031
                {
                    _log?.Warning("indexing.debugger.handler_failed", "A debugger activity listener threw.", ("error", ex.GetType().Name), ("detail", ex.Message));
                }
            }
        }

        return VSConstants.S_OK;
    }
}

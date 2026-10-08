using Microsoft.Extensions.Logging;
using Pillion.Contracts;

namespace Pillion.Host.Lifecycle;

/// <summary>Describes a change of <see cref="HostState"/>.</summary>
/// <param name="previous">The state before the change.</param>
/// <param name="current">The state after the change.</param>
/// <param name="reason">Why the state changed (for logs).</param>
internal sealed class HostStateChangedEventArgs(HostState previous, HostState current, string reason) : EventArgs
{
    /// <summary>Gets the state before the change.</summary>
    public HostState Previous { get; } = previous;

    /// <summary>Gets the state after the change.</summary>
    public HostState Current { get; } = current;

    /// <summary>Gets why the state changed.</summary>
    public string Reason { get; } = reason;
}

/// <summary>
/// The host's lifecycle state (<see cref="HostState"/>) and the transitions allowed between states. Thread-safe.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="HostState.Starting"/> while the RPC server is being set up, <see cref="HostState.Ready"/> once it is
/// listening, <see cref="HostState.Failed"/> on an unrecoverable start-up error. <see cref="HostState.Degraded"/>
/// and <see cref="HostState.Restarting"/> are for later components (for example a model provider going away).
/// </para>
/// <para>
/// Allowed transitions: Starting to Ready or Failed; Ready to Degraded, Restarting or Failed; Degraded to Ready,
/// Restarting or Failed; Restarting to Starting, Ready or Failed. Failed is terminal. A transition to the current
/// state is a no-op.
/// </para>
/// </remarks>
internal sealed partial class HostStateMachine
{
    private static readonly Dictionary<HostState, HostState[]> AllowedTransitions = new()
    {
        [HostState.Starting] = [HostState.Ready, HostState.Failed],
        [HostState.Ready] = [HostState.Degraded, HostState.Restarting, HostState.Failed],
        [HostState.Degraded] = [HostState.Ready, HostState.Restarting, HostState.Failed],
        [HostState.Restarting] = [HostState.Starting, HostState.Ready, HostState.Failed],
        [HostState.Failed] = [],
    };

    private readonly Lock _lock = new();
    private readonly ILogger<HostStateMachine> _logger;
    private HostState _current = HostState.Starting;

    /// <summary>Initializes a new instance of the <see cref="HostStateMachine"/> class in <see cref="HostState.Starting"/>.</summary>
    /// <param name="logger">The logger.</param>
    public HostStateMachine(ILogger<HostStateMachine> logger)
    {
        _logger = logger;
    }

    /// <summary>
    /// Raised after the state changes, outside the internal lock, on the thread that made the change. Handlers must
    /// not block.
    /// </summary>
    public event EventHandler<HostStateChangedEventArgs>? StateChanged;

    /// <summary>Gets the current state.</summary>
    public HostState Current
    {
        get
        {
            lock (_lock)
            {
                return _current;
            }
        }
    }

    /// <summary>Determines whether the state machine allows a transition.</summary>
    /// <param name="from">The current state.</param>
    /// <param name="to">The requested state.</param>
    /// <returns><see langword="true"/> when the transition is allowed or <paramref name="from"/> equals <paramref name="to"/>.</returns>
    public static bool IsAllowed(HostState from, HostState to) =>
        from == to || (AllowedTransitions.TryGetValue(from, out var targets) && Array.IndexOf(targets, to) >= 0);

    /// <summary>Moves to <paramref name="next"/> when the transition is allowed.</summary>
    /// <param name="next">The requested state.</param>
    /// <param name="reason">Why the state is changing (logged).</param>
    /// <returns><see langword="true"/> when the state is now <paramref name="next"/>; otherwise <see langword="false"/>.</returns>
    public bool TryTransition(HostState next, string reason)
    {
        HostState previous;
        lock (_lock)
        {
            previous = _current;
            if (previous == next)
            {
                return true;
            }

            if (!IsAllowed(previous, next))
            {
                LogTransitionRejected(_logger, previous, next, reason);
                return false;
            }

            _current = next;
        }

        LogTransition(_logger, previous, next, reason);
        StateChanged?.Invoke(this, new HostStateChangedEventArgs(previous, next, reason));
        return true;
    }

    [LoggerMessage(EventId = 100, Level = LogLevel.Information, Message = "Host state {Previous} -> {Current}: {Reason}")]
    private static partial void LogTransition(ILogger logger, HostState previous, HostState current, string reason);

    [LoggerMessage(EventId = 101, Level = LogLevel.Warning, Message = "Host state transition {Previous} -> {Requested} is not allowed ({Reason}).")]
    private static partial void LogTransitionRejected(ILogger logger, HostState previous, HostState requested, string reason);
}

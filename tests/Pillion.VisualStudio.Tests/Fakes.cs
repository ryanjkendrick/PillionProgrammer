using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Pillion.Contracts;
using Pillion.VisualStudio.Core;

namespace Pillion.VisualStudio.Tests;

/// <summary>A clock whose delays complete at once and move time forward.</summary>
internal sealed class FakeClock : ISupervisorClock
{
    private readonly object _gate = new();
    private readonly List<TimeSpan> _delays = [];
    private DateTimeOffset _now = new(2026, 10, 8, 12, 0, 0, TimeSpan.Zero);

    public DateTimeOffset UtcNow
    {
        get
        {
            lock (_gate)
            {
                return _now;
            }
        }
    }

    public IReadOnlyList<TimeSpan> Delays
    {
        get
        {
            lock (_gate)
            {
                return [.. _delays];
            }
        }
    }

    public void Advance(TimeSpan by)
    {
        lock (_gate)
        {
            _now += by;
        }
    }

    public Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            _delays.Add(delay);
            _now += delay;
        }

        return Task.CompletedTask;
    }
}

/// <summary>A launcher that hands out <see cref="FakeSession"/>s or fails as configured.</summary>
internal sealed class FakeLauncher : IHostLauncher
{
    private readonly object _gate = new();
    private readonly List<FakeSession> _sessions = [];
    private int _launches;

    /// <summary>Gets or sets what the next launch does; by default it returns a session in the given state.</summary>
    public Func<int, CancellationToken, Task<IHostSession>>? Behaviour { get; set; }

    public HostState InitialState { get; set; } = HostState.Ready;

    public int Launches => Volatile.Read(ref _launches);

    public IReadOnlyList<FakeSession> Sessions
    {
        get
        {
            lock (_gate)
            {
                return [.. _sessions];
            }
        }
    }

    public FakeSession LastSession
    {
        get
        {
            lock (_gate)
            {
                return _sessions[_sessions.Count - 1];
            }
        }
    }

    public Task<IHostSession> LaunchAsync(CancellationToken cancellationToken)
    {
        var launch = Interlocked.Increment(ref _launches);
        if (Behaviour is { } behaviour)
        {
            return behaviour(launch, cancellationToken);
        }

        return Task.FromResult<IHostSession>(CreateSession(launch));
    }

    public FakeSession CreateSession(int launch)
    {
        var session = new FakeSession(1000 + launch, InitialState);
        lock (_gate)
        {
            _sessions.Add(session);
        }

        return session;
    }
}

/// <summary>A session whose crashes and host states are driven by the test.</summary>
internal sealed class FakeSession(int processId, HostState initialState) : IHostSession
{
    private readonly TaskCompletionSource<string> _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private volatile HostState _latest = initialState;

    public event EventHandler<HostState>? HostStateChanged;

    public int ProcessId { get; } = processId;

    public HostState LatestHostState => _latest;

    public Task<string> Completion => _completion.Task;

    public bool Disposed { get; private set; }

    public TimeSpan? ShutdownTimeout { get; private set; }

    public void Report(HostState state)
    {
        _latest = state;
        HostStateChanged?.Invoke(this, state);
    }

    public void Crash(string description = "host process exited with code 1") => _completion.TrySetResult(description);

    public T GetProxy<T>()
        where T : class
    {
        if (Disposed)
        {
            throw new ObjectDisposedException(nameof(FakeSession));
        }

        return typeof(T) == typeof(IHostLifecycleService)
            ? (T)(object)new FakeLifecycleProxy(this)
            : throw new NotSupportedException(typeof(T).Name);
    }

    public Task ShutdownAsync(TimeSpan timeout)
    {
        ShutdownTimeout = timeout;
        _completion.TrySetResult("shut down");
        return Task.CompletedTask;
    }

    public void Dispose()
    {
        Disposed = true;
        _completion.TrySetResult("disposed");
    }

    private sealed class FakeLifecycleProxy(FakeSession session) : IHostLifecycleService
    {
        public Task<HandshakeResponse> HandshakeAsync(HandshakeRequest request, CancellationToken cancellationToken) =>
            Task.FromResult(new HandshakeResponse { Accepted = true });

        public Task<HostState> GetStateAsync(CancellationToken cancellationToken) => Task.FromResult(session.LatestHostState);

        public Task ShutdownAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }
}

internal static class Wait
{
    public static async Task UntilAsync(Func<bool> condition, string what, int timeoutMs = 10000)
    {
        var stopwatch = Stopwatch.StartNew();
        while (!condition())
        {
            if (stopwatch.ElapsedMilliseconds > timeoutMs)
            {
                throw new TimeoutException("Timed out waiting until " + what + ".");
            }

            await Task.Delay(10);
        }
    }
}

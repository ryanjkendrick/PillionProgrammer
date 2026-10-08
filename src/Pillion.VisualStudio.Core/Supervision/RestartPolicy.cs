using System;
using System.Collections.Generic;

namespace Pillion.VisualStudio.Core;

/// <summary>What the supervisor should do after the host crashed or disconnected.</summary>
public readonly struct RestartDecision
{
    private RestartDecision(bool giveUp, TimeSpan delay, int crashesInWindow)
    {
        GiveUp = giveUp;
        Delay = delay;
        CrashesInWindow = crashesInWindow;
    }

    /// <summary>Gets a value indicating whether the supervisor should stop restarting and enter Failed.</summary>
    public bool GiveUp { get; }

    /// <summary>Gets how long to wait before relaunching, when <see cref="GiveUp"/> is false.</summary>
    public TimeSpan Delay { get; }

    /// <summary>Gets the number of crashes inside the window, including this one.</summary>
    public int CrashesInWindow { get; }

    /// <summary>Creates a decision to relaunch after a delay.</summary>
    /// <param name="delay">The backoff delay.</param>
    /// <param name="crashesInWindow">The crashes in the window, including this one.</param>
    /// <returns>The decision.</returns>
    public static RestartDecision Restart(TimeSpan delay, int crashesInWindow) => new(giveUp: false, delay, crashesInWindow);

    /// <summary>Creates a decision to give up.</summary>
    /// <param name="crashesInWindow">The crashes in the window, including this one.</param>
    /// <returns>The decision.</returns>
    public static RestartDecision Stop(int crashesInWindow) => new(giveUp: true, TimeSpan.Zero, crashesInWindow);
}

/// <summary>
/// Restart backoff for Pillion Host (design doc section 4, "Failure modes").
/// </summary>
/// <remarks>
/// <para>
/// Each crash is recorded with its time. When <see cref="MaxCrashes"/> crashes (three) fall inside a sliding
/// <see cref="Window"/> (five minutes), the supervisor gives up and enters Failed.
/// </para>
/// <para>
/// Otherwise it relaunches after a backoff of 1 s, 5 s, then 30 s for every further consecutive crash. A crash is
/// consecutive when it comes within <see cref="Window"/> of the previous one; a host that stays up longer than that
/// starts again at 1 s. So the 30 s step is reached only by crashes spaced just far enough apart to stay below the
/// three-in-five-minutes limit. <see cref="Reset"/> (the Retry command) forgets all crashes.
/// </para>
/// </remarks>
public sealed class RestartPolicy
{
    private static readonly TimeSpan[] DefaultBackoff = [TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(30)];

    private readonly Queue<DateTimeOffset> _crashes = new();
    private readonly TimeSpan[] _backoff;
    private DateTimeOffset? _lastCrash;
    private int _consecutive;

    /// <summary>Initializes a new instance of the <see cref="RestartPolicy"/> class with the default schedule.</summary>
    public RestartPolicy()
        : this(DefaultBackoff, maxCrashes: 3, TimeSpan.FromMinutes(5))
    {
    }

    /// <summary>Initializes a new instance of the <see cref="RestartPolicy"/> class.</summary>
    /// <param name="backoff">The delays for the first, second, ... consecutive crash; the last one repeats.</param>
    /// <param name="maxCrashes">The number of crashes inside <paramref name="window"/> that makes the supervisor give up.</param>
    /// <param name="window">The sliding crash window.</param>
    public RestartPolicy(IReadOnlyList<TimeSpan> backoff, int maxCrashes, TimeSpan window)
    {
        if (backoff is null || backoff.Count == 0)
        {
            throw new ArgumentException("At least one backoff delay is required.", nameof(backoff));
        }

        if (maxCrashes < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(maxCrashes));
        }

        _backoff = [.. backoff];
        MaxCrashes = maxCrashes;
        Window = window;
    }

    /// <summary>Gets the number of crashes inside <see cref="Window"/> that makes the supervisor give up.</summary>
    public int MaxCrashes { get; }

    /// <summary>Gets the sliding crash window.</summary>
    public TimeSpan Window { get; }

    /// <summary>Records a crash and decides what to do next.</summary>
    /// <param name="now">The time of the crash.</param>
    /// <returns>The decision.</returns>
    public RestartDecision RecordCrash(DateTimeOffset now)
    {
        while (_crashes.Count > 0 && now - _crashes.Peek() >= Window)
        {
            _crashes.Dequeue();
        }

        _crashes.Enqueue(now);

        _consecutive = _lastCrash is { } last && now - last < Window ? _consecutive + 1 : 1;
        _lastCrash = now;

        if (_crashes.Count >= MaxCrashes)
        {
            return RestartDecision.Stop(_crashes.Count);
        }

        var delay = _backoff[Math.Min(_consecutive, _backoff.Length) - 1];
        return RestartDecision.Restart(delay, _crashes.Count);
    }

    /// <summary>Forgets every recorded crash (used by the Retry command).</summary>
    public void Reset()
    {
        _crashes.Clear();
        _lastCrash = null;
        _consecutive = 0;
    }
}

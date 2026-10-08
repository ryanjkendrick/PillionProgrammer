using System;
using System.Threading;
using System.Threading.Tasks;

namespace Pillion.VisualStudio.Core;

/// <summary>Time source for the supervisor, replaceable in tests.</summary>
public interface ISupervisorClock
{
    /// <summary>Gets the current UTC time.</summary>
    DateTimeOffset UtcNow { get; }

    /// <summary>Waits for the given time.</summary>
    /// <param name="delay">How long to wait.</param>
    /// <param name="cancellationToken">A token that cancels the wait.</param>
    /// <returns>A task that completes after <paramref name="delay"/>.</returns>
    Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken);
}

/// <summary>The real clock.</summary>
public sealed class SystemClock : ISupervisorClock
{
    /// <summary>Gets the shared instance.</summary>
    public static SystemClock Instance { get; } = new();

    /// <inheritdoc/>
    public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;

    /// <inheritdoc/>
    public Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken) => Task.Delay(delay, cancellationToken);
}

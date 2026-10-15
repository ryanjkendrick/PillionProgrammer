using System.Collections.Concurrent;
using Pillion.Contracts;

namespace Pillion.ModelGateway;

/// <summary>The concurrency lane a task class uses (section 6, Cost and rate governance).</summary>
public enum ConcurrencyLane
{
    /// <summary>Autocomplete: one request in flight per provider.</summary>
    Autocomplete,

    /// <summary>Chat, inline edit, vision and summarise: two in flight per provider.</summary>
    Chat,

    /// <summary>Agent: one in flight per provider.</summary>
    Agent,

    /// <summary>Embedding: one batch in flight per provider, so that indexing cannot crowd out chat on the same server.</summary>
    Embedding,
}

/// <summary>
/// Limits requests in flight per provider and lane with <see cref="SemaphoreSlim"/>s, so a 429 storm from autocomplete
/// cannot starve chat. Thread-safe.
/// </summary>
/// <remarks>The semaphores are never disposed: a lease may outlive the gateway during shutdown, and a SemaphoreSlim whose wait handle is never used holds no unmanaged resources.</remarks>
public sealed class ConcurrencyLimiter
{
    private readonly ConcurrentDictionary<(string ProviderId, ConcurrencyLane Lane), SemaphoreSlim> _semaphores = new();
    private readonly ModelGatewayOptions _options;

    /// <summary>Initializes a new instance of the <see cref="ConcurrencyLimiter"/> class.</summary>
    /// <param name="options">The gateway options with the per-lane limits.</param>
    public ConcurrencyLimiter(ModelGatewayOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        _options = options;
    }

    /// <summary>Gets the lane for a task class.</summary>
    /// <param name="taskClass">The task class.</param>
    /// <returns>The lane.</returns>
    public static ConcurrencyLane LaneFor(TaskClass taskClass) => taskClass switch
    {
        TaskClass.Autocomplete => ConcurrencyLane.Autocomplete,
        TaskClass.Agent => ConcurrencyLane.Agent,
        TaskClass.Embedding => ConcurrencyLane.Embedding,
        _ => ConcurrencyLane.Chat,
    };

    /// <summary>Gets the limit for a lane.</summary>
    /// <param name="lane">The lane.</param>
    /// <returns>The number of requests allowed in flight per provider.</returns>
    public int LimitFor(ConcurrencyLane lane) => lane switch
    {
        ConcurrencyLane.Autocomplete => _options.AutocompleteConcurrency,
        ConcurrencyLane.Agent => _options.AgentConcurrency,
        ConcurrencyLane.Embedding => _options.EmbeddingConcurrency,
        _ => _options.ChatConcurrency,
    };

    /// <summary>Waits for a slot.</summary>
    /// <param name="providerId">The provider id.</param>
    /// <param name="lane">The lane.</param>
    /// <param name="cancellationToken">A token that cancels the wait.</param>
    /// <returns>A lease that releases the slot when disposed.</returns>
    public async ValueTask<IDisposable> AcquireAsync(string providerId, ConcurrencyLane lane, CancellationToken cancellationToken)
    {
        var semaphore = _semaphores.GetOrAdd((providerId.ToUpperInvariant(), lane), key => new SemaphoreSlim(LimitFor(key.Lane), LimitFor(key.Lane)));
        await semaphore.WaitAsync(cancellationToken).ConfigureAwait(false);
        return new Lease(semaphore);
    }

    /// <summary>Gets the number of free slots (for tests and diagnostics).</summary>
    /// <param name="providerId">The provider id.</param>
    /// <param name="lane">The lane.</param>
    /// <returns>The free slots.</returns>
    public int Available(string providerId, ConcurrencyLane lane) =>
        _semaphores.TryGetValue((providerId.ToUpperInvariant(), lane), out var semaphore) ? semaphore.CurrentCount : LimitFor(lane);

    private sealed class Lease(SemaphoreSlim semaphore) : IDisposable
    {
        private SemaphoreSlim? _semaphore = semaphore;

        public void Dispose() => Interlocked.Exchange(ref _semaphore, null)?.Release();
    }
}

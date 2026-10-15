using Microsoft.Extensions.Logging;
using Pillion.Contracts;

namespace Pillion.Host.Indexing;

/// <summary>
/// The reasons indexing is paused (design sections 7 and 13): the user, the memory cap, a build, a test run, the debugger
/// and battery saver. Any one of them pauses; the pipeline waits at its next checkpoint (before each file and each
/// embedding batch) until none is left. Thread-safe.
/// </summary>
internal sealed class PauseGate
{
    /// <summary>The order reasons are reported in when several hold: the user's own choice first.</summary>
    private static readonly IndexPauseReason[] Priority =
    [
        IndexPauseReason.User, IndexPauseReason.Memory, IndexPauseReason.Build, IndexPauseReason.Test, IndexPauseReason.Debug,
        IndexPauseReason.Battery, IndexPauseReason.LowDisk,
    ];

    private readonly Lock _gate = new();
    private readonly HashSet<IndexPauseReason> _reasons = [];
    private TaskCompletionSource _resumed = NewResumed(completed: true);

    /// <summary>Raised after the set of reasons changes (outside the lock).</summary>
    public event EventHandler? Changed;

    /// <summary>Gets a value indicating whether any reason holds.</summary>
    public bool IsPaused
    {
        get
        {
            lock (_gate)
            {
                return _reasons.Count > 0;
            }
        }
    }

    /// <summary>Gets the reason reported for the pause, or <see cref="IndexPauseReason.None"/>.</summary>
    public IndexPauseReason Current
    {
        get
        {
            lock (_gate)
            {
                foreach (var reason in Priority)
                {
                    if (_reasons.Contains(reason))
                    {
                        return reason;
                    }
                }

                return IndexPauseReason.None;
            }
        }
    }

    /// <summary>Gets whether one reason holds.</summary>
    /// <param name="reason">The reason.</param>
    /// <returns><see langword="true"/> when it holds.</returns>
    public bool Has(IndexPauseReason reason)
    {
        lock (_gate)
        {
            return _reasons.Contains(reason);
        }
    }

    /// <summary>Adds or removes one reason.</summary>
    /// <param name="reason">The reason.</param>
    /// <param name="on">Whether it holds now.</param>
    public void Set(IndexPauseReason reason, bool on)
    {
        if (reason == IndexPauseReason.None)
        {
            return;
        }

        TaskCompletionSource? release = null;
        lock (_gate)
        {
            var changed = on ? _reasons.Add(reason) : _reasons.Remove(reason);
            if (!changed)
            {
                return;
            }

            if (_reasons.Count == 0)
            {
                release = _resumed;
            }
            else if (_resumed.Task.IsCompleted)
            {
                _resumed = NewResumed(completed: false);
            }
        }

        release?.TrySetResult();
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Waits until no reason holds; completes at once when none does.</summary>
    /// <param name="cancellationToken">Cancels the wait.</param>
    /// <returns>A task that completes when indexing may continue.</returns>
    public Task WaitWhilePausedAsync(CancellationToken cancellationToken)
    {
        Task resumed;
        lock (_gate)
        {
            resumed = _resumed.Task;
        }

        return resumed.IsCompleted ? Task.CompletedTask : resumed.WaitAsync(cancellationToken);
    }

    private static TaskCompletionSource NewResumed(bool completed)
    {
        var source = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        if (completed)
        {
            source.SetResult();
        }

        return source;
    }
}

/// <summary>
/// Keeps the host under its memory cap while indexing (design section 13: <c>indexing.maxMemoryMb</c>, 1,536 MB by default;
/// under pressure the host drops caches, halves the embedding batch, then pauses indexing, in that order). Sampled before
/// every embedding batch; while paused for memory it samples on a timer and resumes below <see cref="IndexingOptions.MemoryResumeFraction"/>
/// of the cap.
/// </summary>
internal sealed partial class MemoryGovernor : IDisposable
{
    private readonly IMemorySampler _sampler;
    private readonly PauseGate _pause;
    private readonly Func<long> _capBytes;
    private readonly IndexingOptions _options;
    private readonly TimeProvider _time;
    private readonly ILogger _logger;
    private readonly Lock _gate = new();
    private ITimer? _poll;
    private int _batchSize;
    private bool _collected;

    /// <summary>Initializes a new instance of the <see cref="MemoryGovernor"/> class.</summary>
    /// <param name="sampler">Reads the working set.</param>
    /// <param name="pause">The pause reasons; the governor owns <see cref="IndexPauseReason.Memory"/>.</param>
    /// <param name="capBytes">Reads the cap (the setting may change while running).</param>
    /// <param name="options">The batch sizes and thresholds.</param>
    /// <param name="time">The clock for the polling timer.</param>
    /// <param name="logger">The logger.</param>
    public MemoryGovernor(IMemorySampler sampler, PauseGate pause, Func<long> capBytes, IndexingOptions options, TimeProvider time, ILogger logger)
    {
        _sampler = sampler;
        _pause = pause;
        _capBytes = capBytes;
        _options = options;
        _time = time;
        _logger = logger;
        _batchSize = Math.Max(1, options.EmbeddingBatchSize);
    }

    /// <summary>Gets the embedding batch size to use now.</summary>
    public int BatchSize => Volatile.Read(ref _batchSize);

    /// <summary>Checks the working set before an embedding batch: halves the batch or pauses when over the cap, restores it when well under.</summary>
    public void BeforeBatch()
    {
        var cap = _capBytes();
        if (cap <= 0)
        {
            return;
        }

        var used = _sampler.WorkingSetBytes();
        lock (_gate)
        {
            if (used > cap && !_collected)
            {
                // First step: drop what the runtime can give back (caches, garbage), then look again.
                _collected = true;
                GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: true);
                used = _sampler.WorkingSetBytes();
            }

            if (used > cap)
            {
                if (_batchSize > _options.MinimumEmbeddingBatchSize)
                {
                    _batchSize = Math.Max(_options.MinimumEmbeddingBatchSize, _batchSize / 2);
                    LogHalved(_logger, used / 1048576, cap / 1048576, _batchSize);
                    return;
                }

                if (!_pause.Has(IndexPauseReason.Memory))
                {
                    LogPaused(_logger, used / 1048576, cap / 1048576);
                    _pause.Set(IndexPauseReason.Memory, true);
                    _poll ??= _time.CreateTimer(_ => Poll(), null, _options.MemoryPollInterval, _options.MemoryPollInterval);
                }

                return;
            }

            if (used < cap * _options.MemoryRecoverFraction)
            {
                _collected = false;
                if (_batchSize < _options.EmbeddingBatchSize)
                {
                    _batchSize = Math.Min(_options.EmbeddingBatchSize, _batchSize * 2);
                }
            }
        }
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        lock (_gate)
        {
            _poll?.Dispose();
            _poll = null;
        }
    }

    private void Poll()
    {
        var cap = _capBytes();
        var used = _sampler.WorkingSetBytes();
        if (cap > 0 && used >= cap * _options.MemoryResumeFraction)
        {
            return;
        }

        lock (_gate)
        {
            _poll?.Dispose();
            _poll = null;
            _collected = false;
        }

        LogResumed(_logger, used / 1048576);
        _pause.Set(IndexPauseReason.Memory, false);
    }

    [LoggerMessage(EventId = 2950, Level = LogLevel.Information, Message = "The host uses {UsedMb} MB of its {CapMb} MB indexing cap; the embedding batch is halved to {BatchSize}.")]
    private static partial void LogHalved(ILogger logger, long usedMb, long capMb, int batchSize);

    [LoggerMessage(EventId = 2951, Level = LogLevel.Warning, Message = "The host uses {UsedMb} MB of its {CapMb} MB indexing cap even with the smallest batch; indexing pauses until memory is freed.")]
    private static partial void LogPaused(ILogger logger, long usedMb, long capMb);

    [LoggerMessage(EventId = 2952, Level = LogLevel.Information, Message = "The host is back to {UsedMb} MB; indexing resumes.")]
    private static partial void LogResumed(ILogger logger, long usedMb);
}

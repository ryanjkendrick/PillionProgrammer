using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Pillion.Contracts;
using Pillion.VisualStudio.Core;

namespace Pillion.VisualStudio.Tests;

/// <summary>A clock whose delays wait until the test moves time past their deadline.</summary>
internal sealed class ManualClock : ISupervisorClock
{
    private readonly object _gate = new();
    private readonly List<(DateTimeOffset Due, TaskCompletionSource<bool> Done, TimeSpan Delay)> _waiters = [];
    private DateTimeOffset _now = new(2026, 10, 9, 9, 0, 0, TimeSpan.Zero);

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

    /// <summary>Gets the delays currently waiting.</summary>
    public IReadOnlyList<TimeSpan> Pending
    {
        get
        {
            lock (_gate)
            {
                return [.. _waiters.Select(w => w.Delay)];
            }
        }
    }

    public Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var done = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_gate)
        {
            if (delay <= TimeSpan.Zero)
            {
                return Task.CompletedTask;
            }

            _waiters.Add((_now + delay, done, delay));
        }

        var registration = cancellationToken.Register(() =>
        {
            lock (_gate)
            {
                _waiters.RemoveAll(w => w.Done == done);
            }

            done.TrySetCanceled(cancellationToken);
        });
        _ = done.Task.ContinueWith(_ => registration.Dispose(), TaskScheduler.Default);
        return done.Task;
    }

    public void Advance(TimeSpan by)
    {
        List<TaskCompletionSource<bool>> due;
        lock (_gate)
        {
            _now += by;
            due = [.. _waiters.Where(w => w.Due <= _now).Select(w => w.Done)];
            _waiters.RemoveAll(w => w.Due <= _now);
        }

        foreach (var waiter in due)
        {
            waiter.TrySetResult(true);
        }
    }

    /// <summary>Waits until something is waiting on the clock (the pipeline reached its debounce).</summary>
    public Task WhenWaitingAsync(TimeSpan? delay = null) =>
        Wait.UntilAsync(() => Pending.Any(d => delay is null || d == delay.Value), "a delay of " + (delay?.TotalMilliseconds.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "any") + " ms is pending");
}

/// <summary>
/// An <see cref="IModelService"/> whose completion streams are scripted by the test: each request gets the chunks
/// from <see cref="Respond"/>, released one at a time by <see cref="Release"/> (or all at once when automatic).
/// </summary>
#pragma warning disable CA1001 // Lives as long as the test; the semaphore holds no unmanaged state once unused (as ChatScript).
internal sealed class FakeCompletionModel : IModelService
#pragma warning restore CA1001
{
    private readonly ConcurrentQueue<CompletionRequest> _requests = new();
    private readonly ConcurrentQueue<string> _cancelled = new();
    private readonly SemaphoreSlim _released = new(0);
    private int _waitsForRelease;

    /// <summary>Gets or sets the chunks each request streams (text pieces; a final chunk is added).</summary>
    public Func<CompletionRequest, IReadOnlyList<string>> Respond { get; set; } = _ => ["42;"];

    /// <summary>Gets or sets a value indicating whether chunks wait for <see cref="Release"/>.</summary>
    public bool Manual { get; set; }

    /// <summary>Gets or sets a delay before each chunk (real time), used by the keystroke measurement.</summary>
    public TimeSpan ChunkDelay { get; set; }

    /// <summary>Gets or sets an exception thrown instead of streaming.</summary>
    public Exception? Failure { get; set; }

    /// <summary>Gets or sets a request id put on the first chunk instead of the request's (a stale chunk).</summary>
    public string? ForeignChunkId { get; set; }

    public IReadOnlyList<CompletionRequest> Requests => [.. _requests];

    public IReadOnlyList<string> Cancelled => [.. _cancelled];

    public int StreamsCancelled;

    public void Release(int chunks = 1) => _released.Release(chunks);

    /// <summary>
    /// Gets how many times a stream has started waiting for <see cref="Release"/> (manual mode). A stream asks for its next
    /// chunk only after the consumer has handled the previous one, so a test that released chunk n waits for this to reach
    /// n + 1 instead of sleeping (a signalled wait, not a timed one).
    /// </summary>
    public int WaitsForRelease => Volatile.Read(ref _waitsForRelease);

    public async IAsyncEnumerable<CompletionChunk> CompleteAsync(CompletionRequest request, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        _requests.Enqueue(request);
        if (Failure is { } failure)
        {
            throw failure;
        }

        var first = true;
        var finished = false;
        try
        {
            foreach (var piece in Respond(request))
            {
                if (Manual)
                {
                    Interlocked.Increment(ref _waitsForRelease);
                    await _released.WaitAsync(cancellationToken).ConfigureAwait(false);
                }
                else if (ChunkDelay > TimeSpan.Zero)
                {
                    await Task.Delay(ChunkDelay, cancellationToken).ConfigureAwait(false);
                }

                yield return new CompletionChunk
                {
                    RequestId = first && ForeignChunkId is not null ? ForeignChunkId : request.RequestId,
                    Text = piece,
                    ModelId = "fake-coder",
                    ProviderId = "fake",
                };
                first = false;
            }

            finished = true;
            yield return new CompletionChunk { RequestId = request.RequestId, IsFinal = true, ModelId = "fake-coder", ProviderId = "fake" };
        }
        finally
        {
            if (!finished)
            {
                // Cancelled while waiting, or the client stopped enumerating early (disposed the stream).
                Interlocked.Increment(ref StreamsCancelled);
            }
        }
    }

    public Task CancelAsync(string requestId, CancellationToken cancellationToken)
    {
        _cancelled.Enqueue(requestId);
        return Task.CompletedTask;
    }

    public IAsyncEnumerable<ChatChunk> ChatAsync(ChatRequest request, CancellationToken cancellationToken) => throw new NotSupportedException();

    public Task<ModelDescriptor[]> ListModelsAsync(CancellationToken cancellationToken) => Task.FromResult(Array.Empty<ModelDescriptor>());
}

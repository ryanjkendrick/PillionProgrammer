using System.Collections.Concurrent;

namespace Pillion.Host.Indexing;

/// <summary>
/// A task scheduler with a fixed number of dedicated background threads at <see cref="ThreadPriority.BelowNormal"/> (design
/// section 13: background indexing runs at below-normal priority on at most half the cores). The pipeline's CPU work
/// (hashing, reading, chunking, symbol extraction, the symbol graph and the database writes) runs here, so autocomplete
/// and chat requests on the thread pool always win.
/// </summary>
/// <remarks>
/// ONNX Runtime's own intra-op threads, which run the bundled embedding model, are created by ONNX Runtime at normal
/// priority; the bundled runtime caps them at half the logical processors (see the Model Gateway README).
/// </remarks>
internal sealed class PipelineScheduler : TaskScheduler, IDisposable
{
    private readonly BlockingCollection<Task> _queue = [];
    private readonly Thread[] _threads;
    private readonly ThreadLocal<bool> _isPipelineThread = new();

    /// <summary>Initializes a new instance of the <see cref="PipelineScheduler"/> class and starts its threads.</summary>
    /// <param name="threads">The number of threads (at least one).</param>
    /// <param name="priority">Their priority.</param>
    public PipelineScheduler(int threads, ThreadPriority priority = ThreadPriority.BelowNormal)
    {
        _threads = new Thread[Math.Max(1, threads)];
        for (var i = 0; i < _threads.Length; i++)
        {
            var thread = new Thread(Work)
            {
                IsBackground = true,
                Priority = priority,
                Name = $"Pillion indexing {i + 1}",
            };
            _threads[i] = thread;
            thread.Start();
        }
    }

    /// <inheritdoc/>
    public override int MaximumConcurrencyLevel => _threads.Length;

    /// <summary>Runs a function on the pipeline threads.</summary>
    /// <typeparam name="T">The result type.</typeparam>
    /// <param name="work">The function.</param>
    /// <param name="cancellationToken">Cancels it before it starts.</param>
    /// <returns>Its result.</returns>
    public Task<T> Run<T>(Func<T> work, CancellationToken cancellationToken) =>
        Task.Factory.StartNew(work, cancellationToken, TaskCreationOptions.DenyChildAttach, this);

    /// <summary>Runs an asynchronous function whose synchronous parts start on the pipeline threads.</summary>
    /// <typeparam name="T">The result type.</typeparam>
    /// <param name="work">The function.</param>
    /// <param name="cancellationToken">Cancels it before it starts.</param>
    /// <returns>Its result.</returns>
    public Task<T> RunAsync<T>(Func<Task<T>> work, CancellationToken cancellationToken) =>
        Task.Factory.StartNew(work, cancellationToken, TaskCreationOptions.DenyChildAttach, this).Unwrap();

    /// <inheritdoc/>
    public void Dispose()
    {
        _queue.CompleteAdding();
        foreach (var thread in _threads)
        {
            if (thread != Thread.CurrentThread)
            {
                thread.Join(TimeSpan.FromSeconds(5));
            }
        }

        _queue.Dispose();
        _isPipelineThread.Dispose();
    }

    /// <inheritdoc/>
    protected override void QueueTask(Task task)
    {
        try
        {
            _queue.Add(task);
        }
        catch (InvalidOperationException)
        {
            // Disposed: run it anywhere rather than lose it (only during shutdown).
            ThreadPool.UnsafeQueueUserWorkItem(_ => TryExecuteTask(task), null);
        }
    }

    /// <inheritdoc/>
    protected override bool TryExecuteTaskInline(Task task, bool taskWasPreviouslyQueued) =>
        _isPipelineThread.Value && TryExecuteTask(task);

    /// <inheritdoc/>
    protected override IEnumerable<Task> GetScheduledTasks() => _queue.ToArray();

    private void Work()
    {
        _isPipelineThread.Value = true;
        try
        {
            foreach (var task in _queue.GetConsumingEnumerable())
            {
                TryExecuteTask(task);
            }
        }
        catch (ObjectDisposedException)
        {
            // Shutting down.
        }
    }
}

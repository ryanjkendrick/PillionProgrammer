namespace Pillion.ContextEngine.Retrieval;

/// <summary>
/// Keeps recent query vectors for a short time, so a query repeated within the window (search as you type settling, a
/// chat turn retrieving after a search, several indexes built with the same model) is embedded once. Keyed by the text,
/// the model id and the vector size. Thread-safe; holds vectors only, in memory.
/// </summary>
public sealed class QueryVectorCache
{
    private readonly Dictionary<(string Text, string Model, int Dimensions), Entry> _entries = [];
    private readonly Lock _gate = new();
    private readonly TimeProvider _time;
    private readonly TimeSpan _lifetime;
    private readonly int _capacity;

    /// <summary>Initializes a new instance of the <see cref="QueryVectorCache"/> class.</summary>
    /// <param name="lifetime">How long an entry is reused; zero or less disables the cache.</param>
    /// <param name="capacity">The most entries kept; the oldest goes first.</param>
    /// <param name="time">The clock.</param>
    public QueryVectorCache(TimeSpan lifetime, int capacity, TimeProvider? time = null)
    {
        _lifetime = lifetime;
        _capacity = Math.Max(1, capacity);
        _time = time ?? TimeProvider.System;
    }

    /// <summary>Gets the number of entries (expired ones included until they are next pruned).</summary>
    public int Count
    {
        get
        {
            lock (_gate)
            {
                return _entries.Count;
            }
        }
    }

    /// <summary>Gets a cached vector.</summary>
    /// <param name="text">The query text.</param>
    /// <param name="modelId">The model id (empty when routing chose).</param>
    /// <param name="dimensions">The vector size (0 when unknown).</param>
    /// <param name="vector">Receives the vector.</param>
    /// <returns><see langword="true"/> when a fresh entry exists.</returns>
    public bool TryGet(string text, string modelId, int dimensions, out QueryEmbedding vector)
    {
        vector = null!;
        if (_lifetime <= TimeSpan.Zero)
        {
            return false;
        }

        lock (_gate)
        {
            if (_entries.TryGetValue((text, modelId, dimensions), out var entry) && entry.Expires > _time.GetUtcNow())
            {
                vector = entry.Vector;
                return true;
            }
        }

        return false;
    }

    /// <summary>Stores a vector.</summary>
    /// <param name="text">The query text.</param>
    /// <param name="modelId">The model id.</param>
    /// <param name="dimensions">The vector size.</param>
    /// <param name="vector">The vector.</param>
    public void Set(string text, string modelId, int dimensions, QueryEmbedding vector)
    {
        ArgumentNullException.ThrowIfNull(vector);
        if (_lifetime <= TimeSpan.Zero)
        {
            return;
        }

        var now = _time.GetUtcNow();
        lock (_gate)
        {
            _entries[(text, modelId, dimensions)] = new Entry(vector, now + _lifetime);
            if (_entries.Count <= _capacity)
            {
                return;
            }

            foreach (var expired in _entries.Where(e => e.Value.Expires <= now).Select(e => e.Key).ToList())
            {
                _entries.Remove(expired);
            }

            while (_entries.Count > _capacity)
            {
                _entries.Remove(_entries.MinBy(e => e.Value.Expires).Key);
            }
        }
    }

    private sealed record Entry(QueryEmbedding Vector, DateTimeOffset Expires);
}

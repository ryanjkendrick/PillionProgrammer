using System.Security.Cryptography;
using System.Text;

namespace Pillion.ModelGateway;

/// <summary>How a cache lookup was served.</summary>
public enum CacheHitKind
{
    /// <summary>Not served from the cache.</summary>
    None,

    /// <summary>The same prefix and suffix were completed before (a repeated keystroke or a backspace).</summary>
    Exact,

    /// <summary>The user typed the start of a cached completion; the rest of it is served.</summary>
    TypeThrough,
}

/// <summary>The hit and miss counts of an <see cref="AutocompleteCache"/>.</summary>
/// <param name="Hits">Exact hits.</param>
/// <param name="TypeThroughHits">Type-through hits.</param>
/// <param name="Misses">Misses.</param>
public readonly record struct CacheCounts(long Hits, long TypeThroughHits, long Misses);

/// <summary>
/// The autocomplete cache (section 6): an LRU keyed by model, a hash of the prefix tail (the last
/// <see cref="PrefixTailLength"/> characters) and a hash of the suffix head (the first <see cref="SuffixHeadLength"/>),
/// so repeated keystrokes and backspaces are served without a request. <see cref="Lookup"/> also serves type-through: when
/// the prefix ends with up to <see cref="MaxTypeThroughCharacters"/> characters that start a cached completion for the
/// shorter prefix, the rest of that completion is served. Thread-safe. Only hashes are kept as keys; the cached
/// completions are held in memory only.
/// </summary>
public sealed class AutocompleteCache
{
    /// <summary>The number of prefix characters, counted back from the caret, that form the key.</summary>
    public const int PrefixTailLength = 2048;

    /// <summary>The number of suffix characters, counted from the caret, that form the key.</summary>
    public const int SuffixHeadLength = 512;

    /// <summary>The most characters typed into a cached completion that a type-through lookup follows.</summary>
    public const int MaxTypeThroughCharacters = 64;

    private readonly object _gate = new();
    private readonly Dictionary<CacheKey, LinkedListNode<(CacheKey Key, string Completion)>> _map = [];
    private readonly LinkedList<(CacheKey Key, string Completion)> _order = new();
    private long _hits;
    private long _typeThroughHits;
    private long _misses;

    /// <summary>Initializes a new instance of the <see cref="AutocompleteCache"/> class.</summary>
    /// <param name="options">The gateway options (capacity).</param>
    public AutocompleteCache(ModelGatewayOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        Capacity = Math.Max(1, options.AutocompleteCacheCapacity);
    }

    /// <summary>Gets the most entries the cache holds.</summary>
    public int Capacity { get; }

    /// <summary>Gets the number of entries.</summary>
    public int Count
    {
        get
        {
            lock (_gate)
            {
                return _map.Count;
            }
        }
    }

    /// <summary>Gets the hit rate so far, exact and type-through hits together (section 13 targets 70% during steady typing).</summary>
    public double HitRate
    {
        get
        {
            var counts = GetCounts();
            var total = counts.Hits + counts.TypeThroughHits + counts.Misses;
            return total == 0 ? 0 : (counts.Hits + counts.TypeThroughHits) / (double)total;
        }
    }

    /// <summary>Builds the key for a single-line request.</summary>
    /// <param name="providerId">The provider id.</param>
    /// <param name="modelId">The model id.</param>
    /// <param name="prefix">The full prefix.</param>
    /// <param name="suffix">The full suffix.</param>
    /// <returns>The key.</returns>
    public static CacheKey CreateKey(string providerId, string modelId, string prefix, string suffix) =>
        CreateKey(providerId, modelId, prefix, suffix, multiLine: false);

    /// <summary>Builds the key for a request.</summary>
    /// <param name="providerId">The provider id.</param>
    /// <param name="modelId">The model id.</param>
    /// <param name="prefix">The full prefix.</param>
    /// <param name="suffix">The full suffix.</param>
    /// <param name="multiLine">Whether the request allows a multi-line completion (single- and multi-line completions are kept apart).</param>
    /// <returns>The key.</returns>
    public static CacheKey CreateKey(string providerId, string modelId, string prefix, string suffix, bool multiLine)
    {
        ArgumentNullException.ThrowIfNull(prefix);
        ArgumentNullException.ThrowIfNull(suffix);
        return new CacheKey(ModelKey(providerId, modelId, multiLine), PrefixHash(prefix.AsSpan()), SuffixHash(suffix));
    }

    /// <summary>Gets the hit and miss counts.</summary>
    /// <returns>The counts.</returns>
    public CacheCounts GetCounts() => new(Interlocked.Read(ref _hits), Interlocked.Read(ref _typeThroughHits), Interlocked.Read(ref _misses));

    /// <summary>Looks up a completion and marks it most recently used.</summary>
    /// <param name="key">The key.</param>
    /// <param name="completion">The cached completion.</param>
    /// <returns><see langword="true"/> on a hit.</returns>
    public bool TryGet(CacheKey key, out string completion)
    {
        if (TryGetCore(key, out completion))
        {
            Interlocked.Increment(ref _hits);
            return true;
        }

        Interlocked.Increment(ref _misses);
        return false;
    }

    /// <summary>
    /// Looks up a request: first the exact key, then type-through (the prefix minus its last 1 to
    /// <see cref="MaxTypeThroughCharacters"/> characters, when the cached completion for it starts with those characters and
    /// is longer than them). Counts one hit or one miss.
    /// </summary>
    /// <param name="providerId">The provider id.</param>
    /// <param name="modelId">The model id.</param>
    /// <param name="multiLine">Whether the request allows a multi-line completion.</param>
    /// <param name="prefix">The full prefix.</param>
    /// <param name="suffix">The full suffix.</param>
    /// <param name="completion">The completion to show (for type-through, the part after what was typed).</param>
    /// <returns>How the lookup was served.</returns>
    public CacheHitKind Lookup(string providerId, string modelId, bool multiLine, string prefix, string suffix, out string completion)
    {
        ArgumentNullException.ThrowIfNull(prefix);
        ArgumentNullException.ThrowIfNull(suffix);

        var model = ModelKey(providerId, modelId, multiLine);
        var suffixHash = SuffixHash(suffix);
        if (TryGetCore(new CacheKey(model, PrefixHash(prefix.AsSpan()), suffixHash), out completion))
        {
            Interlocked.Increment(ref _hits);
            return CacheHitKind.Exact;
        }

        if (Count > 0)
        {
            var limit = Math.Min(MaxTypeThroughCharacters, prefix.Length);
            for (var typed = 1; typed <= limit; typed++)
            {
                var key = new CacheKey(model, PrefixHash(prefix.AsSpan(0, prefix.Length - typed)), suffixHash);
                if (!TryGetCore(key, out var cached))
                {
                    continue;
                }

                if (cached.Length > typed && cached.AsSpan(0, typed).SequenceEqual(prefix.AsSpan(prefix.Length - typed)))
                {
                    completion = cached[typed..];
                    Interlocked.Increment(ref _typeThroughHits);
                    return CacheHitKind.TypeThrough;
                }
            }
        }

        Interlocked.Increment(ref _misses);
        completion = string.Empty;
        return CacheHitKind.None;
    }

    /// <summary>Stores a completion, evicting the least recently used entry when full.</summary>
    /// <param name="key">The key.</param>
    /// <param name="completion">The completion.</param>
    public void Set(CacheKey key, string completion)
    {
        ArgumentNullException.ThrowIfNull(completion);
        lock (_gate)
        {
            if (_map.TryGetValue(key, out var existing))
            {
                _order.Remove(existing);
                _map.Remove(key);
            }

            var node = _order.AddFirst((key, completion));
            _map[key] = node;
            while (_map.Count > Capacity)
            {
                var last = _order.Last!;
                _order.RemoveLast();
                _map.Remove(last.Value.Key);
            }
        }
    }

    /// <summary>Removes every entry.</summary>
    public void Clear()
    {
        lock (_gate)
        {
            _map.Clear();
            _order.Clear();
        }
    }

    private bool TryGetCore(CacheKey key, out string completion)
    {
        lock (_gate)
        {
            if (_map.TryGetValue(key, out var node))
            {
                _order.Remove(node);
                _order.AddFirst(node);
                completion = node.Value.Completion;
                return true;
            }
        }

        completion = string.Empty;
        return false;
    }

    private static string ModelKey(string providerId, string modelId, bool multiLine) =>
        providerId + "/" + modelId + (multiLine ? "|multi-line" : "|single-line");

    private static UInt128 PrefixHash(ReadOnlySpan<char> prefix) =>
        Hash(prefix.Length > PrefixTailLength ? prefix[^PrefixTailLength..] : prefix);

    private static UInt128 SuffixHash(string suffix) =>
        Hash(suffix.Length > SuffixHeadLength ? suffix.AsSpan(0, SuffixHeadLength) : suffix.AsSpan());

    private static UInt128 Hash(ReadOnlySpan<char> text)
    {
        Span<byte> digest = stackalloc byte[32];
        var bytes = Encoding.UTF8.GetBytes(text.ToArray());
        SHA256.HashData(bytes, digest);
        return new UInt128(BitConverter.ToUInt64(digest[..8]), BitConverter.ToUInt64(digest[8..16]));
    }

    /// <summary>An autocomplete cache key.</summary>
    /// <param name="Model">The qualified model id and the single- or multi-line variant.</param>
    /// <param name="PrefixTailHash">The hash of the prefix tail.</param>
    /// <param name="SuffixHeadHash">The hash of the suffix head.</param>
    public readonly record struct CacheKey(string Model, UInt128 PrefixTailHash, UInt128 SuffixHeadHash);
}

using System.Security.Cryptography;
using System.Text;

namespace Pillion.ModelGateway;

/// <summary>
/// The autocomplete cache (section 6): an LRU keyed by model, a hash of the prefix tail (the last
/// <see cref="PrefixTailLength"/> characters) and a hash of the suffix head (the first <see cref="SuffixHeadLength"/>),
/// so repeated keystrokes and backspaces are served without a request. Thread-safe. Only hashes are kept as keys; the
/// cached completions are held in memory only.
/// </summary>
public sealed class AutocompleteCache
{
    /// <summary>The number of prefix characters, counted back from the caret, that form the key.</summary>
    public const int PrefixTailLength = 2048;

    /// <summary>The number of suffix characters, counted from the caret, that form the key.</summary>
    public const int SuffixHeadLength = 512;

    private readonly object _gate = new();
    private readonly Dictionary<CacheKey, LinkedListNode<(CacheKey Key, string Completion)>> _map = [];
    private readonly LinkedList<(CacheKey Key, string Completion)> _order = new();
    private long _hits;
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

    /// <summary>Gets the hit rate so far (section 13 targets 70% during steady typing).</summary>
    public double HitRate
    {
        get
        {
            var total = Interlocked.Read(ref _hits) + Interlocked.Read(ref _misses);
            return total == 0 ? 0 : Interlocked.Read(ref _hits) / (double)total;
        }
    }

    /// <summary>Builds the key for a request.</summary>
    /// <param name="providerId">The provider id.</param>
    /// <param name="modelId">The model id.</param>
    /// <param name="prefix">The full prefix.</param>
    /// <param name="suffix">The full suffix.</param>
    /// <returns>The key.</returns>
    public static CacheKey CreateKey(string providerId, string modelId, string prefix, string suffix)
    {
        ArgumentNullException.ThrowIfNull(prefix);
        ArgumentNullException.ThrowIfNull(suffix);

        var tail = prefix.Length > PrefixTailLength ? prefix.AsSpan(prefix.Length - PrefixTailLength) : prefix.AsSpan();
        var head = suffix.Length > SuffixHeadLength ? suffix.AsSpan(0, SuffixHeadLength) : suffix.AsSpan();
        return new CacheKey(providerId + "/" + modelId, Hash(tail), Hash(head));
    }

    /// <summary>Looks up a completion and marks it most recently used.</summary>
    /// <param name="key">The key.</param>
    /// <param name="completion">The cached completion.</param>
    /// <returns><see langword="true"/> on a hit.</returns>
    public bool TryGet(CacheKey key, out string completion)
    {
        lock (_gate)
        {
            if (_map.TryGetValue(key, out var node))
            {
                _order.Remove(node);
                _order.AddFirst(node);
                completion = node.Value.Completion;
                Interlocked.Increment(ref _hits);
                return true;
            }
        }

        Interlocked.Increment(ref _misses);
        completion = string.Empty;
        return false;
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

    private static UInt128 Hash(ReadOnlySpan<char> text)
    {
        Span<byte> digest = stackalloc byte[32];
        var bytes = Encoding.UTF8.GetBytes(text.ToArray());
        SHA256.HashData(bytes, digest);
        return new UInt128(BitConverter.ToUInt64(digest[..8]), BitConverter.ToUInt64(digest[8..16]));
    }

    /// <summary>An autocomplete cache key.</summary>
    /// <param name="Model">The qualified model id.</param>
    /// <param name="PrefixTailHash">The hash of the prefix tail.</param>
    /// <param name="SuffixHeadHash">The hash of the suffix head.</param>
    public readonly record struct CacheKey(string Model, UInt128 PrefixTailHash, UInt128 SuffixHeadHash);
}

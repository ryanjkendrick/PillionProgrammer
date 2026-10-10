using System.Runtime.Serialization;

namespace Pillion.Contracts;

/// <summary>
/// The host side of autocomplete telemetry: the gateway's counters since the host started, and the latencies and prompt
/// sizes of the autocomplete records in the Outbound log over the last <see cref="WindowSeconds"/> seconds. Counts and
/// durations only. The cache counters and the window figures describe the same requests from two angles: the counters
/// are cumulative, the window is the recent past.
/// </summary>
[DataContract]
public sealed class AutocompleteHostCounters
{
    /// <summary>Gets or sets the completion requests the gateway received since the host started.</summary>
    [DataMember(Order = 0)]
    public long Requests { get; set; }

    /// <summary>Gets or sets the requests served from the cache with the same prefix (repeated keystrokes, backspaces).</summary>
    [DataMember(Order = 1)]
    public long CacheHits { get; set; }

    /// <summary>Gets or sets the requests served from the cache because the user typed the start of a cached completion.</summary>
    [DataMember(Order = 2)]
    public long TypeThroughHits { get; set; }

    /// <summary>Gets or sets the requests the cache could not serve.</summary>
    [DataMember(Order = 3)]
    public long CacheMisses { get; set; }

    /// <summary>Gets or sets the share of cache lookups that hit (exact or type-through), from 0 to 1; 0 before the first lookup.</summary>
    [DataMember(Order = 4)]
    public double HitRate { get; set; }

    /// <summary>Gets or sets the requests sent to a model (attempts, including fallbacks).</summary>
    [DataMember(Order = 5)]
    public long ModelRequests { get; set; }

    /// <summary>Gets or sets the cloud requests dropped at the hard limit.</summary>
    [DataMember(Order = 6)]
    public long Dropped { get; set; }

    /// <summary>Gets or sets the requests that failed.</summary>
    [DataMember(Order = 7)]
    public long Failed { get; set; }

    /// <summary>Gets or sets the requests the caller cancelled (usually the next keystroke).</summary>
    [DataMember(Order = 8)]
    public long Cancelled { get; set; }

    /// <summary>Gets or sets the length of the window the figures below cover, in seconds (300).</summary>
    [DataMember(Order = 9)]
    public int WindowSeconds { get; set; }

    /// <summary>Gets or sets the autocomplete records in the Outbound log within the window.</summary>
    [DataMember(Order = 10)]
    public int WindowRequests { get; set; }

    /// <summary>Gets or sets how many of the window's records were served from the cache.</summary>
    [DataMember(Order = 11)]
    public int WindowCacheHits { get; set; }

    /// <summary>Gets or sets the median time to the first token in milliseconds, over the window's model requests (cache hits excluded); <see langword="null"/> without samples.</summary>
    [DataMember(Order = 12)]
    public double? FirstTokenP50Ms { get; set; }

    /// <summary>Gets or sets the 95th percentile time to the first token in milliseconds; <see langword="null"/> without samples.</summary>
    [DataMember(Order = 13)]
    public double? FirstTokenP95Ms { get; set; }

    /// <summary>Gets or sets the median total duration in milliseconds, over the window's model requests (cache hits excluded); <see langword="null"/> without samples.</summary>
    [DataMember(Order = 14)]
    public double? TotalP50Ms { get; set; }

    /// <summary>Gets or sets the 95th percentile total duration in milliseconds; <see langword="null"/> without samples.</summary>
    [DataMember(Order = 15)]
    public double? TotalP95Ms { get; set; }

    /// <summary>Gets or sets how many of the window's model requests had their prompt tokens counted with the model's tokenizer.</summary>
    [DataMember(Order = 16)]
    public int PromptCountedRequests { get; set; }

    /// <summary>Gets or sets how many of the window's model requests had their prompt tokens estimated.</summary>
    [DataMember(Order = 17)]
    public int PromptEstimatedRequests { get; set; }

    /// <summary>Gets or sets the prompt tokens of the requests counted with the tokenizer.</summary>
    [DataMember(Order = 18)]
    public long PromptTokensCounted { get; set; }

    /// <summary>Gets or sets the prompt tokens of the requests that were estimated.</summary>
    [DataMember(Order = 19)]
    public long PromptTokensEstimated { get; set; }

    /// <summary>Gets or sets the largest prompt in the window, in tokens.</summary>
    [DataMember(Order = 20)]
    public int PromptTokensMax { get; set; }
}

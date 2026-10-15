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

    /// <summary>
    /// Gets or sets the local models that are loaded now, as Ollama last reported them (read at most every 30 seconds):
    /// context size, GPU memory and keep-alive, for the status bar. Empty when no local provider is configured or none has
    /// been asked yet.
    /// </summary>
    [DataMember(Order = 21)]
    public ResidentModelInfo[] ResidentModels { get; set; } = [];

    /// <summary>
    /// Gets or sets the model requests since the host started whose document had an earlier model request to the same model, so
    /// their prompt prefix could be compared with it (the denominator of <see cref="PromptPrefixReuseRate"/>). Added in
    /// contract 1.3 (work item 2.12).
    /// </summary>
    [DataMember(Order = 22)]
    public long PromptPrefixChecks { get; set; }

    /// <summary>
    /// Gets or sets how many of those requests sent a prompt prefix (retrieved snippets, header and code before the caret) that
    /// starts with the previous request's for the same document and model, so a resident local model can reuse its cached
    /// prompt (issue #47, option 1). Added in contract 1.3 (work item 2.12).
    /// </summary>
    [DataMember(Order = 23)]
    public long PromptPrefixReuses { get; set; }

    /// <summary>Gets or sets <see cref="PromptPrefixReuses"/> over <see cref="PromptPrefixChecks"/>, from 0 to 1; 0 before the first check. Added in contract 1.3 (work item 2.12).</summary>
    [DataMember(Order = 24)]
    public double PromptPrefixReuseRate { get; set; }
}

/// <summary>A local model that is loaded in its runtime (design section 6, Local model residency).</summary>
[DataContract]
public sealed class ResidentModelInfo
{
    /// <summary>Gets or sets the provider that serves the model.</summary>
    [DataMember(Order = 0)]
    public string ProviderId { get; set; } = string.Empty;

    /// <summary>Gets or sets the provider's model id.</summary>
    [DataMember(Order = 1)]
    public string ModelId { get; set; } = string.Empty;

    /// <summary>Gets or sets the context size (<c>num_ctx</c>) it is loaded with, or <see langword="null"/> when the runtime does not say.</summary>
    [DataMember(Order = 2)]
    public int? ContextLength { get; set; }

    /// <summary>Gets or sets the GPU memory it uses, in bytes (0 when it runs on the CPU).</summary>
    [DataMember(Order = 3)]
    public long VramBytes { get; set; }

    /// <summary>Gets or sets the keep-alive: <c>-1</c> for loaded for ever, otherwise the time left such as <c>29m</c>.</summary>
    [DataMember(Order = 4)]
    public string KeepAlive { get; set; } = string.Empty;

    /// <summary>Gets or sets a value indicating whether the model stays loaded for ever (the autocomplete model).</summary>
    [DataMember(Order = 5)]
    public bool IsPinned { get; set; }
}

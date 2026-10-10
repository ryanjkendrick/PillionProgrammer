using System;
using System.Runtime.Serialization;

namespace Pillion.Contracts;

/// <summary>
/// One request Pillion Host sent to a model provider, from any task class, as the Outbound log keeps it (design section 12,
/// Data flows). Counts and durations only: it never holds prompt or completion text.
/// </summary>
[DataContract]
public sealed class OutboundRecordDto
{
    /// <summary>Gets or sets when the request was sent, in UTC.</summary>
    [DataMember(Order = 0)]
    public DateTime TimestampUtc { get; set; }

    /// <summary>Gets or sets the caller's request id.</summary>
    [DataMember(Order = 1)]
    public string RequestId { get; set; } = string.Empty;

    /// <summary>Gets or sets the provider that served the request.</summary>
    [DataMember(Order = 2)]
    public string ProviderId { get; set; } = string.Empty;

    /// <summary>Gets or sets the provider's model id.</summary>
    [DataMember(Order = 3)]
    public string ModelId { get; set; } = string.Empty;

    /// <summary>Gets or sets the task class of the request.</summary>
    [DataMember(Order = 4)]
    public TaskClass TaskClass { get; set; }

    /// <summary>Gets or sets a value indicating whether the provider runs on this machine (no cloud boundary was crossed).</summary>
    [DataMember(Order = 5)]
    public bool IsLocal { get; set; }

    /// <summary>Gets or sets the prompt size in tokens (counted with the model's tokenizer or estimated; 0 for a cache hit).</summary>
    [DataMember(Order = 6)]
    public int PromptTokens { get; set; }

    /// <summary>Gets or sets the tokens generated.</summary>
    [DataMember(Order = 7)]
    public int CompletionTokens { get; set; }

    /// <summary>Gets or sets the number of secrets replaced by placeholders before the request left the host.</summary>
    [DataMember(Order = 8)]
    public int RedactionCount { get; set; }

    /// <summary>Gets or sets the number of items in the assembled context (pins, current file, rules, history, ...); 0 when the request was not assembled.</summary>
    [DataMember(Order = 9)]
    public int ContextItems { get; set; }

    /// <summary>Gets or sets how many of those items were truncated or dropped to fit the model's window.</summary>
    [DataMember(Order = 10)]
    public int ContextTrimmed { get; set; }

    /// <summary>Gets or sets how long the request took, in milliseconds.</summary>
    [DataMember(Order = 11)]
    public long DurationMs { get; set; }

    /// <summary>Gets or sets how the request ended: <c>Succeeded</c>, <c>Failed</c>, <c>Cancelled</c>, <c>TimedOut</c> or <c>Dropped</c>.</summary>
    [DataMember(Order = 12)]
    public string Outcome { get; set; } = string.Empty;

    /// <summary>Gets or sets the error code when the request failed, else <see langword="null"/>.</summary>
    [DataMember(Order = 13)]
    public PillionErrorCode? ErrorCode { get; set; }

    /// <summary>Gets or sets a value indicating whether an autocomplete request was served from the cache (nothing was sent).</summary>
    [DataMember(Order = 14)]
    public bool CacheHit { get; set; }
}

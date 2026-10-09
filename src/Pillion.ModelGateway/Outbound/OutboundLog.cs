using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Pillion.Contracts;

namespace Pillion.ModelGateway;

/// <summary>How an outbound request ended.</summary>
public enum OutboundOutcome
{
    /// <summary>The model answered.</summary>
    Succeeded,

    /// <summary>The provider failed.</summary>
    Failed,

    /// <summary>The caller cancelled the request.</summary>
    Cancelled,

    /// <summary>The request timed out.</summary>
    TimedOut,

    /// <summary>A cloud autocomplete request ran past its hard limit and was dropped silently.</summary>
    Dropped,
}

/// <summary>
/// One request sent to a model provider (section 12, Data flows). Never contains prompt or completion text; counts only.
/// </summary>
/// <param name="Timestamp">When the request was sent.</param>
/// <param name="RequestId">The caller's request id.</param>
/// <param name="ProviderId">The provider.</param>
/// <param name="ModelId">The model.</param>
/// <param name="TaskClass">The task class.</param>
/// <param name="IsLocal">Whether the provider is local (no cloud boundary was crossed).</param>
/// <param name="EstimatedPromptTokens">The prompt size in tokens: counted with the model's tokenizer when <paramref name="PromptTokensCounted"/> is set, otherwise estimated (<see cref="TokenEstimator"/> for chat, the calibrated family estimate for autocomplete). 0 for a cache hit, which sends nothing.</param>
/// <param name="CompletionTokens">The tokens generated (provider-reported when available, else estimated).</param>
/// <param name="RedactionCount">The number of secrets replaced by placeholders.</param>
/// <param name="Duration">How long the request took.</param>
/// <param name="Outcome">How it ended.</param>
/// <param name="ErrorCode">The error code when it failed.</param>
/// <param name="PromptTokensCounted">Whether <paramref name="EstimatedPromptTokens"/> was counted with the model's tokenizer rather than estimated.</param>
/// <param name="CacheHit">Whether an autocomplete request was served from the cache; nothing was sent to the provider then.</param>
/// <param name="TimeToFirstToken">How long the first token took, when one arrived.</param>
public sealed record OutboundRecord(
    DateTimeOffset Timestamp,
    string RequestId,
    string ProviderId,
    string ModelId,
    TaskClass TaskClass,
    bool IsLocal,
    int EstimatedPromptTokens,
    int CompletionTokens,
    int RedactionCount,
    TimeSpan Duration,
    OutboundOutcome Outcome,
    PillionErrorCode? ErrorCode = null,
    bool PromptTokensCounted = false,
    bool CacheHit = false,
    TimeSpan? TimeToFirstToken = null);

/// <summary>The Outbound log: every request sent to a model provider, from every task class.</summary>
public interface IOutboundLog
{
    /// <summary>Records a request.</summary>
    /// <param name="record">The record.</param>
    void Record(OutboundRecord record);

    /// <summary>Gets the most recent records, oldest first.</summary>
    /// <returns>A copy of the records.</returns>
    IReadOnlyList<OutboundRecord> GetRecent();
}

/// <summary>
/// An <see cref="IOutboundLog"/> that keeps the last <see cref="ModelGatewayOptions.OutboundLogCapacity"/> records in a
/// ring buffer and writes one structured log line per record. A later work item persists it with the audit trail.
/// </summary>
public sealed partial class InMemoryOutboundLog : IOutboundLog
{
    private readonly object _gate = new();
    private readonly OutboundRecord?[] _buffer;
    private readonly ILogger _logger;
    private int _next;
    private int _count;

    /// <summary>Initializes a new instance of the <see cref="InMemoryOutboundLog"/> class.</summary>
    /// <param name="options">The gateway options (capacity).</param>
    /// <param name="logger">The logger for the structured line.</param>
    public InMemoryOutboundLog(ModelGatewayOptions options, ILogger<InMemoryOutboundLog>? logger = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        _buffer = new OutboundRecord?[Math.Max(1, options.OutboundLogCapacity)];
        _logger = logger ?? NullLogger<InMemoryOutboundLog>.Instance;
    }

    /// <summary>Gets the capacity of the ring buffer.</summary>
    public int Capacity => _buffer.Length;

    /// <inheritdoc/>
    public void Record(OutboundRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);
        lock (_gate)
        {
            _buffer[_next] = record;
            _next = (_next + 1) % _buffer.Length;
            _count = Math.Min(_count + 1, _buffer.Length);
        }

        var errorCode = record.ErrorCode is { } code ? code.ToString() : "none";
        LogRecord(
            _logger,
            record.RequestId,
            record.ProviderId,
            record.ModelId,
            record.TaskClass,
            record.IsLocal ? "local" : "cloud",
            record.EstimatedPromptTokens,
            record.PromptTokensCounted ? "counted" : "estimated",
            record.CompletionTokens,
            record.CacheHit,
            record.TimeToFirstToken is { } firstToken ? (long)firstToken.TotalMilliseconds : -1,
            record.RedactionCount,
            (long)record.Duration.TotalMilliseconds,
            record.Outcome,
            errorCode);
    }

    /// <inheritdoc/>
    public IReadOnlyList<OutboundRecord> GetRecent()
    {
        lock (_gate)
        {
            var result = new List<OutboundRecord>(_count);
            var start = (_next - _count + _buffer.Length) % _buffer.Length;
            for (var i = 0; i < _count; i++)
            {
                result.Add(_buffer[(start + i) % _buffer.Length]!);
            }

            return result;
        }
    }

    [LoggerMessage(
        EventId = 420,
        Level = LogLevel.Information,
        Message = "Outbound {RequestId}: {ProviderId}/{ModelId} {TaskClass} {Locality}, prompt {PromptTokens} tokens ({PromptTokenCount}), completion {CompletionTokens} tokens, cache hit {CacheHit}, first token {FirstTokenMs} ms, {Redactions} redactions, {DurationMs} ms, {Outcome} ({ErrorCode}).")]
    private static partial void LogRecord(
        ILogger logger,
        string requestId,
        string providerId,
        string modelId,
        TaskClass taskClass,
        string locality,
        int promptTokens,
        string promptTokenCount,
        int completionTokens,
        bool cacheHit,
        long firstTokenMs,
        int redactions,
        long durationMs,
        OutboundOutcome outcome,
        string errorCode);
}

/// <summary>Estimates token counts without a tokenizer: about four characters per token for code and English.</summary>
public static class TokenEstimator
{
    /// <summary>Estimates the tokens in a text.</summary>
    /// <param name="text">The text.</param>
    /// <returns>The estimate, rounded up.</returns>
    public static int Estimate(string? text) => string.IsNullOrEmpty(text) ? 0 : (text.Length + 3) / 4;

    /// <summary>Estimates the tokens in several texts, adding a few tokens of framing per text.</summary>
    /// <param name="texts">The texts (for example the messages of a conversation).</param>
    /// <returns>The estimate.</returns>
    public static int Estimate(IEnumerable<string> texts)
    {
        ArgumentNullException.ThrowIfNull(texts);
        return texts.Sum(t => Estimate(t) + 4);
    }
}

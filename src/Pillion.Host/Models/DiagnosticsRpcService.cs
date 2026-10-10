using Pillion.Common.Settings;
using Pillion.Contracts;
using Pillion.ModelGateway;

namespace Pillion.Host.Models;

/// <summary>
/// Implements <see cref="IDiagnosticsService"/>: a read-only view of the Model Gateway's autocomplete counters, of the
/// Outbound log, and the Context Inspector's preview of a chat request. It reads what the gateway already keeps (the
/// counters and the ring buffer) and holds no state of its own; it never returns prompt or completion text.
/// </summary>
/// <remarks>
/// <see cref="GetOutboundRecordsAsync"/> needs the <see cref="FeatureNames.OutboundLogView"/> feature;
/// <see cref="PreviewContextAsync"/> needs it and <see cref="FeatureNames.ModelGateway"/> (the preview routes the request).
/// </remarks>
internal sealed class DiagnosticsRpcService : IDiagnosticsService
{
    /// <summary>The length of the window the Outbound figures cover (design section 13: five minutes).</summary>
    public static readonly TimeSpan Window = TimeSpan.FromMinutes(5);

    /// <summary>The number of Outbound records returned when the caller asks for 0 or fewer.</summary>
    public const int DefaultOutboundCount = 200;

    /// <summary>The message of the error returned while the <see cref="FeatureNames.OutboundLogView"/> feature is off.</summary>
    internal const string OutboundLogViewDisabledMessage =
        "The Context Inspector and the Outbound log view are turned off by settings or policy (feature outboundLogView). "
        + "Set features.outboundLogView to true in settings.json, or remove it from disabledFeatures in the repository policy.";

    private readonly AutocompleteCounters _counters;
    private readonly IOutboundLog _outbound;
    private readonly TimeProvider _time;
    private readonly LocalResidencyMap? _residency;
    private readonly ContextPreview? _preview;
    private readonly IFeatureFlags? _features;

    /// <summary>Initializes a new instance of the <see cref="DiagnosticsRpcService"/> class.</summary>
    /// <param name="counters">The gateway's autocomplete counters.</param>
    /// <param name="outbound">The Outbound log.</param>
    /// <param name="time">The clock.</param>
    /// <param name="residency">The map of resident local models; the report lists none without it.</param>
    /// <param name="preview">Assembles the Context Inspector's preview; without it <see cref="PreviewContextAsync"/> fails.</param>
    /// <param name="features">The feature flags; <see langword="null"/> means every feature is on.</param>
    public DiagnosticsRpcService(
        AutocompleteCounters counters,
        IOutboundLog outbound,
        TimeProvider time,
        LocalResidencyMap? residency = null,
        ContextPreview? preview = null,
        IFeatureFlags? features = null)
    {
        ArgumentNullException.ThrowIfNull(counters);
        ArgumentNullException.ThrowIfNull(outbound);
        ArgumentNullException.ThrowIfNull(time);
        _counters = counters;
        _outbound = outbound;
        _time = time;
        _residency = residency;
        _preview = preview;
        _features = features;
    }

    /// <inheritdoc/>
    public Task<OutboundRecordDto[]> GetOutboundRecordsAsync(int maxCount, CancellationToken cancellationToken)
    {
        EnsureEnabled(FeatureNames.OutboundLogView, OutboundLogViewDisabledMessage);
        return Task.FromResult(ToDtos(_outbound.GetRecent(), maxCount));
    }

    /// <inheritdoc/>
    public Task<ContextReportDto> PreviewContextAsync(ChatRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        EnsureEnabled(FeatureNames.ModelGateway, ModelRpcService.GatewayDisabledMessage);
        EnsureEnabled(FeatureNames.OutboundLogView, OutboundLogViewDisabledMessage);
        if (_preview is null)
        {
            throw new PillionRpcException(new PillionError
            {
                Code = PillionErrorCode.ProviderUnavailable,
                Message = "This Pillion Host cannot preview context.",
            });
        }

        return _preview.PreviewAsync(request, cancellationToken);
    }

    /// <summary>Maps Outbound records to their DTOs, newest first.</summary>
    /// <param name="records">The records, oldest first (as <see cref="IOutboundLog.GetRecent"/> returns them).</param>
    /// <param name="maxCount">The most to return; 0 or less means <see cref="DefaultOutboundCount"/>.</param>
    /// <returns>The DTOs.</returns>
    internal static OutboundRecordDto[] ToDtos(IReadOnlyList<OutboundRecord> records, int maxCount)
    {
        ArgumentNullException.ThrowIfNull(records);
        var count = maxCount > 0 ? maxCount : DefaultOutboundCount;
        var result = new List<OutboundRecordDto>(Math.Min(count, records.Count));
        for (var i = records.Count - 1; i >= 0 && result.Count < count; i--)
        {
            var record = records[i];
            result.Add(new OutboundRecordDto
            {
                TimestampUtc = record.Timestamp.UtcDateTime,
                RequestId = record.RequestId,
                ProviderId = record.ProviderId,
                ModelId = record.ModelId,
                TaskClass = record.TaskClass,
                IsLocal = record.IsLocal,
                PromptTokens = record.EstimatedPromptTokens,
                CompletionTokens = record.CompletionTokens,
                RedactionCount = record.RedactionCount,
                ContextItems = record.ContextItems,
                ContextTrimmed = record.ContextTrimmed,
                DurationMs = (long)record.Duration.TotalMilliseconds,
                Outcome = record.Outcome.ToString(),
                ErrorCode = record.ErrorCode,
                CacheHit = record.CacheHit,
            });
        }

        return [.. result];
    }

    /// <inheritdoc/>
    public async Task<AutocompleteHostCounters> GetAutocompleteCountersAsync(CancellationToken cancellationToken)
    {
        var report = Build(_counters.Snapshot(), _outbound.GetRecent(), _time.GetUtcNow());
        if (_residency is not null)
        {
            // Asks Ollama at most once every 30 s; between asks this is the cached answer.
            var snapshot = await _residency.RefreshAsync(force: false, cancellationToken).ConfigureAwait(false);
            report.ResidentModels =
            [
                .. snapshot.Models.Select(m => new ResidentModelInfo
                {
                    ProviderId = m.ProviderId,
                    ModelId = m.ModelId,
                    ContextLength = m.ContextLength,
                    VramBytes = m.VramBytes,
                    KeepAlive = m.KeepAlive,
                    IsPinned = m.IsPinned,
                }),
            ];
        }

        return report;
    }

    /// <summary>Builds the report from the counters and the Outbound records.</summary>
    /// <param name="counters">The counters snapshot.</param>
    /// <param name="records">The Outbound records, any task class.</param>
    /// <param name="now">The current time.</param>
    /// <returns>The report.</returns>
    internal static AutocompleteHostCounters Build(AutocompleteCountersSnapshot counters, IReadOnlyList<OutboundRecord> records, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(counters);
        ArgumentNullException.ThrowIfNull(records);

        var cutoff = now - Window;
        var recent = records
            .Where(r => r.TaskClass == TaskClass.Autocomplete && r.Timestamp >= cutoff)
            .ToList();

        // Model requests only: a cache hit sends nothing, so its time says nothing about the model.
        var sent = recent.Where(r => !r.CacheHit).ToList();
        var firstTokens = sent.Where(r => r.TimeToFirstToken is not null && r.Outcome == OutboundOutcome.Succeeded)
            .Select(r => r.TimeToFirstToken!.Value.TotalMilliseconds)
            .ToList();
        var totals = sent.Where(r => r.Outcome == OutboundOutcome.Succeeded)
            .Select(r => r.Duration.TotalMilliseconds)
            .ToList();
        var counted = sent.Where(r => r.PromptTokensCounted).ToList();
        var estimated = sent.Where(r => !r.PromptTokensCounted).ToList();

        return new AutocompleteHostCounters
        {
            Requests = counters.Requests,
            CacheHits = counters.CacheHits,
            TypeThroughHits = counters.TypeThroughHits,
            CacheMisses = counters.CacheMisses,
            HitRate = counters.HitRate,
            ModelRequests = counters.ModelRequests,
            Dropped = counters.Dropped,
            Failed = counters.Failed,
            Cancelled = counters.Cancelled,
            WindowSeconds = (int)Window.TotalSeconds,
            WindowRequests = recent.Count,
            WindowCacheHits = recent.Count - sent.Count,
            FirstTokenP50Ms = Percentile(firstTokens, 0.50),
            FirstTokenP95Ms = Percentile(firstTokens, 0.95),
            TotalP50Ms = Percentile(totals, 0.50),
            TotalP95Ms = Percentile(totals, 0.95),
            PromptCountedRequests = counted.Count,
            PromptEstimatedRequests = estimated.Count,
            PromptTokensCounted = counted.Sum(r => (long)r.EstimatedPromptTokens),
            PromptTokensEstimated = estimated.Sum(r => (long)r.EstimatedPromptTokens),
            PromptTokensMax = sent.Count == 0 ? 0 : sent.Max(r => r.EstimatedPromptTokens),
        };
    }

    private void EnsureEnabled(string feature, string message)
    {
        if (_features is not null && !_features.IsEnabled(feature))
        {
            throw new PillionRpcException(new PillionError
            {
                Code = PillionErrorCode.PolicyDenied,
                Message = message,
            });
        }
    }

    /// <summary>The nearest-rank percentile (the same rule as the extension's window).</summary>
    private static double? Percentile(List<double> values, double fraction)
    {
        if (values.Count == 0)
        {
            return null;
        }

        values.Sort();
        var rank = (int)Math.Ceiling(fraction * values.Count);
        return values[Math.Min(values.Count, Math.Max(1, rank)) - 1];
    }
}

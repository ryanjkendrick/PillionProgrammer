using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Pillion.ModelGateway;

/// <summary>
/// Receives provider health evidence from the circuit breakers: every request outcome that counts for or against a
/// provider, and the circuit opening and closing. Pillion Host feeds it to the connectivity monitor (section 11), which
/// turns repeated failures into the Online, Degraded and Offline states; <see cref="NoProviderHealthSink"/> ignores it.
/// Calls are made outside the breaker's lock, on the thread that recorded the outcome, and must not block.
/// </summary>
public interface IProviderHealthSink
{
    /// <summary>Called for every request failure that counts against the provider (see <see cref="ProviderException.IsProviderFault"/>).</summary>
    /// <param name="providerId">The provider id.</param>
    void RequestFailed(string providerId);

    /// <summary>Called for every successful request to the provider.</summary>
    /// <param name="providerId">The provider id.</param>
    void RequestSucceeded(string providerId);

    /// <summary>Called when a provider's circuit opens.</summary>
    /// <param name="providerId">The provider id.</param>
    /// <param name="retryAt">When the circuit lets a trial request through.</param>
    void CircuitOpened(string providerId, DateTimeOffset retryAt);

    /// <summary>Called when a provider's circuit closes after a successful request.</summary>
    /// <param name="providerId">The provider id.</param>
    void CircuitClosed(string providerId);
}

/// <summary>An <see cref="IProviderHealthSink"/> that does nothing.</summary>
public sealed class NoProviderHealthSink : IProviderHealthSink
{
    /// <inheritdoc/>
    public void RequestFailed(string providerId)
    {
    }

    /// <inheritdoc/>
    public void RequestSucceeded(string providerId)
    {
    }

    /// <inheritdoc/>
    public void CircuitOpened(string providerId, DateTimeOffset retryAt)
    {
    }

    /// <inheritdoc/>
    public void CircuitClosed(string providerId)
    {
    }
}

/// <summary>The state of one provider's circuit.</summary>
public enum CircuitState
{
    /// <summary>Requests flow normally.</summary>
    Closed,

    /// <summary>Requests are refused until the open period ends.</summary>
    Open,

    /// <summary>The open period has ended; one trial request is allowed through.</summary>
    HalfOpen,
}

/// <summary>
/// One circuit breaker per provider (section 6): it opens after <see cref="ModelGatewayOptions.CircuitFailureThreshold"/>
/// consecutive failures for <see cref="ModelGatewayOptions.CircuitOpenDuration"/>, then lets one trial request through;
/// a success closes it, a failure opens it again. Thread-safe.
/// </summary>
public sealed partial class CircuitBreakerRegistry
{
    private readonly object _gate = new();
    private readonly Dictionary<string, Entry> _entries = new(StringComparer.OrdinalIgnoreCase);
    private readonly ModelGatewayOptions _options;
    private readonly TimeProvider _time;
    private readonly IProviderHealthSink _health;
    private readonly ILogger _logger;

    /// <summary>Initializes a new instance of the <see cref="CircuitBreakerRegistry"/> class.</summary>
    /// <param name="options">The gateway options.</param>
    /// <param name="time">The clock.</param>
    /// <param name="health">Receives open and close events.</param>
    /// <param name="logger">The logger.</param>
    public CircuitBreakerRegistry(ModelGatewayOptions options, TimeProvider? time = null, IProviderHealthSink? health = null, ILogger<CircuitBreakerRegistry>? logger = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        _options = options;
        _time = time ?? TimeProvider.System;
        _health = health ?? new NoProviderHealthSink();
        _logger = logger ?? NullLogger<CircuitBreakerRegistry>.Instance;
    }

    /// <summary>Gets a provider's circuit state.</summary>
    /// <param name="providerId">The provider id.</param>
    /// <returns>The state.</returns>
    public CircuitState GetState(string providerId)
    {
        lock (_gate)
        {
            if (!_entries.TryGetValue(providerId, out var entry) || entry.OpenUntil is null)
            {
                return CircuitState.Closed;
            }

            return _time.GetUtcNow() < entry.OpenUntil ? CircuitState.Open : CircuitState.HalfOpen;
        }
    }

    /// <summary>Determines whether a request may be sent to a provider now. In the half-open state only one trial request is allowed at a time.</summary>
    /// <param name="providerId">The provider id.</param>
    /// <returns><see langword="true"/> when the request may proceed; it must then report <see cref="RecordSuccess"/> or <see cref="RecordFailure"/>.</returns>
    public bool TryAcquire(string providerId)
    {
        lock (_gate)
        {
            var entry = GetEntry(providerId);
            if (entry.OpenUntil is null)
            {
                return true;
            }

            if (_time.GetUtcNow() < entry.OpenUntil || entry.TrialInFlight)
            {
                return false;
            }

            entry.TrialInFlight = true;
            return true;
        }
    }

    /// <summary>Determines whether routing should consider the provider (the circuit is closed or ready for a trial).</summary>
    /// <param name="providerId">The provider id.</param>
    /// <returns><see langword="true"/> unless the circuit is open.</returns>
    public bool IsAvailable(string providerId) => GetState(providerId) != CircuitState.Open;

    /// <summary>Records a successful request; closes the circuit.</summary>
    /// <param name="providerId">The provider id.</param>
    public void RecordSuccess(string providerId)
    {
        bool closed;
        lock (_gate)
        {
            var entry = GetEntry(providerId);
            closed = entry.OpenUntil is not null;
            entry.ConsecutiveFailures = 0;
            entry.OpenUntil = null;
            entry.TrialInFlight = false;
        }

        _health.RequestSucceeded(providerId);
        if (closed)
        {
            LogClosed(_logger, providerId);
            _health.CircuitClosed(providerId);
        }
    }

    /// <summary>Records a failure that counts against the provider (see <see cref="ProviderException.IsProviderFault"/>).</summary>
    /// <param name="providerId">The provider id.</param>
    public void RecordFailure(string providerId)
    {
        DateTimeOffset? openedUntil = null;
        lock (_gate)
        {
            var entry = GetEntry(providerId);
            entry.ConsecutiveFailures++;
            var wasTrial = entry.TrialInFlight;
            entry.TrialInFlight = false;
            if (wasTrial || entry.ConsecutiveFailures >= _options.CircuitFailureThreshold)
            {
                entry.OpenUntil = _time.GetUtcNow() + _options.CircuitOpenDuration;
                openedUntil = entry.OpenUntil;
            }
        }

        _health.RequestFailed(providerId);
        if (openedUntil is { } until)
        {
            LogOpened(_logger, providerId, until);
            _health.CircuitOpened(providerId, until);
        }
    }

    /// <summary>Releases a half-open trial slot without recording an outcome (the request was cancelled or failed for a reason unrelated to the provider).</summary>
    /// <param name="providerId">The provider id.</param>
    public void RecordNeutral(string providerId)
    {
        lock (_gate)
        {
            GetEntry(providerId).TrialInFlight = false;
        }
    }

    private Entry GetEntry(string providerId)
    {
        if (!_entries.TryGetValue(providerId, out var entry))
        {
            entry = new Entry();
            _entries[providerId] = entry;
        }

        return entry;
    }

    [LoggerMessage(EventId = 410, Level = LogLevel.Warning, Message = "Circuit for model provider '{ProviderId}' opened until {RetryAt:O} after repeated failures.")]
    private static partial void LogOpened(ILogger logger, string providerId, DateTimeOffset retryAt);

    [LoggerMessage(EventId = 411, Level = LogLevel.Information, Message = "Circuit for model provider '{ProviderId}' closed.")]
    private static partial void LogClosed(ILogger logger, string providerId);

    private sealed class Entry
    {
        public int ConsecutiveFailures { get; set; }

        public DateTimeOffset? OpenUntil { get; set; }

        public bool TrialInFlight { get; set; }
    }
}

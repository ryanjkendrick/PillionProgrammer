using System.Text.RegularExpressions;
using Pillion.Contracts;

namespace Pillion.ModelGateway;

/// <summary>How the gateway runs one request against one model.</summary>
internal sealed record AttemptPolicy
{
    /// <summary>Gets the request id.</summary>
    public required string RequestId { get; init; }

    /// <summary>Gets the task class.</summary>
    public required TaskClass TaskClass { get; init; }

    /// <summary>Gets the prompt tokens (estimated unless <see cref="PromptTokensCounted"/>).</summary>
    public required int PromptTokens { get; init; }

    /// <summary>Gets a value indicating whether <see cref="PromptTokens"/> was counted with the model's tokenizer.</summary>
    public bool PromptTokensCounted { get; init; }

    /// <summary>Gets the hard limit for the whole request (cloud autocomplete), or <see langword="null"/>.</summary>
    public TimeSpan? OverallDeadline { get; init; }

    /// <summary>Gets the limit for the first token (cloud chat), or <see langword="null"/>.</summary>
    public TimeSpan? FirstTokenTimeout { get; init; }

    /// <summary>Gets the first-token budget after which a local request reports that it is slow, or <see langword="null"/>.</summary>
    public TimeSpan? LocalBudget { get; init; }

    /// <summary>Gets the number of retries on 429 and 5xx.</summary>
    public int MaxRetries { get; init; }

    /// <summary>Gets the number of items in the context report of an assembled chat request (0 when none was assembled).</summary>
    public int ContextItems { get; init; }

    /// <summary>Gets the number of those items that were truncated or dropped.</summary>
    public int ContextTrimmed { get; init; }

    /// <summary>Gets the names of the tools declared to the model (work item 3.23); empty when none.</summary>
    public IReadOnlyList<string> ToolsOffered { get; init; } = [];

    /// <summary>Gets the UTF-8 size of the tool results the request carries back, after the cap.</summary>
    public int ToolResultBytes { get; init; }
}

/// <summary>Thrown when a cloud autocomplete request runs past its hard limit; the suggestion is dropped silently.</summary>
internal sealed class AutocompleteDroppedException : Exception
{
    public AutocompleteDroppedException()
        : base("The autocomplete request was dropped after its time limit.")
    {
    }

    public AutocompleteDroppedException(string message)
        : base(message)
    {
    }

    public AutocompleteDroppedException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>
/// One request streamed from one model: the circuit breaker, the concurrency slot, retries with jitter before the first
/// token, the first-token timeout (cloud) or progress notices (local), the overall deadline (cloud autocomplete), and
/// the Outbound record. Items are pulled with <see cref="MoveNextAsync"/>; failures surface as
/// <see cref="ProviderException"/>, <see cref="PillionRpcException"/>, <see cref="AutocompleteDroppedException"/> or
/// <see cref="OperationCanceledException"/> (caller cancelled).
/// </summary>
/// <typeparam name="T">The item type the provider streams.</typeparam>
internal sealed class StreamAttempt<T> : IAsyncDisposable
{
    private readonly GatewayServices _services;
    private readonly RoutedModel _model;
    private readonly AttemptPolicy _policy;
    private readonly Func<CancellationToken, IAsyncEnumerable<T>> _open;
    private readonly Func<T, bool> _hasContent;
    private readonly Func<int> _redactionCount;
    private readonly CancellationToken _callerToken;
    private readonly long _startTimestamp;

    private CancellationTokenSource? _cts;
    private IAsyncEnumerator<T>? _enumerator;
    private IDisposable? _lease;
    private bool _breakerAcquired;
    private readonly Queue<T> _buffered = new();
    private bool _finished;
    private long? _firstTokenTimestamp;

    public StreamAttempt(
        GatewayServices services,
        RoutedModel model,
        AttemptPolicy policy,
        Func<CancellationToken, IAsyncEnumerable<T>> open,
        Func<T, bool> hasContent,
        Func<int> redactionCount,
        CancellationToken callerToken)
    {
        _services = services;
        _model = model;
        _policy = policy;
        _open = open;
        _hasContent = hasContent;
        _redactionCount = redactionCount;
        _callerToken = callerToken;
        _startTimestamp = services.Time.GetTimestamp();
    }

    /// <summary>Gets the current item.</summary>
    public T Current { get; private set; } = default!;

    /// <summary>Gets or sets the number of completion tokens the provider reported (0 to estimate from the text).</summary>
    public int ReportedCompletionTokens { get; set; }

    /// <summary>Gets the time from the start of the attempt to its first token, or <see langword="null"/> before one arrived.</summary>
    public TimeSpan? TimeToFirstToken => _firstTokenTimestamp is { } first ? _services.Time.GetElapsedTime(_startTimestamp, first) : null;

    /// <summary>Gets or sets the number of characters generated, for the estimate.</summary>
    public long GeneratedCharacters { get; set; }

    /// <summary>Gets the names of the tool calls the model made, for the Outbound record.</summary>
    public List<string> ToolCalls { get; } = [];

    /// <summary>Gets or sets the UTF-8 size of the arguments of those calls, in total.</summary>
    public int ToolArgumentBytes { get; set; }

    /// <summary>
    /// Opens the stream and waits for the first item with content (or the end of the stream), retrying on 429 and 5xx.
    /// </summary>
    public async Task StartAsync()
    {
        if (!_services.Breakers.TryAcquire(_model.ProviderId))
        {
            throw new ProviderException(
                PillionErrorCode.ProviderUnavailable,
                $"Model provider '{_model.ProviderId}' is paused after repeated failures; it will be retried shortly.",
                isProviderFault: false);
        }

        _breakerAcquired = true;
        _lease = await _services.Limiter.AcquireAsync(_model.ProviderId, ConcurrencyLimiter.LaneFor(_policy.TaskClass), _callerToken).ConfigureAwait(false);

        for (var attempt = 0; ; attempt++)
        {
            _cts = CancellationTokenSource.CreateLinkedTokenSource(_callerToken);
            if (_policy.OverallDeadline is { } deadline)
            {
                var remaining = deadline - _services.Time.GetElapsedTime(_startTimestamp);
                _cts.CancelAfter(remaining > TimeSpan.Zero ? remaining : TimeSpan.Zero);
            }
            else if (_policy.FirstTokenTimeout is { } firstToken)
            {
                _cts.CancelAfter(firstToken);
            }

            try
            {
                _enumerator = _open(_cts.Token).GetAsyncEnumerator(_cts.Token);
                await MoveToFirstContentAsync().ConfigureAwait(false);
                if (_policy.OverallDeadline is null)
                {
                    _cts.CancelAfter(Timeout.InfiniteTimeSpan);
                }

                return;
            }
            catch (Exception ex) when (Classify(ex) is ProviderException { IsRetryable: true } retryable && attempt < _policy.MaxRetries)
            {
                await ResetAsync().ConfigureAwait(false);
                var delay = Backoff(attempt);
                _services.Progress.Report(new ModelProgress(
                    _policy.RequestId,
                    _model.ProviderId,
                    _model.ModelId,
                    ModelProgressKind.Retrying,
                    _services.Time.GetElapsedTime(_startTimestamp),
                    $"{retryable.Message} Retrying in {delay.TotalSeconds:0.#} s."));
                await Task.Delay(delay, _services.Time, _callerToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !_callerToken.IsCancellationRequested)
            {
                throw Classify(ex);
            }
        }
    }

    /// <summary>Moves to the next item.</summary>
    /// <returns><see langword="true"/> while there are items.</returns>
    public async ValueTask<bool> MoveNextAsync()
    {
        if (_buffered.TryDequeue(out var buffered))
        {
            Current = buffered;
            return true;
        }

        if (_finished || _enumerator is null)
        {
            return false;
        }

        try
        {
            if (await _enumerator.MoveNextAsync().ConfigureAwait(false))
            {
                Current = _enumerator.Current;
                return true;
            }

            _finished = true;
            return false;
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !_callerToken.IsCancellationRequested)
        {
            throw Classify(ex);
        }
    }

    /// <summary>Records the outcome in the Outbound log and the circuit breaker. Call once.</summary>
    /// <param name="outcome">How the attempt ended.</param>
    /// <param name="failure">The failure, if any.</param>
    public void Complete(OutboundOutcome outcome, Exception? failure = null)
    {
        if (_breakerAcquired)
        {
            _breakerAcquired = false;
            if (outcome == OutboundOutcome.Succeeded)
            {
                _services.Breakers.RecordSuccess(_model.ProviderId);
            }
            else if (failure is ProviderException { IsProviderFault: true })
            {
                _services.Breakers.RecordFailure(_model.ProviderId);
            }
            else
            {
                _services.Breakers.RecordNeutral(_model.ProviderId);
            }
        }

        var completionTokens = ReportedCompletionTokens > 0 ? ReportedCompletionTokens : (int)((GeneratedCharacters + 3) / 4);
        _services.OutboundLog.Record(new OutboundRecord(
            _services.Time.GetUtcNow() - _services.Time.GetElapsedTime(_startTimestamp),
            _policy.RequestId,
            _model.ProviderId,
            _model.ModelId,
            _policy.TaskClass,
            _model.IsLocal,
            _policy.PromptTokens,
            completionTokens,
            SafeRedactionCount(),
            _services.Time.GetElapsedTime(_startTimestamp),
            outcome,
            failure switch
            {
                ProviderException p => p.Code,
                PillionRpcException r => r.Error.Code,
                OperationCanceledException => PillionErrorCode.Cancelled,
                _ => null,
            },
            _policy.PromptTokensCounted,
            CacheHit: false,
            TimeToFirstToken,
            _policy.ContextItems,
            _policy.ContextTrimmed,
            _policy.ToolsOffered,
            ToolCalls.Count > 0 ? ToolCalls.ToList() : null,
            ToolArgumentBytes,
            _policy.ToolResultBytes));
    }

    public async ValueTask DisposeAsync()
    {
        await ResetAsync().ConfigureAwait(false);
        if (_breakerAcquired)
        {
            _breakerAcquired = false;
            _services.Breakers.RecordNeutral(_model.ProviderId);
        }

        _lease?.Dispose();
        _lease = null;
    }

    private int SafeRedactionCount()
    {
        try
        {
            return _redactionCount();
        }
        catch (ObjectDisposedException)
        {
            return 0;
        }
    }

    private async Task MoveToFirstContentAsync()
    {
        var progressDelay = _model.IsLocal ? _services.Options.LocalProgressDelay : (TimeSpan?)null;
        var reportedWaiting = false;
        var reportedSlow = false;
        while (true)
        {
            var moveNext = _enumerator!.MoveNextAsync().AsTask();
            if (_model.IsLocal && _policy.LocalBudget is { } budget)
            {
                // A slow local model reports progress instead of failing (NF1): the request keeps waiting.
                while (!moveNext.IsCompleted)
                {
                    var elapsed = _services.Time.GetElapsedTime(_startTimestamp);
                    var next = !reportedWaiting ? progressDelay!.Value - elapsed : !reportedSlow ? budget - elapsed : Timeout.InfiniteTimeSpan;
                    if (next == Timeout.InfiniteTimeSpan)
                    {
                        break;
                    }

                    using var wait = CancellationTokenSource.CreateLinkedTokenSource(_callerToken);
                    var delay = Task.Delay(next > TimeSpan.Zero ? next : TimeSpan.Zero, _services.Time, wait.Token);
                    if (await Task.WhenAny(moveNext, delay).ConfigureAwait(false) == moveNext)
                    {
                        await wait.CancelAsync().ConfigureAwait(false);
                        break;
                    }

                    elapsed = _services.Time.GetElapsedTime(_startTimestamp);
                    if (!reportedWaiting)
                    {
                        reportedWaiting = true;
                        _services.Progress.Report(new ModelProgress(_policy.RequestId, _model.ProviderId, _model.ModelId, ModelProgressKind.WaitingForLocalModel, elapsed, $"Waiting for {_model.ModelId} to load or read the prompt..."));
                    }
                    else
                    {
                        reportedSlow = true;
                        _services.Progress.Report(new ModelProgress(_policy.RequestId, _model.ProviderId, _model.ModelId, ModelProgressKind.SlowerThanExpected, elapsed, $"{_model.ModelId} is taking longer than expected; still waiting."));
                    }
                }
            }

            if (!await moveNext.ConfigureAwait(false))
            {
                _finished = true;
                return;
            }

            // Items without content (a role-only or usage-only update) are kept for the caller; the first token is
            // the first item with content.
            _buffered.Enqueue(_enumerator.Current);
            if (_hasContent(_enumerator.Current))
            {
                _firstTokenTimestamp = _services.Time.GetTimestamp();
                return;
            }
        }
    }

    private async ValueTask ResetAsync()
    {
        if (_enumerator is not null)
        {
            try
            {
                await _enumerator.DisposeAsync().ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is OperationCanceledException or HttpRequestException or IOException or ProviderException)
            {
                // The stream already failed or was cancelled; disposing it is best effort.
            }

            _enumerator = null;
        }

        _cts?.Dispose();
        _cts = null;
        _buffered.Clear();
        _finished = false;
    }

    private TimeSpan Backoff(int attempt)
    {
        // Exponential backoff with full jitter.
        var ceiling = Math.Min(_services.Options.RetryMaxDelay.TotalMilliseconds, _services.Options.RetryBaseDelay.TotalMilliseconds * Math.Pow(2, attempt));
        return TimeSpan.FromMilliseconds(ceiling * (0.5 + (Random.Shared.NextDouble() / 2)));
    }

    /// <summary>Turns any failure into the exception the gateway acts on.</summary>
    private Exception Classify(Exception ex)
    {
        if (ex is OperationCanceledException && _callerToken.IsCancellationRequested)
        {
            return ex;
        }

        var timedOut = ex is OperationCanceledException && _cts is { IsCancellationRequested: true };
        if (timedOut && _policy.OverallDeadline is not null)
        {
            return new AutocompleteDroppedException();
        }

        if (timedOut)
        {
            return new ProviderException(
                PillionErrorCode.ProviderUnavailable,
                $"{_model.ModelId} did not start answering within {_policy.FirstTokenTimeout?.TotalSeconds ?? 0:0} s.",
                innerException: ex);
        }

        return ex switch
        {
            ProviderException or PillionRpcException or AutocompleteDroppedException => ex,
            RegexMatchTimeoutException => new PillionRpcException(
                new PillionError
                {
                    Code = PillionErrorCode.PolicyDenied,
                    Message = "The request could not be checked for secrets in time, so it was not sent.",
                },
                ex),
            HttpRequestException http when http.StatusCode is { } status => new ProviderException(
                PillionErrorCode.ProviderUnavailable,
                $"Model provider '{_model.ProviderId}' returned HTTP {(int)status}.",
                status,
                http),
            HttpRequestException http => new ProviderException(
                PillionErrorCode.ProviderUnavailable,
                $"Model provider '{_model.ProviderId}' could not be reached.",
                innerException: http),
            OperationCanceledException or TimeoutException => new ProviderException(
                PillionErrorCode.ProviderUnavailable,
                $"Model provider '{_model.ProviderId}' timed out.",
                innerException: ex),
            _ => ex,
        };
    }
}

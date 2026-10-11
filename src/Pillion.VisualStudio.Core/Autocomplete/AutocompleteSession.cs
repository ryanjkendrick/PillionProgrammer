using System;
using System.Threading;
using System.Threading.Tasks;
using Pillion.Contracts;

namespace Pillion.VisualStudio.Core.Autocomplete;

/// <summary>
/// The autocomplete request pipeline of one text view (design sections 5, 9 and 13). Each <see cref="Start"/> is one
/// attempt with its own request id: it waits out the debounce (200 ms, 350 ms while degraded, none right after an
/// accept), captures the context, decides single-line or multi-line, streams the host's answer through the stop rules
/// and discards it when a newer attempt has started. <see cref="CancelPending"/>, called on every keystroke and caret
/// move, cancels the attempt in flight. All waiting and every RPC await run on thread-pool threads; nothing here
/// blocks the caller, and host failures never surface: they end the attempt with no suggestion and are logged at
/// most once a minute.
/// </summary>
public sealed class AutocompleteSession : IDisposable
{
    private readonly AutocompleteEngine _engine;
    private readonly object _gate = new();
    private CompletionAttempt? _current;
    private CompletionInput? _currentInput;
    private long _latestAttempt;
    private int _acceptedAtLineEnd;
    private bool _disposed;

    internal AutocompleteSession(AutocompleteEngine engine, string key)
    {
        _engine = engine;
        Key = key;
    }

    /// <summary>Gets the session key (unique per engine).</summary>
    public string Key { get; }

    /// <summary>Gets the remembered suggestion for typing through and partial accepts.</summary>
    public ContinuationCache Continuations { get; } = new();

    /// <summary>Gets the number of the newest attempt (0 before the first).</summary>
    public long LatestAttempt => Interlocked.Read(ref _latestAttempt);

    /// <summary>
    /// Gets or sets a value indicating whether the previous suggestion was accepted at the end of a line, which makes
    /// the next request multi-line. The next attempt consumes it.
    /// </summary>
    public bool PreviousAcceptedAtLineEnd
    {
        get => Volatile.Read(ref _acceptedAtLineEnd) != 0;
        set => Volatile.Write(ref _acceptedAtLineEnd, value ? 1 : 0);
    }

    /// <summary>Gets a value indicating whether an attempt is the newest one.</summary>
    /// <param name="attempt">The attempt number.</param>
    /// <returns><see langword="true"/> when no newer attempt has started.</returns>
    public bool IsCurrent(long attempt) => Interlocked.Read(ref _latestAttempt) == attempt;

    /// <summary>Gets the attempt in flight when it was started for exactly this text version and caret.</summary>
    /// <param name="textVersion">The text version.</param>
    /// <param name="caret">The caret position.</param>
    /// <returns>The attempt, or <see langword="null"/>.</returns>
    public CompletionAttempt? CurrentFor(long textVersion, int caret)
    {
        lock (_gate)
        {
            return _current is not null && _currentInput is { } input && input.TextVersion == textVersion && input.Caret == caret ? _current : null;
        }
    }

    /// <summary>Cancels the attempt in flight, if any. Cheap.</summary>
    public void CancelPending()
    {
        CompletionAttempt? current;
        lock (_gate)
        {
            current = _current;
            _current = null;
            _currentInput = null;
        }

        if (current is not null)
        {
            _engine.Log?.Debug("autocomplete.attempt.cancelled", "The attempt in flight was cancelled.", ("request", current.RequestId), ("by", "cancelPending"));
        }

        current?.Cancel();
    }

    /// <summary>Cancels the attempt in flight unless it is the given one (a pushed suggestion that is still streaming).</summary>
    /// <param name="attempt">The attempt number to keep.</param>
    public void CancelPendingExcept(long attempt)
    {
        CompletionAttempt? current;
        lock (_gate)
        {
            if (_current is null || _current.Id == attempt)
            {
                return;
            }

            current = _current;
            _current = null;
            _currentInput = null;
        }

        current.Cancel();
    }

    /// <summary>
    /// Cancels the attempt in flight unless it was started for exactly this text version (and caret, when given).
    /// Called on every keystroke and caret move; the version check keeps an attempt the broker started for the new
    /// text (it may ask before this handler runs) alive.
    /// </summary>
    /// <param name="textVersion">The text version after the change.</param>
    /// <param name="caret">The caret position after a move, or <see langword="null"/> for a text change.</param>
    public void CancelPendingUnless(long textVersion, int? caret)
    {
        CompletionAttempt? current;
        CompletionInput? input;
        lock (_gate)
        {
            if (_current is null || _currentInput is null
                || (_currentInput.TextVersion == textVersion && (caret is null || _currentInput.Caret == caret.Value)))
            {
                return;
            }

            current = _current;
            input = _currentInput;
            _current = null;
            _currentInput = null;
        }

        _engine.Log?.Debug(
            "autocomplete.attempt.cancelled",
            "The attempt in flight was cancelled.",
            ("request", current.RequestId),
            ("by", caret is null ? "textChanged" : "caretMoved"),
            ("attemptVersion", input.TextVersion),
            ("version", textVersion),
            ("attemptCaret", input.Caret),
            ("caret", caret));
        current.Cancel();
    }

    /// <summary>
    /// Starts an attempt, cancelling the previous one. Returns at once; the work runs on the thread pool. Every request
    /// path of the extension (the proposal broker, the coexistence requests outside it, "Pillion: Suggest") comes through
    /// here, so the availability gate (<see cref="AutocompleteEngine.Gate"/>: the feature flag and the disabled
    /// languages) is checked here first, and again after the debounce: with the gate shut the attempt ends at once with
    /// nothing to show and nothing reaches the host.
    /// </summary>
    /// <param name="input">What the editor captured.</param>
    /// <param name="cancellationToken">The caller's token (the proposal broker's), linked to the attempt.</param>
    /// <returns>The attempt.</returns>
    public CompletionAttempt Start(CompletionInput input, CancellationToken cancellationToken)
    {
        if (input is null)
        {
            throw new ArgumentNullException(nameof(input));
        }

        if (input.Text is null)
        {
            throw new ArgumentException("The input has no text.", nameof(input));
        }

        var id = _engine.NextAttempt();
        var cancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var attempt = new CompletionAttempt(id, _engine.RequestIdFor(id), cancellation);
        if (_engine.Gate(input.LanguageId) != SuppressionReason.None)
        {
            CompletionAttempt? superseded;
            lock (_gate)
            {
                superseded = _current;
                _current = null;
                _currentInput = null;
                Interlocked.Exchange(ref _latestAttempt, id);
            }

            superseded?.Cancel();
            cancellation.Cancel();
            cancellation.Dispose();
            _engine.Metrics.Suppressed(input.LanguageId);
            attempt.SetCompleted(null);
            return attempt;
        }

        _engine.Metrics.RequestStarted(id, input.LanguageId);
        CompletionAttempt? previous;
        lock (_gate)
        {
            if (_disposed)
            {
                cancellation.Cancel();
            }

            previous = _current;
            _current = attempt;
            _currentInput = input;
            Interlocked.Exchange(ref _latestAttempt, id);
        }

        previous?.Cancel();
        _ = Task.Run(() => RunAsync(attempt, input, cancellation), CancellationToken.None);
        return attempt;
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        lock (_gate)
        {
            _disposed = true;
        }

        CancelPending();
        Continuations.Clear();
    }

    private static async Task<string?> LookUpDeclarationAsync(Func<CancellationToken, Task<string?>> provider, CancellationToken token)
    {
        try
        {
            return await provider(token).ConfigureAwait(false);
        }
#pragma warning disable CA1031 // A failed lookup falls back to the heuristic.
        catch (Exception)
#pragma warning restore CA1031
        {
            return null;
        }
    }

    private async Task RunAsync(CompletionAttempt attempt, CompletionInput input, CancellationTokenSource cancellation)
    {
        var token = attempt.Token;
        CompletionResult? result = null;
        try
        {
            var text = input.Text!;
            var declarationTask = input.DeclarationProvider is { } provider ? LookUpDeclarationAsync(provider, token) : null;

            var debounce = input.Immediate ? TimeSpan.Zero : _engine.Latency.Debounce;
            if (debounce > TimeSpan.Zero)
            {
                await _engine.Clock.DelayAsync(debounce, token).ConfigureAwait(false);
            }

            // The settings may have changed during the debounce: the gate is checked again before anything is sent.
            var gated = _engine.Gate(input.LanguageId) != SuppressionReason.None;
            if ((gated || !_engine.HostUsable) && !token.IsCancellationRequested && IsCurrent(attempt.Id))
            {
                _engine.Metrics.RequestFailed(attempt.Id, AutocompleteOutcome.Suppressed);
            }

            if (token.IsCancellationRequested || !IsCurrent(attempt.Id) || gated || !_engine.HostUsable)
            {
                _engine.Log?.Debug(
                    "autocomplete.attempt.dropped",
                    "An attempt ended before it asked the model.",
                    ("request", attempt.RequestId),
                    ("cancelled", token.IsCancellationRequested),
                    ("current", IsCurrent(attempt.Id)),
                    ("hostUsable", _engine.HostUsable));
                return;
            }

            var context = ContextCapture.Capture(text, input.Caret, input.VirtualSpaces);
            var reason = MultiLineDecision.Decide(new MultiLineInput
            {
                LineBeforeCaret = context.LineBeforeCaret,
                LineAfterCaret = context.LineAfterCaret,
                PreviousNonBlankLine = context.PreviousNonBlankLine,
                LanguageId = input.LanguageId,
                PreviousAcceptedAtLineEnd = Interlocked.Exchange(ref _acceptedAtLineEnd, 0) != 0,
                Degraded = _engine.Latency.IsDegraded,
                ForceMultiLine = input.ForceMultiLine,
            });
            var multiLine = MultiLineDecision.IsMultiLine(reason);

            string? declaration = null;
            if (declarationTask is not null)
            {
                var budget = _engine.Clock.DelayAsync(AutocompleteEngine.DeclarationBudget, token);
                if (await Task.WhenAny(declarationTask, budget).ConfigureAwait(false) == declarationTask)
                {
                    declaration = await declarationTask.ConfigureAwait(false);
                }
            }

            declaration ??= DeclarationHeuristic.FindEnclosingDeclaration(text, input.Caret, input.LanguageId);
            token.ThrowIfCancellationRequested();

            var request = _engine.BuildRequest(attempt.RequestId, input, context, multiLine, declaration);
            var shaper = new CompletionShaper(new ShapingContext
            {
                LanguageId = input.LanguageId,
                MultiLine = multiLine,
                LineBeforeCaret = context.LineBeforeCaret,
                LineAfterCaret = context.LineAfterCaret,
                LinesBelow = context.LinesBelow,
                StopAfterFirstBlock = reason == MultiLineReason.AfterDeclaration,
            });

            result = await StreamAsync(attempt, request, shaper, reason, token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            _engine.Log?.Debug(
                "autocomplete.attempt.dropped",
                "An attempt was cancelled while it waited or streamed.",
                ("request", attempt.RequestId),
                ("cancelled", token.IsCancellationRequested),
                ("current", IsCurrent(attempt.Id)));
            result = null;
        }
#pragma warning disable CA1031 // Host failures and restarts never surface to the editor.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            var failure = RpcFailure.Describe(ex);
            _engine.Metrics.RequestFailed(attempt.Id, AutocompleteOutcome.Error);
            _engine.Failures.Warning(
                "autocomplete.request.failed",
                "An autocomplete request failed; no suggestion was shown.",
                ("request", attempt.RequestId),
                ("error", ex.GetType().Name),
                ("code", failure.Code?.ToString()),
                ("hostLost", failure.HostLost));
            result = null;
        }
        finally
        {
            _engine.Metrics.RequestEnded(attempt.Id);
            attempt.SetCompleted(IsCurrent(attempt.Id) && !token.IsCancellationRequested ? result : null);
            lock (_gate)
            {
                if (ReferenceEquals(_current, attempt))
                {
                    _current = null;
                    _currentInput = null;
                }
            }

            cancellation.Dispose();
        }
    }

    private async Task<CompletionResult?> StreamAsync(
        CompletionAttempt attempt,
        CompletionRequest request,
        CompletionShaper shaper,
        MultiLineReason reason,
        CancellationToken token)
    {
        var multiLine = MultiLineDecision.IsMultiLine(reason);
        _engine.Metrics.RequestSent(attempt.Id, multiLine, _engine.Latency.IsDegraded);
        var service = AutocompleteOverrides.ModelService ?? _engine.Connection.GetProxy<IModelService>();
        var sent = _engine.Clock.UtcNow;
        var firstChunkSeen = false;
        string? modelId = null;
        string? providerId = null;
        var stoppedEarly = false;
        var recorded = 0;
        var readySent = false;
        string? lastShown = null;
        var lastShownAt = DateTimeOffset.MinValue;
        var updatePending = false;

        CompletionResult Snapshot(string text, bool complete) => new()
        {
            Attempt = attempt.Id,
            RequestId = attempt.RequestId,
            Text = text,
            IsMultiLine = multiLine,
            MultiLineReason = reason,
            StopReason = complete ? shaper.StopReason : CompletionStopReason.None,
            IsComplete = complete,
            Latency = _engine.Clock.UtcNow - sent,
            ModelId = modelId,
            ProviderId = providerId,
        };

        void RecordLatency(TimeSpan latency)
        {
            if (Interlocked.Exchange(ref recorded, 1) == 0)
            {
                _engine.Latency.Record(latency);
                _engine.Metrics.SuggestionReady(attempt.Id, latency);
            }
        }

        // The display rule (decision of 2026-10-11): the first visible text is shown at once (a single-line request's
        // first chunk, a multi-line request's first complete line) and the suggestion then grows as chunks stream, with
        // at most one update per DisplayUpdateInterval; what is shown has passed the stop rules.
        void ShowProgress()
        {
            CompletionResult? ready = null;
            CompletionResult? update = null;
            var wait = TimeSpan.Zero;
            lock (shaper)
            {
                var text = shaper.DisplayText;
                if (text is null || text == lastShown || attempt.Completed.IsCompleted || !IsCurrent(attempt.Id) || token.IsCancellationRequested)
                {
                    return;
                }

                var now = _engine.Clock.UtcNow;
                if (!readySent)
                {
                    readySent = true;
                    ready = Snapshot(text, complete: false);
                }
                else if (now - lastShownAt >= AutocompleteEngine.DisplayUpdateInterval)
                {
                    update = Snapshot(text, complete: false);
                }
                else if (!updatePending)
                {
                    updatePending = true;
                    wait = AutocompleteEngine.DisplayUpdateInterval - (now - lastShownAt);
                }
                else
                {
                    return;
                }

                if (ready is not null || update is not null)
                {
                    lastShown = text;
                    lastShownAt = now;
                }
            }

            if (ready is not null)
            {
                RecordLatency(ready.Latency);
                attempt.SetReady(ready);
            }
            else if (update is not null)
            {
                attempt.Publish(update);
            }
            else
            {
                _ = ShowLaterAsync(wait);
            }
        }

        async Task ShowLaterAsync(TimeSpan wait)
        {
            try
            {
                await _engine.Clock.DelayAsync(wait, token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            lock (shaper)
            {
                updatePending = false;
            }

            ShowProgress();
        }

        var enumerator = service.CompleteAsync(request, token).GetAsyncEnumerator(token);
        try
        {
            while (await enumerator.MoveNextAsync().ConfigureAwait(false))
            {
                var chunk = enumerator.Current;
                if (chunk is null || (!string.IsNullOrEmpty(chunk.RequestId) && !string.Equals(chunk.RequestId, request.RequestId, StringComparison.Ordinal)))
                {
                    continue; // Another request's chunk: stale.
                }

                if (!IsCurrent(attempt.Id) || token.IsCancellationRequested)
                {
                    return null; // Superseded: everything from here on is stale.
                }

                if (!firstChunkSeen)
                {
                    firstChunkSeen = true;
                    _engine.Metrics.FirstChunk(attempt.Id);
                }

                if (!string.IsNullOrEmpty(chunk.ModelId))
                {
                    modelId = chunk.ModelId;
                    providerId = chunk.ProviderId;
                }

                if (chunk.Error is { } error)
                {
                    _engine.Metrics.RequestFailed(attempt.Id, AutocompleteOutcome.Error);
                    _engine.Failures.Warning(
                        "autocomplete.stream.failed",
                        "An autocomplete stream failed part way; no suggestion was shown.",
                        ("request", request.RequestId),
                        ("code", error.Code.ToString()));
                    return null;
                }

                bool stop;
                lock (shaper)
                {
                    stop = shaper.Append(chunk.Text);
                }

                if (stop)
                {
                    stoppedEarly = !chunk.IsFinal;
                    break;
                }

                if (chunk.IsFinal)
                {
                    break;
                }

                ShowProgress();
            }
        }
        finally
        {
            await enumerator.DisposeAsync().ConfigureAwait(false);
        }

        if (stoppedEarly)
        {
            CancelOnHost(request.RequestId);
        }

        string final;
        lock (shaper)
        {
            final = shaper.Complete();
        }

        if (!IsCurrent(attempt.Id))
        {
            return null;
        }

        var result = Snapshot(final, complete: true);
        RecordLatency(result.Latency);
        _engine.Log?.Debug(
            "autocomplete.completed",
            "Autocomplete attempt finished.",
            ("request", request.RequestId),
            ("multiLine", multiLine),
            ("reason", reason.ToString()),
            ("stop", shaper.StopReason.ToString()),
            ("chars", final.Length),
            ("ms", (long)result.Latency.TotalMilliseconds),
            ("model", modelId));
        if (final.Trim().Length == 0)
        {
            _engine.Metrics.RequestFailed(attempt.Id, AutocompleteOutcome.Empty);
            return null;
        }

        return result;
    }

    /// <summary>Tells the host to stop generating after a stop rule fired (best effort, not awaited).</summary>
    private void CancelOnHost(string requestId)
    {
        _ = Task.Run(async () =>
        {
            try
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                await _engine.Connection.GetProxy<IModelService>().CancelAsync(requestId, timeout.Token).ConfigureAwait(false);
            }
#pragma warning disable CA1031 // Best effort: the host also stops when the stream is disposed.
            catch (Exception)
#pragma warning restore CA1031
            {
            }
        });
    }
}

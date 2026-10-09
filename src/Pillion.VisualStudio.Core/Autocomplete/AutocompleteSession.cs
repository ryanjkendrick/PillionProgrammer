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

        current?.Cancel();
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
        lock (_gate)
        {
            if (_current is null || _currentInput is null
                || (_currentInput.TextVersion == textVersion && (caret is null || _currentInput.Caret == caret.Value)))
            {
                return;
            }

            current = _current;
            _current = null;
            _currentInput = null;
        }

        current.Cancel();
    }

    /// <summary>Starts an attempt, cancelling the previous one. Returns at once; the work runs on the thread pool.</summary>
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

            if (token.IsCancellationRequested || !IsCurrent(attempt.Id) || !_engine.HostUsable)
            {
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
            result = null;
        }
#pragma warning disable CA1031 // Host failures and restarts never surface to the editor.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            var failure = RpcFailure.Describe(ex);
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
        var service = _engine.Connection.GetProxy<IModelService>();
        var sent = _engine.Clock.UtcNow;
        string? modelId = null;
        string? providerId = null;
        var stoppedEarly = false;
        var recorded = 0;
        var graceStarted = false;

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
            }
        }

        async Task GraceAsync()
        {
            try
            {
                await _engine.Clock.DelayAsync(AutocompleteEngine.PartialGrace, token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            CompletionResult? partial;
            lock (shaper)
            {
                partial = shaper.PartialText is { } text && !attempt.Completed.IsCompleted && IsCurrent(attempt.Id)
                    ? Snapshot(text, complete: false)
                    : null;
            }

            if (partial is not null)
            {
                RecordLatency(partial.Latency);
                attempt.SetReady(partial);
            }
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

                if (!string.IsNullOrEmpty(chunk.ModelId))
                {
                    modelId = chunk.ModelId;
                    providerId = chunk.ProviderId;
                }

                if (chunk.Error is { } error)
                {
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

                if (multiLine && !graceStarted && shaper.CompleteContentLines > 0)
                {
                    graceStarted = true;
                    _ = GraceAsync();
                }
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
        return final.Trim().Length == 0 ? null : result;
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

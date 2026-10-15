using System.Diagnostics;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Pillion.Contracts;
using Pillion.ModelGateway;
using Pillion.ModelGateway.Editing;

namespace Pillion.Host.InlineEdit;

/// <summary>
/// Turns an <see cref="InlineEditRequest"/> into a validated proposal (work item 1.8; design sections 5, 6, 8 and 12).
/// </summary>
/// <remarks>
/// <para>
/// Each model call goes through <see cref="IModelGateway.ChatAsync"/> with <see cref="TaskClass.InlineEdit"/>, so the
/// gateway routes it (Balanced online, Local-strong offline, or the request's override), redacts the prompt for a cloud
/// provider, restores placeholders in the reply and writes one Outbound log record per call.
/// </para>
/// <para>
/// The reply is a set of search-and-replace blocks (<see cref="EditBlockParser"/>) applied to an in-memory copy of the
/// code (<see cref="EditBlockApplier"/>). Malformed blocks, blocks that do not match exactly once, code without blocks
/// and placeholders no secret stands behind are sent back to the model with a corrective message, up to
/// <see cref="MaxRetries"/> times. A C# or VB edit that raises the file's syntax error count is sent back once with the
/// errors; if the retry is no better, the best proposal is returned with a warning.
/// </para>
/// <para>
/// While the model streams, complete lines of the result that cannot change any more (the code before the edit being
/// written and the replacement lines so far) are sent early; if the final result does not start with them, it is sent
/// with <see cref="InlineEditChunk.ResetText"/>.
/// </para>
/// <para>
/// A scope of at most <see cref="InlineEditOptions.WholeScopeMaxLines"/> lines (and any command but <c>/test</c> and
/// <c>/explain</c>) is edited in whole-scope mode instead: the model rewrites the whole scope as plain code
/// (<see cref="WholeScopeParser"/>), its complete lines stream out as they arrive, and the same checks apply to the
/// result (not empty, comments only for <c>/doc</c>, Roslyn syntax, placeholders). A refinement works the same way on
/// the previous proposal. An unusable whole-scope reply is sent back once; if the retry is no better the request starts
/// again in the block format above, with one retry, so it degrades rather than fails.
/// </para>
/// </remarks>
internal sealed partial class InlineEditEngine
{
    /// <summary>How many times a reply that cannot be used is sent back to the model before the request fails.</summary>
    public const int MaxRetries = 2;

    /// <summary>How many times a whole-scope reply that cannot be used is sent back before the request falls back to blocks.</summary>
    public const int WholeScopeMaxRetries = 1;

    /// <summary>How many times a reply that cannot be used is sent back to the model after the request fell back from whole-scope to blocks.</summary>
    public const int FallbackMaxRetries = 1;

    private const int MaxReportedErrors = 8;

    private static readonly HashSet<string> CommentDocumented = new(StringComparer.OrdinalIgnoreCase)
    {
        "csharp", "vb", "fsharp", "c", "cpp", "javascript", "javascriptreact", "typescript", "typescriptreact",
    };

    private readonly IModelGateway _gateway;
    private readonly InlineEditPromptBuilder _prompts;
    private readonly ISyntaxValidator _validator;
    private readonly InlineEditConversations _conversations;
    private readonly InlineEditOptions _options;
    private readonly Context.Retrieval.IInlineEditContext? _related;
    private readonly ILogger _logger;

    /// <summary>Initializes a new instance of the <see cref="InlineEditEngine"/> class.</summary>
    /// <param name="gateway">The Model Gateway.</param>
    /// <param name="prompts">The prompt builder.</param>
    /// <param name="validator">The syntax validator.</param>
    /// <param name="conversations">The instructions of recent inline sessions.</param>
    /// <param name="logger">The logger.</param>
    /// <param name="options">The inline edit options; the defaults when <see langword="null"/>.</param>
    /// <param name="related">Retrieves related code for the prompt (work item 2.12); none when <see langword="null"/>.</param>
    public InlineEditEngine(
        IModelGateway gateway,
        InlineEditPromptBuilder prompts,
        ISyntaxValidator validator,
        InlineEditConversations conversations,
        ILogger<InlineEditEngine>? logger = null,
        InlineEditOptions? options = null,
        Context.Retrieval.IInlineEditContext? related = null)
    {
        ArgumentNullException.ThrowIfNull(gateway);
        ArgumentNullException.ThrowIfNull(prompts);
        ArgumentNullException.ThrowIfNull(validator);
        ArgumentNullException.ThrowIfNull(conversations);
        _gateway = gateway;
        _prompts = prompts;
        _validator = validator;
        _conversations = conversations;
        _options = options ?? new InlineEditOptions();
        _related = related;
        _logger = logger ?? NullLogger<InlineEditEngine>.Instance;
    }

    /// <summary>Runs an inline edit.</summary>
    /// <param name="request">The request.</param>
    /// <param name="cancellationToken">A token that cancels the request.</param>
    /// <returns>The chunks; see <see cref="InlineEditChunk"/> for the stream rules.</returns>
    /// <exception cref="PillionRpcException">The request failed before any text was sent.</exception>
    public async IAsyncEnumerable<InlineEditChunk> RunAsync(InlineEditRequest request, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.Command == InlineEditCommand.None && string.IsNullOrWhiteSpace(request.Instruction))
        {
            throw new ArgumentException("An inline edit needs an instruction or a slash command.", nameof(request));
        }

        var started = Stopwatch.GetTimestamp();
        var state = new RunState(request);
        var command = request.Command;
        var languageId = request.LanguageId ?? string.Empty;
        var isEdit = command is InlineEditCommand.None or InlineEditCommand.Doc or InlineEditCommand.Fix;
        var validate = isEdit && _validator.Supports(languageId);
        var baseErrors = validate ? _validator.GetErrors(state.Compose(state.Scope), languageId, cancellationToken).Count : 0;
        var baseCode = state.Previous ?? state.Scope;
        var history = _conversations.Get(request.ConversationId);
        var related = await GetRelatedAsync(request, cancellationToken).ConfigureAwait(false);
        List<ChatMessage> BuildMessages(bool wholeScope) => [.. _prompts.Build(new InlineEditPromptInput(
            languageId,
            FileName(request.FilePath),
            command,
            request.Instruction ?? string.Empty,
            baseCode,
            request.ScopeStartLine + 1,
            state.Previous is not null,
            state.Before,
            state.After,
            request.Diagnostics ?? [],
            history,
            wholeScope,
            related))];

        // Whole-scope mode for a small scope, blocks for a large one; /test and /explain never edit the scope.
        var wholeLimit = _options.WholeScopeMaxLines;
        var lineCount = WholeScopeParser.CountLines(baseCode);
        var useWhole = isEdit && lineCount <= wholeLimit;
        var fellBack = false;
        LogMode(_logger, request.RequestId, useWhole ? "whole-scope" : "blocks", lineCount, wholeLimit);
        var messages = BuildMessages(useWhole);
        var maxRetries = useWhole ? WholeScopeMaxRetries : MaxRetries;

        var working = baseCode;
        var retries = 0;
        var syntaxBounced = false;
        (string Text, IReadOnlyList<SyntaxError> Errors, string? Explanation)? best = null;
        Outcome? outcome = null;
        PillionError? failure = null;
        Exception? failureException = null;

        for (var attempt = 1; outcome is null && failure is null; attempt++)
        {
            // 1. One model call, previewing what can no longer change.
            var reply = new StringBuilder();
            state.PreviewStopped = command == InlineEditCommand.Explain;
            var chat = new ChatRequest
            {
                RequestId = request.RequestId,
                Messages = [.. messages],
                ModelId = string.IsNullOrWhiteSpace(request.ModelId) ? null : request.ModelId,
                Profile = request.Profile,
            };

            var stream = _gateway.ChatAsync(chat, TaskClass.InlineEdit, cancellationToken).GetAsyncEnumerator(cancellationToken);
            try
            {
                while (true)
                {
                    ChatChunk chunk;
                    try
                    {
                        if (!await stream.MoveNextAsync().ConfigureAwait(false))
                        {
                            break;
                        }

                        chunk = stream.Current;
                    }
                    catch (PillionRpcException ex)
                    {
                        failure = ex.Error;
                        failureException = ex;
                        break;
                    }

                    state.Observe(chunk);
                    if (chunk.IsFinal && chunk.Error is not null)
                    {
                        failure = chunk.Error;
                        break;
                    }

                    if (chunk.Text.Length == 0)
                    {
                        continue;
                    }

                    reply.Append(chunk.Text);
                    if (!state.PreviewStopped &&
                        chunk.Text.Contains('\n', StringComparison.Ordinal) &&
                        (useWhole ? PreviewWhole(state, reply.ToString(), baseCode) : Preview(state, reply.ToString(), working)) is { } preview)
                    {
                        yield return preview;
                    }
                }
            }
            finally
            {
                await stream.DisposeAsync().ConfigureAwait(false);
            }

            if (failure is not null)
            {
                break;
            }

            // 2. Read the reply.
            var text = reply.ToString();
            var evaluation = useWhole ? EvaluateWhole(state, text, baseCode, attempt) : Evaluate(state, text, working, attempt);
            if (evaluation.Correction is null && evaluation.Text is not null)
            {
                var introduced = EditText.FindIntroducedPlaceholders(evaluation.Text, state.Scope, state.Previous);
                if (introduced.Count > 0)
                {
                    evaluation = Evaluation.Bounce(_prompts.Placeholders(introduced, useWhole), 0, PlaceholderError(introduced));
                }
            }

            if (evaluation.Correction is null && evaluation.Error is not null)
            {
                failure = evaluation.Error;
                break;
            }

            if (evaluation.Correction is not null)
            {
                state.RejectedBlocks += evaluation.Rejected;
                if (retries < maxRetries)
                {
                    retries++;
                    LogBounced(_logger, request.RequestId, attempt, evaluation.Reason);
                    messages.Add(new ChatMessage { Role = ChatRole.Assistant, Content = text });
                    messages.Add(new ChatMessage { Role = ChatRole.User, Content = evaluation.Correction });
                    continue;
                }

                if (best is { } fallback && evaluation.Error!.Code != PillionErrorCode.PolicyDenied)
                {
                    outcome = new Outcome(fallback.Text, fallback.Explanation, SyntaxWarning(fallback.Errors.Count - baseErrors, fallback.Errors, state));
                    break;
                }

                if (useWhole)
                {
                    // The model cannot rewrite this scope as plain code: start again in the block format.
                    LogFellBack(_logger, request.RequestId, attempt, evaluation.Reason);
                    useWhole = false;
                    fellBack = true;
                    retries = 0;
                    maxRetries = FallbackMaxRetries;
                    working = baseCode;
                    messages = BuildMessages(wholeScope: false);
                    continue;
                }

                failure = evaluation.Error;
                break;
            }

            // 3. Validate C# and VB syntax: an edit that adds syntax errors goes back once.
            if (validate && evaluation.Text is { } proposal)
            {
                var errors = _validator.GetErrors(state.Compose(proposal), languageId, cancellationToken);
                if (errors.Count > baseErrors)
                {
                    if (best is null || errors.Count < best.Value.Errors.Count)
                    {
                        best = (proposal, errors, evaluation.Explanation);
                    }

                    if (!syntaxBounced)
                    {
                        syntaxBounced = true;
                        working = proposal;
                        var reason = string.Create(CultureInfo.InvariantCulture, $"{errors.Count - baseErrors} new syntax error(s)");
                        LogBounced(_logger, request.RequestId, attempt, reason);

                        messages.Add(new ChatMessage { Role = ChatRole.Assistant, Content = text });
                        messages.Add(new ChatMessage
                        {
                            Role = ChatRole.User,
                            Content = _prompts.Syntax(DescribeErrors(errors, state), proposal, request.ScopeStartLine + 1, languageId, useWhole),
                        });
                        continue;
                    }

                    var chosen = best.Value;
                    outcome = new Outcome(chosen.Text, chosen.Explanation, SyntaxWarning(chosen.Errors.Count - baseErrors, chosen.Errors, state));
                    break;
                }
            }

            outcome = new Outcome(evaluation.Text, evaluation.Explanation, evaluation.Warning);
        }

        if (outcome is null)
        {
            // Every failure fails the call, even after preview text: a partial edit means nothing (the VSIX discards it).
            failure ??= new PillionError { Code = PillionErrorCode.ProviderUnavailable, Message = "The inline edit failed." };
            LogFailed(_logger, request.RequestId, failure.Code, failure.Message);
            throw failureException as PillionRpcException ?? new PillionRpcException(failure);
        }

        _conversations.Add(request.ConversationId, HistoryEntry(request));
        if (outcome.Text is { } final)
        {
            if (final.StartsWith(state.Emitted, StringComparison.Ordinal))
            {
                if (final.Length > state.Emitted.Length)
                {
                    yield return state.Text(final[state.Emitted.Length..], reset: false);
                }
            }
            else
            {
                yield return state.Text(final, reset: true);
            }

            state.Emitted = final;
        }

        var elapsedMs = (long)Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        var finalMode = fellBack ? "blocks (after whole-scope)" : useWhole ? "whole-scope" : "blocks";
        LogFinished(_logger, request.RequestId, finalMode, state.RejectedBlocks, elapsedMs);

        yield return state.Final(outcome.Explanation, outcome.Warning);
    }

    /// <summary>Retrieved code for the prompt (work item 2.12); none without a retriever or when it fails.</summary>
    private async Task<IReadOnlyList<ContextChunk>> GetRelatedAsync(InlineEditRequest request, CancellationToken cancellationToken)
    {
        if (_related is null)
        {
            return [];
        }

        try
        {
            return await _related.GetRelatedAsync(request, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            LogNoRelated(_logger, request.RequestId, ex.Message);
            return [];
        }
    }

    /// <summary>The early preview: the result's complete lines that later output can no longer change.</summary>
    private static InlineEditChunk? Preview(RunState state, string reply, string working)
    {
        string candidate;
        if (state.Request.Command == InlineEditCommand.Test)
        {
            var (blocks, _) = FencedCodeParser.Parse(reply, isComplete: false);
            if (blocks.Count == 0 || blocks[0].Code.Length == 0)
            {
                return null;
            }

            candidate = blocks[0].Code;
        }
        else
        {
            var parse = EditBlockParser.Parse(reply, isComplete: false);
            if (parse.Malformed.Count > 0)
            {
                state.PreviewStopped = true;
                return null;
            }

            IReadOnlyList<EditBlock> blocks = parse.Partial is null ? parse.Blocks : [.. parse.Blocks, parse.Partial];
            if (blocks.Count == 0)
            {
                return null;
            }

            var application = EditBlockApplier.Apply(working, blocks);
            if (!application.Succeeded)
            {
                state.PreviewStopped = true;
                return null;
            }

            if (application.LastEditEndLine == 0)
            {
                return null;
            }

            // Lines up to the end of the latest edit; the newline after the last one is sent with the next line.
            candidate = string.Join('\n', application.Text.Split('\n').Take(application.LastEditEndLine));
        }

        return Emit(state, candidate);
    }

    /// <summary>The early preview in whole-scope mode: the complete lines of the code written so far.</summary>
    private static InlineEditChunk? PreviewWhole(RunState state, string reply, string baseCode)
    {
        var candidate = WholeScopeParser.Preview(reply, baseCode, out var stop);
        if (stop)
        {
            state.PreviewStopped = true;
            return null;
        }

        return candidate is null ? null : Emit(state, candidate);
    }

    /// <summary>Sends the part of a preview candidate that was not sent yet, or stops the preview when it contradicts what was.</summary>
    private static InlineEditChunk? Emit(RunState state, string candidate)
    {
        if (!candidate.StartsWith(state.Emitted, StringComparison.Ordinal) ||
            EditText.FindIntroducedPlaceholders(candidate, state.Scope, state.Previous).Count > 0)
        {
            state.PreviewStopped = true;
            return null;
        }

        if (candidate.Length == state.Emitted.Length)
        {
            return null;
        }

        var delta = candidate[state.Emitted.Length..];
        state.Emitted = candidate;
        return state.Text(delta, reset: false);
    }

    /// <summary>Reads a complete reply.</summary>
    private Evaluation Evaluate(RunState state, string reply, string working, int attempt)
    {
        switch (state.Request.Command)
        {
            case InlineEditCommand.Explain:
                var answer = reply.Trim();
                return answer.Length > 0
                    ? Evaluation.Done(null, answer)
                    : Evaluation.Fail(UnusableError("the model returned an empty answer", attempt));

            case InlineEditCommand.Test:
                var (code, prose) = FencedCodeParser.Parse(reply);
                if (code.Count == 0 || code.All(c => c.Code.Trim().Length == 0))
                {
                    return Evaluation.Bounce(_prompts.NoCode(), 0, UnusableError("the reply has no test code", attempt));
                }

                var test = code.OrderByDescending(c => c.Code.Length).First();
                return Evaluation.Done(test.Code, Nullable(prose));
        }

        var parse = EditBlockParser.Parse(reply);
        if (parse.Malformed.Count > 0)
        {
            return Evaluation.Bounce(
                _prompts.Malformed(parse.Malformed),
                parse.Malformed.Count,
                UnusableError($"{parse.Malformed.Count} edit block(s) were malformed ({parse.Malformed[0].Reason})", attempt));
        }

        if (parse.Blocks.Count == 0)
        {
            if (parse.HasFenceOutsideBlocks)
            {
                return Evaluation.Bounce(_prompts.NoBlocks(), 0, UnusableError("the reply had code but no edit blocks", attempt));
            }

            // No blocks and no code: the model says no change is needed.
            return Evaluation.Done(working, Nullable(parse.Explanation), "The model proposed no change.");
        }

        var application = EditBlockApplier.Apply(working, parse.Blocks);
        if (!application.Succeeded)
        {
            return Evaluation.Bounce(
                _prompts.Unmatched(application.Failures),
                application.Failures.Count,
                UnusableError(application.Failures[0].Describe(), attempt));
        }

        // /doc adds comments only; checked for languages whose documentation is comments (not Python docstrings).
        if (state.Request.Command == InlineEditCommand.Doc &&
            CommentDocumented.Contains(state.Request.LanguageId ?? string.Empty) &&
            EditText.CodeChangesBesidesComments(working, application.Text) is { Count: > 0 } changes)
        {
            return Evaluation.Bounce(
                _prompts.DocOnly(changes),
                0,
                UnusableError($"/doc changed code, not only comments ({changes[0]})", attempt));
        }

        return Evaluation.Done(application.Text, Nullable(parse.Explanation));
    }

    /// <summary>Reads a complete whole-scope reply: the rewritten code, checked like the result of applied blocks.</summary>
    private Evaluation EvaluateWhole(RunState state, string reply, string baseCode, int attempt)
    {
        var parsed = WholeScopeParser.Parse(reply, baseCode);
        if (parsed.Code is not { } code)
        {
            return Evaluation.Bounce(_prompts.NoWholeCode(), 0, UnusableError($"the reply had no usable code ({parsed.Problem})", attempt));
        }

        // /doc adds comments only; checked for languages whose documentation is comments (not Python docstrings).
        if (state.Request.Command == InlineEditCommand.Doc &&
            CommentDocumented.Contains(state.Request.LanguageId ?? string.Empty) &&
            EditText.CodeChangesBesidesComments(baseCode, code) is { Count: > 0 } changes)
        {
            return Evaluation.Bounce(
                _prompts.DocOnly(changes, wholeScope: true),
                0,
                UnusableError($"/doc changed code, not only comments ({changes[0]})", attempt));
        }

        return string.Equals(code, baseCode, StringComparison.Ordinal)
            ? Evaluation.Done(baseCode, Nullable(parsed.Explanation), "The model proposed no change.")
            : Evaluation.Done(code, Nullable(parsed.Explanation));
    }

    private static PillionError UnusableError(string reason, int attempts) => new()
    {
        Code = PillionErrorCode.ProviderUnavailable,
        Message = $"The model's edit could not be used after {attempts} {(attempts == 1 ? "attempt" : "attempts")}: {reason.TrimEnd('.')}.",
        RetryHint = "Try again, rephrase the instruction, or choose a stronger model.",
        Details = "inline-edit: unusable reply",
    };

    private static PillionError PlaceholderError(IReadOnlyList<string> placeholders) => new()
    {
        Code = PillionErrorCode.PolicyDenied,
        Message = $"The proposed edit was rejected because it contains {string.Join(", ", placeholders)}, a placeholder for a removed secret that stands for nothing in this code. Nothing was changed.",
        RetryHint = "Try again; if it keeps happening, remove the secret from the selection or use a local model.",
        Details = "inline-edit: unresolved placeholder",
    };

    private static string SyntaxWarning(int added, IReadOnlyList<SyntaxError> errors, RunState state)
    {
        var first = DescribeErrors(errors, state).FirstOrDefault();
        return string.Create(CultureInfo.InvariantCulture, $"The proposal has {added} more syntax error(s) than the original code") +
            (first is null ? "." : $", for example {first}.");
    }

    /// <summary>Words syntax errors with document line numbers, those in the edited code first.</summary>
    private static List<string> DescribeErrors(IReadOnlyList<SyntaxError> errors, RunState state)
    {
        var firstLine = state.Request.ScopeStartLine - state.BeforeLineCount;
        var inScope = errors.Where(e => e.Line >= state.BeforeLineCount).ToList();
        return (inScope.Count > 0 ? inScope : errors)
            .Take(MaxReportedErrors)
            .Select(e => string.Create(CultureInfo.InvariantCulture, $"line {firstLine + e.Line + 1}: {e.Code}: {e.Message}"))
            .ToList();
    }

    private static string HistoryEntry(InlineEditRequest request)
    {
        var instruction = request.Instruction?.Trim() ?? string.Empty;
        if (request.Command == InlineEditCommand.None)
        {
            return instruction;
        }

        var command = "/" + request.Command.ToString().ToLowerInvariant();
        return instruction.Length == 0 ? command : $"{command} {instruction}";
    }

    private static string FileName(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return string.Empty;
        }

        var name = Path.GetFileName(path.TrimEnd('\\', '/'));
        return string.IsNullOrEmpty(name) ? path : name;
    }

    private static string? Nullable(string text) => string.IsNullOrWhiteSpace(text) ? null : text.Trim();

    [LoggerMessage(EventId = 600, Level = LogLevel.Information, Message = "Inline edit {RequestId}: reply {Attempt} sent back to the model ({Reason})")]
    private static partial void LogBounced(ILogger logger, string requestId, int attempt, string reason);

    [LoggerMessage(EventId = 601, Level = LogLevel.Information, Message = "Inline edit {RequestId} finished in {ElapsedMs} ms (mode {Mode}, {Rejected} block(s) rejected)")]
    private static partial void LogFinished(ILogger logger, string requestId, string mode, int rejected, long elapsedMs);

    [LoggerMessage(EventId = 603, Level = LogLevel.Information, Message = "Inline edit {RequestId}: {Mode} mode ({Lines} line(s) to edit, whole-scope limit {Limit})")]
    private static partial void LogMode(ILogger logger, string requestId, string mode, int lines, int limit);

    [LoggerMessage(EventId = 604, Level = LogLevel.Information, Message = "Inline edit {RequestId}: whole-scope reply {Attempt} unusable after a retry ({Reason}); falling back to edit blocks")]
    private static partial void LogFellBack(ILogger logger, string requestId, int attempt, string reason);

    [LoggerMessage(EventId = 605, Level = LogLevel.Debug, Message = "Inline edit {RequestId}: no related code ({Reason})")]
    private static partial void LogNoRelated(ILogger logger, string requestId, string reason);

    [LoggerMessage(EventId = 602, Level = LogLevel.Warning, Message = "Inline edit {RequestId} failed ({Code}): {Message}")]
    private static partial void LogFailed(ILogger logger, string requestId, PillionErrorCode code, string message);

    /// <summary>The final result: the proposal (LF; <see langword="null"/> for /explain), the explanation and any warning.</summary>
    private sealed record Outcome(string? Text, string? Explanation, string? Warning);

    /// <summary>What to do with a reply.</summary>
    private sealed record Evaluation(string? Text, string? Explanation, string? Warning, string? Correction, int Rejected, PillionError? Error)
    {
        public string Reason => Error?.Message ?? "unusable reply";

        public static Evaluation Done(string? text, string? explanation, string? warning = null) => new(text, explanation, warning, null, 0, null);

        public static Evaluation Bounce(string correction, int rejected, PillionError error) => new(null, null, null, correction, rejected, error);

        public static Evaluation Fail(PillionError error) => new(null, null, null, null, 0, error);
    }

    /// <summary>The per-request state: normalised texts, what was sent, totals.</summary>
    private sealed class RunState
    {
        public RunState(InlineEditRequest request)
        {
            Request = request;
            Scope = EditText.ToLf(request.ScopeText ?? string.Empty);
            Previous = request.PreviousProposal is null ? null : EditText.ToLf(request.PreviousProposal);
            Before = EditText.ToLf(request.TextBefore ?? string.Empty);
            After = EditText.ToLf(request.TextAfter ?? string.Empty);
            Newline = EditText.DetectNewline(request.ScopeText, request.PreviousProposal, request.TextBefore, request.TextAfter);
            BeforeLineCount = Before.Count(c => c == '\n');
        }

        public InlineEditRequest Request { get; }

        public string Scope { get; }

        public string? Previous { get; }

        public string Before { get; }

        public string After { get; }

        public string Newline { get; }

        public int BeforeLineCount { get; }

        /// <summary>Gets or sets the proposal text sent so far (LF).</summary>
        public string Emitted { get; set; } = string.Empty;

        public bool PreviewStopped { get; set; }

        public int RejectedBlocks { get; set; }

        private int PromptTokens { get; set; }

        private int CompletionTokens { get; set; }

        private string ModelId { get; set; } = string.Empty;

        private string ProviderId { get; set; } = string.Empty;

        public string Compose(string code) => Before + code + After;

        public void Observe(ChatChunk chunk)
        {
            if (!string.IsNullOrEmpty(chunk.ModelId))
            {
                ModelId = chunk.ModelId;
                ProviderId = chunk.ProviderId;
            }

            if (chunk.IsFinal)
            {
                PromptTokens += chunk.PromptTokens;
                CompletionTokens += chunk.CompletionTokens;
            }
        }

        public InlineEditChunk Text(string lfText, bool reset) => new()
        {
            RequestId = Request.RequestId,
            Text = EditText.WithNewline(lfText, Newline),
            ResetText = reset,
            ModelId = ModelId,
            ProviderId = ProviderId,
        };

        public InlineEditChunk Final(string? explanation, string? warning) => new()
        {
            RequestId = Request.RequestId,
            IsFinal = true,
            Explanation = explanation,
            Warning = warning,
            RejectedBlocks = RejectedBlocks,
            ModelId = ModelId,
            ProviderId = ProviderId,
            PromptTokens = PromptTokens,
            CompletionTokens = CompletionTokens,
        };
    }
}

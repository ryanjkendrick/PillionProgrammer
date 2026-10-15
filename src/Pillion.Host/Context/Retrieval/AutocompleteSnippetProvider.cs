using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Pillion.Contracts;
using Pillion.ModelGateway;
using Pillion.ModelGateway.Editing;

namespace Pillion.Host.Context.Retrieval;

/// <summary>
/// The retrieved snippets of autocomplete prompts (design section 7, Autocomplete prompt budget; work item 2.12): at most
/// <see cref="AutocompleteSnippets.MaxSnippets"/> chunks from other files, found by hybrid retrieval with the enclosing
/// declaration and the identifiers around the caret as the query, the open documents as focus files and a budget of
/// <see cref="TimeBudget"/>. A degraded search gives no snippets.
/// </summary>
/// <remarks>
/// The snippets are chosen once per document and enclosing member and returned unchanged for every keystroke in it, until
/// the caret leaves the member (the declaration changes) or <see cref="ReuseTime"/> passes, so the prompt prefix the
/// model sees does not change and a resident local model reuses its cached prefix. The search runs once per member however
/// many keystrokes ask for it (single flight, not cancelled by the next keystroke); requests that arrive while it runs wait
/// for it within their own cancellation.
/// </remarks>
internal sealed partial class AutocompleteSnippetProvider : IAutocompleteSnippetSource
{
    /// <summary>The time budget of one snippet search (design section 13 leaves autocomplete little room).</summary>
    public static readonly TimeSpan TimeBudget = TimeSpan.FromMilliseconds(100);

    /// <summary>How long the snippets of one member are reused.</summary>
    public static readonly TimeSpan ReuseTime = TimeSpan.FromSeconds(30);

    /// <summary>The most documents whose snippets are kept.</summary>
    public const int Capacity = 32;

    /// <summary>The prefix lines before the caret whose identifiers join the query.</summary>
    public const int ContextLines = 3;

    private readonly CodeContextRetriever _retriever;
    private readonly TimeProvider _time;
    private readonly ILogger _logger;
    private readonly object _gate = new();
    private readonly Dictionary<string, Entry> _entries = new(StringComparer.OrdinalIgnoreCase);
    private long _searches;
    private long _reuses;

    /// <summary>Initializes a new instance of the <see cref="AutocompleteSnippetProvider"/> class.</summary>
    /// <param name="retriever">The shared retrieval helper.</param>
    /// <param name="time">The clock.</param>
    /// <param name="logger">The logger.</param>
    public AutocompleteSnippetProvider(CodeContextRetriever retriever, TimeProvider? time = null, ILogger<AutocompleteSnippetProvider>? logger = null)
    {
        ArgumentNullException.ThrowIfNull(retriever);
        _retriever = retriever;
        _time = time ?? TimeProvider.System;
        _logger = logger ?? NullLogger<AutocompleteSnippetProvider>.Instance;
    }

    /// <summary>Gets how many snippet searches ran.</summary>
    public long Searches => Interlocked.Read(ref _searches);

    /// <summary>Gets how many requests reused the snippets of their member.</summary>
    public long Reuses => Interlocked.Read(ref _reuses);

    /// <inheritdoc/>
    public async ValueTask<IReadOnlyList<AutocompleteSnippet>> GetSnippetsAsync(CompletionRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!_retriever.IsEnabled || string.IsNullOrWhiteSpace(request.FilePath))
        {
            return [];
        }

        var document = request.FilePath.Trim();
        var member = Member(request);
        var now = _time.GetUtcNow();
        Task<IReadOnlyList<AutocompleteSnippet>> search;
        lock (_gate)
        {
            if (_entries.TryGetValue(document, out var entry) && string.Equals(entry.Member, member, StringComparison.Ordinal) && now - entry.Created < ReuseTime
                && !(entry.Search.IsCompleted && !entry.Search.IsCompletedSuccessfully))
            {
                Interlocked.Increment(ref _reuses);
                search = entry.Search;
            }
            else
            {
                Interlocked.Increment(ref _searches);
                search = SearchAsync(request);
                _entries[document] = new Entry(member, now, search);
                while (_entries.Count > Capacity)
                {
                    _entries.Remove(_entries.MinBy(e => e.Value.Created).Key);
                }
            }
        }

        return await search.WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>The member the caret is in: the enclosing declaration with its whitespace collapsed (empty outside any).</summary>
    /// <param name="request">The request.</param>
    /// <returns>The member key.</returns>
    internal static string Member(CompletionRequest request) =>
        string.Join(' ', (request.EnclosingDeclaration ?? string.Empty).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

    /// <summary>The query: the enclosing declaration, then the identifiers of the lines around the caret.</summary>
    /// <param name="request">The request.</param>
    /// <returns>The query text.</returns>
    internal static string Query(CompletionRequest request)
    {
        var prefix = request.Prefix ?? string.Empty;
        var lines = prefix.Split('\n');
        var before = string.Join('\n', lines.Skip(Math.Max(0, lines.Length - ContextLines)));
        var suffix = request.Suffix ?? string.Empty;
        var newline = suffix.IndexOf('\n', StringComparison.Ordinal);
        var after = newline < 0 ? suffix : suffix[..newline];
        var declaration = Member(request);
        var identifiers = CodeContextRetriever.Identifiers(declaration + "\n" + before + after);
        return (declaration + " " + string.Join(' ', identifiers)).Trim();
    }

    private async Task<IReadOnlyList<AutocompleteSnippet>> SearchAsync(CompletionRequest request)
    {
        // Detached from the request: the next keystroke cancels the request, not the member's search. The time budget bounds it.
        await Task.Yield();
        var query = Query(request);
        if (query.Length == 0)
        {
            return [];
        }

        var focus = (request.OpenDocuments ?? []).Where(p => !string.IsNullOrWhiteSpace(p)).Take(8).ToList();
        var found = await _retriever.RetrieveAsync(query, AutocompleteSnippets.MaxSnippets * 3, focus, TimeBudget, CancellationToken.None).ConfigureAwait(false);
        if (found.Degraded)
        {
            LogDegraded(_logger, request.RequestId, found.TotalMs);
            return [];
        }

        // Code around the caret is already in the prompt; snippets come from other files.
        return
        [
            .. found.Chunks
                .Where(c => !string.Equals(c.FilePath, request.FilePath, StringComparison.OrdinalIgnoreCase))
                .Take(AutocompleteSnippets.MaxSnippets)
                .Select(c => new AutocompleteSnippet(c.Path, c.StartLine, c.EndLine, c.Text)),
        ];
    }

    [LoggerMessage(EventId = 2941, Level = LogLevel.Debug, Message = "Autocomplete {RequestId}: the snippet search was degraded after {ElapsedMs:F0} ms, so the prompt has no snippets for this member.")]
    private static partial void LogDegraded(ILogger logger, string requestId, double elapsedMs);

    private sealed record Entry(string Member, DateTimeOffset Created, Task<IReadOnlyList<AutocompleteSnippet>> Search);
}

/// <summary>Retrieved code for inline edit prompts (work item 2.12).</summary>
internal interface IInlineEditContext
{
    /// <summary>Gets the chunks related to an inline edit request, best first; none when retrieval is off or nothing is indexed.</summary>
    /// <param name="request">The request.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>At most <see cref="InlineEditPromptBuilder.MaxRelatedChunks"/> chunks.</returns>
    Task<IReadOnlyList<ContextChunk>> GetRelatedAsync(InlineEditRequest request, CancellationToken cancellationToken);
}

/// <summary>
/// The retrieved chunks of an inline edit prompt (work item 2.12): hybrid retrieval with the instruction and the identifiers
/// of the code to edit as the query and the document as the focus file; chunks that overlap the scope or the context sent
/// around it are left out, and at most four are kept.
/// </summary>
internal sealed class InlineEditRetrieval : IInlineEditContext
{
    private readonly CodeContextRetriever _retriever;

    /// <summary>Initializes a new instance of the <see cref="InlineEditRetrieval"/> class.</summary>
    /// <param name="retriever">The shared retrieval helper.</param>
    public InlineEditRetrieval(CodeContextRetriever retriever)
    {
        ArgumentNullException.ThrowIfNull(retriever);
        _retriever = retriever;
    }

    /// <inheritdoc/>
    public async Task<IReadOnlyList<ContextChunk>> GetRelatedAsync(InlineEditRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!_retriever.IsEnabled)
        {
            return [];
        }

        var code = request.PreviousProposal ?? request.ScopeText ?? string.Empty;
        var instruction = (request.Instruction ?? string.Empty).Trim();
        var query = (instruction + " " + string.Join(' ', CodeContextRetriever.Identifiers(code))).Trim();
        if (query.Length == 0)
        {
            return [];
        }

        var focus = string.IsNullOrWhiteSpace(request.FilePath) ? [] : new[] { request.FilePath };
        var found = await _retriever.RetrieveAsync(query, InlineEditPromptBuilder.MaxRelatedChunks * 2, focus, timeBudget: null, cancellationToken).ConfigureAwait(false);

        // Lines (1-based) of the document the prompt already shows: the context before, the scope and the context after.
        var scopeStart = request.ScopeStartLine + 1;
        var shownFrom = scopeStart - Lines(request.TextBefore);
        var shownTo = scopeStart + Lines(request.ScopeText) + Lines(request.TextAfter);
        return
        [
            .. found.Chunks
                .Where(c => !string.Equals(c.FilePath, request.FilePath, StringComparison.OrdinalIgnoreCase) || c.EndLine < shownFrom || c.StartLine > shownTo)
                .Take(InlineEditPromptBuilder.MaxRelatedChunks),
        ];
    }

    private static int Lines(string? text) => string.IsNullOrEmpty(text) ? 0 : text.Count(c => c == '\n');
}

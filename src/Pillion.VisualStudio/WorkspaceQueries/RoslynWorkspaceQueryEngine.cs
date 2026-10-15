using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.FindSymbols;
using Microsoft.CodeAnalysis.Text;
using Microsoft.VisualStudio.Threading;
using Pillion.Contracts;
using Pillion.VisualStudio.Core;
using Pillion.VisualStudio.Core.WorkspaceQueries;
using Pillion.VisualStudio.Signals;

namespace Pillion.VisualStudio.WorkspaceQueries;

/// <summary>
/// Answers find-references and callers-of from Visual Studio's live Roslyn workspace (work item 2.14), for C#, Visual
/// Basic and any other Roslyn language in the solution. Runs entirely on the thread pool: the workspace's current solution
/// and <see cref="SymbolFinder"/> are free-threaded, and nothing here switches to the UI thread. The workspace is looked up
/// once (<see cref="WarmUpAsync"/>, after the package's start-up) and reused.
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item>The symbol is resolved through the document's semantic model at the requested position (the column, then the
/// requested name on the line, then the first declaration on it), or, for callers-of without a usable position, by name
/// with <see cref="SymbolFinder.FindSourceDeclarationsAsync(Solution, string, bool, SymbolFilter, CancellationToken)"/>
/// filtered by container (at most ten overloads or namesakes). A type's callers are the calls of its constructors.</item>
/// <item>References stream in through <see cref="IFindReferencesProgress"/>, so a query the budget stops keeps what it
/// found. Callers are the references (minus those in documentation comments) with the member that contains them, which is
/// what <see cref="SymbolFinder.FindCallersAsync(ISymbol, Solution, CancellationToken)"/> computes too, but that method
/// returns nothing until it has finished.</item>
/// <item>A reference Roslyn could only match as a candidate (code that does not bind) is reported as approximate.</item>
/// </list>
/// </remarks>
internal sealed class RoslynWorkspaceQueryEngine : IWorkspaceQueryEngine
{
    private const int MaxNameCandidates = 10;
    private const int MaxSnippetLength = 300;

    private static readonly TimeSpan RetryInterval = TimeSpan.FromSeconds(30);

    private static readonly SymbolDisplayFormat ContainerFormat = new(
        typeQualificationStyle: SymbolDisplayTypeQualificationStyle.NameAndContainingTypesAndNamespaces,
        genericsOptions: SymbolDisplayGenericsOptions.None);

    private static readonly SymbolDisplayFormat CallerFormat = new(
        typeQualificationStyle: SymbolDisplayTypeQualificationStyle.NameAndContainingTypesAndNamespaces,
        genericsOptions: SymbolDisplayGenericsOptions.None,
        memberOptions: SymbolDisplayMemberOptions.IncludeContainingType);

    private static readonly Regex GenericArguments = new(@"<[^<>]*>|\(Of [^()]*\)", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    private readonly Func<Task<(Workspace? Workspace, string? Reason)>> _resolve;
    private readonly PillionLog? _log;
    private readonly object _gate = new();
    private Task<(Workspace? Workspace, string? Reason)>? _resolving;
    private Workspace? _workspace;
    private DateTime _retryAfterUtc;

    /// <summary>Initializes a new instance of the <see cref="RoslynWorkspaceQueryEngine"/> class.</summary>
    /// <param name="log">The extension log, or <see langword="null"/>.</param>
    /// <param name="resolve">Finds the workspace; the default is <see cref="VisualStudioWorkspaceLocator.ResolveAsync"/>.</param>
    public RoslynWorkspaceQueryEngine(PillionLog? log, Func<Task<(Workspace? Workspace, string? Reason)>>? resolve = null)
    {
        _log = log;
        _resolve = resolve ?? VisualStudioWorkspaceLocator.ResolveAsync;
    }

    /// <summary>Looks the workspace up now, so no query pays for it (call it once the package's start-up is over).</summary>
    /// <returns>A task that completes when the lookup is over.</returns>
    public Task WarmUpAsync() => GetWorkspaceAsync();

    /// <inheritdoc/>
    public async Task FindReferencesAsync(SourceLocation location, WorkspaceQueryResults<SourceLocation> results, CancellationToken cancellationToken)
    {
        var solution = (await RequireWorkspaceAsync(cancellationToken).ConfigureAwait(false)).CurrentSolution;
        var document = FindDocument(solution, location.FilePath);
        if (document is null)
        {
            return;
        }

        var symbol = await SymbolOnLineAsync(document, location.Line, location.Column, name: null, cancellationToken).ConfigureAwait(false);
        if (symbol is null)
        {
            return;
        }

        results.MarkFound();
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var progress = new ReferenceProgress(reference =>
        {
            if (!reference.Location.IsInSource)
            {
                return;
            }

            var span = reference.Location.GetLineSpan();
            var added = results.TryAdd(new SourceLocation
            {
                FilePath = span.Path,
                Line = span.StartLinePosition.Line,
                Column = span.StartLinePosition.Character,
            });
            if (!added)
            {
                Stop(stop);
            }
        });

        try
        {
            await SymbolFinder.FindReferencesAsync(symbol, solution, progress, documents: null, stop.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // The collector is full.
        }
    }

    /// <inheritdoc/>
    public async Task CallersOfAsync(CallersOfRequest request, WorkspaceQueryResults<CallSite> results, CancellationToken cancellationToken)
    {
        var solution = (await RequireWorkspaceAsync(cancellationToken).ConfigureAwait(false)).CurrentSolution;
        var targets = await ResolveTargetsAsync(solution, request, cancellationToken).ConfigureAwait(false);
        if (targets.Count == 0)
        {
            return;
        }

        results.MarkFound();

        // The progress callbacks are synchronous; the caller of each reference needs the document's semantic model, so the
        // references go through a queue to a consumer that runs beside the search and adds each call site as it is ready.
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        using var signal = new SemaphoreSlim(0);
        var queue = new ConcurrentQueue<ReferenceLocation>();
        var searchDone = 0;
        var progress = new ReferenceProgress(reference =>
        {
            if (reference.Location.IsInSource)
            {
                queue.Enqueue(reference);
                signal.Release();
            }
        });

        var consumer = Task.Run(
            async () =>
            {
                var models = new Dictionary<DocumentId, SemanticModel?>();
                while (true)
                {
                    await signal.WaitAsync(stop.Token).ConfigureAwait(false);
                    if (!queue.TryDequeue(out var reference))
                    {
                        if (Volatile.Read(ref searchDone) == 1)
                        {
                            return;
                        }

                        continue;
                    }

                    var site = await ToCallSiteAsync(reference, models, stop.Token).ConfigureAwait(false);
                    if (site is not null && !results.TryAdd(site))
                    {
                        Stop(stop);
                        return;
                    }
                }
            },
            CancellationToken.None);

        try
        {
            foreach (var target in targets)
            {
                await SymbolFinder.FindReferencesAsync(target, solution, progress, documents: null, stop.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // The collector is full.
        }
        finally
        {
            Volatile.Write(ref searchDone, 1);
            signal.Release();
        }

        try
        {
            await consumer.ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // The collector is full.
        }
    }

    /// <summary>Normalizes a container as the host may send it: an index breadcrumb, C++ or path separators, type arguments.</summary>
    /// <param name="container">The requested container.</param>
    /// <returns>A dotted name without type arguments, or empty.</returns>
    internal static string NormalizeContainer(string? container)
    {
        if (string.IsNullOrWhiteSpace(container))
        {
            return string.Empty;
        }

        var text = container!.Trim();
        if (text.StartsWith("global::", StringComparison.Ordinal))
        {
            text = text.Substring("global::".Length);
        }

        text = text.Replace(" > ", ".").Replace("::", ".").Replace('/', '.').Replace('\\', '.');
        string previous;
        do
        {
            previous = text;
            text = GenericArguments.Replace(text, string.Empty);
        }
        while (!string.Equals(previous, text, StringComparison.Ordinal));

        return text.Trim('.', ' ');
    }

    /// <summary>Whether a symbol's container is the requested one (or ends with it).</summary>
    /// <param name="symbolContainer">The symbol's containing type or namespace, dotted.</param>
    /// <param name="container">The normalized request container.</param>
    /// <returns><see langword="true"/> when they match, or when no container was requested.</returns>
    internal static bool ContainerMatches(string symbolContainer, string container) =>
        container.Length == 0
        || string.Equals(symbolContainer, container, StringComparison.OrdinalIgnoreCase)
        || symbolContainer.EndsWith("." + container, StringComparison.OrdinalIgnoreCase);

    private static void Stop(CancellationTokenSource stop)
    {
        try
        {
            stop.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // The search has already ended.
        }
    }

    private static Document? FindDocument(Solution solution, string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        var ids = solution.GetDocumentIdsWithFilePath(path);
        return ids.IsDefaultOrEmpty ? null : solution.GetDocument(ids[0]);
    }

    private static string Snippet(SourceText text, int line)
    {
        if (line < 0 || line >= text.Lines.Count)
        {
            return string.Empty;
        }

        var snippet = text.Lines[line].ToString().Trim();
        return snippet.Length <= MaxSnippetLength ? snippet : snippet.Substring(0, MaxSnippetLength);
    }

    private static bool NameMatches(ISymbol symbol, string name) =>
        string.Equals(symbol.Name, name, StringComparison.OrdinalIgnoreCase)
        || (symbol is IMethodSymbol { MethodKind: MethodKind.Constructor or MethodKind.StaticConstructor } constructor
            && string.Equals(constructor.ContainingType.Name, name, StringComparison.OrdinalIgnoreCase));

    private static ISymbol Normalize(ISymbol symbol) =>
        symbol is IMethodSymbol { ReducedFrom: { } reduced } ? reduced.OriginalDefinition : symbol.OriginalDefinition;

    /// <summary>
    /// The symbol on a line: at the column when it names one (and the name when one is given), else at each occurrence of
    /// the name, else the first symbol the line declares, else the first source symbol a word on the line refers to.
    /// </summary>
    private static async Task<ISymbol?> SymbolOnLineAsync(Document document, int line, int column, string? name, CancellationToken cancellationToken)
    {
        var text = await document.GetTextAsync(cancellationToken).ConfigureAwait(false);
        if (line < 0 || line >= text.Lines.Count)
        {
            return null;
        }

        var textLine = text.Lines[line];
        if (column >= 0 && column <= textLine.Span.Length)
        {
            var atColumn = await SymbolFinder.FindSymbolAtPositionAsync(document, textLine.Start + column, cancellationToken).ConfigureAwait(false);
            if (atColumn is not null && (name is null || NameMatches(atColumn, name)))
            {
                return Normalize(atColumn);
            }
        }

        var lineText = textLine.ToString();
        if (name is not null)
        {
            for (var index = IndexOfWord(lineText, name, 0); index >= 0; index = IndexOfWord(lineText, name, index + name.Length))
            {
                var symbol = await SymbolFinder.FindSymbolAtPositionAsync(document, textLine.Start + index, cancellationToken).ConfigureAwait(false);
                if (symbol is not null && NameMatches(symbol, name))
                {
                    return Normalize(symbol);
                }
            }

            return null;
        }

        var root = await document.GetSyntaxRootAsync(cancellationToken).ConfigureAwait(false);
        var model = await document.GetSemanticModelAsync(cancellationToken).ConfigureAwait(false);
        if (root is null || model is null)
        {
            return null;
        }

        var words = root.DescendantTokens(textLine.Span).Where(IsWord).ToList();
        foreach (var token in words)
        {
            if (token.Parent is { } parent && model.GetDeclaredSymbol(parent, cancellationToken) is { } declared
                && declared.Locations.Any(l => l.IsInSource && l.SourceSpan.Contains(token.SpanStart)))
            {
                return Normalize(declared);
            }
        }

        foreach (var token in words)
        {
            var symbol = await SymbolFinder.FindSymbolAtPositionAsync(document, token.SpanStart, cancellationToken).ConfigureAwait(false);
            if (symbol is not null && symbol.Locations.Any(l => l.IsInSource))
            {
                return Normalize(symbol);
            }
        }

        return null;
    }

    private static bool IsWord(SyntaxToken token) =>
        token.Text.Length > 0 && (char.IsLetter(token.Text[0]) || token.Text[0] == '_' || token.Text[0] == '@');

    private static int IndexOfWord(string text, string word, int start)
    {
        for (var index = text.IndexOf(word, start, StringComparison.OrdinalIgnoreCase); index >= 0; index = text.IndexOf(word, index + 1, StringComparison.OrdinalIgnoreCase))
        {
            var before = index == 0 || !IsIdentifierChar(text[index - 1]);
            var after = index + word.Length >= text.Length || !IsIdentifierChar(text[index + word.Length]);
            if (before && after)
            {
                return index;
            }
        }

        return -1;
    }

    private static bool IsIdentifierChar(char c) => char.IsLetterOrDigit(c) || c == '_';

    private static async Task<IReadOnlyList<ISymbol>> ResolveTargetsAsync(Solution solution, CallersOfRequest request, CancellationToken cancellationToken)
    {
        var targets = new List<ISymbol>();
        if (!string.IsNullOrWhiteSpace(request.FilePath) && request.Line >= 0 && FindDocument(solution, request.FilePath) is { } document)
        {
            var symbol = await SymbolOnLineAsync(document, request.Line, -1, request.SymbolName, cancellationToken).ConfigureAwait(false);
            if (symbol is not null)
            {
                targets.Add(symbol);
            }
        }

        if (targets.Count == 0)
        {
            var container = NormalizeContainer(request.Container);
            var declarations = await SymbolFinder.FindSourceDeclarationsAsync(solution, request.SymbolName, ignoreCase: true, SymbolFilter.TypeAndMember, cancellationToken).ConfigureAwait(false);
            var matches = declarations
                .Where(d => ContainerMatches(d.ContainingSymbol?.ToDisplayString(ContainerFormat) ?? string.Empty, container))
                .ToList();
            var exact = matches.Where(d => string.Equals(d.Name, request.SymbolName, StringComparison.Ordinal)).ToList();
            targets.AddRange((exact.Count > 0 ? exact : matches).Take(MaxNameCandidates).Select(Normalize));
        }

        // Calling a type means constructing it: its callers are the calls of its constructors.
        return targets
            .SelectMany(t => t is INamedTypeSymbol { InstanceConstructors.Length: > 0 } type ? type.InstanceConstructors.Cast<ISymbol>() : [t])
            .Distinct<ISymbol>(SymbolEqualityComparer.Default)
            .ToList();
    }

    private static async Task<CallSite?> ToCallSiteAsync(ReferenceLocation reference, Dictionary<DocumentId, SemanticModel?> models, CancellationToken cancellationToken)
    {
        var tree = reference.Location.SourceTree;
        if (tree is null)
        {
            return null;
        }

        var start = reference.Location.SourceSpan.Start;
        var root = await tree.GetRootAsync(cancellationToken).ConfigureAwait(false);
        if (root.FindToken(start, findInsideTrivia: true).IsPartOfStructuredTrivia())
        {
            return null; // A cref in a documentation comment names the member but does not call it.
        }

        var text = await tree.GetTextAsync(cancellationToken).ConfigureAwait(false);
        var span = reference.Location.GetLineSpan();
        var line = span.StartLinePosition.Line;
        if (!models.TryGetValue(reference.Document.Id, out var model))
        {
            model = await reference.Document.GetSemanticModelAsync(cancellationToken).ConfigureAwait(false);
            models[reference.Document.Id] = model;
        }

        var caller = model?.GetEnclosingSymbol(start, cancellationToken);
        while (caller is IMethodSymbol { MethodKind: MethodKind.AnonymousFunction })
        {
            caller = caller.ContainingSymbol;
        }

        return new CallSite
        {
            FilePath = span.Path,
            Line = line,
            Snippet = Snippet(text, line),
            CallerName = caller is null or INamespaceSymbol ? string.Empty : caller.ToDisplayString(CallerFormat),
            IsApproximate = reference.IsCandidateLocation,
        };
    }

    private Task<(Workspace? Workspace, string? Reason)> GetWorkspaceAsync()
    {
        lock (_gate)
        {
            if (_workspace is { } found)
            {
                return Task.FromResult<(Workspace?, string?)>((found, null));
            }

            // A lookup that failed is tried again once the retry interval has passed.
            if (_resolving is null || (_resolving.IsCompleted && DateTime.UtcNow >= _retryAfterUtc))
            {
                _resolving = Task.Run(ResolveAsync);
            }

            return _resolving;
        }
    }

    private async Task<(Workspace? Workspace, string? Reason)> ResolveAsync()
    {
        var (workspace, reason) = await _resolve().ConfigureAwait(false);
        if (workspace is not null)
        {
            lock (_gate)
            {
                _workspace = workspace;
            }
        }
        else
        {
            lock (_gate)
            {
                _retryAfterUtc = DateTime.UtcNow + RetryInterval;
            }

            _log?.Info("workspace.query.unavailable", "The Roslyn workspace is not available for workspace queries.", ("reason", reason));
        }

        return (workspace, reason);
    }

    private async Task<Workspace> RequireWorkspaceAsync(CancellationToken cancellationToken)
    {
        var (workspace, reason) = await GetWorkspaceAsync().WithCancellation(cancellationToken).ConfigureAwait(false);
        return workspace ?? throw new PillionRpcException(new PillionError
        {
            Code = PillionErrorCode.Cancelled,
            Message = "The Roslyn workspace is not available.",
            RetryHint = "Answer from the index's approximate edges; Visual Studio looks for the workspace again within 30 s.",
            Details = reason,
        });
    }

    /// <summary>Forwards each reference Roslyn finds; may be called on several threads at once.</summary>
    private sealed class ReferenceProgress(Action<ReferenceLocation> onReference) : IFindReferencesProgress
    {
        public void OnStarted()
        {
        }

        public void OnCompleted()
        {
        }

        public void OnFindInDocumentStarted(Document document)
        {
        }

        public void OnFindInDocumentCompleted(Document document)
        {
        }

        public void OnDefinitionFound(ISymbol symbol)
        {
        }

        public void OnReferenceFound(ISymbol symbol, ReferenceLocation location) => onReference(location);

        public void ReportProgress(int current, int maximum)
        {
        }
    }
}

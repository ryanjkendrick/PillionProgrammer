using System.Globalization;
using System.Text;
using Pillion.ContextEngine.Chunking;
using Pillion.ContextEngine.Symbols;
using Pillion.Contracts;
using Pillion.Host.Context.Mentions;
using Pillion.Host.WorkspaceQueries;
using Pillion.ModelGateway;

namespace Pillion.Host.Context;

/// <summary>
/// <c>@symbol</c> (work item 2.13, design section 7): a definition from the index's symbol graph (exact name first, then the
/// same name in any case, then a prefix or substring), its chunk text, and a capped summary of its references from
/// <see cref="IWorkspaceQueries"/> (the live Roslyn workspace for C# and VB, the index's approximate edges otherwise). The
/// value may end with <c>#path:line</c> to pick one definition; the candidates of an ambiguous match carry it.
/// </summary>
internal sealed partial class ContextRpcService
{
    /// <summary>The most reference sites asked for.</summary>
    internal const int MaxSymbolReferences = 50;

    /// <summary>The most reference sites listed in the summary.</summary>
    internal const int MaxListedReferences = 30;

    /// <summary>The most lines of a definition read from disk when the index has no chunk for it.</summary>
    internal const int MaxDefinitionLines = 400;

    /// <summary>The share of the token limit the definition text may take; the rest is for the reference summary.</summary>
    internal const double DefinitionShare = 0.7;

    /// <summary>Formats a symbol candidate: the dotted name, then <c>#</c>, the full path, a colon and the 1-based line.</summary>
    /// <param name="definition">The definition.</param>
    /// <param name="fullPath">Its file's full path.</param>
    /// <returns>A value that <c>@symbol:</c> resolves to exactly this definition.</returns>
    internal static string SymbolCandidate(SymbolDefinition definition, string fullPath) =>
        DottedName(definition) + "#" + fullPath + ":" + definition.StartLine.ToString(CultureInfo.InvariantCulture);

    /// <summary>Splits an <c>@symbol</c> value into the name and the optional <c>#path:line</c> that picks one definition.</summary>
    /// <param name="value">The value.</param>
    /// <returns>The name, the path (or empty) and the 1-based line (or 0).</returns>
    internal static (string Name, string Path, int Line) ParseSymbolValue(string value)
    {
        var hash = value.IndexOf('#', StringComparison.Ordinal);
        if (hash < 0)
        {
            return (value.Trim(), string.Empty, 0);
        }

        var name = value[..hash].Trim();
        var location = value[(hash + 1)..].Trim();
        var colon = location.LastIndexOf(':');
        if (colon > 0 && int.TryParse(location[(colon + 1)..], NumberStyles.Integer, CultureInfo.InvariantCulture, out var line))
        {
            return (name, location[..colon], line);
        }

        return (name, location, 0);
    }

    /// <summary>The dotted name of a definition, without type parameters: <c>Fixtures.Sample.Greeter.Greet</c>.</summary>
    private static string DottedName(SymbolDefinition definition) =>
        string.Join('.', definition.Container.Split(Breadcrumbs.Separator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Append(definition.Name).Select(WithoutTypeParameters));

    private static string WithoutTypeParameters(string segment)
    {
        var angle = segment.IndexOfAny(['<', '(']);
        return angle > 0 ? segment[..angle] : segment;
    }

    /// <summary>The last part of a dotted or <c>::</c>-qualified name.</summary>
    private static string SimpleName(string name)
    {
        var cut = Math.Max(name.LastIndexOf('.'), name.LastIndexOf(':'));
        return cut >= 0 && cut + 1 < name.Length ? name[(cut + 1)..] : name;
    }

    private async Task<MentionResolution> ResolveSymbolAsync(Resolution context, CancellationToken cancellationToken, bool exactOnly = false)
    {
        var (name, hintPath, hintLine) = ParseSymbolValue(context.Parsed.Value);
        if (name.Length == 0)
        {
            return context.Unresolved(MentionKinds.Symbol, "Name a symbol, for example @symbol:OrderService.");
        }

        var views = await context.GetViewsAsync(cancellationToken).ConfigureAwait(false);
        if (views.Count == 0)
        {
            return context.Unresolved(MentionKinds.Symbol, "@symbol reads the index, and no index is open for this solution or folder (see the Pillion Index window).");
        }

        // With a location the simple name finds the namesakes and the location picks one (a dotted container may not match
        // the graph's spelling, for example a type's parameters).
        var lookup = hintPath.Length > 0 ? SimpleName(name) : name;
        var (matches, loose) = FindSymbols(views, lookup, exactOnly);
        if (hintPath.Length > 0)
        {
            var inFile = matches.Where(m => SamePath(m.View.FullPath(m.Definition.FilePath), hintPath) || string.Equals(m.Definition.FilePath, hintPath.Replace('\\', '/'), StringComparison.OrdinalIgnoreCase)).ToList();
            if (hintLine > 0 && inFile.Count > 1)
            {
                var atLine = inFile.Where(m => m.Definition.StartLine == hintLine).ToList();
                inFile = atLine.Count > 0 ? atLine : inFile.Where(m => m.Definition.StartLine <= hintLine && hintLine <= m.Definition.EndLine).OrderBy(m => m.Definition.EndLine - m.Definition.StartLine).Take(1).ToList();
            }

            matches = inFile.Count > 0 ? inFile : matches;
        }

        if (matches.Count == 0)
        {
            return context.Unresolved(MentionKinds.Symbol, $"No symbol named '{name}' is in the index.");
        }

        if (matches.Count > 1)
        {
            var candidates = matches.Take(FileMatcher.MaxCandidates).Select(m => SymbolCandidate(m.Definition, m.View.FullPath(m.Definition.FilePath))).ToArray();
            return context.Ambiguous(
                MentionKinds.Symbol,
                candidates,
                loose ? $"No symbol is named '{name}'; these are the closest." : $"{matches.Count} symbols are named '{name}'; pick one.",
                loose);
        }

        return await DescribeSymbolAsync(context, matches[0].View, matches[0].Definition, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>The definitions a name matches, best first, and whether they are only fuzzy matches.</summary>
    internal static (List<(MentionIndexView View, SymbolDefinition Definition)> Matches, bool Loose) FindSymbols(IReadOnlyList<MentionIndexView> views, string name, bool exactOnly)
    {
        static bool Pinnable(SymbolDefinition d) => d.Kind is not (SymbolKind.Namespace or SymbolKind.Module);

        List<(MentionIndexView View, SymbolDefinition Definition)> Collect(Func<MentionIndexView, IEnumerable<SymbolDefinition>> select) =>
            [.. views.SelectMany(v => select(v).Where(Pinnable).Select(d => (View: v, Definition: d)))];

        var exact = Collect(v => v.Graph.FindDefinitions(name));
        if (exact.Count == 0)
        {
            exact = Collect(v => v.Graph.Definitions.Where(d => string.Equals(d.Name, name, StringComparison.OrdinalIgnoreCase)));
        }

        if (exact.Count > 0)
        {
            return (Order(WithoutConstructorsOfMatchedTypes(exact)), false);
        }

        if (exactOnly || name.Length < 2)
        {
            return ([], false);
        }

        // Fuzzy: a prefix, then a substring (each resolves when unique); never letter-order guesses for symbols.
        var prefix = Collect(v => v.Graph.Definitions.Where(d => d.Kind != SymbolKind.Constructor && d.Name.StartsWith(name, StringComparison.OrdinalIgnoreCase)));
        if (prefix.Count > 0)
        {
            return (Order(prefix), prefix.Count > 1);
        }

        var contains = Collect(v => v.Graph.Definitions.Where(d => d.Kind != SymbolKind.Constructor && d.Name.Contains(name, StringComparison.OrdinalIgnoreCase)));
        return (Order(contains), contains.Count > 1);

        static List<(MentionIndexView View, SymbolDefinition Definition)> Order(List<(MentionIndexView View, SymbolDefinition Definition)> list) =>
            [.. list
                .OrderByDescending(m => SymbolKinds.IsType(m.Definition.Kind))
                .ThenByDescending(m => m.View.Graph.Ranking.ScoreOf(m.Definition.Id))
                .ThenBy(m => m.Definition.FilePath, StringComparer.OrdinalIgnoreCase)
                .ThenBy(m => m.Definition.StartLine)];
    }

    /// <summary>A type and its constructors share a name: the type stands for them.</summary>
    private static List<(MentionIndexView View, SymbolDefinition Definition)> WithoutConstructorsOfMatchedTypes(List<(MentionIndexView View, SymbolDefinition Definition)> matches)
    {
        var types = matches.Where(m => SymbolKinds.IsType(m.Definition.Kind)).Select(m => (m.View, Qualified: m.Definition.QualifiedName)).ToHashSet();
        var kept = matches.Where(m => m.Definition.Kind != SymbolKind.Constructor || !types.Contains((m.View, m.Definition.Container))).ToList();

        // Partial classes: one entry per type name and container is enough to pin; keep the biggest declaration.
        return [.. kept
            .GroupBy(m => (m.View, m.Definition.Kind, m.Definition.QualifiedName, Type: SymbolKinds.IsType(m.Definition.Kind) ? string.Empty : m.Definition.FilePath + ":" + m.Definition.StartLine))
            .Select(g => g.OrderByDescending(m => m.Definition.EndLine - m.Definition.StartLine).First())];
    }

    private async Task<MentionResolution> DescribeSymbolAsync(Resolution context, MentionIndexView view, SymbolDefinition definition, CancellationToken cancellationToken)
    {
        var fullPath = view.FullPath(definition.FilePath);
        var access = context.Policy.Check(fullPath, () => ReadSmallText(fullPath));
        if (!access.IsAllowed && !context.Request.ApprovedSensitive)
        {
            return context.NeedsApproval(MentionKinds.Symbol, fullPath, definition.Name, access.Reason + " Approve it to pin the symbol.");
        }

        var citations = new List<Citation>();
        var language = LanguageCatalog.Default.Find(definition.FilePath)?.Id ?? string.Empty;
        var dotted = DottedName(definition);
        var builder = new StringBuilder();
        builder.Append(SymbolKinds.Name(definition.Kind)).Append(' ').Append(dotted)
            .Append(CultureInfo.InvariantCulture, $" in {definition.FilePath}, lines {definition.StartLine}-{definition.EndLine}\n");
        if (definition.Signature.Length > 0)
        {
            builder.Append("Signature: ").Append(definition.Signature).Append('\n');
        }

        // The definition's chunks (or its lines from disk when the index has none), within its share of the limit.
        var definitionText = await DefinitionTextAsync(view, definition, fullPath, citations, cancellationToken).ConfigureAwait(false);
        var fitted = TextTruncation.KeepHead(definitionText, (int)(context.MaxTokens * DefinitionShare), context.Counter);
        builder.Append("\n```").Append(language).Append('\n').Append(fitted.Text.TrimEnd('\n')).Append("\n```\n\n");

        var references = await ReferencesAsync(view, definition, fullPath, context.Policy, cancellationToken).ConfigureAwait(false);
        builder.Append(references.Summary);
        foreach (var site in references.Sites.Take(MaxListedReferences))
        {
            if (citations.Count >= MaxCitations)
            {
                break;
            }

            citations.Add(new Citation { FilePath = site.FilePath, StartLine = site.Line, EndLine = site.Line });
        }

        var resolution = context.Resolved(MentionKinds.Symbol, definition.Name, fullPath, builder.ToString(), fitted.Truncated, [.. citations]);
        resolution.IsSensitive = !access.IsAllowed;
        return resolution;
    }

    private static async Task<string> DefinitionTextAsync(MentionIndexView view, SymbolDefinition definition, string fullPath, List<Citation> citations, CancellationToken cancellationToken)
    {
        if (view.FindRelative(definition.FilePath) is { } file)
        {
            var chunks = (await view.GetChunksAsync(file.Id, cancellationToken).ConfigureAwait(false))
                .Where(c => c.EndLine >= definition.StartLine && c.StartLine <= definition.EndLine)
                .ToList();
            if (chunks.Count > 0)
            {
                var builder = new StringBuilder();
                var lastEnd = 0;
                foreach (var chunk in chunks)
                {
                    if (chunk.EndLine <= lastEnd)
                    {
                        continue; // Wholly inside a chunk already taken.
                    }

                    builder.Append(chunk.Text.TrimEnd('\n')).Append('\n');
                    lastEnd = chunk.EndLine;
                    citations.Add(new Citation
                    {
                        FilePath = fullPath,
                        StartLine = Math.Max(0, chunk.StartLine - 1),
                        EndLine = Math.Max(0, chunk.EndLine - 1),
                        ChunkId = view.IndexId + ":" + chunk.Id.ToString(CultureInfo.InvariantCulture),
                        Breadcrumb = chunk.Breadcrumb,
                    });
                }

                return builder.ToString();
            }
        }

        citations.Add(new Citation { FilePath = fullPath, StartLine = Math.Max(0, definition.StartLine - 1), EndLine = Math.Max(0, definition.EndLine - 1) });
        var lines = new SourceLineCache();
        var text = new StringBuilder();
        var last = Math.Min(definition.EndLine, definition.StartLine + MaxDefinitionLines - 1);
        for (var line = definition.StartLine; line <= last; line++)
        {
            if (lines.Line(fullPath, line - 1) is not { } content)
            {
                break;
            }

            text.Append(content).Append('\n');
        }

        return text.Length > 0 ? text.ToString() : "(the file could not be read)";
    }

    /// <summary>The reference sites: from <see cref="IWorkspaceQueries"/> when it is there, else (or when it finds none) the graph's edges.</summary>
    private async Task<(string Summary, IReadOnlyList<WorkspaceSite> Sites)> ReferencesAsync(
        MentionIndexView view,
        SymbolDefinition definition,
        string fullPath,
        FileAccessPolicy policy,
        CancellationToken cancellationToken)
    {
        IReadOnlyList<WorkspaceSite> sites = [];
        var truncated = false;
        var source = "approximate, from the index";
        if (_queries is not null)
        {
            var result = await _queries.FindReferencesAsync(
                new WorkspaceSymbolQuery
                {
                    Name = definition.Name,
                    Container = definition.Container,
                    FilePath = fullPath,
                    Line = definition.StartLine - 1,
                    MaxResults = MaxSymbolReferences,
                },
                cancellationToken).ConfigureAwait(false);
            if (result.Sites.Count > 0)
            {
                sites = result.Sites;
                truncated = result.Truncated;
                source = result.Source == WorkspaceQuerySource.Roslyn
                    ? (result.IsApproximate ? "from Roslyn, completed from the index" : "from Roslyn")
                    : "approximate, from the index";
            }
        }

        if (sites.Count == 0)
        {
            var lines = new SourceLineCache();
            var edges = view.Graph.FindReferences(definition.Id);
            truncated = edges.Count > MaxSymbolReferences;
            sites = [.. edges
                .Select(e => (Edge: e, Path: view.FullPath(e.FilePath)))
                .DistinctBy(e => (e.Path.ToUpperInvariant(), e.Edge.Line))
                .Take(MaxSymbolReferences)
                .Select(e => new WorkspaceSite(
                    e.Path,
                    e.Edge.Line - 1,
                    lines.Snippet(e.Path, e.Edge.Line - 1),
                    e.Edge.FromSymbolId is { } from && view.Graph.GetDefinition(from) is { } caller ? DottedName(caller) : string.Empty,
                    IsApproximate: true))];
        }

        // Issue #88: a snippet is never shown from a file the exclusion globs or the secret-file rules refuse.
        sites = [.. sites.Select(s => s.Snippet.Length > 0 && !policy.Check(s.FilePath).IsAllowed ? s with { Snippet = string.Empty } : s)];
        var builder = new StringBuilder();
        var files = sites.Select(s => s.FilePath).Distinct(StringComparer.OrdinalIgnoreCase).Count();
        if (sites.Count == 0)
        {
            builder.Append("References: none found (").Append(source).Append(").\n");
            return (builder.ToString(), sites);
        }

        builder.Append(CultureInfo.InvariantCulture, $"References: {sites.Count}{(truncated ? "+" : string.Empty)} in {files} file{(files == 1 ? string.Empty : "s")} ({source})\n");
        foreach (var site in sites.Take(MaxListedReferences))
        {
            var shown = view.FindFull(site.FilePath) is { } indexed ? indexed.RelativePath : site.FilePath;
            builder.Append("  ").Append(shown).Append(':').Append((site.Line + 1).ToString(CultureInfo.InvariantCulture));
            if (site.CallerName.Length > 0)
            {
                builder.Append(" in ").Append(site.CallerName);
            }

            if (site.Snippet.Length > 0)
            {
                builder.Append(": ").Append(site.Snippet.Length > 160 ? site.Snippet[..160] + "..." : site.Snippet);
            }

            builder.Append('\n');
        }

        if (sites.Count > MaxListedReferences)
        {
            builder.Append(CultureInfo.InvariantCulture, $"  ... {sites.Count - MaxListedReferences} more\n");
        }

        return (builder.ToString(), sites);
    }

    private static string? ReadSmallText(string path)
    {
        try
        {
            var info = new FileInfo(path);
            return info.Exists && info.Length <= MaxFileBytes ? File.ReadAllText(path) : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}

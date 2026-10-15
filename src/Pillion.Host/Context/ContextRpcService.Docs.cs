using System.Globalization;
using System.Text;
using Pillion.ContextEngine.Chunking;
using Pillion.ContextEngine.Symbols;
using Pillion.Contracts;
using Pillion.Host.Context.Mentions;
using Pillion.ModelGateway;

namespace Pillion.Host.Context;

/// <summary>
/// <c>@docs</c> and <c>@solution</c> (work item 2.13). <c>@docs</c> matches the Markdown and text files of the open indexes
/// and of the folders in <c>mentions.docsFolders</c> by name, and pins a document's chunks in order up to the token limit;
/// a bare <c>@docs</c> lists the documents. <c>@solution</c> renders the repo map of each open index, focused on the
/// documents open in Visual Studio.
/// </summary>
internal sealed partial class ContextRpcService
{
    /// <summary>The most documents listed by a bare <c>@docs</c>, and enumerated from one registered folder.</summary>
    internal const int MaxDocs = 2000;

    /// <summary>How long <c>@solution</c> waits for Visual Studio's open documents.</summary>
    internal static readonly TimeSpan OpenDocumentsTimeout = TimeSpan.FromMilliseconds(1500);

    private static readonly string[] DocExtensions = [".md", ".markdown", ".mdx", ".txt", ".rst", ".adoc", ".asciidoc"];

    /// <summary>Determines whether a file is documentation: Markdown or text by extension, or by the language the indexer gave it.</summary>
    /// <param name="path">The path.</param>
    /// <param name="language">The indexer's language id, or empty.</param>
    /// <returns><see langword="true"/> for documentation.</returns>
    internal static bool IsDoc(string path, string language) =>
        language is "markdown" or "text" || DocExtensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase);

    private static async Task<MentionResolution> ResolveDocsAsync(Resolution context, List<string> docsFolders, CancellationToken cancellationToken)
    {
        var request = context.Request;
        var views = await context.GetViewsAsync(cancellationToken).ConfigureAwait(false);
        var docs = new List<DocFile>();
        foreach (var view in views)
        {
            docs.AddRange(view.Files.Where(f => IsDoc(f.RelativePath, f.Language)).Select(f => new DocFile(f.FullPath, f.RelativePath, view, f)));
        }

        foreach (var folder in RegisteredFolders(docsFolders, request.WorkspaceRoots))
        {
            docs.AddRange(EnumerateFiles(folder).Where(f => IsDoc(f, string.Empty)).Take(MaxDocs)
                .Where(f => !docs.Any(d => SamePath(d.FullPath, f)))
                .Select(f => new DocFile(f, Path.GetFileName(folder.TrimEnd('\\', '/')) + "/" + Path.GetRelativePath(folder, f).Replace('\\', '/'), null, null)));
        }

        if (docs.Count == 0)
        {
            return context.Unresolved(MentionKinds.Docs, views.Count == 0 && docsFolders.Count == 0
                ? "@docs reads the index, and no index is open for this solution or folder (see the Pillion Index window); folders can also be added with mentions.docsFolders."
                : "No Markdown or text documentation is in the index or in mentions.docsFolders.");
        }

        var value = context.Parsed.Value;
        if (value.Length == 0)
        {
            return await ListDocsAsync(context, docs, cancellationToken).ConfigureAwait(false);
        }

        var paths = docs.Select(d => d.FullPath).ToList();
        var path = FindExactFile(value, [.. request.WorkspaceRoots.Concat(views.Select(v => v.RootPath))], paths) is { } exact && paths.Any(p => SamePath(p, exact)) ? exact : null;
        if (path is null)
        {
            var match = FileMatcher.Match(value, paths);
            if (match.Single is { } single)
            {
                path = single;
            }
            else if (match.Candidates.Count > 0)
            {
                return context.Ambiguous(
                    MentionKinds.Docs,
                    [.. match.Candidates],
                    match.IsLoose ? $"No document is named '{value}'; these are the closest." : $"{match.Candidates.Count} documents match '{value}'; pick one.",
                    match.IsLoose);
            }
            else
            {
                return context.Unresolved(MentionKinds.Docs, $"No document matches '{value}'.");
            }
        }

        var doc = docs.First(d => SamePath(d.FullPath, path));
        return await PinDocAsync(context, doc, cancellationToken).ConfigureAwait(false);
    }

    private static async Task<MentionResolution> PinDocAsync(Resolution context, DocFile doc, CancellationToken cancellationToken)
    {
        var file = new FileInfo(doc.FullPath);
        string? text = null;
        var access = context.Policy.Check(doc.FullPath, () => text ??= ReadSmallText(doc.FullPath));
        if (!access.IsAllowed && !context.Request.ApprovedSensitive)
        {
            return context.NeedsApproval(MentionKinds.Docs, doc.FullPath, file.Name, access.Reason + " Approve it to pin the document.");
        }

        // The index's chunks when the document is indexed, else the document chunked now (a registered folder).
        var chunks = new List<(int Start, int End, string Breadcrumb, string Text, string ChunkId)>();
        if (doc.View is { } view && doc.File is { } indexed)
        {
            foreach (var chunk in await view.GetChunksAsync(indexed.Id, cancellationToken).ConfigureAwait(false))
            {
                chunks.Add((chunk.StartLine, chunk.EndLine, chunk.Breadcrumb, chunk.Text, view.IndexId + ":" + chunk.Id.ToString(CultureInfo.InvariantCulture)));
            }
        }

        if (chunks.Count == 0)
        {
            if (!file.Exists)
            {
                return context.Unresolved(MentionKinds.Docs, $"The document '{doc.FullPath}' does not exist.");
            }

            var (read, _, binary) = await ReadFileAsync(file, cancellationToken).ConfigureAwait(false);
            if (binary)
            {
                return context.Unresolved(MentionKinds.Docs, "The document is binary.");
            }

            foreach (var chunk in new FileChunker().Chunk(doc.DisplayPath, read, ChunkOptions.Default))
            {
                chunks.Add((chunk.StartLine, chunk.EndLine, chunk.Breadcrumb, chunk.Text, string.Empty));
            }
        }

        // Whole chunks in order while they fit; the first is cut if it alone is too big.
        var builder = new StringBuilder();
        builder.Append(doc.DisplayPath).Append('\n');
        var citations = new List<Citation>();
        var used = context.Counter.Count(builder.ToString());
        var cut = false;
        foreach (var (start, end, breadcrumb, chunkText, chunkId) in chunks)
        {
            var piece = chunkText.TrimEnd('\n') + "\n";
            var tokens = context.Counter.Count(piece);
            if (used + tokens > context.MaxTokens)
            {
                cut = true;
                if (citations.Count == 0)
                {
                    builder.Append(TextTruncation.KeepHead(piece, Math.Max(0, context.MaxTokens - used), context.Counter).Text);
                    citations.Add(new Citation { FilePath = doc.FullPath, StartLine = Math.Max(0, start - 1), EndLine = Math.Max(0, end - 1), ChunkId = chunkId, Breadcrumb = breadcrumb });
                }

                break;
            }

            builder.Append(piece);
            used += tokens;
            if (citations.Count < MaxCitations)
            {
                citations.Add(new Citation { FilePath = doc.FullPath, StartLine = Math.Max(0, start - 1), EndLine = Math.Max(0, end - 1), ChunkId = chunkId, Breadcrumb = breadcrumb });
            }
        }

        if (cut)
        {
            builder.Append(CultureInfo.InvariantCulture, $"[... {chunks.Count - citations.Count} more sections of this document not shown ...]\n");
        }

        var resolution = context.Resolved(MentionKinds.Docs, file.Name, doc.FullPath, builder.ToString(), cut, [.. citations]);
        resolution.IsSensitive = !access.IsAllowed;
        return resolution;
    }

    private static async Task<MentionResolution> ListDocsAsync(Resolution context, List<DocFile> docs, CancellationToken cancellationToken)
    {
        var builder = new StringBuilder();
        var listed = docs.Where(d => context.Policy.Check(d.FullPath).IsAllowed).OrderBy(d => d.DisplayPath, StringComparer.OrdinalIgnoreCase).ToList();
        builder.Append(CultureInfo.InvariantCulture, $"Documentation: {listed.Count} files (pin one with @docs:name)\n");
        foreach (var doc in listed.Take(MaxOutlineFiles))
        {
            builder.Append(doc.DisplayPath);
            var first = string.Empty;
            if (doc.View is { } view && doc.File is { } file && (await view.GetChunksAsync(file.Id, cancellationToken).ConfigureAwait(false)) is { Count: > 0 } chunks)
            {
                first = chunks[0].Text.Split('\n').Select(l => l.Trim()).FirstOrDefault(l => l.Length > 0) ?? string.Empty;
            }
            else
            {
                first = await FirstLineAsync(doc.FullPath, cancellationToken).ConfigureAwait(false);
            }

            if (first.Length > 0)
            {
                builder.Append(": ").Append(first.Length > 100 ? first[..100] + "..." : first);
            }

            builder.Append('\n');
        }

        if (listed.Count > MaxOutlineFiles)
        {
            builder.Append(CultureInfo.InvariantCulture, $"... {listed.Count - MaxOutlineFiles} more\n");
        }

        return context.Resolved(MentionKinds.Docs, "Docs", null, builder.ToString(), false);
    }

    /// <summary>The registered documentation folders that exist: full paths, or paths relative to the first workspace root.</summary>
    internal static IEnumerable<string> RegisteredFolders(IReadOnlyList<string> docsFolders, string[] workspaceRoots)
    {
        foreach (var folder in docsFolders.Where(f => !string.IsNullOrWhiteSpace(f)))
        {
            string? full = null;
            try
            {
                full = Path.IsPathRooted(folder) ? Path.GetFullPath(folder)
                    : workspaceRoots.Length > 0 ? Path.GetFullPath(Path.Combine(workspaceRoots[0], folder))
                    : null;
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
            {
                // Not a usable path: skipped.
            }

            if (full is not null && Directory.Exists(full))
            {
                yield return full;
            }
        }
    }

    private async Task<MentionResolution> ResolveSolutionAsync(Resolution context, CancellationToken cancellationToken)
    {
        var views = await context.GetViewsAsync(cancellationToken).ConfigureAwait(false);
        if (views.Count == 0)
        {
            return context.Unresolved(MentionKinds.Solution, "@solution renders the repo map from the index, and no index is open for this solution or folder (see the Pillion Index window).");
        }

        var open = await OpenDocumentsAsync(cancellationToken).ConfigureAwait(false);
        var counter = context.Counter;
        var budget = Math.Max(256, context.MaxTokens / views.Count);
        var builder = new StringBuilder();
        var complete = true;
        foreach (var view in views)
        {
            var focus = open.Select(p => view.FindFull(p)?.RelativePath).OfType<string>().ToList();
            var map = RepoMapRenderer.Render(view.Graph, new RepoMapOptions
            {
                MaxTokens = budget,
                FocusFiles = focus,
                TokenCounter = text => counter.Count(text.ToString()),
            });
            complete &= map.IsComplete;
            builder.Append(CultureInfo.InvariantCulture, $"Repo map of {view.RootPath} ({view.Graph.Files.Count} files with symbols, {view.Graph.Definitions.Count} definitions");
            if (focus.Count > 0)
            {
                builder.Append(CultureInfo.InvariantCulture, $"; focused on {focus.Count} open document{(focus.Count == 1 ? string.Empty : "s")}");
            }

            builder.Append(map.IsComplete ? ")\n" : "; the most referenced first, the rest left out)\n");
            builder.Append(map.Text.TrimEnd('\n')).Append("\n\n");
        }

        var name = views.Count == 1 ? "Repo map: " + Path.GetFileName(views[0].RootPath.TrimEnd('\\', '/')) : string.Create(CultureInfo.InvariantCulture, $"Repo map ({views.Count} roots)");
        return context.Resolved(MentionKinds.Solution, name, null, builder.ToString(), !complete);
    }

    /// <summary>The documents open in Visual Studio (the active one first), or none when it is not connected or slow.</summary>
    private async Task<IReadOnlyList<string>> OpenDocumentsAsync(CancellationToken cancellationToken)
    {
        if (_vsix is not { IsConnected: true } vsix)
        {
            return [];
        }

        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(OpenDocumentsTimeout);
            var documents = await vsix.GetEditorService().GetOpenDocumentsAsync(timeout.Token).ConfigureAwait(false);
            return [.. (documents ?? []).OrderByDescending(d => d.IsActive).Select(d => d.Path).Where(p => !string.IsNullOrEmpty(p))];
        }
#pragma warning disable CA1031 // Any failure only loses the focus.
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
#pragma warning restore CA1031
        {
            LogOpenDocumentsFailed(_logger, ex);
            return [];
        }
    }

    [Microsoft.Extensions.Logging.LoggerMessage(EventId = 451, Level = Microsoft.Extensions.Logging.LogLevel.Debug, Message = "@solution has no focus: the open documents could not be read.")]
    private static partial void LogOpenDocumentsFailed(Microsoft.Extensions.Logging.ILogger logger, Exception exception);

    /// <summary>A document <c>@docs</c> can pin: from an index, or from a registered folder.</summary>
    private sealed record DocFile(string FullPath, string DisplayPath, MentionIndexView? View, IndexedFile? File);
}

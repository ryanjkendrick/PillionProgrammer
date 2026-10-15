using System.Globalization;
using System.Text;
using Pillion.ContextEngine.Symbols;
using Pillion.Contracts;
using Pillion.Host.Context.Mentions;

namespace Pillion.Host.Context;

/// <summary>
/// <c>@file</c> and <c>@folder</c> (work items 1.10 and 2.13). With an open index for the root, names are matched against its
/// <c>files</c> table and folder outlines are built from its symbol graph; the client's file list (or the folder on disk)
/// answers when there is no index, and also when the index has no strong match, because the index leaves out excluded and
/// secret files, which still resolve here with the per-file approval.
/// </summary>
internal sealed partial class ContextRpcService
{
    /// <summary>The most definitions listed under one file of a folder outline.</summary>
    internal const int MaxOutlineSymbolsPerFile = 12;

    private static async Task<MentionResolution> ResolveFileAsync(Resolution context, CancellationToken cancellationToken)
    {
        var value = context.Parsed.Value;
        if (value.Length == 0)
        {
            return context.Unresolved(MentionKinds.File, "Name a file, for example @file:Program.cs.");
        }

        var request = context.Request;
        var views = await context.GetViewsAsync(cancellationToken).ConfigureAwait(false);
        var indexed = views.SelectMany(v => v.Files).Select(f => f.FullPath).ToList();
        var supplied = request.CandidateFiles.Where(f => !string.IsNullOrWhiteSpace(f)).ToList();
        var roots = WorkspaceAndIndexRoots(request, views);
        var path = FindExactFile(value, request.WorkspaceRoots, supplied.Count > 0 ? supplied : indexed);
        FileMatch? looseIndexMatch = null;
        if (path is null && indexed.Count > 0)
        {
            var match = FileMatcher.Match(value, indexed);
            if (match.Single is { } single)
            {
                path = single;
            }
            else if (match.Candidates.Count > 0 && !match.IsLoose)
            {
                return context.Ambiguous(
                    MentionKinds.File,
                    [.. match.Candidates],
                    $"{match.Candidates.Count} files match '{value}'; pick one.",
                    false);
            }
            else if (match.Candidates.Count > 0)
            {
                looseIndexMatch = match;
            }
        }

        if (path is null)
        {
            // No index, or no strong match in it: the client's list (or the folders on disk) may still hold the file, for
            // example a secret file the index leaves out.
            var candidates = indexed.Count > 0 && supplied.Count == 0 ? [] : GetCandidateFiles(request);
            var match = FileMatcher.Match(value, candidates);
            if (match.Single is { } single)
            {
                path = single;
            }
            else if (match.Candidates.Count > 0 && (!match.IsLoose || looseIndexMatch is null))
            {
                return context.Ambiguous(
                    MentionKinds.File,
                    [.. match.Candidates],
                    match.IsLoose ? $"No file is named '{value}'; these are the closest." : $"{match.Candidates.Count} files match '{value}'; pick one.",
                    match.IsLoose);
            }
            else if (looseIndexMatch is not null)
            {
                return context.Ambiguous(MentionKinds.File, [.. looseIndexMatch.Candidates], $"No file is named '{value}'; these are the closest.", true);
            }
            else if (indexed.Count > 0 && supplied.Count == 0 && FindOnDisk(value, request.WorkspaceRoots) is { } onDisk)
            {
                path = onDisk;
            }
            else
            {
                return context.Unresolved(MentionKinds.File, candidates.Count == 0 && indexed.Count == 0 && request.WorkspaceRoots.Length == 0
                    ? $"No file list or workspace folder was supplied, so '{value}' cannot be found."
                    : $"No file matches '{value}'.");
            }
        }

        if (!IsInWorkspace(path, roots, supplied))
        {
            return context.Unresolved(MentionKinds.File, "The file is outside the open solution or folder.");
        }

        var file = new FileInfo(path);
        if (!file.Exists)
        {
            return context.Unresolved(MentionKinds.File, $"The file '{path}' does not exist.");
        }

        var (text, wasCut, binary) = await ReadFileAsync(file, cancellationToken).ConfigureAwait(false);
        if (binary)
        {
            return context.Unresolved(MentionKinds.File, "The file is binary.");
        }

        var access = context.Policy.Check(path, () => text);
        if (!access.IsAllowed && !request.ApprovedSensitive)
        {
            return context.NeedsApproval(MentionKinds.File, path, file.Name, access.Reason + " Approve it to pin the file.");
        }

        var lines = CountLines(text);
        var resolution = context.Resolved(MentionKinds.File, file.Name, path, text, wasCut, [new Citation { FilePath = path, StartLine = 0, EndLine = Math.Max(0, lines - 1) }]);
        resolution.IsSensitive = !access.IsAllowed;
        return resolution;
    }

    private static async Task<MentionResolution> ResolveFolderAsync(Resolution context, CancellationToken cancellationToken)
    {
        var value = context.Parsed.Value;
        if (value.Length == 0)
        {
            return context.Unresolved(MentionKinds.Folder, "Name a folder, for example @folder:src/Api.");
        }

        var request = context.Request;
        var views = await context.GetViewsAsync(cancellationToken).ConfigureAwait(false);
        if (views.Count > 0 && await ResolveIndexedFolderAsync(context, views, cancellationToken).ConfigureAwait(false) is { } fromIndex)
        {
            return fromIndex;
        }

        var candidates = GetCandidateFiles(request);
        var directory = FindExactFolder(value, request.WorkspaceRoots, candidates);
        if (directory is null)
        {
            var match = FileMatcher.Match(value, FolderPaths(candidates, request.WorkspaceRoots));
            if (match.Single is { } single)
            {
                directory = single;
            }
            else if (match.Candidates.Count > 0)
            {
                return context.Ambiguous(
                    MentionKinds.Folder,
                    [.. match.Candidates],
                    match.IsLoose ? $"No folder is named '{value}'; these are the closest." : $"{match.Candidates.Count} folders match '{value}'; pick one.",
                    match.IsLoose);
            }
            else
            {
                return context.Unresolved(MentionKinds.Folder, $"No folder matches '{value}'.");
            }
        }

        if (request.WorkspaceRoots.Length > 0 && !request.WorkspaceRoots.Any(r => IsUnder(directory, r) || SamePath(directory, r)))
        {
            return context.Unresolved(MentionKinds.Folder, "The folder is outside the open solution or folder.");
        }

        var files = candidates.Where(f => IsUnder(f, directory)).ToList();
        if (files.Count == 0 && Directory.Exists(directory))
        {
            files = EnumerateFiles(directory);
        }

        var outline = await BuildOutlineAsync(directory, files, context.Policy, cancellationToken).ConfigureAwait(false);
        return context.Resolved(MentionKinds.Folder, FolderName(directory), directory, outline, false);
    }

    /// <summary>A folder of an indexed root, outlined from the index; <see langword="null"/> when the index does not know it (the phase 1 path then answers).</summary>
    private static async Task<MentionResolution?> ResolveIndexedFolderAsync(Resolution context, IReadOnlyList<MentionIndexView> views, CancellationToken cancellationToken)
    {
        var value = context.Parsed.Value;
        var request = context.Request;
        var all = views.SelectMany(v => v.Files.Select(f => (View: v, File: f))).ToList();
        var fullPaths = all.Select(f => f.File.FullPath).ToList();
        var roots = WorkspaceAndIndexRoots(request, views);
        var directory = FindExactFolder(value, [.. views.Select(v => v.RootPath).Concat(request.WorkspaceRoots).Distinct(StringComparer.OrdinalIgnoreCase)], fullPaths);
        if (directory is not null && !fullPaths.Any(f => IsUnder(f, directory)))
        {
            directory = null;
        }

        if (directory is null)
        {
            var match = FileMatcher.Match(value, FolderPaths(fullPaths, roots));
            if (match.Single is { } single)
            {
                directory = single;
            }
            else if (match.Candidates.Count > 0 && !match.IsLoose)
            {
                return context.Ambiguous(MentionKinds.Folder, [.. match.Candidates], $"{match.Candidates.Count} folders match '{value}'; pick one.", false);
            }
            else
            {
                return null;
            }
        }

        var files = all.Where(f => IsUnder(f.File.FullPath, directory)).OrderBy(f => f.File.FullPath, StringComparer.OrdinalIgnoreCase).ToList();
        if (files.Count == 0)
        {
            return null;
        }

        var (outline, citations) = await BuildIndexedOutlineAsync(directory, files, context.Policy, cancellationToken).ConfigureAwait(false);
        return context.Resolved(MentionKinds.Folder, FolderName(directory), directory, outline, false, citations);
    }

    /// <summary>
    /// A folder outline from the index: each file with its first chunk's breadcrumb and first line as its summary, and the
    /// file's top definitions from the symbol graph (ranked by the repo map's PageRank, listed in source order).
    /// </summary>
    private static async Task<(string Text, Citation[] Citations)> BuildIndexedOutlineAsync(
        string directory,
        List<(MentionIndexView View, IndexedFile File)> files,
        ModelGateway.FileAccessPolicy policy,
        CancellationToken cancellationToken)
    {
        var listed = new List<(MentionIndexView View, IndexedFile File)>();
        var excluded = 0;
        foreach (var entry in files)
        {
            if (!policy.Check(entry.File.FullPath).IsAllowed)
            {
                excluded++;
                continue;
            }

            listed.Add(entry);
        }

        var shown = listed.Take(MaxOutlineFiles).ToList();
        var builder = new StringBuilder();
        builder.Append("Folder ").Append(directory).Append(": ")
            .Append(listed.Count.ToString(CultureInfo.InvariantCulture)).Append(" indexed files");
        if (listed.Count > shown.Count)
        {
            builder.Append(CultureInfo.InvariantCulture, $", first {shown.Count} listed");
        }

        if (excluded > 0)
        {
            builder.Append(CultureInfo.InvariantCulture, $", {excluded} excluded or secret files not listed");
        }

        builder.Append('\n');
        var citations = new List<Citation>();
        foreach (var (view, file) in shown)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var relative = Path.GetRelativePath(directory, file.FullPath).Replace('\\', '/');
            builder.Append(relative);
            var chunks = await view.GetChunksAsync(file.Id, cancellationToken).ConfigureAwait(false);
            if (chunks.Count > 0)
            {
                var first = chunks[0];
                var summary = Summary(first.Breadcrumb, file.RelativePath, first.Text);
                if (summary.Length > 0)
                {
                    builder.Append(": ").Append(summary);
                }

                if (citations.Count < MaxCitations)
                {
                    citations.Add(new Citation
                    {
                        FilePath = file.FullPath,
                        StartLine = Math.Max(0, first.StartLine - 1),
                        EndLine = Math.Max(0, chunks[^1].EndLine - 1),
                        ChunkId = view.IndexId + ":" + first.Id.ToString(CultureInfo.InvariantCulture),
                        Breadcrumb = first.Breadcrumb,
                    });
                }
            }

            builder.Append('\n');
            foreach (var line in OutlineSymbols(view.Graph, file.RelativePath))
            {
                builder.Append("  ").Append(line).Append('\n');
            }
        }

        return (builder.ToString(), [.. citations]);
    }

    /// <summary>The top definitions of a file as indented lines (the repo map's ranking picks them; source order lists them).</summary>
    internal static IEnumerable<string> OutlineSymbols(SymbolGraph graph, string relativePath)
    {
        var definitions = graph.DefinitionsIn(relativePath)
            .Where(d => d.Kind is not (SymbolKind.Module or SymbolKind.EnumMember or SymbolKind.Namespace))
            .ToList();
        if (definitions.Count == 0)
        {
            yield break;
        }

        var ranking = graph.Ranking;
        var chosen = definitions
            .OrderByDescending(d => SymbolKinds.IsType(d.Kind))
            .ThenByDescending(d => ranking.ScoreOf(d.Id))
            .Take(MaxOutlineSymbolsPerFile)
            .ToHashSet();
        foreach (var definition in definitions.Where(chosen.Contains))
        {
            var depth = 0;
            for (var parent = graph.ParentOf(definition.Id); parent is not null && depth < 4; parent = graph.ParentOf(parent.Id))
            {
                depth += parent.Kind == SymbolKind.Namespace ? 0 : 1;
            }

            var text = definition.Signature.Length > 0 ? definition.Signature : SymbolKinds.Name(definition.Kind) + " " + definition.Name;
            yield return new string(' ', depth * 2) + (text.Length > 140 ? text[..140] + "..." : text)
                + string.Create(CultureInfo.InvariantCulture, $" (line {definition.StartLine})");
        }

        if (definitions.Count > chosen.Count)
        {
            yield return string.Create(CultureInfo.InvariantCulture, $"... {definitions.Count - chosen.Count} more definitions");
        }
    }

    /// <summary>A file's summary: its first chunk's breadcrumb without the path, and that chunk's first non-empty line.</summary>
    internal static string Summary(string breadcrumb, string relativePath, string text)
    {
        var crumbs = breadcrumb ?? string.Empty;
        if (crumbs.StartsWith(relativePath, StringComparison.OrdinalIgnoreCase))
        {
            crumbs = crumbs[relativePath.Length..];
        }

        crumbs = crumbs.Trim().TrimStart('>').Trim();
        var firstLine = (text ?? string.Empty).Split('\n').Select(l => l.Trim()).FirstOrDefault(l => l.Length > 0) ?? string.Empty;
        firstLine = firstLine.Length > 100 ? firstLine[..100] + "..." : firstLine;
        return crumbs.Length == 0 ? firstLine : firstLine.Length == 0 ? crumbs : crumbs + " | " + firstLine;
    }

    private static string FolderName(string directory) =>
        Path.GetFileName(directory.TrimEnd('\\', '/')) is { Length: > 0 } name ? name + "/" : directory;

    private static string[] WorkspaceAndIndexRoots(MentionRequest request, IReadOnlyList<MentionIndexView> views) =>
        [.. request.WorkspaceRoots.Concat(views.Select(v => v.RootPath)).Where(r => !string.IsNullOrWhiteSpace(r)).Distinct(StringComparer.OrdinalIgnoreCase)];

    /// <summary>A file named exactly by a path relative to a root, when it exists on disk (the index left it out).</summary>
    private static string? FindOnDisk(string value, string[] roots) =>
        ExpandPath(value, roots).FirstOrDefault(File.Exists);

    private static int CountLines(string text)
    {
        if (text.Length == 0)
        {
            return 1;
        }

        var count = 1;
        foreach (var c in text.AsSpan())
        {
            count += c == '\n' ? 1 : 0;
        }

        return text.EndsWith('\n') ? count - 1 : count;
    }
}

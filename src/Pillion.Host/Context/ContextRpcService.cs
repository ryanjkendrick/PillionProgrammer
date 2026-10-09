using System.Globalization;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Pillion.Contracts;
using Pillion.ModelGateway;

namespace Pillion.Host.Context;

/// <summary>
/// Implements <see cref="IContextService"/> for phase 1 (work item 1.10). <see cref="ResolveMentionAsync"/> turns <c>@file</c>,
/// <c>@folder</c>, <c>@selection</c>, <c>@clipboard</c> and <c>@errors</c> mentions into pinned content with a token count;
/// there is no index yet, so <see cref="SearchAsync"/> returns an empty array, <see cref="GetIndexStatusAsync"/> reports that
/// nothing is indexed and <see cref="PauseIndexingAsync"/> does nothing (the index arrives in phase 2).
/// </summary>
/// <remarks>
/// Files honour the effective <c>indexing.exclude</c> globs and the built-in secret-file rules
/// (<see cref="FileAccessPolicy"/>): such a file resolves as unresolved with <see cref="MentionResolution.RequiresApproval"/>
/// until the request carries <see cref="MentionRequest.ApprovedSensitive"/>, and an approved one is marked
/// <see cref="MentionResolution.IsSensitive"/> so context assembly never sends it to a cloud model.
/// </remarks>
internal sealed partial class ContextRpcService : IContextService
{
    /// <summary>The token limit used when a request does not set <see cref="MentionRequest.MaxTokens"/>.</summary>
    internal const int DefaultMaxTokens = 12000;

    /// <summary>The most bytes read from a file; a larger file is cut and the resolution is marked truncated.</summary>
    internal const int MaxFileBytes = 1024 * 1024;

    /// <summary>The most files listed in a folder outline.</summary>
    internal const int MaxOutlineFiles = 200;

    /// <summary>The most files enumerated from disk when the client sent no file list.</summary>
    internal const int MaxEnumeratedFiles = 20000;

    private static readonly string[] SkippedFolders = [".git", ".vs", "bin", "obj", "node_modules"];

    private readonly ITokenCounter _tokens;
    private readonly IGatewaySettingsSource _settings;
    private readonly IModelRouter? _router;
    private readonly ILogger _logger;

    /// <summary>Initializes a new instance of the <see cref="ContextRpcService"/> class.</summary>
    /// <param name="tokens">Counts tokens.</param>
    /// <param name="settings">Supplies the effective <c>indexing.exclude</c> globs.</param>
    /// <param name="router">Names the default chat model whose tokenizer counts the content; without one the estimating counter is used.</param>
    /// <param name="logger">The logger.</param>
    public ContextRpcService(ITokenCounter tokens, IGatewaySettingsSource settings, IModelRouter? router = null, ILogger<ContextRpcService>? logger = null)
    {
        ArgumentNullException.ThrowIfNull(tokens);
        ArgumentNullException.ThrowIfNull(settings);
        _tokens = tokens;
        _settings = settings;
        _router = router;
        _logger = logger ?? NullLogger<ContextRpcService>.Instance;
    }

    /// <inheritdoc/>
    public Task<SearchResult[]> SearchAsync(SearchRequest request, CancellationToken cancellationToken) =>
        Task.FromResult<SearchResult[]>([]);

    /// <inheritdoc/>
    public Task<IndexStatus> GetIndexStatusAsync(CancellationToken cancellationToken) =>
        Task.FromResult(new IndexStatus());

    /// <inheritdoc/>
    public Task PauseIndexingAsync(bool pause, CancellationToken cancellationToken) => Task.CompletedTask;

    /// <inheritdoc/>
    public async Task<MentionResolution> ResolveMentionAsync(MentionRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var parsed = MentionParser.Parse(request.Mention, request.Kind);
        var maxTokens = request.MaxTokens > 0 ? request.MaxTokens : DefaultMaxTokens;
        var counter = _tokens.ForModel(DefaultModelId());
        var context = new Resolution(request, parsed, maxTokens, counter, new FileAccessPolicy(_settings.GetCurrent().Exclude));

        try
        {
            return parsed.Kind switch
            {
                "file" => await ResolveFileAsync(context, cancellationToken).ConfigureAwait(false),
                "folder" => await ResolveFolderAsync(context, cancellationToken).ConfigureAwait(false),
                "selection" => ResolvePayload(context, "Selection", "There is no selection to pin."),
                "clipboard" => ResolvePayload(context, "Clipboard", "The clipboard holds no text."),
                "errors" => ResolveErrors(context),
                "" => await ResolveFuzzyAsync(context, cancellationToken).ConfigureAwait(false),
                var other => context.Unresolved(other, $"@{other} is not available in this version."),
            };
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            LogReadFailed(_logger, parsed.Kind, ex.GetType().Name);
            return context.Unresolved(parsed.Kind, "The file could not be read: " + ex.Message);
        }
    }

    private string DefaultModelId()
    {
        if (_router is not null)
        {
            try
            {
                return _router.Resolve(new RoutingRequest { TaskClass = TaskClass.Chat }).ModelId;
            }
            catch (PillionRpcException)
            {
                // No model is routable now (nothing configured, or Local-only with no local model); count with the estimate.
            }
        }

        return "unknown";
    }

    private static async Task<MentionResolution> ResolveFuzzyAsync(Resolution context, CancellationToken cancellationToken)
    {
        if (context.Parsed.Value.Length == 0)
        {
            return context.Unresolved(string.Empty, "Type a name after @, for example @Program.cs.");
        }

        // A file wins over a folder, except when the file side only has loose letter-order guesses.
        var asFile = await ResolveFileAsync(context, cancellationToken).ConfigureAwait(false);
        var fileWasLoose = context.Loose;
        if (asFile.IsResolved || asFile.RequiresApproval || (asFile.Candidates.Length > 0 && !fileWasLoose))
        {
            return asFile;
        }

        var asFolder = await ResolveFolderAsync(context, cancellationToken).ConfigureAwait(false);
        if (asFolder.IsResolved || (asFolder.Candidates.Length > 0 && !context.Loose))
        {
            return asFolder;
        }

        return asFile.Candidates.Length > 0 ? asFile : asFolder.Candidates.Length > 0 ? asFolder : asFile;
    }

    private static async Task<MentionResolution> ResolveFileAsync(Resolution context, CancellationToken cancellationToken)
    {
        var value = context.Parsed.Value;
        if (value.Length == 0)
        {
            return context.Unresolved("file", "Name a file, for example @file:Program.cs.");
        }

        var request = context.Request;
        var candidates = GetCandidateFiles(request);
        var path = FindExactFile(value, request.WorkspaceRoots, candidates);
        if (path is null)
        {
            var match = FileMatcher.Match(value, candidates);
            if (match.Single is { } single)
            {
                path = single;
            }
            else if (match.Candidates.Count > 0)
            {
                return context.Ambiguous(
                    "file",
                    [.. match.Candidates],
                    match.IsLoose ? $"No file is named '{value}'; these are the closest." : $"{match.Candidates.Count} files match '{value}'; pick one.",
                    match.IsLoose);
            }
            else
            {
                return context.Unresolved("file", candidates.Count == 0 && request.WorkspaceRoots.Length == 0
                    ? $"No file list or workspace folder was supplied, so '{value}' cannot be found."
                    : $"No file matches '{value}'.");
            }
        }

        if (!IsInWorkspace(path, request.WorkspaceRoots, candidates))
        {
            return context.Unresolved("file", "The file is outside the open solution or folder.");
        }

        var file = new FileInfo(path);
        if (!file.Exists)
        {
            return context.Unresolved("file", $"The file '{path}' does not exist.");
        }

        var (text, wasCut, binary) = await ReadFileAsync(file, cancellationToken).ConfigureAwait(false);
        if (binary)
        {
            return context.Unresolved("file", "The file is binary.");
        }

        var access = context.Policy.Check(path, () => text);
        if (!access.IsAllowed && !request.ApprovedSensitive)
        {
            var refused = context.Unresolved("file", access.Reason + " Approve it to pin the file.");
            refused.FilePath = path;
            refused.DisplayName = file.Name;
            refused.RequiresApproval = true;
            return refused;
        }

        var resolution = context.Resolved("file", file.Name, path, text, wasCut);
        resolution.IsSensitive = !access.IsAllowed;
        return resolution;
    }

    private static async Task<MentionResolution> ResolveFolderAsync(Resolution context, CancellationToken cancellationToken)
    {
        var value = context.Parsed.Value;
        if (value.Length == 0)
        {
            return context.Unresolved("folder", "Name a folder, for example @folder:src/Api.");
        }

        var request = context.Request;
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
                    "folder",
                    [.. match.Candidates],
                    match.IsLoose ? $"No folder is named '{value}'; these are the closest." : $"{match.Candidates.Count} folders match '{value}'; pick one.",
                    match.IsLoose);
            }
            else
            {
                return context.Unresolved("folder", $"No folder matches '{value}'.");
            }
        }

        if (request.WorkspaceRoots.Length > 0 && !request.WorkspaceRoots.Any(r => IsUnder(directory, r)))
        {
            return context.Unresolved("folder", "The folder is outside the open solution or folder.");
        }

        var files = candidates.Where(f => IsUnder(f, directory)).ToList();
        if (files.Count == 0 && Directory.Exists(directory))
        {
            files = EnumerateFiles(directory);
        }

        var outline = await BuildOutlineAsync(directory, files, context.Policy, cancellationToken).ConfigureAwait(false);
        return context.Resolved("folder", Path.GetFileName(directory.TrimEnd('\\', '/')) is { Length: > 0 } name ? name + "/" : directory, directory, outline, false);
    }

    private static MentionResolution ResolvePayload(Resolution context, string displayName, string emptyReason)
    {
        var payload = context.Request.Payload;
        if (string.IsNullOrWhiteSpace(payload))
        {
            return context.Unresolved(context.Parsed.Kind, emptyReason);
        }

        return context.Resolved(context.Parsed.Kind, displayName, null, payload, false);
    }

    private static MentionResolution ResolveErrors(Resolution context)
    {
        var payload = context.Request.Payload;
        if (string.IsNullOrWhiteSpace(payload))
        {
            return context.Unresolved("errors", "No diagnostics were supplied.");
        }

        var (text, errors, warnings) = DiagnosticsFormatter.Format(payload);
        var name = string.Create(CultureInfo.InvariantCulture, $"Errors ({errors} errors, {warnings} warnings)");
        return context.Resolved("errors", name, null, text, false);
    }

    private static List<string> GetCandidateFiles(MentionRequest request)
    {
        var supplied = request.CandidateFiles?.Where(f => !string.IsNullOrWhiteSpace(f)).ToList() ?? [];
        if (supplied.Count > 0 || request.WorkspaceRoots.Length == 0)
        {
            return supplied;
        }

        // The client sent no list: look on disk, skipping build output and dependency folders.
        var files = new List<string>();
        foreach (var root in request.WorkspaceRoots.Where(Directory.Exists))
        {
            files.AddRange(EnumerateFiles(root));
            if (files.Count >= MaxEnumeratedFiles)
            {
                break;
            }
        }

        return files;
    }

    private static List<string> EnumerateFiles(string directory)
    {
        var result = new List<string>();
        var pending = new Stack<string>();
        pending.Push(directory);
        while (pending.Count > 0 && result.Count < MaxEnumeratedFiles)
        {
            var current = pending.Pop();
            try
            {
                foreach (var file in Directory.EnumerateFiles(current))
                {
                    result.Add(file);
                }

                foreach (var child in Directory.EnumerateDirectories(current))
                {
                    var name = Path.GetFileName(child);
                    if (!SkippedFolders.Contains(name, StringComparer.OrdinalIgnoreCase))
                    {
                        pending.Push(child);
                    }
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // A folder that cannot be listed is skipped.
            }
        }

        return result;
    }

    private static string? FindExactFile(string value, string[] roots, List<string> candidates)
    {
        foreach (var full in ExpandPath(value, roots))
        {
            if (candidates.Any(c => SamePath(c, full)))
            {
                return candidates.First(c => SamePath(c, full));
            }

            if (File.Exists(full))
            {
                return full;
            }
        }

        return null;
    }

    private static string? FindExactFolder(string value, string[] roots, List<string> candidates)
    {
        foreach (var full in ExpandPath(value, roots))
        {
            if (Directory.Exists(full) || candidates.Any(c => IsUnder(c, full)))
            {
                return full.TrimEnd('\\', '/');
            }
        }

        return null;
    }

    private static IEnumerable<string> ExpandPath(string value, string[] roots)
    {
        string[] absolute = [];
        try
        {
            if (Path.IsPathRooted(value))
            {
                absolute = [Path.GetFullPath(value)];
            }
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            // Not a usable path; it may still be a fuzzy name.
        }

        foreach (var path in absolute)
        {
            yield return path;
        }

        if (absolute.Length > 0)
        {
            yield break;
        }

        foreach (var root in roots)
        {
            string? combined = null;
            try
            {
                combined = Path.GetFullPath(Path.Combine(root, value));
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
            {
                // Skip this root.
            }

            if (combined is not null)
            {
                yield return combined;
            }
        }
    }

    private static HashSet<string> FolderPaths(List<string> files, string[] roots)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in files)
        {
            var directory = Path.GetDirectoryName(file);
            while (!string.IsNullOrEmpty(directory) && seen.Add(directory))
            {
                if (roots.Any(r => SamePath(r, directory)))
                {
                    break;
                }

                directory = Path.GetDirectoryName(directory);
            }
        }

        return seen;
    }

    private static bool IsInWorkspace(string path, string[] roots, List<string> candidates) =>
        roots.Any(r => IsUnder(path, r)) || candidates.Any(c => SamePath(c, path));

    private static bool IsUnder(string path, string directory)
    {
        var normalizedDirectory = directory.Replace('/', '\\').TrimEnd('\\') + "\\";
        return path.Replace('/', '\\').StartsWith(normalizedDirectory, StringComparison.OrdinalIgnoreCase);
    }

    private static bool SamePath(string a, string b) =>
        string.Equals(a.Replace('/', '\\').TrimEnd('\\'), b.Replace('/', '\\').TrimEnd('\\'), StringComparison.OrdinalIgnoreCase);

    private static async Task<(string Text, bool Cut, bool Binary)> ReadFileAsync(FileInfo file, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(file.FullName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 4096, useAsync: true);
        var length = (int)Math.Min(stream.Length, MaxFileBytes);
        var buffer = new byte[length];
        var read = 0;
        while (read < length)
        {
            var n = await stream.ReadAsync(buffer.AsMemory(read, length - read), cancellationToken).ConfigureAwait(false);
            if (n == 0)
            {
                break;
            }

            read += n;
        }

        if (Array.IndexOf(buffer, (byte)0, 0, Math.Min(read, 8000)) >= 0)
        {
            return (string.Empty, false, true);
        }

        var text = Encoding.UTF8.GetString(buffer, 0, read).TrimStart('﻿');
        return (text, stream.Length > MaxFileBytes, false);
    }

    private static async Task<string> BuildOutlineAsync(string directory, List<string> files, FileAccessPolicy policy, CancellationToken cancellationToken)
    {
        var listed = new List<string>();
        var excluded = 0;
        foreach (var file in files.OrderBy(f => f, StringComparer.OrdinalIgnoreCase))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!policy.Check(file).IsAllowed)
            {
                excluded++;
                continue;
            }

            listed.Add(file);
        }

        var builder = new StringBuilder();
        var shown = listed.Take(MaxOutlineFiles).ToList();
        builder.Append("Folder ").Append(directory).Append(": ")
            .Append(listed.Count.ToString(CultureInfo.InvariantCulture)).Append(" files");
        if (listed.Count > shown.Count)
        {
            builder.Append(CultureInfo.InvariantCulture, $", first {shown.Count} listed");
        }

        if (excluded > 0)
        {
            builder.Append(CultureInfo.InvariantCulture, $", {excluded} excluded or secret files not listed");
        }

        builder.Append('\n');
        foreach (var file in shown)
        {
            var relative = Path.GetRelativePath(directory, file).Replace('\\', '/');
            builder.Append(relative).Append(" (").Append(FormatSize(file)).Append(')');
            var first = await FirstLineAsync(file, cancellationToken).ConfigureAwait(false);
            if (first.Length > 0)
            {
                builder.Append(": ").Append(first);
            }

            builder.Append('\n');
        }

        return builder.ToString();
    }

    private static string FormatSize(string file)
    {
        try
        {
            var length = new FileInfo(file).Length;
            return length < 1024 ? length.ToString(CultureInfo.InvariantCulture) + " B" : (length / 1024.0).ToString("0.#", CultureInfo.InvariantCulture) + " KB";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return "?";
        }
    }

    private static async Task<string> FirstLineAsync(string file, CancellationToken cancellationToken)
    {
        try
        {
            await using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 4096, useAsync: true);
            var buffer = new byte[2048];
            var read = await stream.ReadAsync(buffer.AsMemory(), cancellationToken).ConfigureAwait(false);
            if (Array.IndexOf(buffer, (byte)0, 0, read) >= 0)
            {
                return "(binary)";
            }

            foreach (var line in Encoding.UTF8.GetString(buffer, 0, read).TrimStart('﻿').Split('\n'))
            {
                var trimmed = line.Trim();
                if (trimmed.Length > 0)
                {
                    return trimmed.Length > 100 ? trimmed[..100] + "..." : trimmed;
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // An unreadable file is listed without its first line.
        }

        return string.Empty;
    }

    [LoggerMessage(EventId = 450, Level = LogLevel.Warning, Message = "A {Kind} mention could not be read ({ExceptionType}).")]
    private static partial void LogReadFailed(ILogger logger, string kind, string exceptionType);

    /// <summary>One mention being resolved: the request, its parse, the limit, the counter and the file policy.</summary>
    private sealed class Resolution(MentionRequest request, ParsedMention parsed, int maxTokens, IModelTokenCounter counter, FileAccessPolicy policy)
    {
        public MentionRequest Request => request;

        public ParsedMention Parsed => parsed;

        public FileAccessPolicy Policy => policy;

        public MentionResolution Unresolved(string kind, string reason) => new()
        {
            Mention = request.Mention,
            IsResolved = false,
            Kind = kind,
            Reason = reason,
        };

        /// <summary>Gets a value indicating whether the last <see cref="Ambiguous"/> answer held only letter-order guesses.</summary>
        public bool Loose { get; private set; }

        public MentionResolution Ambiguous(string kind, string[] candidates, string reason, bool loose)
        {
            Loose = loose;
            var result = Unresolved(kind, reason);
            result.Candidates = candidates;
            return result;
        }

        public MentionResolution Resolved(string kind, string displayName, string? filePath, string content, bool alreadyCut)
        {
            var fitted = TextTruncation.KeepHead(content, maxTokens, counter);
            return new MentionResolution
            {
                Mention = request.Mention,
                IsResolved = true,
                Kind = kind,
                DisplayName = displayName,
                FilePath = filePath,
                Content = fitted.Text,
                TokenCount = fitted.Tokens,
                Truncated = fitted.Truncated || alreadyCut,
            };
        }
    }
}

/// <summary>Turns the diagnostics a client sends as JSON lines into text for a prompt.</summary>
internal static class DiagnosticsFormatter
{
    /// <summary>The most diagnostics kept; errors come first.</summary>
    public const int MaxLines = 200;

    /// <summary>Formats diagnostics.</summary>
    /// <param name="payload">One JSON object per line with <c>severity</c>, <c>id</c>, <c>message</c>, <c>file</c> and <c>line</c> (all optional); a line that is not JSON is kept as written.</param>
    /// <returns>The text, and how many errors and warnings it holds.</returns>
    public static (string Text, int Errors, int Warnings) Format(string payload)
    {
        var entries = new List<(int Rank, string Line)>();
        var errors = 0;
        var warnings = 0;
        foreach (var raw in payload.Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0)
            {
                continue;
            }

            var severity = string.Empty;
            var text = line;
            try
            {
                using var document = JsonDocument.Parse(line);
                if (document.RootElement.ValueKind == JsonValueKind.Object)
                {
                    severity = Read(document.RootElement, "severity").ToLowerInvariant();
                    var id = Read(document.RootElement, "id");
                    var message = Read(document.RootElement, "message");
                    var file = Read(document.RootElement, "file");
                    var number = Read(document.RootElement, "line");
                    var where = file.Length == 0 ? string.Empty : number.Length == 0 ? $" ({file})" : $" ({file}:{number})";
                    text = $"{(severity.Length == 0 ? "info" : severity)} {id}: {message}{where}".Replace("  ", " ", StringComparison.Ordinal);
                }
            }
            catch (JsonException)
            {
                // Not JSON: keep the line as it is.
            }

            var rank = severity.StartsWith("err", StringComparison.Ordinal) ? 0 : severity.StartsWith("warn", StringComparison.Ordinal) ? 1 : 2;
            errors += rank == 0 ? 1 : 0;
            warnings += rank == 1 ? 1 : 0;
            entries.Add((rank, text));
        }

        var ordered = entries.Select((e, i) => (e.Rank, e.Line, Index: i)).OrderBy(e => e.Rank).ThenBy(e => e.Index).ToList();
        var shown = ordered.Take(MaxLines).Select(e => e.Line).ToList();
        var result = string.Join("\n", shown);
        if (ordered.Count > shown.Count)
        {
            result += string.Create(CultureInfo.InvariantCulture, $"\n[... {ordered.Count - shown.Count} more diagnostics not shown ...]");
        }

        return (result, errors, warnings);
    }

    private static string Read(JsonElement element, string name)
    {
        foreach (var property in element.EnumerateObject())
        {
            if (!string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            return property.Value.ValueKind switch
            {
                JsonValueKind.String => property.Value.GetString() ?? string.Empty,
                JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False => property.Value.GetRawText(),
                _ => string.Empty,
            };
        }

        return string.Empty;
    }
}

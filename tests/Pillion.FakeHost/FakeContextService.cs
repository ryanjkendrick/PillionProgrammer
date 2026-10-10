using Pillion.Contracts;

namespace Pillion.FakeHost;

/// <summary>
/// A canned <see cref="IContextService"/> for the chat window's mentions. <see cref="ResolveMentionAsync"/> answers by the
/// mention text:
/// <list type="bullet">
/// <item>a name with <c>ambiguous</c> in it: unresolved with two candidate paths (the sub-picker);</item>
/// <item>a name with <c>secret</c> in it, or <c>.env</c>: unresolved with <see cref="MentionResolution.RequiresApproval"/>, and
/// resolved as <see cref="MentionResolution.IsSensitive"/> once the request carries <see cref="MentionRequest.ApprovedSensitive"/>;</item>
/// <item>a name with <c>missing</c> in it: unresolved with a reason;</item>
/// <item><c>@selection</c>, <c>@clipboard</c> and <c>@errors</c>: resolved from the payload (unresolved without one);</item>
/// <item>anything else: a resolved file, read from disk when the path (or a candidate file with that name) exists, else
/// with canned content.</item>
/// </list>
/// Search and the index answer as phase 1's host does (nothing indexed). Only kinds and outcomes are written to stderr.
/// </summary>
internal sealed class FakeContextService : IContextService
{
    /// <summary>The content of a file the fake host cannot read.</summary>
    public const string CannedContent = "// Canned content from Pillion's fake host.\nclass Canned { }\n";

    public Task<SearchResult[]> SearchAsync(SearchRequest request, CancellationToken cancellationToken) => Task.FromResult<SearchResult[]>([]);

    public Task<IndexStatus> GetIndexStatusAsync(CancellationToken cancellationToken) => Task.FromResult(new IndexStatus());

    public Task PauseIndexingAsync(bool pause, CancellationToken cancellationToken) => Task.CompletedTask;

    public Task<MentionResolution> ResolveMentionAsync(MentionRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var (kind, value) = Parse(request);
        var result = Resolve(request, kind, value);
        Console.Error.WriteLine(
            "fakehost: mention " + (kind.Length == 0 ? "fuzzy" : kind) + " "
            + (result.IsResolved ? "resolved" : result.RequiresApproval ? "needs approval" : result.Candidates.Length > 0 ? "ambiguous" : "unresolved"));
        return Task.FromResult(result);
    }

    private static (string Kind, string Value) Parse(MentionRequest request)
    {
        var text = request.Mention.TrimStart('@');
        var colon = text.IndexOf(':', StringComparison.Ordinal);
        var kind = request.Kind;
        if (colon > 0 && text[..colon] is "file" or "folder" or "selection" or "errors" or "clipboard")
        {
            kind = kind.Length == 0 ? text[..colon] : kind;
            text = text[(colon + 1)..];
        }
        else if (kind.Length == 0 && text is "selection" or "errors" or "clipboard")
        {
            kind = text;
            text = string.Empty;
        }

        return (kind, text);
    }

    private static MentionResolution Resolve(MentionRequest request, string kind, string value)
    {
        var mention = request.Mention;
        switch (kind)
        {
            case "selection" or "clipboard" or "errors":
                return string.IsNullOrEmpty(request.Payload)
                    ? new MentionResolution { Mention = mention, Kind = kind, Reason = "There is nothing to pin for @" + kind + "." }
                    : Resolved(mention, kind, kind == "errors" ? "Errors and warnings" : char.ToUpperInvariant(kind[0]) + kind[1..], null, request.Payload!, false);
        }

        var name = Path.GetFileName(value.Replace('/', '\\').TrimEnd('\\'));
        if (value.Contains("ambiguous", StringComparison.OrdinalIgnoreCase))
        {
            var root = request.WorkspaceRoots.FirstOrDefault() ?? @"C:\fake";
            return new MentionResolution
            {
                Mention = mention,
                Kind = "file",
                Candidates = [Path.Combine(root, "A", "Ambiguous.cs"), Path.Combine(root, "B", "Ambiguous.cs")],
                Reason = "More than one file matches.",
            };
        }

        if (value.Contains("missing", StringComparison.OrdinalIgnoreCase))
        {
            return new MentionResolution { Mention = mention, Kind = kind.Length == 0 ? "file" : kind, Reason = "No file in the solution or folder matches " + value + "." };
        }

        var path = Path.IsPathRooted(value) ? value
            : request.CandidateFiles.FirstOrDefault(f => string.Equals(Path.GetFileName(f), name, StringComparison.OrdinalIgnoreCase))
            ?? Path.Combine(request.WorkspaceRoots.FirstOrDefault() ?? @"C:\fake", value);
        var sensitive = value.Contains("secret", StringComparison.OrdinalIgnoreCase) || name.Equals(".env", StringComparison.OrdinalIgnoreCase);
        if (sensitive && !request.ApprovedSensitive)
        {
            return new MentionResolution
            {
                Mention = mention,
                Kind = "file",
                DisplayName = Path.GetFileName(path),
                FilePath = path,
                RequiresApproval = true,
                Reason = "The file looks like it holds secrets.",
            };
        }

        var content = CannedContent;
        try
        {
            if (File.Exists(path))
            {
                content = File.ReadAllText(path);
                content = content.Length > 4000 ? content[..4000] : content;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Canned content instead.
        }

        return Resolved(mention, kind == "folder" ? "folder" : "file", Path.GetFileName(path), path, content, sensitive);
    }

    private static MentionResolution Resolved(string mention, string kind, string displayName, string? path, string content, bool sensitive) => new()
    {
        Mention = mention,
        IsResolved = true,
        Kind = kind,
        DisplayName = displayName,
        FilePath = path,
        Content = content,
        TokenCount = Math.Max(1, content.Length / 4),
        IsSensitive = sensitive,
    };
}

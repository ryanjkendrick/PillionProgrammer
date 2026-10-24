using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using LibGit2Sharp;
using Pillion.Common.Redaction;
using Pillion.Contracts;
using Pillion.ModelGateway;

namespace Pillion.Host.Context.Mentions;

/// <summary>What an <c>@git</c> mention asks for.</summary>
internal enum GitForm
{
    /// <summary>The working-tree changes not yet staged, untracked files included (the default).</summary>
    Diff,

    /// <summary>The staged changes (the index against HEAD).</summary>
    Staged,

    /// <summary>The last N commits with their patches.</summary>
    Log,

    /// <summary>The commits of a revision range (<c>A..B</c>) with their patches.</summary>
    Range,
}

/// <summary>An <c>@git</c> request read from <see cref="MentionRequest.Arguments"/> (or the mention's value).</summary>
/// <param name="Form">What to read.</param>
/// <param name="Count">How many commits for <see cref="GitForm.Log"/>.</param>
/// <param name="Range">The revision range for <see cref="GitForm.Range"/>.</param>
/// <param name="Paths">Paths that limit the changes (relative to the first workspace root, or full).</param>
internal sealed record GitRequest(GitForm Form, int Count, string Range, IReadOnlyList<string> Paths);

/// <summary>The text an <c>@git</c> mention resolved to.</summary>
/// <param name="Reason">Why it failed, or empty.</param>
/// <param name="DisplayName">The pin's name.</param>
/// <param name="Text">The redacted text.</param>
/// <param name="Citations">The changed spans (new side) for the diff forms.</param>
/// <param name="Cut">Whether the text was cut at <see cref="GitMentions.MaxChars"/> or the file limit.</param>
/// <param name="Redactions">How many secrets the scanner replaced.</param>
internal sealed record GitResult(string Reason, string DisplayName, string Text, IReadOnlyList<Citation> Citations, bool Cut, int Redactions)
{
    /// <summary>Gets a value indicating whether it succeeded.</summary>
    public bool IsOk => Reason.Length == 0;

    /// <summary>A failure.</summary>
    /// <param name="reason">Why.</param>
    /// <returns>The result.</returns>
    public static GitResult Fail(string reason) => new(reason, string.Empty, string.Empty, [], false, 0);
}

/// <summary>
/// The <c>@git</c> mention (work item 2.13, design section 7): the working-tree diff, the staged changes or the last N commits
/// of the repository that holds the first workspace root, read with LibGit2Sharp. Every file the exclusion globs or the
/// secret-file rules refuse is listed without its changes, every patch goes through the secret scanner (secrets become
/// <c>&lt;redacted:kind&gt;</c>), and the text stops at <see cref="MaxChars"/> before the token cap cuts it further. Paths
/// outside the repository (or outside the workspace roots) are refused.
/// </summary>
internal static partial class GitMentions
{
    /// <summary>The commits listed when <c>@git:log</c> names no count.</summary>
    public const int DefaultLogCount = 10;

    /// <summary>The most commits <c>@git:log</c> lists.</summary>
    public const int MaxLogCount = 50;

    /// <summary>The most characters gathered before the token cap applies.</summary>
    public const int MaxChars = 400_000;

    /// <summary>The most files listed in one diff.</summary>
    public const int MaxFiles = 300;

    /// <summary>The most citations returned.</summary>
    public const int MaxCitations = 50;

    /// <summary>Reads the request from the arguments, or from the mention's value when there are none.</summary>
    /// <param name="arguments">The request's <see cref="MentionRequest.Arguments"/>.</param>
    /// <param name="value">The value after <c>@git:</c>, for example <c>log 5</c>, <c>log:5</c> or <c>staged</c>.</param>
    /// <returns>The request.</returns>
    public static GitRequest Parse(IReadOnlyList<string>? arguments, string? value)
    {
        var tokens = new List<string>();
        foreach (var argument in arguments is { Count: > 0 } ? arguments : (IReadOnlyList<string>)[value ?? string.Empty])
        {
            tokens.AddRange((argument ?? string.Empty).Split([' ', '\t', ','], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        }

        // "log:5" and "staged:src/Api" split on their first colon (a drive letter's colon comes later, in a path).
        if (tokens.Count > 0 && tokens[0].IndexOf(':', StringComparison.Ordinal) is var colon and > 0
            && IsKeyword(tokens[0][..colon]))
        {
            var rest = tokens[0][(colon + 1)..];
            tokens[0] = tokens[0][..colon];
            if (rest.Length > 0)
            {
                tokens.Insert(1, rest);
            }
        }

        var form = GitForm.Diff;
        var count = DefaultLogCount;
        var range = string.Empty;
        var index = 0;
        if (tokens.Count > 0)
        {
            var first = tokens[0].ToLowerInvariant();
            switch (first)
            {
                case "diff" or "working" or "worktree" or "changes":
                    index = 1;
                    break;
                case "staged" or "cached" or "index":
                    form = GitForm.Staged;
                    index = 1;
                    break;
                case "log" or "commits" or "history":
                    form = GitForm.Log;
                    index = 1;
                    if (tokens.Count > 1 && int.TryParse(tokens[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var n))
                    {
                        count = Math.Clamp(n, 1, MaxLogCount);
                        index = 2;
                    }

                    break;
                default:
                    if (tokens[0].Contains("..", StringComparison.Ordinal) && !tokens[0].Contains('/', StringComparison.Ordinal) && !tokens[0].Contains('\\', StringComparison.Ordinal))
                    {
                        form = GitForm.Range;
                        range = tokens[0];
                        index = 1;
                    }

                    break;
            }
        }

        return new GitRequest(form, count, range, [.. tokens.Skip(index)]);
    }

    /// <summary>Reads the changes or commits.</summary>
    /// <param name="request">What to read.</param>
    /// <param name="workspaceRoots">The open roots; the repository is the one that holds the first root with one.</param>
    /// <param name="policy">The exclusion globs and secret-file rules.</param>
    /// <param name="cancellationToken">Cancels the read between files.</param>
    /// <returns>The result.</returns>
    public static GitResult Read(GitRequest request, IReadOnlyList<string> workspaceRoots, FileAccessPolicy policy, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(workspaceRoots);
        ArgumentNullException.ThrowIfNull(policy);
        if (workspaceRoots.Count == 0)
        {
            return GitResult.Fail("No solution or folder is open, so there is no repository to read.");
        }

        string? repositoryPath = null;
        foreach (var root in workspaceRoots.Where(r => !string.IsNullOrWhiteSpace(r)))
        {
            repositoryPath = Repository.Discover(root);
            if (repositoryPath is not null)
            {
                break;
            }
        }

        if (repositoryPath is null)
        {
            return GitResult.Fail("The open solution or folder is not in a git repository.");
        }

        using var repository = new Repository(repositoryPath);
        if (repository.Info.IsBare || repository.Info.WorkingDirectory is not { } workingDirectory)
        {
            return GitResult.Fail("The repository has no working tree (it is bare).");
        }

        var paths = new List<string>();
        foreach (var path in request.Paths)
        {
            var relative = ToRepositoryPath(path, workingDirectory, workspaceRoots);
            if (relative is null)
            {
                return GitResult.Fail($"'{path}' is outside the repository or the open folders; @git only reads the repository at {workingDirectory.TrimEnd('\\')}.");
            }

            paths.Add(relative);
        }

        var name = Path.GetFileName(workingDirectory.TrimEnd('\\', '/'));
        var builder = new Output(workingDirectory, policy, cancellationToken);
        switch (request.Form)
        {
            case GitForm.Diff:
            {
                var patch = repository.Diff.Compare<Patch>(paths.Count == 0 ? null : paths, includeUntracked: true);
                builder.Header($"Working-tree changes (not staged) in {name}{Scope(paths)}: {Summary(patch)}");
                builder.AddPatch(patch, cite: true);
                return builder.Finish("Git: working tree" + Scope(paths), patch.Any() ? null : "No working-tree changes.");
            }

            case GitForm.Staged:
            {
                var patch = repository.Diff.Compare<Patch>(repository.Head.Tip?.Tree, DiffTargets.Index, paths.Count == 0 ? null : paths);
                builder.Header($"Staged changes in {name}{Scope(paths)}: {Summary(patch)}");
                builder.AddPatch(patch, cite: true);
                return builder.Finish("Git: staged" + Scope(paths), patch.Any() ? null : "Nothing is staged.");
            }

            default:
            {
                if (repository.Head.Tip is null)
                {
                    return GitResult.Fail("The repository has no commits yet.");
                }

                var filter = new CommitFilter { SortBy = CommitSortStrategies.Topological | CommitSortStrategies.Time, IncludeReachableFrom = repository.Head };
                if (request.Form == GitForm.Range)
                {
                    var parts = request.Range.Split("..", 2);
                    var from = parts[0].Length == 0 ? null : repository.Lookup<Commit>(parts[0]);
                    var to = parts.Length < 2 || parts[1].Length == 0 ? repository.Head.Tip : repository.Lookup<Commit>(parts[1]);
                    if ((parts[0].Length > 0 && from is null) || to is null)
                    {
                        return GitResult.Fail($"'{request.Range}' does not name commits in this repository.");
                    }

                    filter = new CommitFilter { SortBy = CommitSortStrategies.Topological | CommitSortStrategies.Time, IncludeReachableFrom = to, ExcludeReachableFrom = from };
                }

                var commits = repository.Commits.QueryBy(filter).Take(request.Form == GitForm.Log ? request.Count : MaxLogCount).ToList();
                builder.Header(request.Form == GitForm.Log
                    ? string.Create(CultureInfo.InvariantCulture, $"The last {commits.Count} commits of {name} ({repository.Head.FriendlyName}){Scope(paths)}, newest first")
                    : string.Create(CultureInfo.InvariantCulture, $"The {commits.Count} commits of {request.Range} in {name}{Scope(paths)}, newest first"));
                foreach (var commit in commits)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    builder.AddCommit(commit);
                    var parent = commit.Parents.FirstOrDefault();
                    var patch = repository.Diff.Compare<Patch>(parent?.Tree, commit.Tree, paths.Count == 0 ? null : paths);
                    builder.AddPatch(patch, cite: false);
                }

                var display = request.Form == GitForm.Log
                    ? string.Create(CultureInfo.InvariantCulture, $"Git: last {commits.Count} commits")
                    : "Git: " + request.Range;
                return builder.Finish(display + Scope(paths), commits.Count == 0 ? "No commits match." : null);
            }
        }
    }

    /// <summary>The path relative to the working directory (forward slashes), or <see langword="null"/> when it is outside the repository or the workspace.</summary>
    internal static string? ToRepositoryPath(string path, string workingDirectory, IReadOnlyList<string> workspaceRoots)
    {
        string full;
        try
        {
            full = Path.IsPathRooted(path) ? Path.GetFullPath(path) : Path.GetFullPath(Path.Combine(workspaceRoots.Count > 0 ? workspaceRoots[0] : workingDirectory, path));
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }

        var work = workingDirectory.Replace('/', '\\').TrimEnd('\\');
        var normalized = full.Replace('/', '\\').TrimEnd('\\');
        var insideRepository = normalized.Equals(work, StringComparison.OrdinalIgnoreCase) || normalized.StartsWith(work + "\\", StringComparison.OrdinalIgnoreCase);
        var insideWorkspace = workspaceRoots.Count == 0 || workspaceRoots.Any(r =>
        {
            var root = r.Replace('/', '\\').TrimEnd('\\');
            return normalized.Equals(root, StringComparison.OrdinalIgnoreCase) || normalized.StartsWith(root + "\\", StringComparison.OrdinalIgnoreCase)
                || root.StartsWith(normalized + "\\", StringComparison.OrdinalIgnoreCase);
        });
        if (!insideRepository || !insideWorkspace)
        {
            return null;
        }

        var relative = normalized.Length == work.Length ? string.Empty : normalized[(work.Length + 1)..];
        return relative.Replace('\\', '/');
    }

    private static bool IsKeyword(string text) => text.ToLowerInvariant() is "diff" or "working" or "worktree" or "changes" or "staged" or "cached" or "index" or "log" or "commits" or "history";

    private static string Scope(List<string> paths) => paths.Count == 0 ? string.Empty : " (" + string.Join(", ", paths.Select(p => p.Length == 0 ? "/" : p)) + ")";

    private static string Summary(Patch patch)
    {
        var files = patch.Count();
        return files == 0 ? "none" : string.Create(CultureInfo.InvariantCulture, $"{files} file{(files == 1 ? string.Empty : "s")}, +{patch.LinesAdded} -{patch.LinesDeleted}");
    }

    [GeneratedRegex(@"^@@ -\d+(?:,\d+)? \+(\d+)(?:,(\d+))? @@", RegexOptions.Multiline | RegexOptions.CultureInvariant)]
    private static partial Regex HunkHeader();

    /// <summary>Gathers the text up to <see cref="MaxChars"/>.</summary>
    private sealed class Output(string workingDirectory, FileAccessPolicy policy, CancellationToken cancellationToken)
    {
        private readonly StringBuilder _text = new();
        private readonly List<Citation> _citations = [];
        private bool _cut;
        private int _files;
        private int _redactions;

        public void Header(string line) => Append(line + "\n\n");

        public void AddCommit(Commit commit)
        {
            var when = commit.Author.When.ToString("yyyy-MM-dd HH:mm zzz", CultureInfo.InvariantCulture);
            var message = string.Join("\n", (commit.Message ?? string.Empty).TrimEnd().Split('\n').Select(l => "    " + l.TrimEnd('\r')));
            Append(Redact($"commit {commit.Sha}\nAuthor: {commit.Author.Name}\nDate:   {when}\n\n{message}\n\n"));
        }

        public void AddPatch(Patch patch, bool cite)
        {
            foreach (var entry in patch)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (_cut)
                {
                    return;
                }

                if (++_files > MaxFiles)
                {
                    Append(string.Create(CultureInfo.InvariantCulture, $"[... more changed files not shown (over {MaxFiles}) ...]\n"));
                    _cut = true;
                    return;
                }

                var full = Path.Combine(workingDirectory, entry.Path.Replace('/', Path.DirectorySeparatorChar));
                var access = policy.Check(entry.Path, () => ReadSmall(full));
                if (!access.IsAllowed)
                {
                    Append($"diff --git a/{entry.Path} b/{entry.Path}\n[changes to this file are not shown: {access.Reason}]\n");
                    continue;
                }

                var text = entry.Patch ?? string.Empty;
                if (entry.IsBinaryComparison)
                {
                    Append($"diff --git a/{entry.Path} b/{entry.Path}\n[binary file {entry.Status.ToString().ToLowerInvariant()}]\n");
                    continue;
                }

                Append(Redact(text.EndsWith('\n') ? text : text + "\n"));
                if (cite && entry.Status != ChangeKind.Deleted)
                {
                    foreach (Match hunk in HunkHeader().Matches(text))
                    {
                        var start = int.Parse(hunk.Groups[1].Value, CultureInfo.InvariantCulture);
                        var length = hunk.Groups[2].Success ? int.Parse(hunk.Groups[2].Value, CultureInfo.InvariantCulture) : 1;
                        if (length > 0 && _citations.Count < MaxCitations)
                        {
                            _citations.Add(new Citation { FilePath = full, StartLine = Math.Max(0, start - 1), EndLine = Math.Max(0, start + length - 2) });
                        }
                    }
                }
            }
        }

        public GitResult Finish(string displayName, string? emptyNote)
        {
            if (emptyNote is not null)
            {
                Append(emptyNote + "\n");
            }

            return new GitResult(string.Empty, displayName, _text.ToString().TrimEnd() + "\n", _citations, _cut, _redactions);
        }

        private static string? ReadSmall(string path)
        {
            try
            {
                var info = new FileInfo(path);
                return info.Exists && info.Length <= 1024 * 1024 ? File.ReadAllText(path) : null;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return null;
            }
        }

        private string Redact(string text)
        {
            var redacted = LogRedactor.Redact(text);
            if (redacted == LogRedactor.ScanFailedPlaceholder)
            {
                _redactions++;
                return "[not shown: the secret scan of this part timed out]\n";
            }

            if (!ReferenceEquals(redacted, text))
            {
                _redactions += Regex.Count(redacted, "<redacted:", RegexOptions.CultureInvariant) - Regex.Count(text, "<redacted:", RegexOptions.CultureInvariant);
            }

            return redacted;
        }

        private void Append(string text)
        {
            if (_cut)
            {
                return;
            }

            if (_text.Length + text.Length > MaxChars)
            {
                var room = Math.Max(0, MaxChars - _text.Length);
                var cutAt = text.LastIndexOf('\n', Math.Max(0, Math.Min(room, text.Length) - 1));
                _text.Append(text, 0, cutAt > 0 ? cutAt + 1 : 0);
                _text.Append("[... the rest is not shown ...]\n");
                _cut = true;
                return;
            }

            _text.Append(text);
        }
    }
}

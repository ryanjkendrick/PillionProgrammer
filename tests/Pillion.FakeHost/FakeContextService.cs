using System.Security.Cryptography;
using System.Text;
using Pillion.Contracts;

namespace Pillion.FakeHost;

/// <summary>
/// A canned <see cref="IContextService"/> for the chat window's mentions and, from contract 1.3, the index.
/// <para>
/// <see cref="ResolveMentionAsync"/> answers by the mention text:
/// <list type="bullet">
/// <item>a name with <c>ambiguous</c> in it: unresolved with two candidate paths (the sub-picker);</item>
/// <item>a name with <c>secret</c> in it, or <c>.env</c>: unresolved with <see cref="MentionResolution.RequiresApproval"/>, and
/// resolved as <see cref="MentionResolution.IsSensitive"/> once the request carries <see cref="MentionRequest.ApprovedSensitive"/>;</item>
/// <item>a name with <c>missing</c> in it: unresolved with a reason;</item>
/// <item><c>@selection</c>, <c>@clipboard</c> and <c>@errors</c>, and from 1.3 <c>@output</c>, <c>@build</c> and <c>@tests</c>:
/// resolved from the payload (unresolved without one);</item>
/// <item>from 1.3, <c>@symbol:Name</c>: a canned class with a citation of its definition; <c>@docs</c>: a README with a citation;
/// <c>@solution</c>: a repo map; <c>@git</c>: a diff, the staged changes or a log, named by <see cref="MentionRequest.Arguments"/>;</item>
/// <item>anything else: a resolved file, read from disk when the path (or a candidate file with that name) exists, else
/// with canned content.</item>
/// </list>
/// </para>
/// <para>
/// The index (for the VSIX tests of work items 2.12 and 2.15): with <c>PILLION_FAKEHOST_INDEX</c> unset there is no index, as
/// in phase 1, and search finds nothing; <c>ready</c> starts with <see cref="CannedRoot"/> indexed. The test hooks are the
/// calls themselves, as with <c>settingsChanged</c>: <see cref="PauseIndexingAsync"/> reports <see cref="IndexPhase.Paused"/>
/// (by the user) or <see cref="IndexPhase.Idle"/>; <see cref="RebuildIndexAsync"/> walks through Discovering, Hashing,
/// Chunking, Embedding and Storing to Idle with the root indexed; <see cref="DeleteIndexAsync"/> removes an index. Each change
/// is raised as <see cref="StatusChanged"/>, which Program.cs sends as the <c>indexStatusChanged</c> notification, never more
/// than twice a second. With a root indexed, search answers two canned hits. <see cref="ListExcludedFilesAsync"/> pages a
/// canned list (the secret-bearing files first) and <see cref="ListIndexesAsync"/> lists the indexed roots and an orphan
/// whose root is gone.
/// </para>
/// Only kinds, phases and outcomes are written to stderr.
/// </summary>
internal sealed class FakeContextService : IContextService, IDisposable
{
    /// <summary>The content of a file the fake host cannot read.</summary>
    public const string CannedContent = "// Canned content from Pillion's fake host.\nclass Canned { }\n";

    /// <summary>The root the canned index covers.</summary>
    public const string CannedRoot = @"C:\fake";

    /// <summary>The id of the canned index of <see cref="CannedRoot"/>.</summary>
    public const string CannedIndexId = "0123456789abcdef0123456789abcdef";

    /// <summary>The id of the canned orphan index (its root no longer exists).</summary>
    public const string OrphanIndexId = "fedcba9876543210fedcba9876543210";

    /// <summary>The shortest time between two status notifications (the contract's "at most twice a second").</summary>
    public static readonly TimeSpan StatusInterval = TimeSpan.FromMilliseconds(500);

    private static readonly ExcludedFile[] CannedExcluded =
    [
        new() { Path = ".env", Reason = "SecretBearing", Description = "Not indexed: the file may hold secrets (.env).", IsSecretBearing = true },
        new() { Path = "src/appsettings.Development.json", Reason = "SecretBearing", Description = "Not indexed: the file may hold secrets (appsettings.*.json).", IsSecretBearing = true },
        new() { Path = "bin", Reason = "BuiltIn", Description = "Not indexed: built-in exclusion (bin).", IsDirectory = true },
        new() { Path = "obj", Reason = "BuiltIn", Description = "Not indexed: built-in exclusion (obj).", IsDirectory = true },
        new() { Path = "package-lock.json", Reason = "LockFile", Description = "Not indexed: lock file (package-lock.json)." },
        new() { Path = "wwwroot/site.min.js", Reason = "MinifiedFile", Description = "Not indexed: minified file (*.min.js)." },
    ];

    private static readonly string[] Kinds =
    [
        MentionKinds.File, MentionKinds.Folder, MentionKinds.Selection, MentionKinds.Errors, MentionKinds.Clipboard, MentionKinds.Symbol,
        MentionKinds.Docs, MentionKinds.Solution, MentionKinds.Git, MentionKinds.Output, MentionKinds.Build, MentionKinds.Tests,
    ];

    private static readonly string[] BareKinds =
    [
        MentionKinds.Selection, MentionKinds.Errors, MentionKinds.Clipboard, MentionKinds.Docs, MentionKinds.Solution, MentionKinds.Git,
        MentionKinds.Output, MentionKinds.Build, MentionKinds.Tests,
    ];

    private readonly Lock _gate = new();
    private readonly SemaphoreSlim _sendGate = new(1, 1);
    private readonly Dictionary<string, IndexRootStatus> _roots = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<IndexInfo> _indexes = [];
    private IndexPhase _phase;
    private IndexProgress? _progress;
    private bool _paused;
    private bool _rebuilding;
    private DateTime? _lastUpdatedUtc;
    private DateTime _lastSent = DateTime.MinValue;

    public FakeContextService()
    {
        _indexes.Add(new IndexInfo
        {
            IndexId = OrphanIndexId,
            RootPath = @"C:\gone\OldRepo",
            EmbeddingModelId = "bge-small-en-v1.5",
            ChunkerVersion = "1",
            DiskBytes = 4_200_000,
            LastUsedUtc = new DateTime(2026, 9, 1, 8, 0, 0, DateTimeKind.Utc),
            RootExists = false,
        });

        if (string.Equals(Environment.GetEnvironmentVariable("PILLION_FAKEHOST_INDEX"), "ready", StringComparison.OrdinalIgnoreCase))
        {
            MarkIndexed(CannedRoot);
        }
    }

    /// <summary>Raised with the new status after every change; Program.cs sends it as <c>indexStatusChanged</c>.</summary>
    public event Action<IndexStatus>? StatusChanged;

    public void Dispose() => _sendGate.Dispose();

    public async Task<SearchResult[]> SearchAsync(SearchRequest request, CancellationToken cancellationToken) =>
        (await SearchDetailedAsync(request, cancellationToken).ConfigureAwait(false)).Results;

    public Task<SearchResponse> SearchDetailedAsync(SearchRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        lock (_gate)
        {
            if (_roots.Count == 0)
            {
                return Task.FromResult(new SearchResponse());
            }
        }

        SearchResult[] hits =
        [
            new()
            {
                FilePath = Path.Combine(CannedRoot, "src", "Program.cs"),
                StartLine = 11,
                EndLine = 14,
                Snippet = "static void Main(string[] args)",
                Score = 0.031,
                ChunkId = CannedIndexId + ":7",
                Breadcrumb = "src/Program.cs > Fake > Program > Main",
                Kind = "method",
                Partition = "src",
                Language = "csharp",
                LexicalRank = 1,
                VectorRank = 2,
                FusedScore = 0.031,
                Text = request.IncludeText ? "static void Main(string[] args)\n{\n    Runner.Run();\n}" : null,
            },
            new()
            {
                FilePath = Path.Combine(CannedRoot, "src", "Runner.cs"),
                StartLine = 3,
                EndLine = 9,
                Snippet = "public static void Run()",
                Score = 0.016,
                ChunkId = CannedIndexId + ":12",
                Breadcrumb = "src/Runner.cs > Fake > Runner > Run",
                Kind = "method",
                Partition = "src",
                Language = "csharp",
                LexicalRank = 0,
                VectorRank = 1,
                FusedScore = 0.016,
                Text = request.IncludeText ? "public static void Run()\n{\n}" : null,
            },
        ];

        var max = request.MaxResults > 0 ? Math.Min(request.MaxResults, hits.Length) : hits.Length;
        Console.Error.WriteLine("fakehost: search " + request.Mode + " " + max + " hits");
        return Task.FromResult(new SearchResponse { Results = [.. hits.Take(max)], LexicalMs = 4.5, VectorMs = 6.25, FusionMs = 0.5, TotalMs = 12 });
    }

    public Task<IndexStatus> GetIndexStatusAsync(CancellationToken cancellationToken) => Task.FromResult(Snapshot());

    public Task PauseIndexingAsync(bool pause, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            _paused = pause;
            _phase = pause ? IndexPhase.Paused : _rebuilding ? IndexPhase.Discovering : IndexPhase.Idle;
        }

        Console.Error.WriteLine("fakehost: indexing " + (pause ? "paused" : "resumed"));
        Raise();
        return Task.CompletedTask;
    }

    public Task<ExcludedFilesPage> ListExcludedFilesAsync(ExcludedFilesRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var matching = CannedExcluded.Where(f => !request.SecretOnly || f.IsSecretBearing).ToList();
        var limit = request.Limit <= 0 ? 200 : Math.Min(request.Limit, 1000);
        var offset = Math.Max(0, request.Offset);
        var page = new ExcludedFilesPage { Items = [.. matching.Skip(offset).Take(limit)], TotalCount = matching.Count, HasMore = offset + limit < matching.Count };
        Console.Error.WriteLine("fakehost: excluded files " + page.Items.Length + " of " + page.TotalCount);
        return Task.FromResult(page);
    }

    public Task RebuildIndexAsync(string rootPath, CancellationToken cancellationToken)
    {
        var root = string.IsNullOrWhiteSpace(rootPath) ? CannedRoot : rootPath;
        lock (_gate)
        {
            if (_rebuilding)
            {
                return Task.CompletedTask;
            }

            _rebuilding = true;
        }

        Console.Error.WriteLine("fakehost: index rebuild started");
        _ = Task.Run(() => RunRebuildAsync(root), CancellationToken.None);
        return Task.CompletedTask;
    }

    public Task<bool> DeleteIndexAsync(string indexId, CancellationToken cancellationToken)
    {
        bool deleted;
        lock (_gate)
        {
            deleted = _indexes.RemoveAll(i => i.IndexId == indexId) > 0;
            foreach (var root in _roots.Where(r => r.Value.IndexId == indexId).Select(r => r.Key).ToList())
            {
                _roots.Remove(root);
            }
        }

        Console.Error.WriteLine("fakehost: index delete " + (deleted ? "done" : "unknown"));
        if (deleted)
        {
            Raise();
        }

        return Task.FromResult(deleted);
    }

    public Task<IndexInfo[]> ListIndexesAsync(CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            return Task.FromResult<IndexInfo[]>([.. _indexes.OrderBy(i => i.RootPath, StringComparer.OrdinalIgnoreCase)]);
        }
    }

    /// <summary>Gets the last workspace the client set (for VSIX tests: proves the relay reached the host).</summary>
    public WorkspaceDescription? Workspace { get; private set; }

    /// <summary>Gets the last IDE activity the client reported through <c>ideActivityChanged</c>.</summary>
    public IdeActivity Activity { get; private set; }

    public Task SetWorkspaceAsync(WorkspaceDescription workspace, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        Workspace = workspace;
        var kind = workspace.SolutionPath is not null ? "solution" : workspace.FolderPath is not null ? "folder" : "none";
        Console.Error.WriteLine("fakehost: workspace " + kind + " with " + workspace.ProjectPaths.Length + " projects");
        return Task.CompletedTask;
    }

    /// <summary>Records an <c>ideActivityChanged</c> notification (registered by Program.cs).</summary>
    /// <param name="activity">The activities in progress.</param>
    public void OnIdeActivityChanged(IdeActivity activity)
    {
        Activity = activity;
        Console.Error.WriteLine("fakehost: ide activity " + activity);
    }

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
        var kind = request.Kind.Length > 0 ? request.Kind : request.Qualifier;
        if (colon > 0 && Kinds.Contains(text[..colon]))
        {
            kind = kind.Length == 0 ? text[..colon] : kind;
            text = text[(colon + 1)..];
        }
        else if (kind.Length == 0 && BareKinds.Contains(text))
        {
            kind = text;
            text = string.Empty;
        }

        return (kind, text);
    }

    private static MentionResolution Resolve(MentionRequest request, string kind, string value)
    {
        var mention = request.Mention;
        var workspace = request.WorkspaceRoots.FirstOrDefault() ?? CannedRoot;
        switch (kind)
        {
            case "selection" or "clipboard" or "errors" or "output" or "build" or "tests":
                return string.IsNullOrEmpty(request.Payload)
                    ? new MentionResolution { Mention = mention, Kind = kind, Reason = "There is nothing to pin for @" + kind + "." }
                    : Resolved(mention, kind, kind == "errors" ? "Errors and warnings" : char.ToUpperInvariant(kind[0]) + kind[1..], null, request.Payload!, false);
            case "symbol":
                {
                    var symbol = value.Length == 0 ? "Canned" : value;
                    var file = Path.Combine(workspace, "src", symbol + ".cs");
                    var resolved = Resolved(mention, kind, symbol, file, "class " + symbol + " { }\n// 2 references (canned)\n", false);
                    resolved.Citations = [new Citation { FilePath = file, StartLine = 4, EndLine = 20, ChunkId = CannedIndexId + ":3", Breadcrumb = "src/" + symbol + ".cs > Fake > " + symbol }];
                    return resolved;
                }

            case "docs":
                {
                    var readme = Path.Combine(workspace, "README.md");
                    var resolved = Resolved(mention, kind, "README.md", readme, "# Fake repository\nCanned documentation.\n", false);
                    resolved.Citations = [new Citation { FilePath = readme, StartLine = 0, EndLine = 1 }];
                    return resolved;
                }

            case "solution":
                return Resolved(mention, kind, "Repo map", null, "src/Program.cs\n  class Program\n    Main(string[])\n", false);
            case "git":
                {
                    var source = request.Arguments.FirstOrDefault() ?? "diff";
                    var display = source switch
                    {
                        "diff" => "Working-tree diff",
                        "staged" => "Staged changes",
                        "log" => "Last " + (request.Arguments.Length > 1 ? request.Arguments[1] : "10") + " commits",
                        _ => "Commits " + source,
                    };
                    return Resolved(mention, kind, display, null, "diff --git a/src/Program.cs b/src/Program.cs\n+    Runner.Run();\n", false);
                }
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

    private static IndexRootStatus IndexedRoot(string root) => new()
    {
        RootPath = root,
        IndexId = string.Equals(root, CannedRoot, StringComparison.OrdinalIgnoreCase)
            ? CannedIndexId
            : Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(root.ToUpperInvariant())))[..32],
        Role = IndexRole.Writer,
        IndexedFileCount = 40,
        TotalFileCount = 40,
        ExcludedFileCount = CannedExcluded.Length,
        SecretFileCount = CannedExcluded.Count(f => f.IsSecretBearing),
        ChunkCount = 320,
        DiskBytes = 480_000,
        LastFullSweepUtc = DateTime.UtcNow,
        EmbeddingModelId = "bge-small-en-v1.5",
        ChunkerVersion = "1",
        SchemaVersion = 2,
    };

    private void MarkIndexed(string root)
    {
        var status = IndexedRoot(root);
        _roots[root] = status;
        _indexes.RemoveAll(i => i.IndexId == status.IndexId);
        _indexes.Add(new IndexInfo
        {
            IndexId = status.IndexId,
            RootPath = root,
            EmbeddingModelId = status.EmbeddingModelId,
            ChunkerVersion = status.ChunkerVersion,
            DiskBytes = status.DiskBytes,
            LastUsedUtc = DateTime.UtcNow,
            RootExists = true,
            IsInUse = true,
            IsOpen = true,
        });
        _lastUpdatedUtc = DateTime.UtcNow;
    }

    private async Task RunRebuildAsync(string root)
    {
        (IndexPhase Phase, int Files, long Chunks)[] steps =
        [
            (IndexPhase.Discovering, 0, 0),
            (IndexPhase.Hashing, 10, 0),
            (IndexPhase.Chunking, 20, 80),
            (IndexPhase.Embedding, 30, 160),
            (IndexPhase.Storing, 40, 320),
        ];

        lock (_gate)
        {
            _roots.Remove(root);
        }

        foreach (var (phase, files, chunks) in steps)
        {
            lock (_gate)
            {
                _phase = _paused ? IndexPhase.Paused : phase;
                _progress = new IndexProgress { FilesDone = files, FilesTotal = 40, ChunksDone = chunks, EstimatedRemainingSeconds = (40 - files) / 10 };
            }

            Console.Error.WriteLine("fakehost: index " + phase);
            await RaiseAsync().ConfigureAwait(false);
        }

        lock (_gate)
        {
            MarkIndexed(root);
            _rebuilding = false;
            _progress = null;
            _phase = _paused ? IndexPhase.Paused : IndexPhase.Idle;
        }

        Console.Error.WriteLine("fakehost: index rebuild finished");
        await RaiseAsync().ConfigureAwait(false);
    }

    private IndexStatus Snapshot()
    {
        lock (_gate)
        {
            var roots = _roots.Values.OrderBy(r => r.RootPath, StringComparer.OrdinalIgnoreCase).ToArray();
            return new IndexStatus
            {
                IsIndexing = _rebuilding && !_paused,
                IsPaused = _paused,
                IndexedFileCount = roots.Sum(r => r.IndexedFileCount) + (_progress?.FilesDone ?? 0),
                TotalFileCount = roots.Sum(r => r.TotalFileCount) + (_progress?.FilesTotal ?? 0),
                LastUpdatedUtc = _lastUpdatedUtc,
                Phase = _phase,
                PauseReason = _paused ? IndexPauseReason.User : IndexPauseReason.None,
                Roots = roots,
                ExcludedSecretFileCount = roots.Sum(r => r.SecretFileCount),
                Progress = _progress is null
                    ? null
                    : new IndexProgress { FilesDone = _progress.FilesDone, FilesTotal = _progress.FilesTotal, ChunksDone = _progress.ChunksDone, EstimatedRemainingSeconds = _progress.EstimatedRemainingSeconds },
            };
        }
    }

    private void Raise() => _ = RaiseAsync();

    /// <summary>Raises <see cref="StatusChanged"/> with the current status, in order and never sooner than <see cref="StatusInterval"/> after the last.</summary>
    private async Task RaiseAsync()
    {
        await _sendGate.WaitAsync().ConfigureAwait(false);
        try
        {
            var wait = _lastSent + StatusInterval - DateTime.UtcNow;
            if (wait > TimeSpan.Zero)
            {
                await Task.Delay(wait).ConfigureAwait(false);
            }

            StatusChanged?.Invoke(Snapshot());
            _lastSent = DateTime.UtcNow;
        }
        finally
        {
            _sendGate.Release();
        }
    }
}

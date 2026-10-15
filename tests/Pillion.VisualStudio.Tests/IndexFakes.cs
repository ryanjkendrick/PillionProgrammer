using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Pillion.Contracts;

namespace Pillion.VisualStudio.Tests;

/// <summary>An <see cref="IContextService"/> whose index state is set by the test (the Index window's view-model tests).</summary>
internal sealed class ScriptedContextService : IContextService
{
    private readonly ConcurrentQueue<string> _calls = new();
    private readonly object _gate = new();

    /// <summary>Gets or sets the status <see cref="GetIndexStatusAsync"/> returns.</summary>
    public IndexStatus Status { get; set; } = new();

    /// <summary>Gets the excluded files, in the order the host would list them (secret-bearing first).</summary>
    public List<ExcludedFile> Excluded { get; } = [];

    /// <summary>Gets the index folders.</summary>
    public List<IndexInfo> Indexes { get; } = [];

    /// <summary>Gets or sets what is called whenever the scripted host changes the status itself (pause, resume, delete); the test raises the notification.</summary>
    public Action<IndexStatus>? StatusChanged { get; set; }

    /// <summary>Gets or sets an exception to throw from the named method.</summary>
    public Dictionary<string, Exception> Failures { get; } = [];

    /// <summary>Gets the requests <see cref="ListExcludedFilesAsync"/> received.</summary>
    public ConcurrentQueue<ExcludedFilesRequest> ExcludedRequests { get; } = new();

    /// <summary>Gets the arguments of <see cref="PauseIndexingAsync"/> calls.</summary>
    public ConcurrentQueue<bool> PauseCalls { get; } = new();

    /// <summary>Gets the roots passed to <see cref="RebuildIndexAsync"/>.</summary>
    public ConcurrentQueue<string> RebuildCalls { get; } = new();

    /// <summary>Gets the ids passed to <see cref="DeleteIndexAsync"/>.</summary>
    public ConcurrentQueue<string> DeleteCalls { get; } = new();

    /// <summary>Gets or sets a task <see cref="RebuildIndexAsync"/> waits for.</summary>
    public Task? RebuildGate { get; set; }

    public int Count(string method) => _calls.Count(c => c == method);

    public Task<IndexStatus> GetIndexStatusAsync(CancellationToken cancellationToken)
    {
        Record(nameof(GetIndexStatusAsync));
        return Task.FromResult(Status);
    }

    public Task PauseIndexingAsync(bool pause, CancellationToken cancellationToken)
    {
        Record(nameof(PauseIndexingAsync));
        PauseCalls.Enqueue(pause);
        IndexStatus changed;
        lock (_gate)
        {
            Status = new IndexStatus
            {
                IsPaused = pause,
                IsIndexing = false,
                Phase = pause ? IndexPhase.Paused : IndexPhase.Idle,
                PauseReason = pause ? IndexPauseReason.User : IndexPauseReason.None,
                Roots = Status.Roots,
                IndexedFileCount = Status.IndexedFileCount,
                TotalFileCount = Status.TotalFileCount,
                ExcludedSecretFileCount = Status.ExcludedSecretFileCount,
            };
            changed = Status;
        }

        StatusChanged?.Invoke(changed);
        return Task.CompletedTask;
    }

    public Task<ExcludedFilesPage> ListExcludedFilesAsync(ExcludedFilesRequest request, CancellationToken cancellationToken)
    {
        Record(nameof(ListExcludedFilesAsync));
        ExcludedRequests.Enqueue(request);
        var matching = Excluded.Where(f => !request.SecretOnly || f.IsSecretBearing).ToList();
        var limit = request.Limit <= 0 ? 200 : Math.Min(request.Limit, 1000);
        var offset = Math.Max(0, request.Offset);
        return Task.FromResult(new ExcludedFilesPage
        {
            Items = [.. matching.Skip(offset).Take(limit)],
            TotalCount = matching.Count,
            HasMore = offset + limit < matching.Count,
        });
    }

    public async Task RebuildIndexAsync(string rootPath, CancellationToken cancellationToken)
    {
        Record(nameof(RebuildIndexAsync));
        RebuildCalls.Enqueue(rootPath);
        if (RebuildGate is { } gate)
        {
            await gate.ConfigureAwait(false);
        }
    }

    public Task<bool> DeleteIndexAsync(string indexId, CancellationToken cancellationToken)
    {
        Record(nameof(DeleteIndexAsync));
        DeleteCalls.Enqueue(indexId);
        lock (_gate)
        {
            var removed = Indexes.RemoveAll(i => i.IndexId == indexId) > 0;
            return Task.FromResult(removed);
        }
    }

    public Task SetWorkspaceAsync(WorkspaceDescription workspace, CancellationToken cancellationToken)
    {
        Record(nameof(SetWorkspaceAsync));
        return Task.CompletedTask;
    }

    public Task<IndexInfo[]> ListIndexesAsync(CancellationToken cancellationToken)
    {
        Record(nameof(ListIndexesAsync));
        lock (_gate)
        {
            return Task.FromResult(Indexes.OrderBy(i => i.RootPath, StringComparer.OrdinalIgnoreCase).ToArray());
        }
    }

    public Task<SearchResult[]> SearchAsync(SearchRequest request, CancellationToken cancellationToken) => Task.FromResult<SearchResult[]>([]);

    public Task<SearchResponse> SearchDetailedAsync(SearchRequest request, CancellationToken cancellationToken) => Task.FromResult(new SearchResponse());

    public Task<MentionResolution> ResolveMentionAsync(MentionRequest request, CancellationToken cancellationToken) => Task.FromResult(new MentionResolution());

    private void Record(string method)
    {
        _calls.Enqueue(method);
        if (Failures.TryGetValue(method, out var failure))
        {
            throw failure;
        }
    }
}

/// <summary>Builders for the index DTOs the Index window tests use.</summary>
internal static class IndexData
{
    public const string RootA = @"C:\src\Alpha";
    public const string RootB = @"C:\src\Beta";

    public static IndexRootStatus Root(string path, int files = 40, long chunks = 320, long bytes = 480_000, string id = "", IndexRole role = IndexRole.Writer, int excluded = 6, int secret = 2) => new()
    {
        RootPath = path,
        IndexId = id.Length > 0 ? id : "id-" + System.IO.Path.GetFileName(path).ToLowerInvariant(),
        Role = role,
        IndexedFileCount = files,
        TotalFileCount = files,
        ExcludedFileCount = excluded,
        SecretFileCount = secret,
        ChunkCount = chunks,
        DiskBytes = bytes,
        LastFullSweepUtc = new DateTime(2026, 10, 14, 9, 30, 0, DateTimeKind.Utc),
        EmbeddingModelId = "bge-small-en-v1.5",
        ChunkerVersion = "1",
        SchemaVersion = 2,
    };

    public static IndexStatus Idle(params IndexRootStatus[] roots) => new()
    {
        Phase = IndexPhase.Idle,
        Roots = roots,
        IndexedFileCount = roots.Sum(r => r.IndexedFileCount),
        TotalFileCount = roots.Sum(r => r.TotalFileCount),
        ExcludedSecretFileCount = roots.Sum(r => r.SecretFileCount),
        LastUpdatedUtc = new DateTime(2026, 10, 14, 9, 31, 0, DateTimeKind.Utc),
    };

    public static IndexStatus Running(IndexPhase phase, int done, int total, long chunks, int remaining, params IndexRootStatus[] roots) => new()
    {
        IsIndexing = true,
        Phase = phase,
        Roots = roots,
        IndexedFileCount = done,
        TotalFileCount = total,
        Progress = new IndexProgress { FilesDone = done, FilesTotal = total, ChunksDone = chunks, EstimatedRemainingSeconds = remaining },
    };

    public static ExcludedFile File(string path, string reason = "BuiltIn", bool secret = false, bool directory = false) => new()
    {
        Path = path,
        Reason = reason,
        Description = "Not indexed: " + reason + " (" + path + ").",
        IsSecretBearing = secret,
        IsDirectory = directory,
    };

    public static IndexInfo Folder(string id, string? root, long bytes = 1_000_000, bool? rootExists = true, bool inUse = false, bool open = false) => new()
    {
        IndexId = id,
        RootPath = root,
        EmbeddingModelId = "bge-small-en-v1.5",
        ChunkerVersion = "1",
        DiskBytes = bytes,
        LastUsedUtc = new DateTime(2026, 10, 1, 8, 0, 0, DateTimeKind.Utc),
        RootExists = rootExists,
        IsInUse = inUse,
        IsOpen = open,
    };
}

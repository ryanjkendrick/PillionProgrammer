using System.Collections.Concurrent;
using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Pillion.ContextEngine;
using Pillion.Host.Indexing;
using Pillion.Host.Tests.TestSupport;
using Pillion.Indexing.Ownership;
using Pillion.Indexing.Retention;

namespace Pillion.Host.Tests.Indexing;

/// <summary>
/// The host schedules the retention sweep (issue #99, found by the Gate 2 run): once the workspace's indexes are open, then
/// once a day on the host's clock; an index a live host holds or that belongs to a root of the open workspace is never
/// deleted; every deletion is logged.
/// </summary>
public sealed class IndexRetentionScheduleTests : IDisposable
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(60);

    private readonly IndexingWorkspace _workspace = new();
    private readonly string _parent;

    public IndexRetentionScheduleTests()
    {
        _parent = Path.GetDirectoryName(_workspace.Repository)!;
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        _workspace.Dispose();
    }

    [Fact]
    public async Task TheSweep_RunsOnceTheWorkspaceIsOpen_ThenDaily_AndNeverDeletesAnIndexOfAnOpenRootOrOneInUse()
    {
        var clock = new ManualTimeProvider();
        if (DateTimeOffset.UtcNow > clock.GetUtcNow())
        {
            clock.Advance(DateTimeOffset.UtcNow - clock.GetUtcNow());
        }

        var paths = new Pillion.Common.PillionPaths(_workspace.Data);
        Fixtures.Copy("shop", _workspace.Repository);

        // Indexes left by earlier sessions: the open root's index under an older embedding model, a folder deleted since,
        // a folder that is deleted later, and a folder nobody opens again.
        var olderModel = await CreateIndexAsync(paths, _workspace.Repository, "fake/older-model");
        var gone = await CreateIndexAsync(paths, Folder("gone"));
        var later = await CreateIndexAsync(paths, Folder("later"));
        var stale = await CreateIndexAsync(paths, Folder("stale"));
        Directory.Delete(Folder("gone"), recursive: true);

        var logs = new ListLoggerFactory();
        var options = new IndexingOptions
        {
            MinimumRefreshInterval = TimeSpan.FromMilliseconds(200),
            PowerPollInterval = TimeSpan.FromHours(1),
            MemoryPollInterval = TimeSpan.FromMilliseconds(50),
            ReaderPollInterval = TimeSpan.FromMilliseconds(200),
            EmbeddingRetryDelay = TimeSpan.FromMilliseconds(10),
            WatchFileSystem = false,
        };
        await using var harness = new IndexingHarness(_workspace.Data, new FakeEmbedder(), options, time: clock, loggerFactory: logs);
        await harness.Service.StartAsync(CancellationToken.None);

        // Nothing is swept before the extension reports the workspace.
        harness.Service.RequestRun();
        await Task.Delay(300);
        Assert.Equal(0, harness.Service.RetentionSweeps);
        Assert.True(Directory.Exists(gone.Directory));

        // The workspace opens: one sweep, which deletes the index of the folder that is gone.
        harness.Service.SetWorkspace(new Pillion.Contracts.WorkspaceDescription { FolderPath = _workspace.Repository });
        await harness.WaitForIdleAsync();
        await WaitUntilAsync(() => harness.Service.RetentionSweeps >= 1, "the first retention sweep");
        var open = harness.Root.Store.Identity.Id;
        var first = harness.Service.LastRetentionReport!;
        Assert.False(Directory.Exists(gone.Directory));
        Assert.Equal(RetentionDecision.DeleteRootMissing, Item(first, gone.Id).Decision);
        Assert.True(Item(first, gone.Id).Deleted);
        Assert.Equal(RetentionDecision.KeepInUse, Item(first, open).Decision);
        Assert.All(new[] { olderModel, later, stale }, f => Assert.True(Directory.Exists(f.Directory)));
        Assert.Contains(logs.Messages, m => m.Contains("Index retention deleted index " + gone.Id, StringComparison.Ordinal) && m.Contains("no longer exists", StringComparison.Ordinal));
        Assert.Contains(logs.Messages, m => m.StartsWith("Index retention swept", StringComparison.Ordinal));

        // Not again within the day, whatever wakes the pipeline.
        Directory.Delete(Folder("later"), recursive: true);
        harness.Service.RequestRun();
        await harness.WaitForAsync(s => s.Phase == Pillion.Contracts.IndexPhase.Idle && harness.Service.FullRuns >= 2, what: "another run");
        clock.Advance(TimeSpan.FromHours(23));
        await Task.Delay(300);
        Assert.Equal(1, harness.Service.RetentionSweeps);
        Assert.True(Directory.Exists(later.Directory));

        // A day after the first sweep: the next one.
        clock.Advance(TimeSpan.FromHours(1));
        await WaitUntilAsync(() => !Directory.Exists(later.Directory), "the daily sweep to delete the index of the folder deleted since");
        await WaitUntilAsync(() => harness.Service.LastRetentionReport?.Items.Any(i => i.Usage.Id == later.Id && i.Deleted) == true, "the daily sweep's report");
        Assert.Equal(2, harness.Service.RetentionSweeps);
        Assert.Contains(logs.Messages, m => m.Contains("Index retention deleted index " + later.Id, StringComparison.Ordinal));

        // A month on: the index nobody opened goes; the open root's index (in use) and its older model's index (an open root) stay.
        clock.Advance(TimeSpan.FromDays(31));
        await WaitUntilAsync(() => harness.Service.LastRetentionReport?.Items.Any(i => i.Usage.Id == stale.Id && i.Deleted) == true, "the sweep to delete the unused index");
        var month = harness.Service.LastRetentionReport!;
        Assert.Equal(RetentionDecision.DeleteUnused, Item(month, stale.Id).Decision);
        Assert.Equal(RetentionDecision.KeepInUse, Item(month, open).Decision);
        Assert.Equal(RetentionDecision.KeepProtected, Item(month, olderModel.Id).Decision);
        Assert.False(Item(month, olderModel.Id).Deleted);
        Assert.False(Directory.Exists(stale.Directory));
        Assert.True(Directory.Exists(olderModel.Directory));
        Assert.True(Directory.Exists(harness.Root.Store.Folder.Directory));
        Assert.Contains(logs.Messages, m => m.Contains("Index retention deleted index " + stale.Id, StringComparison.Ordinal) && m.Contains("not opened for more than 30 days", StringComparison.Ordinal));
        Assert.DoesNotContain(logs.Messages, m => m.Contains("Index retention deleted index " + olderModel.Id, StringComparison.Ordinal) || m.Contains("Index retention deleted index " + open, StringComparison.Ordinal));
    }

    [Fact]
    public async Task WithTheIntervalOff_NothingIsSwept()
    {
        var gone = await CreateIndexAsync(new Pillion.Common.PillionPaths(_workspace.Data), Folder("gone"));
        Directory.Delete(Folder("gone"), recursive: true);
        Fixtures.Copy("shop", _workspace.Repository);
        await using var harness = new IndexingHarness(_workspace.Data, new FakeEmbedder(), new IndexingOptions { RetentionSweepInterval = TimeSpan.Zero, WatchFileSystem = false });
        await harness.StartAsync(_workspace.Repository);
        await harness.WaitForIdleAsync();

        Assert.Equal(0, harness.Service.RetentionSweeps);
        Assert.True(Directory.Exists(gone.Directory));
    }

    private static async Task<IndexFolder> CreateIndexAsync(Pillion.Common.PillionPaths paths, string root, string model = "fake/hashed-words")
    {
        var factory = new IndexStoreFactory(paths, new TestIndex.AllOn());
        await using var store = await factory.OpenAsync(new IndexRequest(root, model, ContextEngineVersions.ChunkerVersion, "pillion-earlier-session"));
        return store.Folder;
    }

    private static IndexRetentionItem Item(IndexRetentionReport report, string id) => Assert.Single(report.Items, i => i.Usage.Id == id);

    private static async Task WaitUntilAsync(Func<bool> condition, string what)
    {
        var clock = Stopwatch.StartNew();
        while (!condition())
        {
            Assert.True(clock.Elapsed < Wait, "Timed out waiting for " + what + ".");
            await Task.Delay(20);
        }
    }

    private string Folder(string name)
    {
        var path = Path.Combine(_parent, name);
        Directory.CreateDirectory(path);
        return path;
    }

    /// <summary>Keeps every formatted log message.</summary>
    private sealed class ListLoggerFactory : ILoggerFactory
    {
        private readonly ConcurrentQueue<string> _messages = new();

        public IReadOnlyList<string> Messages => [.. _messages];

        public void AddProvider(ILoggerProvider provider)
        {
        }

        public ILogger CreateLogger(string categoryName) => new ListLogger(_messages);

        public void Dispose()
        {
        }

        private sealed class ListLogger(ConcurrentQueue<string> messages) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state)
                where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
                messages.Enqueue(formatter(state, exception));
        }
    }
}

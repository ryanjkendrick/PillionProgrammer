using System.Collections.Concurrent;
using Pillion.Common.Settings;
using Pillion.Contracts;
using Pillion.Host.Indexing;
using IndexRole = Pillion.Contracts.IndexRole;

namespace Pillion.Host.Tests.Indexing;

/// <summary>
/// The resource rules of the indexing pipeline (design section 13): the bounded below-normal scheduler, the pauses for IDE
/// activity, battery saver, the memory cap (with the batch halving first) and the user, the status notifications, and the
/// one-writer rule (a reader host does not index; it takes over when the owner dies).
/// </summary>
public sealed class IndexingResourceTests : IDisposable
{
    private readonly IndexingWorkspace _workspace = new();

    public void Dispose() => _workspace.Dispose();

    [Fact]
    public async Task ThePipeline_RunsOnAtMostHalfTheCores_AtBelowNormalPriority()
    {
        GenerateFiles(12);
        var priorities = new ConcurrentBag<ThreadPriority>();
        var threads = new ConcurrentDictionary<int, byte>();
        var options = new IndexingOptions
        {
            OnFilePrepared = _ =>
            {
                priorities.Add(Thread.CurrentThread.Priority);
                threads[Environment.CurrentManagedThreadId] = 0;
            },
        };
        await using var harness = new IndexingHarness(_workspace.Data, options: options);
        await harness.StartAsync(_workspace.Repository);
        await harness.WaitForIdleAsync();

        var half = Math.Max(1, Environment.ProcessorCount / 2);
        Assert.Equal(half, harness.Service.Scheduler.MaximumConcurrencyLevel);
        Assert.NotEmpty(priorities);
        Assert.All(priorities, p => Assert.Equal(ThreadPriority.BelowNormal, p));
        Assert.InRange(threads.Count, 1, half);
    }

    [Theory]
    [InlineData(IdeActivity.Build, IndexPauseReason.Build)]
    [InlineData(IdeActivity.Test, IndexPauseReason.Test)]
    [InlineData(IdeActivity.Debug, IndexPauseReason.Debug)]
    public async Task IdeActivity_PausesIndexing_UntilItEnds(IdeActivity activity, IndexPauseReason reason)
    {
        Fixtures.Copy("shop", _workspace.Repository);
        await using var harness = new IndexingHarness(_workspace.Data);
        harness.Activity.Set(activity);
        await harness.StartAsync(_workspace.Repository);

        var paused = await harness.WaitForAsync(s => s.Phase == IndexPhase.Paused && s.Roots.Length == 1, what: "the pause");
        Assert.Equal(reason, paused.PauseReason);
        Assert.True(paused.IsPaused);
        await Task.Delay(300);
        Assert.Equal(0, ((FakeEmbedder)harness.Embedder).ChunksEmbedded);
        Assert.Equal(0, harness.Service.GetStatus().Roots[0].IndexedFileCount);

        harness.Activity.Set(IdeActivity.None);
        var done = await harness.WaitForIdleAsync();
        Assert.Equal(8, done.Roots[0].IndexedFileCount);
        Assert.Equal(IndexPauseReason.None, done.PauseReason);
    }

    [Fact]
    public async Task BatterySaver_PausesIndexing_OnlyWhenPauseOnBatteryIsSet()
    {
        Fixtures.Copy("shop", _workspace.Repository);
        await using var harness = new IndexingHarness(_workspace.Data);
        harness.Power.BatterySaver = true;
        await harness.StartAsync(_workspace.Repository);

        var paused = await harness.WaitForAsync(s => s.Phase == IndexPhase.Paused && s.Roots.Length == 1, what: "the battery pause");
        Assert.Equal(IndexPauseReason.Battery, paused.PauseReason);

        // indexing.pauseOnBattery off: battery saver no longer pauses.
        harness.Settings.Update(new PillionSettings { Indexing = new IndexingSettings { PauseOnBattery = false } });
        await harness.WaitForIdleAsync();
    }

    [Fact]
    public async Task TheUser_PausesAndResumes()
    {
        GenerateFiles(30);
        var embedder = new FakeEmbedder { Delay = TimeSpan.FromMilliseconds(100) };
        await using var harness = new IndexingHarness(_workspace.Data, embedder);
        await harness.StartAsync(_workspace.Repository);
        await harness.WaitForAsync(s => s.Phase == IndexPhase.Embedding, what: "embedding to start");

        harness.Service.SetUserPause(true);
        var paused = await harness.WaitForAsync(s => s.Phase == IndexPhase.Paused, what: "the user pause");
        Assert.Equal(IndexPauseReason.User, paused.PauseReason);
        Assert.False(paused.IsIndexing);
        Assert.NotNull(paused.Progress);
        await Task.Delay(200);
        var embedded = embedder.ChunksEmbedded;
        await Task.Delay(300);
        Assert.Equal(embedded, embedder.ChunksEmbedded);

        harness.Service.SetUserPause(false);
        var done = await harness.WaitForIdleAsync();
        Assert.Equal(30, done.Roots[0].IndexedFileCount);
    }

    [Fact]
    public async Task OverTheMemoryCap_TheBatchIsHalved_ThenIndexingPauses_AndResumesWhenMemoryIsBack()
    {
        GenerateFiles(40);
        var embedder = new FakeEmbedder();
        var settings = new PillionSettings { Indexing = new IndexingSettings { MaxMemoryMb = 1000 } };
        await using var harness = new IndexingHarness(_workspace.Data, embedder, settings: settings);
        harness.Memory.Bytes = 1100L * 1024 * 1024; // over the cap from the start
        await harness.StartAsync(_workspace.Repository);

        var paused = await harness.WaitForAsync(s => s.Phase == IndexPhase.Paused, what: "the memory pause");
        Assert.Equal(IndexPauseReason.Memory, paused.PauseReason);
        Assert.Equal(4, harness.Service.Memory.BatchSize);
        Assert.True(embedder.MemoryReleases >= 2, $"{embedder.MemoryReleases} releases"); // the first step, and again on pausing

        // Halved 32 -> 16 -> 8 -> 4 before pausing; the batches sent so far are no larger than the halved sizes.
        Assert.All(embedder.BatchSizes, size => Assert.True(size <= 16, $"batch of {size}"));
        var embedded = embedder.ChunksEmbedded;
        await Task.Delay(300);
        Assert.Equal(embedded, embedder.ChunksEmbedded);

        harness.Memory.Bytes = 200L * 1024 * 1024; // well under: resume, and the batch grows back
        var done = await harness.WaitForIdleAsync();
        Assert.Equal(40, done.Roots[0].IndexedFileCount);
        Assert.Contains(32, embedder.BatchSizes);
    }

    [Fact]
    public async Task StatusNotifications_ArriveWithThePhasesInOrder_AtMostTwiceASecond()
    {
        GenerateFiles(60);
        var embedder = new FakeEmbedder { Delay = TimeSpan.FromMilliseconds(80) };
        await using var harness = new IndexingHarness(_workspace.Data, embedder);
        await harness.StartAsync(_workspace.Repository);
        await harness.WaitForIdleAsync();
        await Task.Delay(700); // the last window's notification

        var sent = harness.Sender.Sent;
        Assert.True(sent.Count >= 3, $"{sent.Count} notifications");
        for (var i = 1; i < sent.Count; i++)
        {
            var gap = sent[i].At - sent[i - 1].At;
            Assert.True(gap >= TimeSpan.FromMilliseconds(450), $"notifications {i - 1} and {i} were {gap.TotalMilliseconds:0} ms apart");
        }

        // Within the run the phase only moves forward; the last notification is Idle.
        var phases = sent.Select(s => s.Status.Phase).ToList();
        var run = phases.Where(p => p is not IndexPhase.Idle and not IndexPhase.Paused).ToList();
        Assert.NotEmpty(run);
        Assert.Equal(run.OrderBy(p => (int)p).ToList(), run);
        Assert.Contains(IndexPhase.Embedding, run);
        Assert.Equal(IndexPhase.Idle, phases[^1]);
        Assert.Contains(sent, s => s.Status.Progress is { FilesTotal: 60 } p && p.FilesDone < 60);
        Assert.Contains(sent, s => s.Status.Progress is { EstimatedRemainingSeconds: >= 0 });
    }

    [Fact]
    public async Task AReaderHost_DoesNotIndex_AndTakesOverWhenTheWriterGoesAway()
    {
        Fixtures.Copy("shop", _workspace.Repository);
        var writerEmbedder = new FakeEmbedder();
        var readerEmbedder = new FakeEmbedder();
        var writer = new IndexingHarness(_workspace.Data, writerEmbedder, pipeName: "pillion-writer");
        await writer.StartAsync(_workspace.Repository);
        await writer.WaitForIdleAsync();

        await using var reader = new IndexingHarness(_workspace.Data, readerEmbedder, pipeName: "pillion-reader");
        await reader.StartAsync(_workspace.Repository);
        var asReader = await reader.WaitForAsync(s => s.Roots.Length == 1, what: "the reader to open the index");
        Assert.Equal(IndexRole.Reader, asReader.Roots[0].Role);
        Assert.Equal(8, asReader.Roots[0].IndexedFileCount);
        await Task.Delay(400);
        Assert.Null(reader.Service.LastRunStatistics);
        Assert.Equal(0, readerEmbedder.ChunksEmbedded);

        // A reader can search (read-only) but not rebuild.
        Assert.NotEmpty(await reader.Root.Storage.Chunks.SearchLexicalAsync("PaymentGateway", 3));
        var denied = Assert.Throws<PillionRpcException>(() => reader.Service.Rebuild(_workspace.Repository));
        Assert.Equal(PillionErrorCode.PolicyDenied, denied.Error.Code);

        // The owner closes: the reader reopens the index as its writer and sweeps (nothing changed, nothing re-embedded).
        await writer.DisposeAsync();
        var asWriter = await reader.WaitForAsync(s => s.Roots.Length == 1 && s.Roots[0].Role == IndexRole.Writer && s.Phase == IndexPhase.Idle && reader.Service.LastRunStatistics is not null, what: "the takeover");
        Assert.Equal(8, asWriter.Roots[0].IndexedFileCount);
        Assert.Equal(0, readerEmbedder.ChunksEmbedded);
    }

    [Fact]
    public async Task ThePauseGate_ReportsTheUsersReasonFirst_AndReleasesWaitersWhenTheLastReasonGoes()
    {
        var gate = new PauseGate();
        Assert.True(gate.WaitWhilePausedAsync(CancellationToken.None).IsCompleted);

        gate.Set(IndexPauseReason.Battery, true);
        gate.Set(IndexPauseReason.User, true);
        Assert.Equal(IndexPauseReason.User, gate.Current);
        var waiting = gate.WaitWhilePausedAsync(CancellationToken.None);

        gate.Set(IndexPauseReason.User, false);
        Assert.Equal(IndexPauseReason.Battery, gate.Current);
        Assert.False(waiting.IsCompleted);

        gate.Set(IndexPauseReason.Battery, false);
        await waiting.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(IndexPauseReason.None, gate.Current);
    }

    private void GenerateFiles(int count)
    {
        for (var i = 0; i < count; i++)
        {
            var path = _workspace.File($"src/Module{i % 4}/Service{i}.cs");
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var body = string.Concat(Enumerable.Range(0, 6).Select(m =>
                $"    /// <summary>Step {m} of service {i}.</summary>\n    public int Step{m}(int value)\n    {{\n        var total = value + {m};\n        return total * {i + 1};\n    }}\n\n"));
            File.WriteAllText(path, $"namespace Generated.Module{i % 4};\n\npublic sealed class Service{i}\n{{\n{body}}}\n");
        }
    }
}

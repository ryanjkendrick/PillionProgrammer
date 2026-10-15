using Pillion.Common.Settings;
using Pillion.Indexing.Ownership;
using Pillion.Indexing.Retention;

namespace Pillion.Indexing.Tests;

public sealed class RetentionTests : IDisposable
{
    private readonly TempDirectory _temp = new();
    private readonly FakeFeatureFlags _features = new();
    private readonly FakeProcessTable _processes = new();

    public RetentionTests()
    {
        Directory.CreateDirectory(Repo("one"));
    }

    private string Repo(string name) => _temp.File("repos/" + name);

    public void Dispose() => _temp.Dispose();

    private async Task<IndexFolder> CreateIndexAsync(string repo, string model = "bge-small-en-v1.5-int8", int processId = 100)
    {
        Directory.CreateDirectory(Repo(repo));
        var factory = new IndexStoreFactory(_temp.NewPaths(), _features, probe: _processes.HostProbe(processId));
        await using var store = await factory.OpenAsync(new IndexRequest(Repo(repo), model, "1", $"pipe-{processId}"));
        await store.Files.UpsertAsync([IndexDatabases.Record("src/file.cs")]);
        return store.Folder;
    }

    private IndexRetention Retention(double daysFromNow = 0, int retentionDays = 30, bool withFeatures = true) =>
        new(
            _temp.NewPaths(),
            withFeatures ? _features : null,
            new IndexRetentionPolicy(retentionDays),
            _processes.HostProbe(900),
            new TestTime(DateTimeOffset.UtcNow.AddDays(daysFromNow)));

    [Fact]
    public async Task GetUsage_ListsEachIndexWithItsRootDiskUseAndLastUse()
    {
        var folder = await CreateIndexAsync("one");

        var usage = Assert.Single(Retention().GetUsage());

        Assert.Equal(folder.Id, usage.Id);
        Assert.Equal(folder.Directory, usage.Directory);
        Assert.Equal(Repo("one"), usage.RootPath);
        Assert.Equal("bge-small-en-v1.5-int8", usage.EmbeddingModel);
        Assert.Equal("1", usage.ChunkerVersion);
        Assert.True(usage.RootExists);
        Assert.False(usage.InUse);
        Assert.True(usage.DiskBytes > 0);
        Assert.Equal(folder.GetDiskUsageBytes(), usage.DiskBytes);
        Assert.True(DateTimeOffset.UtcNow - usage.LastUsedUtc < TimeSpan.FromMinutes(5));
    }

    [Fact]
    public async Task DiskUse_CountsEveryFileOfTheFolder_IncludingMigrationBackups()
    {
        var folder = await CreateIndexAsync("one");
        var before = folder.GetDiskUsageBytes();

        File.WriteAllBytes(Path.Combine(folder.Directory, "index.db.v1.bak"), new byte[10_000]);

        Assert.Equal(before + 10_000, folder.GetDiskUsageBytes());
    }

    [Fact]
    public async Task GetUsage_IsEmptyWithoutAnIndexFolder_AndOrdersByIdOtherwise()
    {
        Assert.Empty(Retention().GetUsage());

        await CreateIndexAsync("one");
        await CreateIndexAsync("one", model: "another-model");
        await CreateIndexAsync("two");

        var ids = Retention().GetUsage().Select(u => u.Id).ToList();
        Assert.Equal(3, ids.Count);
        Assert.Equal(ids.Order(StringComparer.Ordinal), ids);
    }

    [Fact]
    public async Task AnIndexNotOpenedForMoreThanTheRetentionDays_IsDeleted_AndOneJustInsideIsKept()
    {
        var folder = await CreateIndexAsync("one");

        var inside = await Retention(daysFromNow: 29).SweepAsync(dryRun: false);
        Assert.Equal(RetentionDecision.Keep, Assert.Single(inside.Items).Decision);
        Assert.True(Directory.Exists(folder.Directory));

        var outside = await Retention(daysFromNow: 31).SweepAsync(dryRun: false);
        var item = Assert.Single(outside.Items);
        Assert.Equal(RetentionDecision.DeleteUnused, item.Decision);
        Assert.True(item.Deleted);
        Assert.True(item.WantsDelete);
        Assert.Null(item.Error);
        Assert.False(Directory.Exists(folder.Directory));
        Assert.True(outside.BytesReclaimed > 0);
        Assert.False(outside.Skipped);
        Assert.False(outside.DryRun);
    }

    [Fact]
    public async Task TheRetentionDaysSetting_DecidesHowLongAnIndexIsKept()
    {
        var folder = await CreateIndexAsync("one");

        var sevenDays = await Retention(daysFromNow: 8, retentionDays: 7).SweepAsync(dryRun: true);
        var ninetyDays = await Retention(daysFromNow: 8, retentionDays: 90).SweepAsync(dryRun: true);
        var overridden = await Retention(daysFromNow: 8, retentionDays: 90).SweepAsync(dryRun: true, retentionDays: 5);

        Assert.Equal(RetentionDecision.DeleteUnused, Assert.Single(sevenDays.Items).Decision);
        Assert.Equal(RetentionDecision.Keep, Assert.Single(ninetyDays.Items).Decision);
        Assert.Equal(RetentionDecision.DeleteUnused, Assert.Single(overridden.Items).Decision);
        Assert.True(Directory.Exists(folder.Directory));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public async Task ZeroOrLessTurnsTheAgeRuleOff_ButNotTheMissingRootRule(int retentionDays)
    {
        var kept = await CreateIndexAsync("one");
        var orphan = await CreateIndexAsync("two");
        Directory.Delete(Repo("two"), recursive: true);

        var report = await Retention(daysFromNow: 1000, retentionDays: retentionDays).SweepAsync();

        Assert.Equal(RetentionDecision.Keep, report.Items.Single(i => i.Usage.Id == kept.Id).Decision);
        Assert.Equal(RetentionDecision.DeleteRootMissing, report.Items.Single(i => i.Usage.Id == orphan.Id).Decision);
        Assert.True(Directory.Exists(kept.Directory));
        Assert.False(Directory.Exists(orphan.Directory));
    }

    [Fact]
    public async Task AnIndexWhoseRepositoryFolderIsGone_IsDeletedAtOnce_EvenWhenUsedToday()
    {
        var folder = await CreateIndexAsync("one");
        Directory.Delete(Repo("one"), recursive: true);

        var report = await Retention().SweepAsync();

        var item = Assert.Single(report.Items);
        Assert.Equal(RetentionDecision.DeleteRootMissing, item.Decision);
        Assert.False(item.Usage.RootExists);
        Assert.True(item.Deleted);
        Assert.False(Directory.Exists(folder.Directory));
    }

    [Fact]
    public async Task ARootOnADriveThatIsNotThere_IsNotMissing_ItIsUnknown()
    {
        var missingDrive = Enumerable.Range('A', 26).Select(c => $"{(char)c}:\\").FirstOrDefault(d => !Directory.Exists(d));
        if (missingDrive is null)
        {
            return;
        }

        var folder = await CreateIndexAsync("one");
        var state = IndexStateFile.Read(folder.StateFile)!;
        IndexStateFile.Write(folder.StateFile, state with { RootPath = missingDrive + @"projects\offline" });

        var report = await Retention().SweepAsync(dryRun: true);

        var item = Assert.Single(report.Items);
        Assert.Null(item.Usage.RootExists);
        Assert.Equal(RetentionDecision.Keep, item.Decision);

        // It is still removed once it has gone unused for long enough.
        var later = await Retention(daysFromNow: 40).SweepAsync(dryRun: true);
        Assert.Equal(RetentionDecision.DeleteUnused, Assert.Single(later.Items).Decision);
    }

    [Fact]
    public async Task AnIndexWithALiveOwner_IsNeverDeleted()
    {
        var factory = new IndexStoreFactory(_temp.NewPaths(), _features, probe: _processes.HostProbe(100));
        await using var store = await factory.OpenAsync(new IndexRequest(Repo("one"), "bge-small-en-v1.5-int8", "1", "pipe-100"));
        Directory.Delete(Repo("one"), recursive: true);

        var report = await Retention(daysFromNow: 1000).SweepAsync();

        var item = Assert.Single(report.Items);
        Assert.Equal(RetentionDecision.KeepInUse, item.Decision);
        Assert.True(item.Usage.InUse);
        Assert.False(item.WantsDelete);
        Assert.True(Directory.Exists(store.Folder.Directory));

        // Once its host is gone the same index is fair game.
        _processes.Kill(100);
        var after = await Retention(daysFromNow: 1000).SweepAsync(dryRun: true);
        Assert.Equal(RetentionDecision.DeleteRootMissing, Assert.Single(after.Items).Decision);
    }

    [Fact]
    public async Task AnIndexTheCallerProtects_IsKept_WhateverItsAgeOrRoot()
    {
        var unused = await CreateIndexAsync("one");
        var missing = await CreateIndexAsync("two");
        var other = await CreateIndexAsync("three");
        Directory.Delete(Repo("two"), recursive: true);

        var report = await Retention(daysFromNow: 1000).SweepAsync(protect: u => u.Id == unused.Id || u.Id == missing.Id);

        Assert.Equal(RetentionDecision.KeepProtected, report.Items.Single(i => i.Usage.Id == unused.Id).Decision);
        Assert.Equal(RetentionDecision.KeepProtected, report.Items.Single(i => i.Usage.Id == missing.Id).Decision);
        Assert.Equal(RetentionDecision.DeleteUnused, report.Items.Single(i => i.Usage.Id == other.Id).Decision);
        Assert.True(Directory.Exists(unused.Directory));
        Assert.True(Directory.Exists(missing.Directory));
        Assert.False(Directory.Exists(other.Directory));
    }

    [Fact]
    public async Task ADryRun_ReportsWhatWouldGo_AndDeletesNothing()
    {
        var old = await CreateIndexAsync("one");
        var fresh = await CreateIndexAsync("two");
        Directory.Delete(Repo("two"), recursive: true);

        var report = await Retention(daysFromNow: 31).SweepAsync(dryRun: true);

        Assert.True(report.DryRun);
        Assert.Equal(2, report.Items.Count);
        Assert.All(report.Items, i => Assert.True(i.WantsDelete));
        Assert.All(report.Items, i => Assert.False(i.Deleted));
        Assert.Equal(old.GetDiskUsageBytes() + fresh.GetDiskUsageBytes(), report.BytesReclaimed);
        Assert.True(Directory.Exists(old.Directory));
        Assert.True(Directory.Exists(fresh.Directory));
    }

    [Fact]
    public async Task OnlyFoldersNamedLikeAnIndexId_AreTouched()
    {
        var folder = await CreateIndexAsync("one");
        var indexRoot = Path.GetDirectoryName(folder.Directory)!;
        Directory.CreateDirectory(Path.Combine(indexRoot, "not-an-index"));
        File.WriteAllText(Path.Combine(indexRoot, "not-an-index", "keep.txt"), "mine");
        Directory.CreateDirectory(Path.Combine(indexRoot, new string('A', 32)));
        File.WriteAllText(Path.Combine(indexRoot, "stray-file.txt"), "mine");

        var report = await Retention(daysFromNow: 1000).SweepAsync();

        Assert.Equal([folder.Id], report.Items.Select(i => i.Usage.Id));
        Assert.True(File.Exists(Path.Combine(indexRoot, "not-an-index", "keep.txt")));
        Assert.True(Directory.Exists(Path.Combine(indexRoot, new string('A', 32))));
        Assert.True(File.Exists(Path.Combine(indexRoot, "stray-file.txt")));
        Assert.False(Directory.Exists(folder.Directory));
    }

    [Fact]
    public async Task AFolderWithoutAReadableState_IsJudgedByItsFileTimes_AndItsRootIsUnknown()
    {
        var folder = await CreateIndexAsync("one");
        File.WriteAllText(folder.StateFile, "garbage");
        Directory.Delete(Repo("one"), recursive: true);

        var now = await Retention().SweepAsync(dryRun: true);
        var item = Assert.Single(now.Items);
        Assert.Null(item.Usage.RootPath);
        Assert.Null(item.Usage.RootExists);
        Assert.Equal(RetentionDecision.Keep, item.Decision);

        var later = await Retention(daysFromNow: 31).SweepAsync(dryRun: true);
        Assert.Equal(RetentionDecision.DeleteUnused, Assert.Single(later.Items).Decision);
    }

    [Fact]
    public async Task WithIndexingOff_TheSweepDoesNothing()
    {
        var folder = await CreateIndexAsync("one");
        _features.Set(FeatureNames.Indexing, false);

        var report = await Retention(daysFromNow: 1000).SweepAsync();

        Assert.True(report.Skipped);
        Assert.Empty(report.Items);
        Assert.True(Directory.Exists(folder.Directory));
        Assert.Equal(0, report.BytesReclaimed);
    }

    [Fact]
    public void ThePolicyDefaultsToThirtyDays()
    {
        Assert.Equal(30, new IndexRetentionPolicy().RetentionDays);
    }
}

using Pillion.Indexing.Discovery;
using Pillion.Indexing.Hashing;
using Pillion.Indexing.Storage;
using Pillion.Indexing.Sweep;
using Pillion.LocalStore;

namespace Pillion.Indexing.Tests;

public sealed class SweepTests : IAsyncLifetime, IDisposable
{
    private readonly TempDirectory _temp = new();
    private readonly TestTime _time = new();
    private EncryptedDatabase _database = null!;
    private FilesRepository _files = null!;

    private string Root => _temp.File("repo");

    public async Task InitializeAsync()
    {
        Directory.CreateDirectory(Root);
        _database = await IndexDatabases.OpenAsync(_temp);
        _files = new FilesRepository(_database, _time);
    }

    public async Task DisposeAsync() => await _database.DisposeAsync();

    public void Dispose() => _temp.Dispose();

    private string Write(string relative, string content = "content") => _temp.Write("repo/" + relative, content);

    private Task<SweepResult> SweepAsync(ScanOptions? options = null, PartitionResolver? partitions = null) =>
        SweepFromStoreAsync(options, partitions);

    private async Task<SweepResult> SweepFromStoreAsync(ScanOptions? options, PartitionResolver? partitions)
    {
        var stored = await _files.GetAllAsync();
        return await new RepositorySweeper(options ?? new ScanOptions { MaxDegreeOfParallelism = 2 }).SweepAsync(Root, stored, partitions);
    }

    /// <summary>Stores the sweep as the pipeline would, with a clock far enough ahead that stored rows are not racy.</summary>
    private async Task CommitAsync(SweepResult sweep)
    {
        _time.Now = DateTimeOffset.UtcNow.AddHours(1);
        await _files.CommitAsync(sweep);
    }

    private static string[] Paths(IEnumerable<FileRecord> records) => [.. records.Select(r => r.Path)];

    [Fact]
    public async Task AFirstSweep_AddsEveryIndexedFile_WithHashSizeAndTime()
    {
        Write("src/a.cs", "class A {}");
        Write("src/b.ts", "export {}");
        Write("README.md", "# readme");
        Write("bin/skipped.dll", "x");

        var sweep = await SweepAsync();

        Assert.Equal(["README.md", "src/a.cs", "src/b.ts"], Paths(sweep.Added));
        Assert.Empty(sweep.Changed);
        Assert.Empty(sweep.Touched);
        Assert.Empty(sweep.Unchanged);
        Assert.Empty(sweep.Deleted);
        Assert.Equal(3, sweep.FilesHashed);
        Assert.True(sweep.HasChanges);
        var a = sweep.Added.Single(r => r.Path == "src/a.cs");
        Assert.Equal(FileHasher.Hash("class A {}"u8), a.Hash);
        Assert.Equal(10, a.Size);
        Assert.Equal("csharp", a.Language);
        Assert.Equal("src", a.Partition);
        Assert.Equal(0, a.Id);
        Assert.Contains(sweep.Excluded, e => e.RelativePath == "bin" && e.IsDirectory && e.Decision.Reason == ExclusionReason.BuiltIn);
    }

    [Fact]
    public async Task ASecondSweepOfAnUntouchedTree_IsAllUnchanged_AndReadsNoFile()
    {
        var paths = new[] { Write("a.cs"), Write("src/b.cs"), Write("src/deep/c.cs") };
        await CommitAsync(await SweepAsync());

        // Locking every file makes any attempt to open it fail: an unchanged file must not be opened at all.
        var locks = paths.Select(p => new FileStream(p, FileMode.Open, FileAccess.Read, FileShare.None)).ToList();
        try
        {
            var sweep = await SweepAsync();

            Assert.Equal(["a.cs", "src/b.cs", "src/deep/c.cs"], Paths(sweep.Unchanged));
            Assert.Empty(sweep.Added);
            Assert.Empty(sweep.Changed);
            Assert.Empty(sweep.Touched);
            Assert.Empty(sweep.Deleted);
            Assert.Empty(sweep.Unreadable);
            Assert.Equal(0, sweep.FilesHashed);
            Assert.False(sweep.HasChanges);
        }
        finally
        {
            locks.ForEach(l => l.Dispose());
        }
    }

    [Fact]
    public async Task AFileWhoseContentChanged_IsReportedChanged_KeepsItsId_AndOthersStayUnchanged()
    {
        var changing = Write("src/a.cs", "version one");
        Write("src/b.cs", "stable");
        await CommitAsync(await SweepAsync());
        var before = (await _files.GetAsync("src/a.cs"))!;

        // Same length, different bytes, and a later modified time.
        File.WriteAllText(changing, "version two");
        File.SetLastWriteTimeUtc(changing, File.GetLastWriteTimeUtc(changing).AddSeconds(30));
        var sweep = await SweepAsync();

        var changed = Assert.Single(sweep.Changed);
        Assert.Equal("src/a.cs", changed.Path);
        Assert.Equal(before.Id, changed.Id);
        Assert.NotEqual(before.Hash, changed.Hash);
        Assert.Equal(FileHasher.Hash("version two"u8), changed.Hash);
        Assert.Equal(["src/b.cs"], Paths(sweep.Unchanged));
        Assert.Empty(sweep.Added);
        Assert.Empty(sweep.Touched);
        Assert.Equal(1, sweep.FilesHashed);
    }

    [Fact]
    public async Task ATouchedFileWithTheSameContent_IsTouchedNotChanged()
    {
        var path = Write("a.cs", "same bytes");
        await CommitAsync(await SweepAsync());

        File.SetLastWriteTimeUtc(path, File.GetLastWriteTimeUtc(path).AddMinutes(10));
        var sweep = await SweepAsync();

        var touched = Assert.Single(sweep.Touched);
        Assert.Equal("a.cs", touched.Path);
        Assert.Empty(sweep.Changed);
        Assert.Empty(sweep.Unchanged);
        Assert.Equal(new DateTimeOffset(File.GetLastWriteTimeUtc(path), TimeSpan.Zero).ToUnixTimeMilliseconds(), touched.ModifiedUnixMilliseconds);

        // Once the refreshed metadata is stored, the file is unchanged again.
        await CommitAsync(sweep);
        var again = await SweepAsync();
        Assert.Equal(["a.cs"], Paths(again.Unchanged));
        Assert.Empty(again.Touched);
    }

    [Fact]
    public async Task ADeletedFile_AndAFileThatBecameExcluded_AreReportedDeleted()
    {
        Write("keep.cs");
        var removed = Write("removed.cs");
        Write("ignored-later.log", "text");
        Write("minified-later.js", "var a=1");
        Write("grew.txt", "small");
        Write("binary-later.txt", "text now");
        await CommitAsync(await SweepAsync());

        File.Delete(removed);
        Write(".gitignore", "*.log\n");
        File.Move(Path.Combine(Root, "minified-later.js"), Path.Combine(Root, "minified-later.min.js"));
        var grew = Path.Combine(Root, "grew.txt");
        File.WriteAllBytes(grew, new byte[ScanOptions.DefaultMaxFileBytes + 1]);
        File.WriteAllBytes(Path.Combine(Root, "binary-later.txt"), [1, 2, 0, 3]);
        var sweep = await SweepAsync();

        Assert.Equal(
            ["binary-later.txt", "grew.txt", "ignored-later.log", "minified-later.js", "removed.cs"],
            Paths(sweep.Deleted));
        Assert.Contains(sweep.Excluded, e => e.RelativePath == "ignored-later.log" && e.Decision.Reason == ExclusionReason.GitIgnore);
        Assert.Contains(sweep.Excluded, e => e.RelativePath == "grew.txt" && e.Decision.Reason == ExclusionReason.TooLarge);
        Assert.Contains(sweep.Excluded, e => e.RelativePath == "binary-later.txt" && e.Decision.Reason == ExclusionReason.Binary);
        Assert.Contains(sweep.Excluded, e => e.RelativePath == "minified-later.min.js" && e.Decision.Reason == ExclusionReason.MinifiedFile);
        Assert.Contains(sweep.Unchanged, r => r.Path == "keep.cs");
        Assert.True(sweep.HasChanges);
    }

    [Fact]
    public async Task ARenamedFile_IsOneDeletedAndOneAdded()
    {
        Write("old-name.cs", "the same content");
        await CommitAsync(await SweepAsync());

        File.Move(Path.Combine(Root, "old-name.cs"), Path.Combine(Root, "new-name.cs"));
        var sweep = await SweepAsync();

        Assert.Equal(["old-name.cs"], Paths(sweep.Deleted));
        Assert.Equal(["new-name.cs"], Paths(sweep.Added));
    }

    [Fact]
    public async Task SecretFiles_AreListedSeparately_AndNeverHashedOrAdded()
    {
        Write("ok.cs");
        Write(".env", "API_KEY=abc");
        Write("config/appsettings.Production.json", "{}");
        Write("tls/server.pem", "-----BEGIN");

        var sweep = await SweepAsync();

        Assert.Equal(["ok.cs"], Paths(sweep.Added));
        Assert.Equal([".env", "config/appsettings.Production.json", "tls/server.pem"], sweep.SecretFiles.Select(e => e.RelativePath));
        Assert.All(sweep.SecretFiles, e => Assert.Equal(ExclusionReason.SecretBearing, e.Decision.Reason));
        Assert.DoesNotContain(sweep.Excluded, e => e.Decision.IsSecretBearing);
    }

    [Fact]
    public async Task AnAlreadyStoredFile_ThatBecomesSecretBearingByRename_IsDeleted()
    {
        Write("settings.cs", "a");
        await CommitAsync(await SweepAsync());

        File.Move(Path.Combine(Root, "settings.cs"), Path.Combine(Root, "secrets.json"));
        var sweep = await SweepAsync();

        Assert.Equal(["settings.cs"], Paths(sweep.Deleted));
        Assert.Equal(["secrets.json"], sweep.SecretFiles.Select(e => e.RelativePath));
        Assert.Empty(sweep.Added);
    }

    [Fact]
    public async Task AFileThatCannotBeRead_IsReported_AndItsStoredRowIsKept()
    {
        var path = Write("busy.cs", "first");
        Write("other.cs");
        await CommitAsync(await SweepAsync());

        File.WriteAllText(path, "second version");
        File.SetLastWriteTimeUtc(path, File.GetLastWriteTimeUtc(path).AddMinutes(1));
        var newLocked = Write("brand-new.cs");
        using var lock1 = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None);
        using var lock2 = new FileStream(newLocked, FileMode.Open, FileAccess.Read, FileShare.None);
        var sweep = await SweepAsync();

        Assert.Equal(["brand-new.cs", "busy.cs"], sweep.Unreadable.Order(StringComparer.Ordinal));
        Assert.Empty(sweep.Deleted);
        Assert.Empty(sweep.Added);
        Assert.Empty(sweep.Changed);
        Assert.Equal(["other.cs"], Paths(sweep.Unchanged));
    }

    [Fact]
    public async Task ARowThatWasStoredWithinTheRacyWindowOfItsModifiedTime_IsHashedAgain()
    {
        var path = Write("racy.cs", "one");
        var modified = new DateTimeOffset(File.GetLastWriteTimeUtc(path), TimeSpan.Zero);

        // Stored 100 ms after the file was last written: size and time alone cannot be trusted.
        _time.Now = modified.AddMilliseconds(100);
        await _files.CommitAsync(await SweepAsync());
        var racy = await SweepAsync();

        Assert.Empty(racy.Unchanged);
        Assert.Equal(["racy.cs"], Paths(racy.Touched));
        Assert.Equal(1, racy.FilesHashed);

        // Stored well clear of the window, it is trusted.
        _time.Now = modified.AddMilliseconds(RepositorySweeper.RacyWindowMilliseconds + 1);
        await _files.CommitAsync(racy);
        var settled = await SweepAsync();
        Assert.Equal(["racy.cs"], Paths(settled.Unchanged));
        Assert.Equal(0, settled.FilesHashed);
    }

    [Fact]
    public async Task APartitionChange_IsATouch_WithoutRehashingMeaning()
    {
        Write("src/App/Program.cs");
        Write("src/Lib/Thing.cs");
        await CommitAsync(await SweepAsync(partitions: new PartitionResolver(Root)));

        var projects = new PartitionResolver(Root, [new IndexedProject("App", Path.Combine(Root, "src/App"))]);
        var sweep = await SweepAsync(partitions: projects);

        Assert.Equal(["src/App/Program.cs"], Paths(sweep.Touched));
        Assert.Equal("App", sweep.Touched[0].Partition);
        Assert.Equal(["src/Lib/Thing.cs"], Paths(sweep.Unchanged));
        Assert.Empty(sweep.Changed);
        Assert.Equal(0, sweep.FilesHashed);
    }

    [Fact]
    public async Task ABranchSwitch_IsOneSweep_NotARebuild()
    {
        const int Files = 60;
        for (var i = 0; i < Files; i++)
        {
            Write($"src/f{i:D3}.cs", $"class C{i} {{}}");
        }

        await CommitAsync(await SweepAsync());

        // Switching branch rewrites 10 files, deletes 5 and adds 5; the rest are not touched.
        for (var i = 0; i < 10; i++)
        {
            var path = Path.Combine(Root, $"src/f{i:D3}.cs");
            File.WriteAllText(path, $"class D{i} {{}}");
            File.SetLastWriteTimeUtc(path, File.GetLastWriteTimeUtc(path).AddMinutes(5));
        }

        for (var i = 10; i < 15; i++)
        {
            File.Delete(Path.Combine(Root, $"src/f{i:D3}.cs"));
        }

        for (var i = 0; i < 5; i++)
        {
            Write($"src/new{i}.cs");
        }

        var sweep = await SweepAsync();

        Assert.Equal(10, sweep.Changed.Count);
        Assert.Equal(5, sweep.Deleted.Count);
        Assert.Equal(5, sweep.Added.Count);
        Assert.Equal(Files - 15, sweep.Unchanged.Count);
        Assert.Equal(15, sweep.FilesHashed);

        await CommitAsync(sweep);
        Assert.Equal(Files - 5 + 5, await _files.CountAsync());
        var after = await SweepAsync();
        Assert.False(after.HasChanges);
        Assert.Equal(Files, after.Unchanged.Count);
    }

    [Fact]
    public async Task Commit_StoresAddedChangedAndTouchedAndDeletesDeleted_InOneTransaction()
    {
        Write("a.cs", "a");
        Write("b.cs", "b");
        await CommitAsync(await SweepAsync());
        File.Delete(Path.Combine(Root, "a.cs"));
        Write("c.cs", "c");

        var sweep = await SweepAsync();
        var (stored, deleted) = await _files.CommitAsync(sweep);

        Assert.Equal(1, deleted);
        Assert.Equal(["c.cs"], stored.Select(r => r.Path));
        Assert.All(stored, r => Assert.True(r.Id > 0));
        Assert.Equal(["b.cs", "c.cs"], (await _files.GetAllAsync()).Select(r => r.Path));
    }

    [Fact]
    public async Task ASweepOfAMissingRoot_Throws_AndACancelledSweepStops()
    {
        var sweeper = new RepositorySweeper();
        await Assert.ThrowsAsync<DirectoryNotFoundException>(() => sweeper.SweepAsync(_temp.File("nope"), []));

        Write("a.cs");
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => sweeper.SweepAsync(Root, [], cancellationToken: cancellation.Token));
    }

    [Fact]
    public async Task TheSweepHonoursTheRulesOfTheScanner_AndRecordsHowLongItTook()
    {
        Write(".gitignore", "generated/\n");
        Write("generated/x.cs");
        Write("src/real.cs");
        Write("src/Skip.g.cs");

        var sweep = await SweepAsync(new ScanOptions { ExcludePatterns = ["docs/"], MaxDegreeOfParallelism = 1 });

        Assert.Equal([".gitignore", "src/real.cs"], Paths(sweep.Added));
        Assert.True(sweep.Duration >= TimeSpan.Zero);
        Assert.Contains(sweep.Excluded, e => e.RelativePath == "generated" && e.Decision.Reason == ExclusionReason.GitIgnore);
        Assert.Contains(sweep.Excluded, e => e.RelativePath == "src/Skip.g.cs" && e.Decision.Reason == ExclusionReason.GeneratedFile);
    }
}

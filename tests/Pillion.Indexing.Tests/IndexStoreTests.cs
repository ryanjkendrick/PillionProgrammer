using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Pillion.Common.Settings;
using Pillion.Indexing.Ownership;
using Pillion.Indexing.Retention;
using Pillion.Indexing.Storage;
using Pillion.LocalStore;

namespace Pillion.Indexing.Tests;

/// <summary>Opening an index: the first host owns it, the others read it and learn the owner's pipe.</summary>
public sealed class IndexStoreTests : IDisposable
{
    private static readonly IndexStoreOptions QuickReaders = new() { ReaderOpenAttempts = 3, ReaderOpenDelay = TimeSpan.FromMilliseconds(20) };

    private readonly TempDirectory _temp = new();
    private readonly FakeFeatureFlags _features = new();
    private readonly FakeProcessTable _processes = new();

    public IndexStoreTests()
    {
        Directory.CreateDirectory(Repo);
    }

    private string Repo => _temp.File("repo");

    public void Dispose() => _temp.Dispose();

    private IndexStoreFactory Host(int processId, TestTime? time = null, IndexStoreOptions? options = null) =>
        new(_temp.NewPaths(), _features, time: time, probe: _processes.HostProbe(processId), options: options ?? QuickReaders);

    private IndexRequest Request(int processId, string model = "bge-small-en-v1.5-int8", string chunker = "1") =>
        new(Repo, model, chunker, $"pillion-host-{processId}");

    [Fact]
    public async Task TheFirstHost_BecomesTheWriter_AndCreatesTheIndexFolder()
    {
        var time = new TestTime(new DateTimeOffset(2026, 10, 15, 9, 0, 0, TimeSpan.Zero));
        await using var store = await Host(100, time).OpenAsync(Request(100));

        Assert.Equal(IndexRole.Writer, store.Role);
        Assert.Equal(1, store.Database.SchemaVersion);
        Assert.False(store.Database.IsReadOnly);
        Assert.True(store.Writer.IsSelf);
        Assert.Equal("pillion-host-100", store.Writer.PipeName);
        Assert.Equal(100, store.Writer.ProcessId);

        var folder = store.Folder;
        Assert.Equal(Path.Combine(_temp.NewPaths().IndexDirectory, store.Identity.Id), folder.Directory);
        Assert.True(File.Exists(folder.DatabaseFile));
        Assert.True(File.Exists(folder.KeyFile));
        Assert.True(File.Exists(folder.LockFile));
        Assert.True(File.Exists(folder.StateFile));
        Assert.Equal(100, WriterLock.Read(folder.LockFile)!.ProcessId);
        Assert.Equal("pillion-host-100", WriterLock.Read(folder.LockFile)!.PipeName);

        var state = IndexStateFile.Read(folder.StateFile)!;
        Assert.Equal(Repo, state.RootPath);
        Assert.Equal("bge-small-en-v1.5-int8", state.EmbeddingModel);
        Assert.Equal("1", state.ChunkerVersion);
        Assert.Equal(1, state.SchemaVersion);
        Assert.Equal(time.Now, state.CreatedUtc);
        Assert.Equal(time.Now, state.LastOpenedUtc);
        Assert.Null(state.LastFullSweepUtc);
        Assert.Equal(state, store.State);

        Assert.Equal("bge-small-en-v1.5-int8", await MetaAsync(store, IndexMetaKeys.EmbeddingModel));
        Assert.Equal("1", await MetaAsync(store, IndexMetaKeys.ChunkerVersion));
        Assert.Equal(Repo, await MetaAsync(store, IndexMetaKeys.RootPath));
        Assert.Equal("index", await MetaAsync(store, "store"));
    }

    [Fact]
    public async Task ASecondStoreOfTheSameProcess_IsAReader_OfTheSameDatabase()
    {
        var host = Host(100);
        await using var writer = await host.OpenAsync(Request(100));
        await writer.Files.UpsertAsync([IndexDatabases.Record("src/a.cs")]);

        await using var reader = await host.OpenAsync(Request(100));

        Assert.Equal(IndexRole.Reader, reader.Role);
        Assert.True(reader.Database.IsReadOnly);
        Assert.Equal(writer.Identity, reader.Identity);
        Assert.True(reader.Writer.IsSelf);
        Assert.Equal("pillion-host-100", reader.Writer.PipeName);
        Assert.Equal(["src/a.cs"], (await reader.Files.GetAllAsync()).Select(r => r.Path));

        // What the writer writes later is visible to the reader, and the reader cannot write.
        await writer.Files.UpsertAsync([IndexDatabases.Record("src/b.cs")]);
        Assert.Equal(2, await reader.Files.CountAsync());
        await Assert.ThrowsAsync<InvalidOperationException>(() => reader.Files.UpsertAsync([IndexDatabases.Record("src/c.cs")]));
        Assert.Throws<InvalidOperationException>(() => reader.RecordFullSweep());
    }

    [Fact]
    public async Task TwoHosts_ShareOneIndex_WithOneWriter_AndTheReaderLearnsTheWritersPipe()
    {
        await using var first = await Host(100).OpenAsync(Request(100));
        await using var second = await Host(200).OpenAsync(Request(200));

        Assert.Equal(IndexRole.Writer, first.Role);
        Assert.Equal(IndexRole.Reader, second.Role);
        Assert.Equal(first.Folder.Directory, second.Folder.Directory);
        Assert.Equal(new IndexWriterEndpoint("pillion-host-100", 100, FakeProcessTable.DefaultStart, IsSelf: false), second.Writer);
        Assert.Equal(second.Writer, second.GetLiveWriter());

        await first.Files.UpsertAsync([IndexDatabases.Record("a.cs")]);
        Assert.Equal(["a.cs"], (await second.Files.GetAllAsync()).Select(r => r.Path));
    }

    [Fact]
    public async Task WhenTheWriterDies_TheReaderSeesIt_AndAReopenReclaimsTheStaleLock()
    {
        var second = Host(200);
        await using var first = await Host(100).OpenAsync(Request(100));
        await using var reader = await second.OpenAsync(Request(200));

        _processes.Kill(100);

        Assert.Null(reader.GetLiveWriter());

        // The dead host's lock file is still there; the next open takes it over.
        Assert.True(File.Exists(first.Folder.LockFile));
        await using var successor = await second.OpenAsync(Request(200));
        Assert.Equal(IndexRole.Writer, successor.Role);
        Assert.Equal(200, WriterLock.Read(successor.Folder.LockFile)!.ProcessId);
        await successor.Files.UpsertAsync([IndexDatabases.Record("after-takeover.cs")]);
        Assert.Equal(["after-takeover.cs"], (await reader.Files.GetAllAsync()).Select(r => r.Path));
    }

    [Fact]
    public async Task TheOldWriterClosingLate_DoesNotReleaseTheNewWritersLock()
    {
        var oldHost = Host(100);
        var newHost = Host(200);
        var old = await oldHost.OpenAsync(Request(100));
        _processes.Kill(100);
        await using var successor = await newHost.OpenAsync(Request(200));

        await old.DisposeAsync();

        Assert.Equal(200, WriterLock.Read(successor.Folder.LockFile)!.ProcessId);
        Assert.Equal(IndexRole.Writer, successor.Role);
    }

    [Fact]
    public async Task ClosingTheWriter_ReleasesTheLock_RecordsTheOpenTime_AndAKeptStateSurvivesTheNextOpen()
    {
        var time = new TestTime(new DateTimeOffset(2026, 10, 15, 9, 0, 0, TimeSpan.Zero));
        var host = Host(100, time);
        string stateFile;
        await using (var store = await host.OpenAsync(Request(100)))
        {
            stateFile = store.Folder.StateFile;
            time.Now = time.Now.AddHours(2);
            var recorded = store.RecordFullSweep();
            Assert.Equal(time.Now, recorded.LastFullSweepUtc);
            time.Now = time.Now.AddHours(1);
        }

        var closed = IndexStateFile.Read(stateFile)!;
        Assert.Equal(new DateTimeOffset(2026, 10, 15, 12, 0, 0, TimeSpan.Zero), closed.LastOpenedUtc);
        Assert.Equal(new DateTimeOffset(2026, 10, 15, 11, 0, 0, TimeSpan.Zero), closed.LastFullSweepUtc);
        Assert.False(File.Exists(Path.Combine(Path.GetDirectoryName(stateFile)!, "writer.lock")));

        time.Now = time.Now.AddDays(1);
        await using var again = await host.OpenAsync(Request(100));
        Assert.Equal(IndexRole.Writer, again.Role);
        Assert.Equal(new DateTimeOffset(2026, 10, 15, 9, 0, 0, TimeSpan.Zero), again.State.CreatedUtc);
        Assert.Equal(time.Now, again.State.LastOpenedUtc);
        Assert.Equal(closed.LastFullSweepUtc, again.State.LastFullSweepUtc);
    }

    [Fact]
    public async Task DataSurvivesClosingAndReopening()
    {
        var host = Host(100);
        await using (var store = await host.OpenAsync(Request(100)))
        {
            await store.Files.UpsertAsync([IndexDatabases.Record("persisted.cs", hash: 0xFFFF_FFFF_FFFF_FFFFUL)]);
        }

        await using var again = await host.OpenAsync(Request(100));

        var row = Assert.Single(await again.Files.GetAllAsync());
        Assert.Equal("persisted.cs", row.Path);
        Assert.Equal(0xFFFF_FFFF_FFFF_FFFFUL, row.Hash);
    }

    [Fact]
    public async Task AnotherModelOrChunkerVersion_IsAnotherIndexInAnotherFolder_WithItsOwnWriter()
    {
        var host = Host(100);
        await using var a = await host.OpenAsync(Request(100, model: "model-a"));
        await using var b = await host.OpenAsync(Request(100, model: "model-b"));
        await using var c = await host.OpenAsync(Request(100, chunker: "2"));

        Assert.All([a, b, c], s => Assert.Equal(IndexRole.Writer, s.Role));
        Assert.Equal(3, new[] { a.Folder.Directory, b.Folder.Directory, c.Folder.Directory }.Distinct().Count());
        Assert.NotEqual(a.Database.KeyFilePath, b.Database.KeyFilePath);
        Assert.Equal(3, Directory.GetDirectories(_temp.NewPaths().IndexDirectory).Length);
    }

    [Fact]
    public async Task EveryIndexHasItsOwnKey_AndTheDatabaseCannotBeReadWithoutIt()
    {
        var host = Host(100);
        await using var a = await host.OpenAsync(Request(100, model: "model-a"));
        await using var b = await host.OpenAsync(Request(100, model: "model-b"));
        await a.Files.UpsertAsync([IndexDatabases.Record("src/TopSecretProjectName.cs")]);
        await a.Database.VacuumAsync();

        Assert.NotEqual(File.ReadAllBytes(a.Folder.KeyFile), File.ReadAllBytes(b.Folder.KeyFile));
        using var shared = new FileStream(a.Folder.DatabaseFile, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var memory = new MemoryStream();
        await shared.CopyToAsync(memory);
        var bytes = memory.ToArray();
        Assert.DoesNotContain("TopSecretProjectName", System.Text.Encoding.Latin1.GetString(bytes), StringComparison.Ordinal);

        // A plain SQLite (no key) cannot read it.
        using var plain = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = a.Folder.DatabaseFile, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString());
        plain.Open();
        using var command = plain.CreateCommand();
        command.CommandText = "SELECT count(*) FROM files";
        Assert.ThrowsAny<SqliteException>(() => command.ExecuteScalar());
    }

    [Fact]
    public async Task WithIndexingOff_NothingIsOpenedOrCreated()
    {
        _features.Set(FeatureNames.Indexing, false);

        var error = await Assert.ThrowsAsync<LocalStoreException>(() => Host(100).OpenAsync(Request(100)));

        Assert.Equal(LocalStoreErrorKind.FeatureDisabled, error.Kind);
        Assert.Contains("indexing", error.Message, StringComparison.Ordinal);
        Assert.Equal(IndexStoreFactory.IndexingDisabledMessage, error.Message);
        Assert.False(Directory.Exists(_temp.NewPaths().Root));
    }

    [Fact]
    public async Task WithTheLocalStoreOff_NothingIsOpenedOrCreated()
    {
        _features.Set(FeatureNames.LocalStore, false);

        var error = await Assert.ThrowsAsync<LocalStoreException>(() => Host(100).OpenAsync(Request(100)));

        Assert.Equal(LocalStoreErrorKind.FeatureDisabled, error.Kind);
        Assert.Contains("localStore", error.Message, StringComparison.Ordinal);
        Assert.False(Directory.Exists(_temp.NewPaths().Root));
    }

    [Fact]
    public async Task AMissingRoot_IsRejected()
    {
        var request = Request(100) with { RootPath = _temp.File("no-such-repo") };

        await Assert.ThrowsAsync<DirectoryNotFoundException>(() => Host(100).OpenAsync(request));
        Assert.False(Directory.Exists(_temp.NewPaths().Root));
    }

    [Fact]
    public async Task AReaderOfAnIndexWhoseOwnerHasNotCreatedItYet_GivesUp_AfterItsRetries_AndCreatesNothing()
    {
        // A live owner holds the lock but has not created the database.
        var identity = new IndexIdentity(Repo, "bge-small-en-v1.5-int8", "1");
        var folder = IndexFolder.For(_temp.NewPaths(), identity);
        Directory.CreateDirectory(folder.Directory);
        _processes.SetAlive(100);
        Assert.True(WriterLock.TryAcquire(folder.LockFile, "pillion-host-100", _processes.HostProbe(100)).Acquired);

        var error = await Assert.ThrowsAsync<LocalStoreException>(() => Host(200).OpenAsync(Request(200)));

        Assert.Equal(LocalStoreErrorKind.DatabaseMissing, error.Kind);
        Assert.False(File.Exists(folder.DatabaseFile));
        Assert.False(File.Exists(folder.KeyFile));
    }

    [Fact]
    public async Task AReaderWaitsForTheOwnerToFinishCreatingTheDatabase()
    {
        var identity = new IndexIdentity(Repo, "bge-small-en-v1.5-int8", "1");
        var folder = IndexFolder.For(_temp.NewPaths(), identity);
        Directory.CreateDirectory(folder.Directory);
        var owner = WriterLock.TryAcquire(folder.LockFile, "pillion-host-100", _processes.HostProbe(100)).Lock!;
        var slow = new IndexStoreOptions { ReaderOpenAttempts = 100, ReaderOpenDelay = TimeSpan.FromMilliseconds(25) };

        var opening = Host(200, options: slow).OpenAsync(Request(200));
        await Task.Delay(150);
        Assert.False(opening.IsCompleted);

        // The owner creates the database now (as the factory would after winning the lock).
        await using var database = await EncryptedDatabase.OpenAsync(new EncryptedDatabaseOptions { DatabasePath = folder.DatabaseFile, KeyFilePath = folder.KeyFile }, IndexSchema.Schema);
        await using var reader = await opening;

        Assert.Equal(IndexRole.Reader, reader.Role);
        Assert.Equal("pillion-host-100", reader.Writer.PipeName);
        owner.Dispose();
    }

    [Fact]
    public async Task AnIndexWrittenByANewerSchema_IsRefusedByAReader_WithoutChangingIt()
    {
        var host = Host(100);
        string database;
        await using (var writer = await host.OpenAsync(Request(100)))
        {
            database = writer.Folder.DatabaseFile;
            await writer.Database.WriteAsync(async (connection, transaction, token) =>
            {
                using var command = connection.CreateCommand();
                command.Transaction = transaction;
                command.CommandText = "UPDATE meta SET value = '9' WHERE key IN ('schema_version', 'min_reader_version')";
                await command.ExecuteNonQueryAsync(token);
            });
        }

        // Another host owns the lock now, so this one is a reader and must refuse the newer file.
        var identity = new IndexIdentity(Repo, "bge-small-en-v1.5-int8", "1");
        var folder = IndexFolder.For(_temp.NewPaths(), identity);
        using var owner = WriterLock.TryAcquire(folder.LockFile, "pillion-host-300", _processes.HostProbe(300)).Lock!;

        var error = await Assert.ThrowsAsync<LocalStoreException>(() => Host(200).OpenAsync(Request(200)));

        Assert.Equal(LocalStoreErrorKind.NewerSchema, error.Kind);
        Assert.True(File.Exists(database));
    }

    [Fact]
    public async Task TheRegisteredServices_ResolveFromTheContainer()
    {
        var services = new ServiceCollection();
        services.AddSingleton(_temp.NewPaths());
        services.AddSingleton<IFeatureFlags>(_features);
        services.AddSingleton<IProcessProbe>(_processes.HostProbe(100));
        services.AddPillionIndexing();
        await using var provider = services.BuildServiceProvider();

        var factory = provider.GetRequiredService<IndexStoreFactory>();
        await using var store = await factory.OpenAsync(Request(100));

        Assert.Same(factory, provider.GetRequiredService<IndexStoreFactory>());
        Assert.NotNull(provider.GetRequiredService<IndexRetention>());
        Assert.Equal(IndexRole.Writer, store.Role);
    }

    [Fact]
    public async Task StateJson_IsPlainTextWithoutAnyCode_AndTheLockNamesThePipe()
    {
        await using var store = await Host(100).OpenAsync(Request(100));

        var state = JsonDocument.Parse(await File.ReadAllTextAsync(store.Folder.StateFile)).RootElement;
        var names = state.EnumerateObject().Select(p => p.Name).ToList();

        Assert.Equal(["version", "rootPath", "embeddingModel", "chunkerVersion", "schemaVersion", "createdUtc", "lastOpenedUtc"], names);
        var lockFile = JsonDocument.Parse(await File.ReadAllTextAsync(store.Folder.LockFile)).RootElement;
        Assert.Equal(100, lockFile.GetProperty("processId").GetInt32());
        Assert.Equal("pillion-host-100", lockFile.GetProperty("pipeName").GetString());
        Assert.True(lockFile.TryGetProperty("processStartUtc", out _));
    }

    private static async Task<string?> MetaAsync(IndexStore store, string key) =>
        (string?)await store.Database.ReadAsync(async (connection, token) =>
        {
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT value FROM meta WHERE key = $key";
            command.Parameters.AddWithValue("$key", key);
            return await command.ExecuteScalarAsync(token);
        });
}

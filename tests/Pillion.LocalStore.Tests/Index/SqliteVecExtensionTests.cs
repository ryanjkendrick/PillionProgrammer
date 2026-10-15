using System.Security.Cryptography;
using Pillion.LocalStore.Index;
using Pillion.LocalStore.Index.SqliteVec;

namespace Pillion.LocalStore.Tests.Index;

public sealed class SqliteVecExtensionTests : IDisposable
{
    private readonly TempDirectory _temp = new();

    public void Dispose() => _temp.Dispose();

    [Fact]
    public void TheManifest_PinsARelease_WithItsHashesAndLicence()
    {
        var manifest = SqliteVecExtension.Manifest;

        Assert.Equal("sqlite-vec", manifest.Name);
        Assert.Matches(@"^\d+\.\d+\.\d+$", manifest.Version);
        Assert.StartsWith("https://github.com/asg017/sqlite-vec/releases/download/v" + manifest.Version + "/", manifest.Url, StringComparison.Ordinal);
        Assert.Matches("^[0-9a-f]{64}$", manifest.ArchiveSha256);
        Assert.Matches("^[0-9a-f]{64}$", manifest.Sha256);
        Assert.Equal("vec0.dll", manifest.File);
        Assert.Equal("MIT OR Apache-2.0", manifest.License);
    }

    [Fact]
    public void TheBuild_PutsThePinnedLibraryBesideTheAssembly()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "vec0.dll");

        Assert.True(File.Exists(path), $"{path} is missing: the build should download it.");
        using var stream = File.OpenRead(path);
        Assert.Equal(SqliteVecExtension.Manifest.Sha256, Convert.ToHexStringLower(SHA256.HashData(stream)));
    }

    [Fact]
    public void TheExtension_IsAvailable_AndReportsThePinnedVersion()
    {
        var status = SqliteVecExtension.Status;

        Assert.True(status.IsAvailable, status.Reason);
        Assert.Equal("v" + SqliteVecExtension.Manifest.Version, status.LoadedVersion);
    }

    [Fact]
    public async Task VecVersion_Answers_OnTheWriterAndOnReaders_AfterTheKeyIsApplied()
    {
        await using var storage = await IndexTestSupport.OpenAsync(_temp);

        Assert.True(storage.SqliteVecLoaded);
        Assert.Equal(SqliteVecIndex.Name, storage.Vectors.Backend);
        Assert.Equal("v" + SqliteVecExtension.Manifest.Version, await storage.Database.ScalarAsync("SELECT vec_version()"));
        var fromWriter = await storage.Database.WriteAsync(async (connection, transaction, token) =>
        {
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = "SELECT vec_version()";
            return (string?)await command.ExecuteScalarAsync(token);
        });
        Assert.Equal("v" + SqliteVecExtension.Manifest.Version, fromWriter);
    }

    [Fact]
    public async Task LoadExtension_IsSwitchedOffAgain_SoSqlCannotLoadLibraries()
    {
        await using var storage = await IndexTestSupport.OpenAsync(_temp);

        var error = await Assert.ThrowsAsync<Microsoft.Data.Sqlite.SqliteException>(
            () => storage.Database.ScalarAsync($"SELECT load_extension('{Path.Combine(AppContext.BaseDirectory, "vec0.dll").Replace("'", "''", StringComparison.Ordinal)}')"));
        Assert.Contains("not authorized", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ALibraryWithAnotherHash_IsNotLoaded()
    {
        var copy = _temp.File("vec0.dll");
        File.Copy(Path.Combine(AppContext.BaseDirectory, "vec0.dll"), copy);
        using (var stream = new FileStream(copy, FileMode.Open, FileAccess.ReadWrite))
        {
            stream.Seek(-1, SeekOrigin.End);
            var last = stream.ReadByte();
            stream.Seek(-1, SeekOrigin.End);
            stream.WriteByte((byte)(last ^ 0xFF));
        }

        var status = SqliteVecExtension.Probe(_temp.Path);

        Assert.False(status.IsAvailable);
        Assert.Contains("not the pinned", status.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void AMissingLibrary_IsReported()
    {
        var status = SqliteVecExtension.Probe(_temp.Path);

        Assert.False(status.IsAvailable);
        Assert.Contains("was not found", status.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public async Task WithoutTheExtension_TheBruteForceBackendIsChosen()
    {
        await using var storage = await IndexTestSupport.OpenWithoutExtensionAsync(_temp);

        Assert.False(storage.SqliteVecLoaded);
        Assert.Null(storage.SqliteVec);
        Assert.Equal(BruteForceVectorIndex.Name, storage.Vectors.Backend);
        await Assert.ThrowsAsync<Microsoft.Data.Sqlite.SqliteException>(() => storage.Database.ScalarAsync("SELECT vec_version()"));
    }

    [Fact]
    public async Task RequiringSqliteVec_WithoutIt_FailsToOpen()
    {
        await Assert.ThrowsAsync<InvalidOperationException>(() => IndexTestSupport.OpenAsync(
            _temp,
            new IndexStorageOptions { LoadSqliteVec = false, VectorBackend = VectorBackendPreference.SqliteVec }));
    }
}

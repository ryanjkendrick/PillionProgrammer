using System.Security.AccessControl;
using System.Security.Principal;
using Pillion.Contracts;
using Pillion.LocalStore.Keys;

namespace Pillion.LocalStore.Tests;

public sealed class KeyTests : IDisposable
{
    private readonly TempDirectory _temp = new();

    public void Dispose() => _temp.Dispose();

    [Fact]
    public async Task TheKeyFile_IsBesideTheDatabase_WithAUserOnlyAcl()
    {
        await using var database = await TestDatabases.OpenAsync(_temp);

        Assert.Equal(_temp.File("key.bin"), database.KeyFilePath);
        var security = new FileInfo(database.KeyFilePath).GetAccessControl();
        var user = WindowsIdentity.GetCurrent().User!;

        Assert.True(security.AreAccessRulesProtected, "inheritance from the folder is removed");
        Assert.Equal(user, security.GetOwner(typeof(SecurityIdentifier)));
        var rules = security.GetAccessRules(includeExplicit: true, includeInherited: true, typeof(SecurityIdentifier)).Cast<FileSystemAccessRule>().ToList();
        var rule = Assert.Single(rules);
        Assert.Equal(user, rule.IdentityReference);
        Assert.Equal(AccessControlType.Allow, rule.AccessControlType);
        Assert.Equal(FileSystemRights.FullControl, rule.FileSystemRights & FileSystemRights.FullControl);
        Assert.False(rule.IsInherited);
    }

    [Fact]
    public void TheKeyFile_HasAVersionedHeader_AndNeverHoldsTheRawKey()
    {
        var path = _temp.File("key.bin");
        using var key = DatabaseKeyFile.LoadOrCreate(path, databaseExists: false);
        var content = File.ReadAllBytes(path);

        Assert.Equal("PPK"u8.ToArray(), content[..3]);
        Assert.Equal(DatabaseKeyFile.FormatVersion, content[3]);
        Assert.Equal(-1, content.AsSpan().IndexOf(key.Bytes));
        Assert.DoesNotContain(Convert.ToHexString(key.Bytes), System.Text.Encoding.ASCII.GetString(content), StringComparison.OrdinalIgnoreCase);

        using var reloaded = DatabaseKeyFile.Load(path);
        Assert.True(reloaded.Bytes.SequenceEqual(key.Bytes));
        Assert.Equal(DatabaseKey.Length, key.Bytes.Length);
    }

    [Fact]
    public async Task ProcessesRacingToCreateAKey_AllGetTheSameOne()
    {
        var path = _temp.File("key.bin");

        var keys = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => Task.Run(() => DatabaseKeyFile.LoadOrCreate(path, databaseExists: false))));

        try
        {
            Assert.All(keys, k => Assert.True(k.Bytes.SequenceEqual(keys[0].Bytes)));
            Assert.Single(Directory.GetFiles(_temp.Path));
        }
        finally
        {
            foreach (var key in keys)
            {
                key.Dispose();
            }
        }
    }

    [Fact]
    public async Task TheWrongKey_FailsClearly()
    {
        await using (var database = await TestDatabases.OpenAsync(_temp))
        {
            await database.AddNoteAsync("secret");
        }

        // Replace the key with another valid key file (DPAPI-protected for this user, but a different key).
        var other = _temp.File("other.bin");
        DatabaseKeyFile.LoadOrCreate(other, databaseExists: false).Dispose();
        File.Copy(other, _temp.File("key.bin"), overwrite: true);

        var error = await Assert.ThrowsAsync<LocalStoreException>(() => TestDatabases.OpenAsync(_temp));

        Assert.Equal(LocalStoreErrorKind.KeyMismatch, error.Kind);
        Assert.IsAssignableFrom<PillionRpcException>(error);
        Assert.Contains("key does not match", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AMissingKey_ForAnExistingDatabase_FailsClearly_AndNoNewKeyIsMade()
    {
        await using (var database = await TestDatabases.OpenAsync(_temp))
        {
            await database.AddNoteAsync("secret");
        }

        File.Delete(_temp.File("key.bin"));

        var error = await Assert.ThrowsAsync<LocalStoreException>(() => TestDatabases.OpenAsync(_temp));

        Assert.Equal(LocalStoreErrorKind.KeyMissing, error.Kind);
        Assert.Contains("key file (key.bin) is missing", error.Message, StringComparison.Ordinal);
        Assert.False(File.Exists(_temp.File("key.bin")));
    }

    [Fact]
    public async Task ADamagedKeyFile_FailsClearly()
    {
        await using (var database = await TestDatabases.OpenAsync(_temp))
        {
            await database.AddNoteAsync("secret");
        }

        var path = _temp.File("key.bin");
        var content = File.ReadAllBytes(path);
        content[^1] ^= 0xFF; // DPAPI detects the change.
        File.WriteAllBytes(path, content);

        var error = await Assert.ThrowsAsync<LocalStoreException>(() => TestDatabases.OpenAsync(_temp));

        Assert.Equal(LocalStoreErrorKind.KeyUnreadable, error.Kind);
        Assert.Contains("could not decrypt", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AKeyFileFromANewerFormat_IsRefused()
    {
        var path = _temp.File("key.bin");
        File.WriteAllBytes(path, [(byte)'P', (byte)'P', (byte)'K', 9, 1, 2, 3]);

        var error = Assert.Throws<LocalStoreException>(() => DatabaseKeyFile.Load(path));

        Assert.Equal(LocalStoreErrorKind.KeyUnreadable, error.Kind);
        Assert.Contains("format version 9", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void TheKeyPragma_UsesTheHexKeyForm()
    {
        using var key = DatabaseKey.FromBytes(Enumerable.Range(0, 32).Select(i => (byte)i).ToArray());

        Assert.Equal("PRAGMA hexkey = '000102030405060708090A0B0C0D0E0F101112131415161718191A1B1C1D1E1F'", key.ToHexKeyPragma());
    }
}

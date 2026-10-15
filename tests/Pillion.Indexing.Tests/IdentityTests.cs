using Pillion.Indexing.Ownership;

namespace Pillion.Indexing.Tests;

public sealed class IdentityTests
{
    [Fact]
    public void TheId_IsAStable32DigitHash()
    {
        var identity = new IndexIdentity(@"C:\src\repo", "bge-small-en-v1.5-int8", "1");

        Assert.Equal(32, identity.Id.Length);
        Assert.Matches("^[0-9a-f]{32}$", identity.Id);

        // Pinned: the id names a folder on disk, so it must never change between builds.
        Assert.Equal("bd5c7d12e5575978edb367d939fd059c", IndexIdentity.ComputeId(@"c:\src\repo", "bge-small-en-v1.5-int8", "1"));
        Assert.Equal(identity.Id, new IndexIdentity(@"C:\src\repo", "bge-small-en-v1.5-int8", "1").Id);
    }

    [Theory]
    [InlineData(@"C:\src\repo")]
    [InlineData(@"c:\SRC\Repo")]
    [InlineData(@"C:\src\repo\")]
    [InlineData(@"C:/src/repo")]
    [InlineData(@"C:\src\other\..\repo")]
    [InlineData(@"C:\src\.\repo")]
    public void DifferentSpellingsOfOneRoot_GiveOneIndex(string root)
    {
        var reference = new IndexIdentity(@"C:\src\repo", "model", "1");

        var identity = new IndexIdentity(root, "model", "1");

        Assert.Equal(reference.Id, identity.Id);
        Assert.Equal(reference, identity);
        Assert.Equal(reference.GetHashCode(), identity.GetHashCode());
        Assert.Equal(@"c:\src\repo", identity.NormalizedRoot);
    }

    [Fact]
    public void ARootAModelOrAChunkerVersion_ThatDiffers_GivesAnotherIndex()
    {
        var baseline = new IndexIdentity(@"C:\src\repo", "model-a", "1");

        Assert.NotEqual(baseline.Id, new IndexIdentity(@"C:\src\repo2", "model-a", "1").Id);
        Assert.NotEqual(baseline.Id, new IndexIdentity(@"C:\src\repo", "model-b", "1").Id);
        Assert.NotEqual(baseline.Id, new IndexIdentity(@"C:\src\repo", "model-a", "2").Id);
        Assert.NotEqual(baseline, new IndexIdentity(@"C:\src\repo", "model-a", "2"));
    }

    [Fact]
    public void ThePartsCannotRunIntoEachOther()
    {
        Assert.NotEqual(
            new IndexIdentity(@"C:\src\repo", "ab", "c").Id,
            new IndexIdentity(@"C:\src\repo", "a", "bc").Id);
    }

    [Fact]
    public void BlankPartsAreRejected()
    {
        Assert.Throws<ArgumentException>(() => new IndexIdentity("", "m", "1"));
        Assert.Throws<ArgumentException>(() => new IndexIdentity(@"C:\r", " ", "1"));
        Assert.Throws<ArgumentException>(() => new IndexIdentity(@"C:\r", "m", ""));
    }

    [Fact]
    public void TheFolder_HoldsTheFourFilesOfTheDesign()
    {
        using var temp = new TempDirectory();
        var paths = temp.NewPaths();
        var identity = new IndexIdentity(temp.Path, "model", "1");

        var folder = IndexFolder.For(paths, identity);

        Assert.Equal(Path.Combine(paths.Root, "index", identity.Id), folder.Directory);
        Assert.Equal(identity.Id, folder.Id);
        Assert.Equal(Path.Combine(folder.Directory, "index.db"), folder.DatabaseFile);
        Assert.Equal(Path.Combine(folder.Directory, "key.bin"), folder.KeyFile);
        Assert.Equal(Path.Combine(folder.Directory, "writer.lock"), folder.LockFile);
        Assert.Equal(Path.Combine(folder.Directory, "state.json"), folder.StateFile);
        Assert.Equal(0, folder.GetDiskUsageBytes());
    }
}

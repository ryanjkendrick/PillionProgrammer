using System.IO.Hashing;
using Pillion.Indexing.Hashing;

namespace Pillion.Indexing.Tests;

public sealed class HashingTests
{
    [Fact]
    public void TheHashOfNothing_IsTheKnownXxHash64Value()
    {
        Assert.Equal(0xEF46DB3751D8E999UL, FileHasher.Hash(ReadOnlySpan<byte>.Empty));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(65535)]
    [InlineData(65536)]
    [InlineData(65537)]
    [InlineData(1_000_000)]
    [InlineData(5_000_003)]
    public void AFileIsHashedLikeItsBytes_WhateverTheBufferBoundaries(int length)
    {
        using var temp = new TempDirectory();
        var content = new byte[length];
        new Random(length).NextBytes(content);
        var path = temp.WriteBytes("data.bin", content);

        var fingerprint = FileHasher.Hash(path);

        Assert.Equal(XxHash64.HashToUInt64(content), fingerprint.Hash);
        Assert.Equal(length, fingerprint.Size);
    }

    [Fact]
    public void SizeAndModifiedTime_ComeFromTheSamePass()
    {
        using var temp = new TempDirectory();
        var path = temp.Write("a.txt", "twelve bytes");
        var stamp = new DateTime(2025, 3, 14, 15, 9, 26, 535, DateTimeKind.Utc);
        File.SetLastWriteTimeUtc(path, stamp);

        var fingerprint = FileHasher.Hash(path);

        Assert.Equal(12, fingerprint.Size);
        Assert.Equal(new DateTimeOffset(stamp).ToUnixTimeMilliseconds(), fingerprint.ModifiedUnixMilliseconds);
    }

    [Fact]
    public void DifferentContent_GivesADifferentHash_AndEqualContentTheSame()
    {
        using var temp = new TempDirectory();
        var a = FileHasher.Hash(temp.Write("a.txt", "hello world")).Hash;
        var b = FileHasher.Hash(temp.Write("b.txt", "hello worle")).Hash;
        var c = FileHasher.Hash(temp.Write("c.txt", "hello world")).Hash;

        Assert.NotEqual(a, b);
        Assert.Equal(a, c);
    }

    [Fact]
    public void AFileAnotherProgramHasOpenForWriting_CanStillBeHashed()
    {
        using var temp = new TempDirectory();
        var path = temp.Write("open.txt", "being edited");
        using var writer = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite | FileShare.Delete);

        Assert.Equal(12, FileHasher.Hash(path).Size);
    }

    [Fact]
    public void AFileLockedExclusively_ThrowsIOException()
    {
        using var temp = new TempDirectory();
        var path = temp.Write("locked.txt", "x");
        using var exclusive = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None);

        Assert.Throws<IOException>(() => FileHasher.Hash(path));
    }

    [Fact]
    public void AMissingFile_ThrowsFileNotFound()
    {
        Assert.Throws<FileNotFoundException>(() => FileHasher.Hash(Path.Combine(Path.GetTempPath(), "pillion-missing-" + Guid.NewGuid().ToString("N"))));
    }
}

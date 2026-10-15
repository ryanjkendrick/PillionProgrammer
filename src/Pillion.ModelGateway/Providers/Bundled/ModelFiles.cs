using System.Globalization;
using System.Security.Cryptography;

namespace Pillion.ModelGateway.Providers.Bundled;

/// <summary>A model file whose size or SHA-256 differs from the manifest; the host refuses to load it.</summary>
public sealed class ModelVerificationException : Exception
{
    /// <summary>Initializes a new instance of the <see cref="ModelVerificationException"/> class.</summary>
    /// <param name="path">The file.</param>
    /// <param name="expectedSha256">The manifest's hash.</param>
    /// <param name="actualSha256">The file's hash, or <see langword="null"/> when the size already differed.</param>
    /// <param name="expectedSize">The manifest's size.</param>
    /// <param name="actualSize">The file's size.</param>
    public ModelVerificationException(string path, string expectedSha256, string? actualSha256, long expectedSize, long actualSize)
        : base(actualSha256 is null
            ? string.Create(CultureInfo.InvariantCulture, $"The model file '{path}' is {actualSize} bytes, but the model manifest expects {expectedSize}; it is refused.")
            : $"The model file '{path}' has SHA-256 {actualSha256}, but the model manifest expects {expectedSha256}; it is refused.")
    {
        FilePath = path;
        ExpectedSha256 = expectedSha256;
        ActualSha256 = actualSha256;
        ExpectedSize = expectedSize;
        ActualSize = actualSize;
    }

    /// <summary>Gets the file.</summary>
    public string FilePath { get; }

    /// <summary>Gets the manifest's hash.</summary>
    public string ExpectedSha256 { get; }

    /// <summary>Gets the file's hash, or <see langword="null"/> when the size already differed.</summary>
    public string? ActualSha256 { get; }

    /// <summary>Gets the manifest's size.</summary>
    public long ExpectedSize { get; }

    /// <summary>Gets the file's size.</summary>
    public long ActualSize { get; }
}

/// <summary>Hashing and verified reads of model files.</summary>
public static class ModelFiles
{
    /// <summary>Gets the SHA-256 of some bytes as 64 lower-case hex digits.</summary>
    /// <param name="data">The bytes.</param>
    /// <returns>The hash.</returns>
    public static string Sha256(ReadOnlySpan<byte> data) => Convert.ToHexStringLower(SHA256.HashData(data));

    /// <summary>Gets the SHA-256 of a file as 64 lower-case hex digits.</summary>
    /// <param name="path">The file.</param>
    /// <param name="cancellationToken">A token that cancels the read.</param>
    /// <returns>The hash.</returns>
    public static async Task<string> Sha256Async(string path, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20, FileOptions.Asynchronous | FileOptions.SequentialScan);
        return Convert.ToHexStringLower(await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false));
    }

    /// <summary>Checks a file against its manifest entry: the size first, then the SHA-256.</summary>
    /// <param name="path">The file.</param>
    /// <param name="file">The manifest entry.</param>
    /// <param name="cancellationToken">A token that cancels the read between blocks.</param>
    /// <exception cref="ModelVerificationException">The size or the hash differs.</exception>
    /// <exception cref="IOException">The file cannot be read.</exception>
    public static void Verify(string path, ModelFile file, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(path);
        ArgumentNullException.ThrowIfNull(file);

        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1, FileOptions.SequentialScan);
        if (stream.Length != file.Size)
        {
            throw new ModelVerificationException(path, file.Sha256, null, file.Size, stream.Length);
        }

        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[1 << 20];
        int read;
        while ((read = stream.Read(buffer, 0, buffer.Length)) > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            hash.AppendData(buffer, 0, read);
        }

        var actual = Convert.ToHexStringLower(hash.GetHashAndReset());
        if (!string.Equals(actual, file.Sha256, StringComparison.Ordinal))
        {
            throw new ModelVerificationException(path, file.Sha256, actual, file.Size, stream.Length);
        }
    }

    /// <summary>Checks a file against its manifest entry without blocking: the size first, then the SHA-256.</summary>
    /// <param name="path">The file.</param>
    /// <param name="file">The manifest entry.</param>
    /// <param name="cancellationToken">A token that cancels the read.</param>
    /// <returns>A task that completes when the file matches.</returns>
    /// <exception cref="ModelVerificationException">The size or the hash differs.</exception>
    /// <exception cref="IOException">The file cannot be read.</exception>
    public static async Task VerifyAsync(string path, ModelFile file, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(path);
        ArgumentNullException.ThrowIfNull(file);

        var size = new FileInfo(path).Length;
        if (size != file.Size)
        {
            throw new ModelVerificationException(path, file.Sha256, null, file.Size, size);
        }

        var actual = await Sha256Async(path, cancellationToken).ConfigureAwait(false);
        if (!string.Equals(actual, file.Sha256, StringComparison.Ordinal))
        {
            throw new ModelVerificationException(path, file.Sha256, actual, file.Size, size);
        }
    }

    /// <summary>
    /// Reads a whole file and checks the bytes read against the manifest entry, so that what is loaded is exactly what was
    /// verified (the file cannot be swapped between the check and the load).
    /// </summary>
    /// <param name="path">The file.</param>
    /// <param name="file">The manifest entry.</param>
    /// <returns>The file's bytes.</returns>
    /// <exception cref="ModelVerificationException">The size or the hash differs.</exception>
    /// <exception cref="IOException">The file cannot be read.</exception>
    public static byte[] ReadVerified(string path, ModelFile file)
    {
        ArgumentNullException.ThrowIfNull(path);
        ArgumentNullException.ThrowIfNull(file);

        var bytes = File.ReadAllBytes(path);
        if (bytes.LongLength != file.Size)
        {
            throw new ModelVerificationException(path, file.Sha256, null, file.Size, bytes.LongLength);
        }

        var actual = Sha256(bytes);
        if (!string.Equals(actual, file.Sha256, StringComparison.Ordinal))
        {
            throw new ModelVerificationException(path, file.Sha256, actual, file.Size, bytes.LongLength);
        }

        return bytes;
    }
}

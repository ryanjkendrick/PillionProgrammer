using System.Buffers;
using System.IO.Hashing;
using Microsoft.Win32.SafeHandles;

namespace Pillion.Indexing.Hashing;

/// <summary>What one pass over a file learned: its xxHash64 content hash, the bytes read and its modified time.</summary>
/// <param name="Hash">The xxHash64 of the whole content.</param>
/// <param name="Size">The number of bytes hashed.</param>
/// <param name="ModifiedUnixMilliseconds">The last write time in UTC as Unix milliseconds, read before the content (see <see cref="FileHasher"/>).</param>
public readonly record struct FileFingerprint(ulong Hash, long Size, long ModifiedUnixMilliseconds);

/// <summary>
/// Hashes a file with xxHash64 (design section 7, Hash), streamed through a pooled 64 KB buffer so memory does not grow
/// with the file. The size is the number of bytes actually read, and the modified time is read from the open handle
/// before the first byte: a file written during the pass keeps the older time, so the next sweep hashes it again rather
/// than trusting a hash of a half-written file.
/// </summary>
public static class FileHasher
{
    private const int BufferSize = 64 * 1024;

    /// <summary>Hashes a file.</summary>
    /// <param name="fullPath">The file; opened with sharing so a file open in an editor can be read.</param>
    /// <returns>The hash, size and modified time from the same pass.</returns>
    /// <exception cref="IOException">The file is locked or vanished.</exception>
    /// <exception cref="UnauthorizedAccessException">Access to the file is denied.</exception>
    public static FileFingerprint Hash(string fullPath)
    {
        ArgumentNullException.ThrowIfNull(fullPath);

        using SafeFileHandle handle = File.OpenHandle(fullPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, FileOptions.SequentialScan);
        var modified = new DateTimeOffset(File.GetLastWriteTimeUtc(handle)).ToUnixTimeMilliseconds();

        var hasher = new XxHash64();
        var buffer = ArrayPool<byte>.Shared.Rent(BufferSize);
        try
        {
            long offset = 0;
            while (true)
            {
                var read = RandomAccess.Read(handle, buffer.AsSpan(0, BufferSize), offset);
                if (read == 0)
                {
                    break;
                }

                hasher.Append(buffer.AsSpan(0, read));
                offset += read;
            }

            return new FileFingerprint(hasher.GetCurrentHashAsUInt64(), offset, modified);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    /// <summary>Hashes bytes already in memory (used by tests and by callers that hold the content).</summary>
    /// <param name="content">The bytes.</param>
    /// <returns>The xxHash64 of the bytes, the same value <see cref="Hash(string)"/> gives for a file with this content.</returns>
    public static ulong Hash(ReadOnlySpan<byte> content) => XxHash64.HashToUInt64(content);
}

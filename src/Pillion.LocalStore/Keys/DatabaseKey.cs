using System.Security.Cryptography;

namespace Pillion.LocalStore.Keys;

/// <summary>
/// A 256-bit database key in pinned memory, cleared on dispose. It is only ever turned into text to build the
/// <c>PRAGMA hexkey</c> statement; it is never logged, put in a connection string or in an exception.
/// </summary>
internal sealed class DatabaseKey : IDisposable
{
    /// <summary>The key length in bytes (256 bits).</summary>
    public const int Length = 32;

    private readonly byte[] _bytes;
    private bool _disposed;

    private DatabaseKey(byte[] pinnedBytes) => _bytes = pinnedBytes;

    /// <summary>Creates a new random key.</summary>
    /// <returns>The key.</returns>
    public static DatabaseKey CreateRandom()
    {
        var bytes = GC.AllocateUninitializedArray<byte>(Length, pinned: true);
        RandomNumberGenerator.Fill(bytes);
        return new DatabaseKey(bytes);
    }

    /// <summary>Copies a key from a buffer. The caller clears its own buffer.</summary>
    /// <param name="source">Exactly <see cref="Length"/> bytes.</param>
    /// <returns>The key.</returns>
    public static DatabaseKey FromBytes(ReadOnlySpan<byte> source)
    {
        if (source.Length != Length)
        {
            throw new ArgumentException($"A database key is {Length} bytes.", nameof(source));
        }

        var bytes = GC.AllocateUninitializedArray<byte>(Length, pinned: true);
        source.CopyTo(bytes);
        return new DatabaseKey(bytes);
    }

    /// <summary>Gets the key bytes. Valid until the key is disposed.</summary>
    public ReadOnlySpan<byte> Bytes
    {
        get
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            return _bytes;
        }
    }

    /// <summary>Builds the statement that applies the key to a new connection (SQLite3 Multiple Ciphers' hex key form).</summary>
    /// <returns><c>PRAGMA hexkey = '...'</c> with the key as 64 hexadecimal digits.</returns>
    public string ToHexKeyPragma()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        const string prefix = "PRAGMA hexkey = '";
        return string.Create(prefix.Length + (Length * 2) + 1, _bytes, static (chars, key) =>
        {
            prefix.AsSpan().CopyTo(chars);
            Convert.TryToHexString(key, chars[prefix.Length..], out _);
            chars[^1] = '\'';
        });
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        if (!_disposed)
        {
            CryptographicOperations.ZeroMemory(_bytes);
            _disposed = true;
        }
    }
}

using System.Buffers;
using System.Buffers.Binary;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace Pillion.ContextEngine.Symbols;

/// <summary>
/// Stable symbol ids: a hash of (file path, container, name, kind, start line), so re-indexing one file never changes
/// the ids of another file's symbols. The id is the first 63 bits of the SHA-256 of the UTF-8 text
/// <c>path \0 container \0 name \0 kind-name \0 start-line</c>, which makes it a positive 64-bit integer (an SQLite
/// <c>INTEGER PRIMARY KEY</c>) that never is 0. The path is hashed exactly as given; callers pass the same normalised
/// repository-relative path they store in <c>files.path</c>.
/// </summary>
public static class SymbolIds
{
    private const int StackLimit = 1024;

    /// <summary>Computes a symbol id.</summary>
    /// <param name="filePath">The file path, as stored.</param>
    /// <param name="container">The container breadcrumb (empty at file scope).</param>
    /// <param name="name">The symbol's name.</param>
    /// <param name="kind">The symbol's kind.</param>
    /// <param name="startLine">The first line, 1-based.</param>
    /// <returns>A positive, non-zero id.</returns>
    public static long Compute(string filePath, string container, string name, SymbolKind kind, int startLine)
    {
        var key = string.Join('\0', filePath ?? string.Empty, container ?? string.Empty, name ?? string.Empty,
            SymbolKinds.Name(kind), startLine.ToString(CultureInfo.InvariantCulture));
        var length = Encoding.UTF8.GetMaxByteCount(key.Length);
        byte[]? rented = null;
        Span<byte> buffer = length <= StackLimit ? stackalloc byte[StackLimit] : (rented = ArrayPool<byte>.Shared.Rent(length));
        try
        {
            var written = Encoding.UTF8.GetBytes(key, buffer);
            Span<byte> hash = stackalloc byte[SHA256.HashSizeInBytes];
            SHA256.HashData(buffer[..written], hash);
            var id = (long)(BinaryPrimitives.ReadUInt64LittleEndian(hash) & 0x7FFF_FFFF_FFFF_FFFFUL);
            return id == 0 ? 1 : id;
        }
        finally
        {
            if (rented is not null)
            {
                ArrayPool<byte>.Shared.Return(rented);
            }
        }
    }
}

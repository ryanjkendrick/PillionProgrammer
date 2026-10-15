using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;

namespace Pillion.LocalStore.Keys;

/// <summary>
/// The key file beside a database (<c>key.bin</c>): the database's 256-bit key protected with DPAPI for the current
/// Windows user (with Pillion-specific entropy), behind a four-byte header that carries the file's format version.
/// The file is created with a user-only ACL: the current user is the owner and has full control, and nothing is
/// inherited from the folder.
/// </summary>
/// <remarks>
/// Format version 1: the bytes <c>P</c>, <c>P</c>, <c>K</c>, <c>1</c>, then the <see cref="ProtectedData"/> blob of the
/// raw key (<see cref="DataProtectionScope.CurrentUser"/>, entropy <see cref="Entropy"/>).
/// </remarks>
[SupportedOSPlatform("windows")]
internal static class DatabaseKeyFile
{
    /// <summary>The current key file format version.</summary>
    public const byte FormatVersion = 1;

    private static readonly byte[] Magic = "PPK"u8.ToArray();

    /// <summary>Gets the DPAPI entropy: binds the protected blob to this purpose, so another application's DPAPI blob is not accepted.</summary>
    internal static ReadOnlySpan<byte> Entropy => "Pillion Programmer local store database key v1"u8;

    /// <summary>
    /// Loads the key of a database, or creates one when neither the key nor the database exists. Several processes may
    /// race to create the same key; exactly one file wins and every caller gets its key.
    /// </summary>
    /// <param name="keyPath">The key file path.</param>
    /// <param name="databaseExists">Whether the database file already exists (then a missing key is an error, never a new key).</param>
    /// <returns>The key; the caller disposes it.</returns>
    /// <exception cref="LocalStoreException">The key is missing for an existing database, or cannot be read or decrypted.</exception>
    public static DatabaseKey LoadOrCreate(string keyPath, bool databaseExists)
    {
        ArgumentException.ThrowIfNullOrEmpty(keyPath);

        if (File.Exists(keyPath))
        {
            return Load(keyPath);
        }

        if (databaseExists)
        {
            throw new LocalStoreException(
                LocalStoreErrorKind.KeyMissing,
                $"Pillion's encrypted database cannot be opened because its key file ({Path.GetFileName(keyPath)}) is missing.",
                "Restore the key file from the same Windows account, or delete the database to start again (its contents cannot be recovered without the key).",
                keyPath);
        }

        return Create(keyPath);
    }

    /// <summary>Reads and decrypts an existing key file.</summary>
    /// <param name="keyPath">The key file path.</param>
    /// <returns>The key; the caller disposes it.</returns>
    /// <exception cref="LocalStoreException">The file cannot be read, has an unknown format, or DPAPI refuses it.</exception>
    public static DatabaseKey Load(string keyPath)
    {
        byte[] content;
        try
        {
            content = File.ReadAllBytes(keyPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw Unreadable(keyPath, "it could not be read", ex);
        }

        if (content.Length <= Magic.Length + 1 || !content.AsSpan(0, Magic.Length).SequenceEqual(Magic))
        {
            throw Unreadable(keyPath, "it is not a Pillion key file", null);
        }

        var version = content[Magic.Length];
        if (version != FormatVersion)
        {
            throw Unreadable(keyPath, $"it has format version {version} and this build reads version {FormatVersion}; it was written by a newer Pillion Programmer", null);
        }

        byte[] raw;
        try
        {
            raw = ProtectedData.Unprotect(content[(Magic.Length + 1)..], Entropy.ToArray(), DataProtectionScope.CurrentUser);
        }
        catch (CryptographicException ex)
        {
            throw Unreadable(keyPath, "Windows could not decrypt it for the current user (it belongs to another Windows account or machine, or it is damaged)", ex);
        }

        try
        {
            if (raw.Length != DatabaseKey.Length)
            {
                throw Unreadable(keyPath, "it does not hold a 256-bit key", null);
            }

            return DatabaseKey.FromBytes(raw);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(raw);
        }
    }

    /// <summary>Creates a new random key and writes it, or returns the key another process wrote first.</summary>
    /// <param name="keyPath">The key file path.</param>
    /// <returns>The key; the caller disposes it.</returns>
    private static DatabaseKey Create(string keyPath)
    {
        var directory = Path.GetDirectoryName(Path.GetFullPath(keyPath))!;
        Directory.CreateDirectory(directory);

        var key = DatabaseKey.CreateRandom();
        try
        {
            var plain = key.Bytes.ToArray();
            byte[] protectedKey;
            try
            {
                protectedKey = ProtectedData.Protect(plain, Entropy.ToArray(), DataProtectionScope.CurrentUser);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(plain);
            }

            var temp = Path.Combine(directory, $"{Path.GetFileName(keyPath)}.{Guid.NewGuid():N}.tmp");
            try
            {
                using (var stream = new FileInfo(temp).Create(FileMode.CreateNew, FileSystemRights.FullControl, FileShare.None, 4096, FileOptions.None, UserOnlySecurity()))
                {
                    stream.Write(Magic);
                    stream.WriteByte(FormatVersion);
                    stream.Write(protectedKey);
                    stream.Flush(flushToDisk: true);
                }

                // A rename on the same volume keeps the file's security descriptor. It fails when another process won.
                File.Move(temp, keyPath, overwrite: false);
                return key;
            }
            catch (IOException) when (File.Exists(keyPath))
            {
                key.Dispose();
                return Load(keyPath);
            }
            finally
            {
                if (File.Exists(temp))
                {
                    File.Delete(temp);
                }
            }
        }
        catch
        {
            key.Dispose();
            throw;
        }
    }

    /// <summary>Builds the user-only file security: the current user owns the file and has full control; no inherited entries.</summary>
    /// <returns>The security descriptor.</returns>
    internal static FileSecurity UserOnlySecurity()
    {
        var user = WindowsIdentity.GetCurrent().User
            ?? throw new InvalidOperationException("The current user's SID is unavailable.");

        var security = new FileSecurity();
        security.SetOwner(user);
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        security.AddAccessRule(new FileSystemAccessRule(user, FileSystemRights.FullControl, AccessControlType.Allow));
        return security;
    }

    private static LocalStoreException Unreadable(string keyPath, string reason, Exception? inner) => new(
        LocalStoreErrorKind.KeyUnreadable,
        new StringBuilder("Pillion's encrypted database cannot be opened because its key file (")
            .Append(Path.GetFileName(keyPath)).Append(") is unusable: ").Append(reason).Append('.').ToString(),
        "Open it from the Windows account that created it, or delete the database and its key file to start again (the contents cannot be recovered without the key).",
        keyPath,
        inner);
}

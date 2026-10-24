using Microsoft.Data.Sqlite;

namespace Pillion.LocalStore.Database;

/// <summary>
/// Loads the native SQLite3 Multiple Ciphers library (e_sqlite3mc from SQLitePCLRaw.bundle_e_sqlite3mc) once per
/// process and checks that it really is the cipher build, so a plain SQLite can never create an unencrypted database.
/// </summary>
internal static class SqliteCipher
{
    private static readonly Lazy<CipherLibraryInfo> Info = new(Load, LazyThreadSafetyMode.ExecutionAndPublication);

    /// <summary>Gets the versions of the loaded library, loading it on first use.</summary>
    /// <exception cref="LocalStoreException">The loaded SQLite is not SQLite3 Multiple Ciphers.</exception>
    public static CipherLibraryInfo Library => Info.Value;

    /// <summary>Loads and checks the library.</summary>
    /// <exception cref="LocalStoreException">The loaded SQLite is not SQLite3 Multiple Ciphers.</exception>
    public static void EnsureLoaded() => _ = Info.Value;

    private static CipherLibraryInfo Load()
    {
        try
        {
            SQLitePCL.Batteries_V2.Init();
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException or TypeInitializationException)
        {
            throw Unavailable(ex);
        }

        using var connection = new SqliteConnection("Data Source=:memory:;Pooling=False");
        connection.Open();
        using var command = connection.CreateCommand();
        try
        {
            command.CommandText = "SELECT sqlite_version(), sqlite3mc_version()";
            using var reader = command.ExecuteReader();
            reader.Read();
            return new CipherLibraryInfo(reader.GetString(0), reader.GetString(1));
        }
        catch (SqliteException ex)
        {
            throw Unavailable(ex);
        }
    }

    private static LocalStoreException Unavailable(Exception ex) => new(
        LocalStoreErrorKind.CipherUnavailable,
        "Pillion's encrypted databases are unavailable: the SQLite encryption library (e_sqlite3mc) did not load, so nothing is stored.",
        "Reinstall Pillion Programmer; the e_sqlite3mc library beside Pillion Host may be missing or replaced.",
        ex.Message,
        ex);
}

/// <summary>The versions of the loaded native library.</summary>
/// <param name="SqliteVersion">The SQLite version, for example <c>3.49.1</c>.</param>
/// <param name="CipherVersion">The SQLite3 Multiple Ciphers version text, for example <c>SQLite3 Multiple Ciphers 2.1.0</c>.</param>
internal sealed record CipherLibraryInfo(string SqliteVersion, string CipherVersion);

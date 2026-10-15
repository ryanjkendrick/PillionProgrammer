using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Data.Sqlite;
using Pillion.LocalStore.Database;

namespace Pillion.LocalStore.Index.SqliteVec;

/// <summary>The pinned sqlite-vec release, as <c>sqlite-vec.manifest.json</c> records it (embedded in this assembly).</summary>
/// <param name="Name">The library name.</param>
/// <param name="Version">The release version, for example <c>0.1.9</c>.</param>
/// <param name="License">The licence expression.</param>
/// <param name="Url">The release archive downloaded at build time.</param>
/// <param name="ArchiveSha256">The SHA-256 of the archive.</param>
/// <param name="File">The library file inside it (<c>vec0.dll</c>).</param>
/// <param name="Sha256">The SHA-256 of the library file, checked again before every load.</param>
/// <param name="EntryPoint">The extension's init function.</param>
public sealed record SqliteVecManifest(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("version")] string Version,
    [property: JsonPropertyName("license")] string License,
    [property: JsonPropertyName("url")] string Url,
    [property: JsonPropertyName("archiveSha256")] string ArchiveSha256,
    [property: JsonPropertyName("file")] string File,
    [property: JsonPropertyName("sha256")] string Sha256,
    [property: JsonPropertyName("entryPoint")] string EntryPoint);

/// <summary>Whether sqlite-vec can be loaded in this process, and why not when it cannot.</summary>
/// <param name="IsAvailable">Whether the library was found, has the pinned hash and loads into SQLite3 Multiple Ciphers.</param>
/// <param name="LibraryPath">The library path, when found.</param>
/// <param name="LoadedVersion">What <c>vec_version()</c> answered, when it loaded.</param>
/// <param name="Reason">Why it is unavailable, when it is.</param>
public sealed record SqliteVecStatus(bool IsAvailable, string? LibraryPath, string? LoadedVersion, string? Reason);

/// <summary>
/// Loads the pinned sqlite-vec run-time extension (phase 2 decision of 2026-10-15) into a connection. The library is
/// looked up beside this assembly (the build copies it there and into the host's publish folder), its SHA-256 must match
/// the embedded manifest, and a trial load must answer <c>vec_version()</c> with the pinned version; the result is decided
/// once per process. Extension loading is switched on only for the load itself, so SQL cannot call
/// <c>load_extension()</c> afterwards.
/// </summary>
public static class SqliteVecExtension
{
    private const string ManifestResource = "Pillion.LocalStore.sqlite-vec.manifest.json";
    private static readonly Lazy<SqliteVecManifest> LazyManifest = new(ReadManifest, LazyThreadSafetyMode.ExecutionAndPublication);
    private static Lazy<SqliteVecStatus> _status = new(() => Probe(null), LazyThreadSafetyMode.ExecutionAndPublication);

    /// <summary>Gets the pinned release.</summary>
    public static SqliteVecManifest Manifest => LazyManifest.Value;

    /// <summary>Gets whether the extension can be loaded, probing on first use.</summary>
    public static SqliteVecStatus Status => _status.Value;

    /// <summary>Loads the extension into an open connection when it is available.</summary>
    /// <param name="connection">The connection, already keyed.</param>
    /// <returns><see langword="true"/> when it was loaded; <see langword="false"/> when it is unavailable (see <see cref="Status"/>).</returns>
    /// <exception cref="SqliteException">The load failed although the probe succeeded.</exception>
    public static bool TryLoad(SqliteConnection connection)
    {
        ArgumentNullException.ThrowIfNull(connection);
        var status = Status;
        if (!status.IsAvailable)
        {
            return false;
        }

        Load(connection, status.LibraryPath!);
        return true;
    }

    /// <summary>Probes again with the library in another folder. For tests.</summary>
    /// <param name="directory">The folder to look in, or <see langword="null"/> for the default.</param>
    /// <returns>The new status.</returns>
    internal static SqliteVecStatus ResetForTests(string? directory)
    {
        _status = new Lazy<SqliteVecStatus>(() => Probe(directory), LazyThreadSafetyMode.ExecutionAndPublication);
        return _status.Value;
    }

    /// <summary>Probes the library in a folder without changing <see cref="Status"/>.</summary>
    /// <param name="directory">The folder to look in, or <see langword="null"/> for the default.</param>
    /// <returns>The status.</returns>
    internal static SqliteVecStatus Probe(string? directory)
    {
        var manifest = Manifest;
        var path = directory is not null ? Path.Combine(directory, manifest.File) : FindLibrary(manifest.File);
        if (path is null || !File.Exists(path))
        {
            return new SqliteVecStatus(false, path, null, $"{manifest.File} was not found beside Pillion Host.");
        }

        string actual;
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            actual = Convert.ToHexStringLower(SHA256.HashData(stream));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new SqliteVecStatus(false, path, null, $"{manifest.File} could not be read: {ex.Message}");
        }

        if (!string.Equals(actual, manifest.Sha256, StringComparison.OrdinalIgnoreCase))
        {
            return new SqliteVecStatus(false, path, null, $"{manifest.File} has SHA-256 {actual}, not the pinned {manifest.Sha256} (sqlite-vec {manifest.Version}); it was not loaded.");
        }

        try
        {
            SqliteCipher.EnsureLoaded();
            using var connection = new SqliteConnection("Data Source=:memory:;Pooling=False");
            connection.Open();
            Load(connection, path);
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT vec_version()";
            var version = command.ExecuteScalar() as string;
            if (!string.Equals(version?.TrimStart('v'), manifest.Version, StringComparison.Ordinal))
            {
                return new SqliteVecStatus(false, path, version, $"{manifest.File} reports version {version}, not the pinned {manifest.Version}.");
            }

            return new SqliteVecStatus(true, path, version, null);
        }
        catch (Exception ex) when (ex is SqliteException or LocalStoreException or DllNotFoundException or BadImageFormatException or InvalidOperationException)
        {
            return new SqliteVecStatus(false, path, null, $"{manifest.File} did not load: {ex.Message}");
        }
    }

    private static void Load(SqliteConnection connection, string path)
    {
        connection.EnableExtensions(true);
        try
        {
            connection.LoadExtension(path, Manifest.EntryPoint);
        }
        finally
        {
            connection.EnableExtensions(false);
        }
    }

    private static string? FindLibrary(string file)
    {
        foreach (var directory in new[] { AppContext.BaseDirectory, Path.GetDirectoryName(typeof(SqliteVecExtension).Assembly.Location) })
        {
            if (!string.IsNullOrEmpty(directory))
            {
                var candidate = Path.Combine(directory, file);
                if (File.Exists(candidate))
                {
                    return candidate;
                }
            }
        }

        return null;
    }

    private static SqliteVecManifest ReadManifest()
    {
        using var stream = typeof(SqliteVecExtension).Assembly.GetManifestResourceStream(ManifestResource)
            ?? throw new InvalidOperationException($"The embedded resource {ManifestResource} is missing.");
        return JsonSerializer.Deserialize<SqliteVecManifest>(stream)
            ?? throw new InvalidOperationException($"The embedded resource {ManifestResource} is empty.");
    }
}

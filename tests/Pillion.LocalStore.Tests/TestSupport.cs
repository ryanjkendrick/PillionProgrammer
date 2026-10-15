using Microsoft.Data.Sqlite;
using Pillion.Common;
using Pillion.Common.Settings;
using Pillion.LocalStore.Conversations;

// DPAPI and file ACLs are Windows-only, like Pillion Host.
[assembly: System.Runtime.Versioning.SupportedOSPlatform("windows")]

namespace Pillion.LocalStore.Tests;

/// <summary>A unique scratch directory that is deleted when disposed.</summary>
internal sealed class TempDirectory : IDisposable
{
    public TempDirectory()
    {
        Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "pillion-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path);
    }

    public string Path { get; }

    public string File(string name) => System.IO.Path.Combine(Path, name);

    public PillionPaths NewPaths(string name = "data") => new(System.IO.Path.Combine(Path, name));

    public void Dispose()
    {
        // Connections are closed with pooling off, but make sure no pooled handle keeps a file open.
        SqliteConnection.ClearAllPools();
        try
        {
            Directory.Delete(Path, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Best effort cleanup of a temp folder.
        }
    }
}

/// <summary>Feature flags with every feature on unless switched off.</summary>
internal sealed class FakeFeatureFlags : IFeatureFlags
{
    private readonly HashSet<string> _off = new(StringComparer.OrdinalIgnoreCase);

    public event EventHandler? Changed;

    public bool IsEnabled(string feature) => !_off.Contains(feature);

    public void Set(string feature, bool enabled)
    {
        if (enabled)
        {
            _off.Remove(feature);
        }
        else
        {
            _off.Add(feature);
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }
}

internal static class TestDatabases
{
    /// <summary>A test schema at version 1: one table of notes.</summary>
    public static StoreSchema NotesV1 { get; } = new("notes", [StoreMigration.Sql(1, 1, "notes", "CREATE TABLE notes (id INTEGER PRIMARY KEY, text TEXT NOT NULL)")]);

    /// <summary>The same schema at version 2: adds a nullable column (old readers stay compatible).</summary>
    public static StoreSchema NotesV2 { get; } = new(
        "notes",
        [
            NotesV1.Migrations[0],
            StoreMigration.Sql(2, 1, "note tags", "ALTER TABLE notes ADD COLUMN tag TEXT NULL; CREATE TABLE tags (name TEXT PRIMARY KEY)"),
        ]);

    public static EncryptedDatabaseOptions Options(TempDirectory temp, string name = "notes.db") => new() { DatabasePath = temp.File(name) };

    public static Task<EncryptedDatabase> OpenAsync(TempDirectory temp, StoreSchema? schema = null, string name = "notes.db") =>
        EncryptedDatabase.OpenAsync(Options(temp, name), schema ?? NotesV1);

    public static Task<EncryptedDatabase> OpenConversationsAsync(TempDirectory temp) =>
        EncryptedDatabase.OpenAsync(new EncryptedDatabaseOptions { DatabasePath = temp.File("conversations.db") }, ConversationsSchema.Schema);

    public static Task AddNoteAsync(this EncryptedDatabase database, string text) =>
        database.WriteAsync(async (connection, transaction, token) =>
        {
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = "INSERT INTO notes (text) VALUES ($text)";
            command.Parameters.AddWithValue("$text", text);
            await command.ExecuteNonQueryAsync(token);
        });

    public static Task<List<string>> GetNotesAsync(this EncryptedDatabase database) =>
        database.ReadAsync(async (connection, token) =>
        {
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT text FROM notes ORDER BY id";
            using var reader = await command.ExecuteReaderAsync(token);
            var notes = new List<string>();
            while (await reader.ReadAsync(token))
            {
                notes.Add(reader.GetString(0));
            }

            return notes;
        });

    public static Task<object?> ScalarAsync(this EncryptedDatabase database, string sql) =>
        database.ReadAsync(async (connection, token) =>
        {
            using var command = connection.CreateCommand();
            command.CommandText = sql;
            return await command.ExecuteScalarAsync(token);
        });

    public static Task SetMetaAsync(this EncryptedDatabase database, string key, string value) =>
        database.WriteAsync(async (connection, transaction, token) =>
        {
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = "UPDATE meta SET value = $value WHERE key = $key";
            command.Parameters.AddWithValue("$key", key);
            command.Parameters.AddWithValue("$value", value);
            await command.ExecuteNonQueryAsync(token);
        });

    /// <summary>Opens a file the way a plain SQLite would: no key.</summary>
    public static SqliteConnection OpenWithoutKey(string path)
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString());
        connection.Open();
        return connection;
    }
}

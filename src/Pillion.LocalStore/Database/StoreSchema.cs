using Microsoft.Data.Sqlite;

namespace Pillion.LocalStore;

/// <summary>
/// One step of a database's schema history. Migration <c>n</c> takes the database from schema version <c>n - 1</c> to
/// <c>n</c>, inside the same transaction that records the new version in <c>meta</c>.
/// </summary>
/// <param name="Version">The schema version this migration produces (1 for the first).</param>
/// <param name="MinReaderVersion">
/// The oldest schema version whose code can still read and write the database after this migration. Keep it at the
/// previous value for additive changes that older builds can ignore (a new table, a new nullable column); raise it to
/// <paramref name="Version"/> when older builds would misread the data.
/// </param>
/// <param name="Description">What the migration does, for the log.</param>
/// <param name="ApplyAsync">Applies the change on the writer connection, within the given transaction.</param>
public sealed record StoreMigration(
    int Version,
    int MinReaderVersion,
    string Description,
    Func<SqliteConnection, SqliteTransaction, CancellationToken, Task> ApplyAsync)
{
    /// <summary>Creates a migration that runs a SQL script (one or more statements).</summary>
    /// <param name="version">The schema version this migration produces.</param>
    /// <param name="minReaderVersion">The oldest schema version that can still read the database afterwards.</param>
    /// <param name="description">What the migration does.</param>
    /// <param name="sql">The script.</param>
    /// <returns>The migration.</returns>
    public static StoreMigration Sql(int version, int minReaderVersion, string description, string sql)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sql);
        return new StoreMigration(version, minReaderVersion, description, async (connection, transaction, cancellationToken) =>
        {
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = sql;
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        });
    }
}

/// <summary>The schema of one kind of Local Store database: its name and its ordered migrations.</summary>
public sealed class StoreSchema
{
    /// <summary>Initializes a new instance of the <see cref="StoreSchema"/> class.</summary>
    /// <param name="name">The database kind, recorded in <c>meta</c> as <c>store</c> (for example <c>conversations</c>).</param>
    /// <param name="migrations">Migrations 1 to n, in order, with no gaps.</param>
    /// <exception cref="ArgumentException">The migrations are empty, out of order, have gaps, or have an invalid reader version.</exception>
    public StoreSchema(string name, IReadOnlyList<StoreMigration> migrations)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(migrations);
        if (migrations.Count == 0)
        {
            throw new ArgumentException("A schema needs at least one migration.", nameof(migrations));
        }

        var previousMinReader = 1;
        for (var i = 0; i < migrations.Count; i++)
        {
            var migration = migrations[i];
            if (migration.Version != i + 1)
            {
                throw new ArgumentException($"Migration {i} produces version {migration.Version}; migrations must produce versions 1, 2, 3, ... in order.", nameof(migrations));
            }

            if (migration.MinReaderVersion < previousMinReader || migration.MinReaderVersion > migration.Version)
            {
                throw new ArgumentException($"Migration {migration.Version} has reader version {migration.MinReaderVersion}; it must be between the previous migration's ({previousMinReader}) and its own version.", nameof(migrations));
            }

            previousMinReader = migration.MinReaderVersion;
        }

        Name = name;
        Migrations = migrations;
    }

    /// <summary>Gets the database kind.</summary>
    public string Name { get; }

    /// <summary>Gets the migrations, in order.</summary>
    public IReadOnlyList<StoreMigration> Migrations { get; }

    /// <summary>Gets the newest schema version this build writes (and the newest it understands).</summary>
    public int CurrentVersion => Migrations.Count;

    /// <summary>Gets the reader version this build records when it migrates a database to <see cref="CurrentVersion"/>.</summary>
    public int CurrentMinReaderVersion => Migrations[^1].MinReaderVersion;
}

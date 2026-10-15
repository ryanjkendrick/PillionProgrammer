namespace Pillion.LocalStore;

/// <summary>Where an <see cref="EncryptedDatabase"/> lives and how it is opened.</summary>
public sealed class EncryptedDatabaseOptions
{
    /// <summary>The default key file name, used when <see cref="KeyFilePath"/> is not set (design section 10: <c>index\&lt;id&gt;\key.bin</c>).</summary>
    public const string DefaultKeyFileName = "key.bin";

    /// <summary>Gets the database file path.</summary>
    public required string DatabasePath { get; init; }

    /// <summary>
    /// Gets the key file path, or <see langword="null"/> for <see cref="DefaultKeyFileName"/> in the database's folder.
    /// A folder that holds more than one database names each key after its database (for example
    /// <c>conversations.key.bin</c>), so every database keeps its own key.
    /// </summary>
    public string? KeyFilePath { get; init; }

    /// <summary>Gets how long a statement waits for another connection's lock (SQLite's busy timeout) before failing. Defaults to 5 seconds.</summary>
    public TimeSpan BusyTimeout { get; init; } = TimeSpan.FromSeconds(5);

    /// <summary>Gets the most reader connections kept open at once. Defaults to 4; each costs a key derivation (tens of milliseconds) when first opened and some page cache.</summary>
    public int MaxReaders { get; init; } = 4;

    /// <summary>Gets the key file path, resolving the default.</summary>
    internal string ResolvedKeyFilePath =>
        Path.GetFullPath(KeyFilePath ?? Path.Combine(Path.GetDirectoryName(Path.GetFullPath(DatabasePath))!, DefaultKeyFileName));
}

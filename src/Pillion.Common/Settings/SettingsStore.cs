using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Pillion.Common.Settings;

/// <summary>Where a settings change came from.</summary>
public enum SettingsChangeSource
{
    /// <summary>This process saved the settings.</summary>
    Save,

    /// <summary>The file was changed by something else (a text editor, another process) and was reloaded.</summary>
    External,
}

/// <summary>Data for <see cref="SettingsStore.Changed"/>.</summary>
public sealed class SettingsChangedEventArgs : EventArgs
{
    /// <summary>Initializes a new instance of the <see cref="SettingsChangedEventArgs"/> class.</summary>
    /// <param name="source">Where the change came from.</param>
    public SettingsChangedEventArgs(SettingsChangeSource source) => Source = source;

    /// <summary>Gets where the change came from.</summary>
    public SettingsChangeSource Source { get; }
}

/// <summary>Options for <see cref="SettingsStore"/>.</summary>
public sealed class SettingsStoreOptions
{
    /// <summary>Gets or sets the migrations that upgrade older files, each from one version to the next.</summary>
    public IEnumerable<ISettingsMigration> Migrations { get; set; } = [];

    /// <summary>Gets or sets a value indicating whether the file is watched for external edits. The default is <see langword="true"/>.</summary>
    public bool WatchForChanges { get; set; } = true;

    /// <summary>Gets or sets how long file events are coalesced before the file is re-read. The default is 250 ms.</summary>
    public TimeSpan DebounceInterval { get; set; } = TimeSpan.FromMilliseconds(250);
}

/// <summary>
/// Stores user settings in <c>settings.json</c>. The file is held as a JSON document so that keys this version does
/// not know about survive a round trip. Writes are atomic. All members are thread-safe.
/// </summary>
public sealed partial class SettingsStore : IDisposable
{
    private readonly object _gate = new();
    private readonly PillionPaths _paths;
    private readonly SettingsStoreOptions _options;
    private readonly SortedDictionary<int, ISettingsMigration> _migrations = [];
    private readonly ILogger _logger;

    private JsonObject? _root;
    private byte[]? _knownHash;
    private FileSystemWatcher? _watcher;
    private Timer? _debounce;
    private bool _disposed;

    /// <summary>Initializes a new instance of the <see cref="SettingsStore"/> class.</summary>
    /// <param name="paths">The data directory layout.</param>
    /// <param name="options">Optional settings for the store.</param>
    /// <param name="logger">An optional logger.</param>
    public SettingsStore(PillionPaths paths, SettingsStoreOptions? options = null, ILogger<SettingsStore>? logger = null)
    {
        ArgumentNullException.ThrowIfNull(paths);

        _paths = paths;
        _options = options ?? new SettingsStoreOptions();
        _logger = logger ?? NullLogger<SettingsStore>.Instance;

        foreach (var migration in _options.Migrations)
        {
            if (!_migrations.TryAdd(migration.FromVersion, migration))
            {
                throw new ArgumentException($"More than one settings migration is registered from version {migration.FromVersion}.", nameof(options));
            }
        }
    }

    /// <summary>
    /// Raised after settings are saved by this process and after an external edit of the file has been reloaded.
    /// External changes are raised on a thread-pool thread. A save that did not change the file's content, and
    /// the store's own writes seen by the file watcher, do not raise a second event.
    /// </summary>
    public event EventHandler<SettingsChangedEventArgs>? Changed;

    /// <summary>Gets the data directory layout.</summary>
    public PillionPaths Paths => _paths;

    /// <summary>
    /// Loads <c>settings.json</c>, creating it with defaults if it does not exist, and starts watching it.
    /// Called automatically on first use. Calling it again re-reads the file.
    /// </summary>
    /// <exception cref="SettingsVersionException">The file is from a newer version of Pillion.</exception>
    /// <exception cref="SettingsValidationException">The file is not valid.</exception>
    public void Load()
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            LoadCore();
            StartWatching();
        }
    }

    /// <summary>Returns a copy of all settings as a typed object (unknown keys are kept in the objects' extension data).</summary>
    /// <returns>A new settings object; changing it does not change the store.</returns>
    public PillionSettings GetSettings()
    {
        lock (_gate)
        {
            EnsureLoaded();
            return Deserialize<PillionSettings>(_root!);
        }
    }

    /// <summary>Reads a section as a typed object.</summary>
    /// <typeparam name="T">The section type, for example <see cref="PrivacySettings"/> or <c>List&lt;ProviderSettings&gt;</c>.</typeparam>
    /// <param name="sectionName">The top-level key, see <see cref="SettingsSections"/>.</param>
    /// <returns>A new object; changing it does not change the store. A section that is not in the file yields <c>new T()</c>.</returns>
    public T Get<T>(string sectionName)
        where T : class, new()
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sectionName);

        lock (_gate)
        {
            EnsureLoaded();
            return _root![sectionName] is { } node ? Deserialize<T>(node) : new T();
        }
    }

    /// <summary>Replaces a section with a typed object and saves.</summary>
    /// <typeparam name="T">The section type.</typeparam>
    /// <param name="sectionName">The top-level key, see <see cref="SettingsSections"/>.</param>
    /// <param name="value">The new section value. Obtain it with <see cref="Get{T}"/> to keep unknown nested properties.</param>
    /// <exception cref="SettingsValidationException">The result would not be a valid settings file.</exception>
    public void Set<T>(string sectionName, T value)
        where T : class
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sectionName);
        ArgumentNullException.ThrowIfNull(value);

        if (string.Equals(sectionName, SettingsSections.Version, StringComparison.Ordinal))
        {
            throw new SettingsValidationException("The settings version is managed by Pillion and cannot be set.");
        }

        var node = JsonSerializer.SerializeToNode(value, SettingsJson.Options);
        Mutate(root => root[sectionName] = node);
    }

    /// <summary>Gets the value at a dotted key path as JSON.</summary>
    /// <param name="keyPath">For example <c>routing.tasks.chat</c> or <c>providers.0.endpoint</c>.</param>
    /// <returns>The JSON text, or <see langword="null"/> if the path does not exist.</returns>
    public string? GetJson(string keyPath)
    {
        var segments = SettingsKeyPath.Parse(keyPath);
        lock (_gate)
        {
            EnsureLoaded();
            return SettingsKeyPath.Find(_root!, segments)?.ToJsonString(SettingsJson.Options);
        }
    }

    /// <summary>Sets the value at a dotted key path from JSON, validates the result and saves.</summary>
    /// <param name="keyPath">For example <c>privacy.localOnly</c>. An array index equal to the array length appends.</param>
    /// <param name="json">The new value as JSON.</param>
    /// <exception cref="SettingsValidationException">The key or value is not valid, or the result would not be a valid settings file.</exception>
    public void SetJson(string keyPath, string json)
    {
        var segments = SettingsKeyPath.Parse(keyPath);
        if (string.Equals(segments[0], SettingsSections.Version, StringComparison.Ordinal))
        {
            throw new SettingsValidationException("The settings version is managed by Pillion and cannot be set.");
        }

        ArgumentNullException.ThrowIfNull(json);
        JsonNode? value;
        try
        {
            value = JsonNode.Parse(json, nodeOptions: null, SettingsJson.DocumentOptions);
        }
        catch (JsonException ex)
        {
            throw new SettingsValidationException($"The value for '{keyPath}' is not valid JSON: {ex.Message}", ex);
        }

        Mutate(root => SettingsKeyPath.Set(root, segments, value));
    }

    /// <summary>Writes the current settings to disk atomically.</summary>
    public void Save()
    {
        lock (_gate)
        {
            EnsureLoaded();
            WriteCore();
        }

        RaiseChanged(SettingsChangeSource.Save);
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _watcher?.Dispose();
            _watcher = null;
            _debounce?.Dispose();
            _debounce = null;
        }
    }

    private static T Deserialize<T>(JsonNode node)
    {
        try
        {
            return node.Deserialize<T>(SettingsJson.Options)
                ?? throw new SettingsValidationException($"The settings value for {typeof(T).Name} is null.");
        }
        catch (JsonException ex)
        {
            throw new SettingsValidationException($"The settings are not valid at {ex.Path}: {ex.Message}", ex);
        }
    }

    private static byte[] Hash(byte[] bytes) => SHA256.HashData(bytes);

    private static int ReadVersion(JsonObject document)
    {
        if (!document.TryGetPropertyValue(SettingsSections.Version, out var node) || node is null)
        {
            return PillionSettings.CurrentVersion;
        }

        if (node is JsonValue value && value.TryGetValue<int>(out var version) && version >= 0)
        {
            return version;
        }

        throw new SettingsValidationException("The 'version' key of settings.json must be a non-negative integer.");
    }

    /// <summary>Adds sections and properties that are missing from a document, using the defaults. Never changes existing values.</summary>
    private static void FillDefaults(JsonObject document)
    {
        var defaults = SettingsJson.CreateDefaultDocument();
        if (!document.ContainsKey(SettingsSections.Version))
        {
            document.Insert(0, SettingsSections.Version, PillionSettings.CurrentVersion);
        }

        foreach (var section in SettingsSections.Known)
        {
            if (document[section] is { } existing)
            {
                MergeMissing(existing, defaults[section]!);
            }
            else
            {
                document[section] = defaults[section]!.DeepClone();
            }
        }
    }

    private static void MergeMissing(JsonNode target, JsonNode defaults)
    {
        if (target is not JsonObject targetObject || defaults is not JsonObject defaultsObject)
        {
            return;
        }

        foreach (var (name, defaultValue) in defaultsObject)
        {
            if (defaultValue is null)
            {
                continue;
            }

            if (targetObject[name] is { } existing)
            {
                MergeMissing(existing, defaultValue);
            }
            else if (!targetObject.ContainsKey(name))
            {
                targetObject[name] = defaultValue.DeepClone();
            }
        }
    }

    private static void Validate(JsonObject document) => _ = Deserialize<PillionSettings>(document);

    private void EnsureLoaded()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_root is null)
        {
            LoadCore();
            StartWatching();
        }
    }

    /// <summary>Applies a change to a copy of the document, validates it, and commits it to memory and disk.</summary>
    private void Mutate(Action<JsonObject> change)
    {
        lock (_gate)
        {
            EnsureLoaded();

            var candidate = (JsonObject)_root!.DeepClone();
            change(candidate);
            Validate(candidate);

            var previous = _root;
            _root = candidate;
            try
            {
                WriteCore();
            }
            catch
            {
                // Keep memory in step with the disk when the write fails.
                _root = previous;
                throw;
            }
        }

        RaiseChanged(SettingsChangeSource.Save);
    }

    /// <summary>Reads the file (creating it when missing), migrates it and makes it the current document.</summary>
    private void LoadCore()
    {
        _paths.EnsureCreated();

        var path = _paths.SettingsFile;
        if (!File.Exists(path))
        {
            var created = SettingsJson.CreateDefaultDocument();
            _root = created;
            WriteCore();
            LogCreated(_logger, path);
            return;
        }

        ApplyBytes(ReadFileWithRetry(path));
    }

    /// <summary>Parses file content, migrates when needed and swaps in the new document.</summary>
    private void ApplyBytes(byte[] bytes)
    {
        JsonObject document;
        try
        {
            document = JsonNode.Parse(bytes, nodeOptions: null, SettingsJson.DocumentOptions) as JsonObject
                ?? throw new SettingsValidationException($"settings.json must contain a JSON object. The file was not changed.");
        }
        catch (JsonException ex)
        {
            throw new SettingsValidationException($"settings.json is not valid JSON ({ex.Message}). Fix or delete the file; it was not changed.", ex);
        }

        var version = ReadVersion(document);
        if (version > PillionSettings.CurrentVersion)
        {
            throw new SettingsVersionException(version, PillionSettings.CurrentVersion);
        }

        var migrated = version < PillionSettings.CurrentVersion;
        if (migrated)
        {
            // Back up the original file before anything is rewritten.
            var backup = $"{_paths.SettingsFile}.bak-{version}";
            File.WriteAllBytes(backup, bytes);
            LogMigrating(_logger, version, PillionSettings.CurrentVersion, backup);

            for (var from = version; from < PillionSettings.CurrentVersion; from++)
            {
                if (!_migrations.TryGetValue(from, out var migration))
                {
                    throw new SettingsException($"settings.json is schema version {version} and no migration is registered from version {from}. The file was not changed; a backup is at '{backup}'.");
                }

                migration.Migrate(document);
                document[SettingsSections.Version] = from + 1;
            }
        }

        FillDefaults(document);
        Validate(document);

        _root = document;
        _knownHash = Hash(bytes);

        if (migrated)
        {
            WriteCore();
        }
    }

    private static byte[] ReadFileWithRetry(string path)
    {
        // An editor or antivirus scanner may hold the file briefly after saving it.
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                return File.ReadAllBytes(path);
            }
            catch (IOException) when (attempt < 4)
            {
                Thread.Sleep(50);
            }
        }
    }

    /// <summary>Writes the current document: temp file in the same directory, flush, then replace or move over the target.</summary>
    private void WriteCore()
    {
        var target = _paths.SettingsFile;
        var json = _root!.ToJsonString(SettingsJson.IndentedOptions) + "\n";
        var bytes = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false).GetBytes(json);

        var directory = Path.GetDirectoryName(target)!;
        Directory.CreateDirectory(directory);
        var temp = Path.Combine(directory, $"settings.json.{Guid.NewGuid():N}.tmp");

        try
        {
            using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
            {
                stream.Write(bytes, 0, bytes.Length);
                stream.Flush(flushToDisk: true);
            }

            if (File.Exists(target))
            {
                File.Replace(temp, target, destinationBackupFileName: null, ignoreMetadataErrors: true);
            }
            else
            {
                File.Move(temp, target, overwrite: true);
            }
        }
        catch
        {
            TryDelete(temp);
            throw;
        }

        _knownHash = Hash(bytes);
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Best effort; the temp file is harmless and has a unique name.
        }
    }

    private void StartWatching()
    {
        if (_watcher is not null || !_options.WatchForChanges)
        {
            return;
        }

        var directory = Path.GetDirectoryName(_paths.SettingsFile)!;
        _debounce = new Timer(OnDebounceElapsed, state: null, Timeout.Infinite, Timeout.Infinite);
        _watcher = new FileSystemWatcher(directory)
        {
            IncludeSubdirectories = false,
            NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.CreationTime,
        };
        _watcher.Changed += OnFileEvent;
        _watcher.Created += OnFileEvent;
        _watcher.Deleted += OnFileEvent;
        _watcher.Renamed += OnFileRenamed;
        _watcher.EnableRaisingEvents = true;
    }

    private void OnFileEvent(object sender, FileSystemEventArgs e) => Schedule(e.Name);

    private void OnFileRenamed(object sender, RenamedEventArgs e)
    {
        Schedule(e.Name);
        Schedule(e.OldName);
    }

    private void Schedule(string? name)
    {
        if (!string.Equals(name, "settings.json", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        lock (_gate)
        {
            _debounce?.Change(_options.DebounceInterval, Timeout.InfiniteTimeSpan);
        }
    }

#pragma warning disable CA1031 // A timer callback must never let an exception escape (it would end the process).
    private void OnDebounceElapsed(object? state)
    {
        try
        {
            lock (_gate)
            {
                if (_disposed)
                {
                    return;
                }

                byte[] bytes;
                try
                {
                    bytes = ReadFileWithRetry(_paths.SettingsFile);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    LogReloadFailed(_logger, ex.Message);
                    return;
                }

                var hash = Hash(bytes);
                if (_knownHash is not null && _knownHash.AsSpan().SequenceEqual(hash))
                {
                    return; // Our own write, or an edit that did not change the content.
                }

                var previousRoot = _root;
                try
                {
                    ApplyBytes(bytes);
                }
                catch (Exception ex)
                {
                    _root = previousRoot;
                    LogReloadFailed(_logger, ex.Message);
                    return;
                }
                finally
                {
                    // Do not retry the same content over and over; a later edit has a different hash.
                    _knownHash = hash;
                }
            }

            RaiseChanged(SettingsChangeSource.External);
        }
        catch (Exception ex)
        {
            LogReloadFailed(_logger, ex.Message);
        }
    }

    private void RaiseChanged(SettingsChangeSource source)
    {
        var handlers = Changed;
        if (handlers is null)
        {
            return;
        }

        var args = new SettingsChangedEventArgs(source);
        foreach (var handler in handlers.GetInvocationList().Cast<EventHandler<SettingsChangedEventArgs>>())
        {
            try
            {
                handler(this, args);
            }
            catch (Exception ex)
            {
                LogHandlerFailed(_logger, ex);
            }
        }
    }
#pragma warning restore CA1031

    [LoggerMessage(Level = LogLevel.Information, Message = "Created default settings at '{Path}'.")]
    private static partial void LogCreated(ILogger logger, string path);

    [LoggerMessage(Level = LogLevel.Information, Message = "Migrating settings from version {From} to {To}; the original is saved as '{Backup}'.")]
    private static partial void LogMigrating(ILogger logger, int from, int to, string backup);

    [LoggerMessage(Level = LogLevel.Warning, Message = "settings.json changed on disk but could not be reloaded; keeping the settings in memory. {Reason}")]
    private static partial void LogReloadFailed(ILogger logger, string reason);

    [LoggerMessage(Level = LogLevel.Error, Message = "A settings Changed handler threw an exception.")]
    private static partial void LogHandlerFailed(ILogger logger, Exception exception);
}

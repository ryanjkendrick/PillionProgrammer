using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Pillion.Common;
using Pillion.Common.Settings;

namespace Pillion.LocalStore.Conversations;

/// <summary>
/// Opens <c>conversations.db</c> on first use and hands out its repository. The <c>localStore</c> feature flag is checked
/// on every call: while it is off, nothing is created or opened and the call fails with a policy error.
/// </summary>
public interface IConversationStore : IAsyncDisposable
{
    /// <summary>Gets the repository, opening (and creating or migrating) the database on first use.</summary>
    /// <param name="cancellationToken">Cancels the open.</param>
    /// <returns>The repository.</returns>
    /// <exception cref="LocalStoreException">The feature is off, or the database cannot be opened; a later call tries again.</exception>
    Task<IConversationRepository> GetRepositoryAsync(CancellationToken cancellationToken = default);
}

/// <summary>The <see cref="IConversationStore"/> for <c>%LOCALAPPDATA%\PillionProgrammer\conversations.db</c>.</summary>
public sealed class ConversationStore : IConversationStore
{
    /// <summary>The message of the error returned while the <see cref="FeatureNames.LocalStore"/> feature is off.</summary>
    public const string DisabledMessage =
        "Pillion's Local Store is turned off by settings or policy (feature localStore), so conversations are not saved. "
        + "Set features.localStore to true in settings.json, or remove it from disabledFeatures in the repository policy.";

    private readonly PillionPaths _paths;
    private readonly IFeatureFlags _features;
    private readonly ILoggerFactory _loggerFactory;
    private readonly SemaphoreSlim _openGate = new(1, 1);
    private EncryptedDatabase? _database;
    private ConversationRepository? _repository;
    private bool _disposed;

    /// <summary>Initializes a new instance of the <see cref="ConversationStore"/> class.</summary>
    /// <param name="paths">The data layout (the database is <see cref="PillionPaths.ConversationsDatabase"/>).</param>
    /// <param name="features">The feature flags.</param>
    /// <param name="loggerFactory">The logger factory.</param>
    public ConversationStore(PillionPaths paths, IFeatureFlags features, ILoggerFactory? loggerFactory = null)
    {
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentNullException.ThrowIfNull(features);
        _paths = paths;
        _features = features;
        _loggerFactory = loggerFactory ?? NullLoggerFactory.Instance;
    }

    /// <summary>Gets the open database, or <see langword="null"/> before the first successful <see cref="GetRepositoryAsync"/>.</summary>
    public EncryptedDatabase? Database => Volatile.Read(ref _database);

    /// <inheritdoc/>
    public async Task<IConversationRepository> GetRepositoryAsync(CancellationToken cancellationToken = default)
    {
        if (!_features.IsEnabled(FeatureNames.LocalStore))
        {
            throw new LocalStoreException(
                LocalStoreErrorKind.FeatureDisabled,
                DisabledMessage,
                "Turn the localStore feature back on to save conversations.");
        }

        if (Volatile.Read(ref _repository) is { } repository)
        {
            return repository;
        }

        await _openGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_repository is not null)
            {
                return _repository;
            }

            _paths.EnsureCreated();
            var database = await EncryptedDatabase.OpenAsync(
                new EncryptedDatabaseOptions
                {
                    DatabasePath = _paths.ConversationsDatabase,
                    KeyFilePath = _paths.ConversationsKeyFile,
                },
                ConversationsSchema.Schema,
                _loggerFactory.CreateLogger<EncryptedDatabase>(),
                cancellationToken).ConfigureAwait(false);

            Volatile.Write(ref _database, database);
            var opened = new ConversationRepository(database);
            Volatile.Write(ref _repository, opened);
            return opened;
        }
        finally
        {
            _openGate.Release();
        }
    }

    /// <inheritdoc/>
    public async ValueTask DisposeAsync()
    {
        await _openGate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            Volatile.Write(ref _repository, null);
            var database = Interlocked.Exchange(ref _database, null);
            if (database is not null)
            {
                await database.DisposeAsync().ConfigureAwait(false);
            }
        }
        finally
        {
            _openGate.Release();
        }
    }
}

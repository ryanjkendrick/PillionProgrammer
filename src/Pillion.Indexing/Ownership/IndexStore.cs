using Pillion.Indexing.Storage;
using Pillion.LocalStore;

namespace Pillion.Indexing.Ownership;

/// <summary>Whether a host owns an index or only reads it.</summary>
public enum IndexRole
{
    /// <summary>This host took <c>writer.lock</c>: it runs the indexer and the watcher and may write.</summary>
    Writer,

    /// <summary>Another host owns the index: this one has the database read-only and forwards update requests to the owner.</summary>
    Reader,
}

/// <summary>
/// Where to send update requests for an index: the pipe of the host that owns it (design section 10). Item 2.9
/// forwards file-change and rebuild requests here.
/// </summary>
/// <param name="PipeName">The owning host's named pipe, as recorded in <c>writer.lock</c>.</param>
/// <param name="ProcessId">The owning process id.</param>
/// <param name="ProcessStartUtc">When the owning process started.</param>
/// <param name="IsSelf">Whether the owner is this very process (a second store on the same index in one process): requests then go in-process.</param>
public sealed record IndexWriterEndpoint(string PipeName, int ProcessId, DateTimeOffset ProcessStartUtc, bool IsSelf);

/// <summary>
/// One open index: its encrypted database, the typed <c>files</c> repository, and the role this host has. Obtain it from
/// <see cref="IndexStoreFactory"/>; dispose it to close the database and, for the writer, release <c>writer.lock</c>.
/// </summary>
public sealed class IndexStore : IAsyncDisposable
{
    private readonly WriterLock? _lock;
    private readonly IProcessProbe _probe;
    private readonly TimeProvider _time;
    private readonly object _stateGate = new();
    private IndexState _state;
    private IndexWriterEndpoint _writer;
    private int _disposed;

    internal IndexStore(
        IndexIdentity identity,
        IndexFolder folder,
        EncryptedDatabase database,
        FilesRepository files,
        WriterLock? writerLock,
        IndexWriterEndpoint writer,
        IndexState state,
        IProcessProbe probe,
        TimeProvider time)
    {
        Identity = identity;
        Folder = folder;
        Database = database;
        Files = files;
        _lock = writerLock;
        _writer = writer;
        _state = state;
        _probe = probe;
        _time = time;
    }

    /// <summary>Gets the identity (root, model, chunker) the index is keyed by.</summary>
    public IndexIdentity Identity { get; }

    /// <summary>Gets the index folder.</summary>
    public IndexFolder Folder { get; }

    /// <summary>Gets the open database; read-only unless this host is the <see cref="IndexRole.Writer"/>.</summary>
    public EncryptedDatabase Database { get; }

    /// <summary>Gets the <c>files</c> table.</summary>
    public FilesRepository Files { get; }

    /// <summary>Gets this host's role.</summary>
    public IndexRole Role => _lock is null ? IndexRole.Reader : IndexRole.Writer;

    /// <summary>Gets the endpoint of the host that owns the index: this host's own pipe for the writer, the writer's pipe for a reader.</summary>
    public IndexWriterEndpoint Writer => Volatile.Read(ref _writer);

    /// <summary>Gets the state as this host last read or wrote it.</summary>
    public IndexState State
    {
        get
        {
            lock (_stateGate)
            {
                return _state;
            }
        }
    }

    /// <summary>
    /// Re-reads <c>writer.lock</c> and reports the owner if it is alive. A reader calls this when forwarding fails, to
    /// learn whether the writer died (then it reopens the index through the factory, which reclaims the stale lock).
    /// </summary>
    /// <returns>The live owner, or <see langword="null"/> when there is none.</returns>
    public IndexWriterEndpoint? GetLiveWriter()
    {
        if (_lock is not null)
        {
            return Writer;
        }

        if (WriterLock.Read(Folder.LockFile) is { } info && _probe.IsAlive(info.ProcessId, info.ProcessStartUtc))
        {
            var endpoint = new IndexWriterEndpoint(info.PipeName, info.ProcessId, info.ProcessStartUtc, info.ProcessId == _probe.CurrentProcessId);
            Volatile.Write(ref _writer, endpoint);
            return endpoint;
        }

        return null;
    }

    /// <summary>Records that a full hash sweep finished now (writer only).</summary>
    /// <returns>The new state.</returns>
    /// <exception cref="InvalidOperationException">This host is only a reader.</exception>
    public IndexState RecordFullSweep()
    {
        RequireWriter();
        return UpdateState(state => state with { LastFullSweepUtc = _time.GetUtcNow() });
    }

    /// <inheritdoc/>
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        try
        {
            if (_lock is not null)
            {
                // The last open time is what retention measures from.
                UpdateState(state => state with { LastOpenedUtc = _time.GetUtcNow() });
            }
        }
        catch (IOException)
        {
            // state.json is advisory; a failed write must not keep the lock.
        }
        finally
        {
            await Database.DisposeAsync().ConfigureAwait(false);
            _lock?.Dispose();
        }
    }

    private IndexState UpdateState(Func<IndexState, IndexState> change)
    {
        lock (_stateGate)
        {
            _state = change(_state);
            IndexStateFile.Write(Folder.StateFile, _state);
            return _state;
        }
    }

    private void RequireWriter()
    {
        if (_lock is null)
        {
            throw new InvalidOperationException("This host does not own the index (another host holds writer.lock); only the writer changes it.");
        }
    }
}

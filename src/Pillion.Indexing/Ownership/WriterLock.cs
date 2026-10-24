using System.Text.Json;
using System.Text.Json.Serialization;

namespace Pillion.Indexing.Ownership;

/// <summary>
/// The content of <c>writer.lock</c>: who owns the index (design section 10). The owner runs the indexer and the watcher;
/// any other host sends its update requests to <see cref="PipeName"/>.
/// </summary>
/// <param name="ProcessId">The owning host's process id.</param>
/// <param name="ProcessStartUtc">When the owning process started, so a reused process id is not mistaken for the owner.</param>
/// <param name="PipeName">The owning host's named pipe, the endpoint other hosts forward update requests to.</param>
/// <param name="AcquiredUtc">When the lock was taken.</param>
/// <param name="Token">A random value that tells this acquisition from any other by the same process.</param>
public sealed record WriterLockInfo(int ProcessId, DateTimeOffset ProcessStartUtc, string PipeName, DateTimeOffset AcquiredUtc, string Token);

/// <summary>The outcome of trying to take a writer lock.</summary>
public sealed class WriterLockAttempt
{
    private WriterLockAttempt(WriterLock? acquired, WriterLockInfo? owner)
    {
        Lock = acquired;
        Owner = owner;
    }

    /// <summary>Gets the lock this caller now holds, or <see langword="null"/> when another host owns the index.</summary>
    public WriterLock? Lock { get; }

    /// <summary>Gets the live owner when the lock was not acquired.</summary>
    public WriterLockInfo? Owner { get; }

    /// <summary>Gets a value indicating whether this caller took the lock.</summary>
    public bool Acquired => Lock is not null;

    internal static WriterLockAttempt Won(WriterLock held) => new(held, null);

    internal static WriterLockAttempt Lost(WriterLockInfo owner) => new(null, owner);
}

/// <summary>
/// The election for one index: a <c>writer.lock</c> file created with create-new semantics, so exactly one process wins
/// when several try at once. The content is written to a temporary file and moved into place without overwriting, so a
/// reader never sees a half-written lock. A lock whose process id no longer exists, or whose process started at another
/// time (the id was reused), or which cannot be parsed, is stale and is reclaimed; reclaiming is serialised by a short
/// guard file so two hosts cannot both delete a stale lock and then each remove the other's fresh one.
/// </summary>
public sealed class WriterLock : IDisposable
{
    private const int FormatVersion = 1;
    private const int MaxAttempts = 200;

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private int _released;

    private WriterLock(string path, WriterLockInfo info)
    {
        Path = path;
        Info = info;
    }

    /// <summary>Gets the lock file's path.</summary>
    public string Path { get; }

    /// <summary>Gets what this lock says.</summary>
    public WriterLockInfo Info { get; }

    /// <summary>
    /// Tries to become the owner: creates the lock if there is none, and takes over a stale one. When a live process owns
    /// it, reports that owner instead.
    /// </summary>
    /// <param name="lockPath">The <c>writer.lock</c> path; its folder must exist.</param>
    /// <param name="pipeName">This host's pipe name, recorded in the lock.</param>
    /// <param name="probe">Decides whether a lock's process is alive; the real one by default.</param>
    /// <param name="time">The clock for the acquisition time.</param>
    /// <returns>Either the held lock or the live owner.</returns>
    /// <exception cref="IOException">The lock could not be settled (the guard stayed taken, or the file system refused).</exception>
    public static WriterLockAttempt TryAcquire(string lockPath, string pipeName, IProcessProbe? probe = null, TimeProvider? time = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(lockPath);
        ArgumentNullException.ThrowIfNull(pipeName);

        probe ??= ProcessProbe.Instance;
        time ??= TimeProvider.System;
        var info = new WriterLockInfo(probe.CurrentProcessId, probe.CurrentProcessStart, pipeName, time.GetUtcNow(), Guid.NewGuid().ToString("N"));
        var path = System.IO.Path.GetFullPath(lockPath);

        for (var attempt = 0; attempt < MaxAttempts; attempt++)
        {
            if (TryCreate(path, info))
            {
                return WriterLockAttempt.Won(new WriterLock(path, info));
            }

            var existing = TryRead(path);
            if (existing.State == LockState.Missing)
            {
                continue;
            }

            if (existing.State == LockState.Unavailable)
            {
                Thread.Sleep(20);
                continue;
            }

            if (existing.Info is { } owner && probe.IsAlive(owner.ProcessId, owner.ProcessStartUtc))
            {
                return WriterLockAttempt.Lost(owner);
            }

            // Stale or unreadable: take it over, but only while holding the reclaim guard, and only after reading it again
            // under the guard (another host may have reclaimed and re-created it in the meantime).
            using var guard = TryEnterReclaimGuard(path);
            if (guard is null)
            {
                Thread.Sleep(20);
                continue;
            }

            var again = TryRead(path);
            if (again.State == LockState.Corrupt || (again.State == LockState.Valid && !probe.IsAlive(again.Info!.ProcessId, again.Info.ProcessStartUtc)))
            {
                try
                {
                    File.Delete(path);
                }
                catch (IOException)
                {
                    // Opened by a reader at this instant; the next attempt tries again.
                }
            }
        }

        throw new IOException($"The writer lock '{path}' could not be settled after {MaxAttempts} attempts.");
    }

    /// <summary>Reads a lock file without taking it.</summary>
    /// <param name="lockPath">The <c>writer.lock</c> path.</param>
    /// <returns>The lock's content, or <see langword="null"/> when there is no lock or it cannot be parsed. Whether the owner is alive is not checked.</returns>
    public static WriterLockInfo? Read(string lockPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(lockPath);
        return TryRead(System.IO.Path.GetFullPath(lockPath)).Info;
    }

    /// <summary>
    /// Checks that the lock file still carries this acquisition's token (work item 2.10: the watcher stops when its host
    /// loses the lock). A file that cannot be read right now (a scanner has it open) counts as still held; a missing,
    /// corrupt or foreign lock does not.
    /// </summary>
    /// <returns><see langword="true"/> while this host still owns the lock.</returns>
    public bool IsHeld()
    {
        if (Volatile.Read(ref _released) != 0)
        {
            return false;
        }

        var (state, current) = TryRead(Path);
        return state == LockState.Unavailable || (current is not null && string.Equals(current.Token, Info.Token, StringComparison.Ordinal));
    }

    /// <summary>
    /// Gives the lock up: deletes the file if it still carries this acquisition's token. If another host reclaimed it in
    /// the meantime, that host's lock is left alone.
    /// </summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _released, 1) != 0)
        {
            return;
        }

        if (TryRead(Path).Info is { } current && string.Equals(current.Token, Info.Token, StringComparison.Ordinal))
        {
            try
            {
                File.Delete(Path);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Best effort; a stale lock is reclaimed by the next host.
            }
        }
    }

    private static bool TryCreate(string path, WriterLockInfo info)
    {
        var temp = $"{path}.{Guid.NewGuid():N}.tmp";
        try
        {
            using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                JsonSerializer.Serialize(stream, new LockFile(FormatVersion, info.ProcessId, info.ProcessStartUtc, info.PipeName, info.AcquiredUtc, info.Token), Json);
                stream.Flush(flushToDisk: true);
            }

            // Fails if the lock exists: that is the atomic create-new.
            File.Move(temp, path, overwrite: false);
            return true;
        }
        catch (IOException ex) when (IsAlreadyExists(ex) || File.Exists(path))
        {
            // Another host holds the lock (or just did and has already been reclaimed by a third: the next read tells).
            return false;
        }
        finally
        {
            try
            {
                File.Delete(temp);
            }
            catch (IOException)
            {
                // Leftover temp files are harmless.
            }
        }
    }

    /// <summary>Whether a failed move says that the destination exists (ERROR_FILE_EXISTS 80 or ERROR_ALREADY_EXISTS 183).</summary>
    private static bool IsAlreadyExists(IOException ex) => (ex.HResult & 0xFFFF) is 80 or 183;

    private static (LockState State, WriterLockInfo? Info) TryRead(string path)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var file = JsonSerializer.Deserialize<LockFile>(stream, Json);
            if (file is null || file.ProcessId <= 0 || string.IsNullOrEmpty(file.Token))
            {
                return (LockState.Corrupt, null);
            }

            return (LockState.Valid, new WriterLockInfo(file.ProcessId, file.ProcessStartUtc, file.PipeName ?? string.Empty, file.AcquiredUtc, file.Token));
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            return (LockState.Missing, null);
        }
        catch (JsonException)
        {
            return (LockState.Corrupt, null);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Busy for a moment (a scanner, an editor): never mistaken for a stale lock.
            return (LockState.Unavailable, null);
        }
    }

    /// <summary>Takes the short-lived guard that serialises reclaiming; the OS deletes the file when the handle closes, even after a crash.</summary>
    private static FileStream? TryEnterReclaimGuard(string lockPath)
    {
        var guardPath = lockPath + ".reclaim";
        try
        {
            return new FileStream(guardPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1, FileOptions.DeleteOnClose);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Taken by another host (while it is being deleted on close, Windows answers "access denied"). A guard left
            // behind by a machine crash is old; remove it so reclaiming is not stuck.
            try
            {
                if (File.Exists(guardPath) && DateTime.UtcNow - File.GetLastWriteTimeUtc(guardPath) > TimeSpan.FromMinutes(1))
                {
                    File.Delete(guardPath);
                }
            }
            catch (Exception inner) when (inner is IOException or UnauthorizedAccessException)
            {
                // Still in use.
            }

            return null;
        }
    }

    private enum LockState
    {
        Missing,
        Valid,
        Corrupt,
        Unavailable,
    }

    private sealed record LockFile(int Version, int ProcessId, DateTimeOffset ProcessStartUtc, string? PipeName, DateTimeOffset AcquiredUtc, string Token);
}

using System.ComponentModel;
using System.Diagnostics;

namespace Pillion.Indexing.Ownership;

/// <summary>Tells whether the process that wrote a lock is still the one running (so tests can simulate dead owners).</summary>
public interface IProcessProbe
{
    /// <summary>Gets the process id of the current process.</summary>
    int CurrentProcessId { get; }

    /// <summary>Gets the start time of the current process (UTC), as <see cref="IsAlive"/> will compare it.</summary>
    DateTimeOffset CurrentProcessStart { get; }

    /// <summary>Determines whether a process with this id exists and started at the recorded time.</summary>
    /// <param name="processId">The recorded process id.</param>
    /// <param name="startUtc">The recorded start time (UTC).</param>
    /// <returns><see langword="false"/> when no such process exists or the id was reused by a process that started at another time.</returns>
    bool IsAlive(int processId, DateTimeOffset startUtc);
}

/// <summary>The <see cref="IProcessProbe"/> over <see cref="Process"/>: a lock is live when its process id exists and started within two seconds of the recorded time.</summary>
public sealed class ProcessProbe : IProcessProbe
{
    private static readonly TimeSpan Tolerance = TimeSpan.FromSeconds(2);

    /// <summary>Gets the shared instance.</summary>
    public static ProcessProbe Instance { get; } = new();

    /// <inheritdoc/>
    public int CurrentProcessId => Environment.ProcessId;

    /// <inheritdoc/>
    public DateTimeOffset CurrentProcessStart
    {
        get
        {
            using var self = Process.GetCurrentProcess();
            return new DateTimeOffset(self.StartTime.ToUniversalTime());
        }
    }

    /// <inheritdoc/>
    public bool IsAlive(int processId, DateTimeOffset startUtc)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            if (process.HasExited)
            {
                return false;
            }

            var started = new DateTimeOffset(process.StartTime.ToUniversalTime());
            return (started - startUtc).Duration() <= Tolerance;
        }
        catch (ArgumentException)
        {
            // No process has this id.
            return false;
        }
        catch (InvalidOperationException)
        {
            // It exited while being inspected.
            return false;
        }
        catch (Win32Exception)
        {
            // The process exists but belongs to someone else, so its start time cannot be read: assume it is the owner.
            return true;
        }
    }
}

using System.Runtime.InteropServices;

namespace Pillion.CommandRunner.Native;

/// <summary>Reads the process table (Toolhelp) to find a process's descendants and to tell whether a process is alive.</summary>
internal static class ProcessTree
{
    /// <summary>Lists the process ids of every descendant of a process, children first.</summary>
    /// <param name="processId">The root process.</param>
    /// <returns>The descendants' ids; empty when the process has none or has exited.</returns>
    public static IReadOnlyList<int> GetDescendants(int processId)
    {
        var parents = Snapshot();
        var result = new List<int>();
        var queue = new Queue<int>();
        queue.Enqueue(processId);
        while (queue.Count > 0)
        {
            var parent = queue.Dequeue();
            foreach (var (child, parentOfChild) in parents)
            {
                if (parentOfChild == parent && child != parent && !result.Contains(child))
                {
                    result.Add(child);
                    queue.Enqueue(child);
                }
            }
        }

        return result;
    }

    /// <summary>Determines whether a process is still running.</summary>
    /// <param name="processId">The process id.</param>
    /// <returns><see langword="true"/> while it runs; <see langword="false"/> once it has exited or when it cannot be opened.</returns>
    public static bool IsAlive(int processId)
    {
        using var handle = NativeMethods.OpenProcess(NativeMethods.PROCESS_QUERY_LIMITED_INFORMATION | NativeMethods.SYNCHRONIZE, bInheritHandle: false, (uint)processId);
        if (handle.IsInvalid)
        {
            // Access denied means it exists but is not ours; anything else means it is gone.
            return Marshal.GetLastPInvokeError() == 5;
        }

        return NativeMethods.WaitForSingleObject(handle, 0) == NativeMethods.WAIT_TIMEOUT;
    }

    /// <summary>Reads (process id, parent process id) for every process.</summary>
    private static unsafe List<(int ProcessId, int ParentProcessId)> Snapshot()
    {
        var result = new List<(int, int)>();
        using var snapshot = NativeMethods.CreateToolhelp32Snapshot(NativeMethods.TH32CS_SNAPPROCESS, 0);
        if (snapshot.IsInvalid)
        {
            return result;
        }

        var entry = new NativeMethods.PROCESSENTRY32W { dwSize = (uint)sizeof(NativeMethods.PROCESSENTRY32W) };
        if (!NativeMethods.Process32FirstW(snapshot, ref entry))
        {
            return result;
        }

        do
        {
            result.Add(((int)entry.th32ProcessID, (int)entry.th32ParentProcessID));
        }
        while (NativeMethods.Process32NextW(snapshot, ref entry));

        return result;
    }
}

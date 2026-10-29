using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Pillion.CommandRunner.Native;

/// <summary>
/// A kill-on-close Job Object (<c>JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE</c>). <see cref="Terminate"/> kills every process in
/// it at once, children and grandchildren included; disposing the last handle does the same, so nothing a session
/// started outlives the session when the host dies. Jobs nest: a process already in the host's own job (the one devenv.exe
/// put it in) can be added to a session job, and a process in the session job to a per-run job, so terminating the run's
/// job kills one command's tree and terminating the session's kills everything the session started.
/// </summary>
internal sealed class JobObject : IDisposable
{
    private readonly SafeJobHandle _handle;

    private JobObject(SafeJobHandle handle)
    {
        _handle = handle;
    }

    /// <summary>Creates an unnamed kill-on-close job.</summary>
    /// <returns>The job.</returns>
    /// <exception cref="Win32Exception">The job could not be created or configured.</exception>
    public static JobObject Create()
    {
        var handle = NativeMethods.CreateJobObjectW(IntPtr.Zero, lpName: null);
        if (handle.IsInvalid)
        {
            var error = Marshal.GetLastPInvokeError();
            handle.Dispose();
            throw new Win32Exception(error, "CreateJobObject failed.");
        }

        var info = new NativeMethods.JOBOBJECT_EXTENDED_LIMIT_INFORMATION();
        info.BasicLimitInformation.LimitFlags = NativeMethods.JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE;
        if (!NativeMethods.SetInformationJobObject(
            handle,
            NativeMethods.JobObjectExtendedLimitInformation,
            ref info,
            (uint)Marshal.SizeOf<NativeMethods.JOBOBJECT_EXTENDED_LIMIT_INFORMATION>()))
        {
            var error = Marshal.GetLastPInvokeError();
            handle.Dispose();
            throw new Win32Exception(error, "SetInformationJobObject failed.");
        }

        return new JobObject(handle);
    }

    /// <summary>Assigns a process (and, from then on, its descendants) to the job.</summary>
    /// <param name="process">The process handle.</param>
    /// <exception cref="Win32Exception">The assignment failed.</exception>
    public void Assign(SafeProcessHandle process)
    {
        ArgumentNullException.ThrowIfNull(process);
        if (!NativeMethods.AssignProcessToJobObject(_handle, process))
        {
            throw new Win32Exception(Marshal.GetLastPInvokeError(), "AssignProcessToJobObject failed.");
        }
    }

    /// <summary>Determines whether a process is in this job.</summary>
    /// <param name="process">The process handle.</param>
    /// <returns><see langword="true"/> when it is.</returns>
    public bool Contains(SafeProcessHandle process) =>
        NativeMethods.IsProcessInJob(process, _handle, out var result) && result;

    /// <summary>Kills every process in the job. Safe to call more than once and after a process has already exited.</summary>
    public void Terminate()
    {
        if (!_handle.IsClosed && !_handle.IsInvalid)
        {
            _ = NativeMethods.TerminateJobObject(_handle, 1);
        }
    }

    /// <summary>Closes the job, which kills every process still in it.</summary>
    public void Dispose() => _handle.Dispose();
}

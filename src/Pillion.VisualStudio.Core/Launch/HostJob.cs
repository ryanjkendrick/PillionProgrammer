using System;
using System.ComponentModel;
using System.Runtime.InteropServices;

namespace Pillion.VisualStudio.Core;

/// <summary>
/// A Win32 job object with <c>JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE</c>: when the last handle closes (the extension
/// disposes it, or devenv.exe exits for any reason), Windows kills every process in the job, so the host and anything
/// it starts die with devenv.exe. One job per host launch, so terminating it kills exactly one host process tree.
/// </summary>
internal sealed class HostJob : IDisposable
{
    private readonly SafeKernelHandle _handle;

    private HostJob(SafeKernelHandle handle)
    {
        _handle = handle;
    }

    /// <summary>Creates an unnamed kill-on-close job.</summary>
    /// <returns>The job.</returns>
    /// <exception cref="Win32Exception">The job could not be created or configured.</exception>
    public static HostJob Create()
    {
        var handle = NativeMethods.CreateJobObjectW(IntPtr.Zero, lpName: null);
        if (handle.IsInvalid)
        {
            var error = Marshal.GetLastWin32Error();
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
            var error = Marshal.GetLastWin32Error();
            handle.Dispose();
            throw new Win32Exception(error, "SetInformationJobObject failed.");
        }

        return new HostJob(handle);
    }

    /// <summary>Assigns a process to the job.</summary>
    /// <param name="process">The process.</param>
    /// <param name="error">The Win32 error when the assignment fails.</param>
    /// <returns><see langword="true"/> when the process is now in the job.</returns>
    public bool TryAssign(HostProcess process, out int error)
    {
        if (process is null)
        {
            throw new ArgumentNullException(nameof(process));
        }

        if (NativeMethods.AssignProcessToJobObject(_handle, process.Handle))
        {
            error = 0;
            return true;
        }

        error = Marshal.GetLastWin32Error();
        return false;
    }

    /// <summary>Determines whether a process is in this job.</summary>
    /// <param name="process">The process.</param>
    /// <returns><see langword="true"/> when it is.</returns>
    public bool Contains(HostProcess process) =>
        process is not null && NativeMethods.IsProcessInJob(process.Handle, _handle, out var result) && result;

    /// <summary>Kills every process in the job.</summary>
    public void Terminate()
    {
        if (!_handle.IsClosed)
        {
            _ = NativeMethods.TerminateJobObject(_handle, 1);
        }
    }

    /// <summary>Closes the job, which kills every process still in it.</summary>
    public void Dispose() => _handle.Dispose();
}

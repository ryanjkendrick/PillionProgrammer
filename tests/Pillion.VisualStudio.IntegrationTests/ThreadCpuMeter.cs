using System;
using System.ComponentModel;
using System.Runtime.InteropServices;

namespace Pillion.VisualStudio.IntegrationTests;

/// <summary>
/// Reads the CPU time (user plus kernel) one thread has used. Taken on the UI thread before and after a window, the
/// difference says how busy that thread was: a long gap with little CPU time means the thread was blocked or starved,
/// not computing; a long gap with a lot means something ran on it.
/// </summary>
public sealed class ThreadCpuMeter : IDisposable
{
    private const uint ThreadQueryLimitedInformation = 0x0800;

    private IntPtr _handle;

    private ThreadCpuMeter(IntPtr handle)
    {
        _handle = handle;
    }

    /// <summary>Gets the native id of the calling thread (to meter it later from another thread).</summary>
    public static uint CurrentThreadId => NativeMethods.GetCurrentThreadId();

    /// <summary>Opens a meter for the calling thread.</summary>
    /// <returns>The meter; dispose it.</returns>
    public static ThreadCpuMeter ForCurrentThread() => ForThread(NativeMethods.GetCurrentThreadId());

    /// <summary>Opens a meter for a thread of this process, which another thread can then read.</summary>
    /// <param name="threadId">The native thread id (<see cref="CurrentThreadId"/> read on that thread).</param>
    /// <returns>The meter; dispose it.</returns>
    public static ThreadCpuMeter ForThread(uint threadId)
    {
        var handle = NativeMethods.OpenThread(ThreadQueryLimitedInformation, bInheritHandle: false, threadId);
        if (handle == IntPtr.Zero)
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "OpenThread failed.");
        }

        return new ThreadCpuMeter(handle);
    }

    /// <summary>Reads the CPU time the thread has used so far.</summary>
    /// <returns>User plus kernel time.</returns>
    public TimeSpan Read()
    {
        if (!NativeMethods.GetThreadTimes(_handle, out _, out _, out var kernel, out var user))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "GetThreadTimes failed.");
        }

        return TimeSpan.FromTicks(((long)kernel.dwHighDateTime << 32 | kernel.dwLowDateTime) + ((long)user.dwHighDateTime << 32 | user.dwLowDateTime));
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        if (_handle != IntPtr.Zero)
        {
            _ = NativeMethods.CloseHandle(_handle);
            _handle = IntPtr.Zero;
        }
    }

    private static class NativeMethods
    {
        [DllImport("kernel32.dll")]
        public static extern uint GetCurrentThreadId();

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern IntPtr OpenThread(uint dwDesiredAccess, [MarshalAs(UnmanagedType.Bool)] bool bInheritHandle, uint dwThreadId);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool GetThreadTimes(IntPtr hThread, out FileTime lpCreationTime, out FileTime lpExitTime, out FileTime lpKernelTime, out FileTime lpUserTime);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool CloseHandle(IntPtr hObject);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FileTime
    {
        public uint dwLowDateTime;
        public uint dwHighDateTime;
    }
}

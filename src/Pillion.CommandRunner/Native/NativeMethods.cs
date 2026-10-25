using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Pillion.CommandRunner.Native;

/// <summary>The kernel32 functions behind the pseudo-console, process creation, Job Objects and the process snapshot.</summary>
internal static unsafe partial class NativeMethods
{
    public const uint PROC_THREAD_ATTRIBUTE_PSEUDOCONSOLE = 0x0002_0016;
    public const uint EXTENDED_STARTUPINFO_PRESENT = 0x0008_0000;
    public const uint CREATE_UNICODE_ENVIRONMENT = 0x0000_0400;
    public const uint CREATE_SUSPENDED = 0x0000_0004;
    public const uint CREATE_NO_WINDOW = 0x0800_0000;
    public const uint STILL_ACTIVE = 259;
    public const uint JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE = 0x0000_2000;
    public const int JobObjectExtendedLimitInformation = 9;
    public const uint TH32CS_SNAPPROCESS = 0x0000_0002;
    public const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;
    public const uint SYNCHRONIZE = 0x0010_0000;
    public const uint WAIT_OBJECT_0 = 0;
    public const uint WAIT_TIMEOUT = 258;

    [StructLayout(LayoutKind.Sequential)]
    public struct COORD
    {
        public short X;
        public short Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct STARTUPINFOW
    {
        public uint cb;
        public IntPtr lpReserved;
        public IntPtr lpDesktop;
        public IntPtr lpTitle;
        public uint dwX;
        public uint dwY;
        public uint dwXSize;
        public uint dwYSize;
        public uint dwXCountChars;
        public uint dwYCountChars;
        public uint dwFillAttribute;
        public uint dwFlags;
        public ushort wShowWindow;
        public ushort cbReserved2;
        public IntPtr lpReserved2;
        public IntPtr hStdInput;
        public IntPtr hStdOutput;
        public IntPtr hStdError;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct STARTUPINFOEXW
    {
        public STARTUPINFOW StartupInfo;
        public IntPtr lpAttributeList;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct PROCESS_INFORMATION
    {
        public IntPtr hProcess;
        public IntPtr hThread;
        public uint dwProcessId;
        public uint dwThreadId;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct JOBOBJECT_BASIC_LIMIT_INFORMATION
    {
        public long PerProcessUserTimeLimit;
        public long PerJobUserTimeLimit;
        public uint LimitFlags;
        public UIntPtr MinimumWorkingSetSize;
        public UIntPtr MaximumWorkingSetSize;
        public uint ActiveProcessLimit;
        public UIntPtr Affinity;
        public uint PriorityClass;
        public uint SchedulingClass;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct IO_COUNTERS
    {
        public ulong ReadOperationCount;
        public ulong WriteOperationCount;
        public ulong OtherOperationCount;
        public ulong ReadTransferCount;
        public ulong WriteTransferCount;
        public ulong OtherTransferCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct JOBOBJECT_EXTENDED_LIMIT_INFORMATION
    {
        public JOBOBJECT_BASIC_LIMIT_INFORMATION BasicLimitInformation;
        public IO_COUNTERS IoInfo;
        public UIntPtr ProcessMemoryLimit;
        public UIntPtr JobMemoryLimit;
        public UIntPtr PeakProcessMemoryUsed;
        public UIntPtr PeakJobMemoryUsed;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct PROCESSENTRY32W
    {
        public uint dwSize;
        public uint cntUsage;
        public uint th32ProcessID;
        public UIntPtr th32DefaultHeapID;
        public uint th32ModuleID;
        public uint cntThreads;
        public uint th32ParentProcessID;
        public int pcPriClassBase;
        public uint dwFlags;
        public fixed char szExeFile[260];
    }

    // ---- pipes and the pseudo-console ----

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool CreatePipe(out SafeFileHandle hReadPipe, out SafeFileHandle hWritePipe, IntPtr lpPipeAttributes, uint nSize);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    public static partial int CreatePseudoConsole(COORD size, SafeFileHandle hInput, SafeFileHandle hOutput, uint dwFlags, out IntPtr phPC);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    public static partial int ResizePseudoConsole(SafePseudoConsoleHandle hPC, COORD size);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    public static partial void ClosePseudoConsole(IntPtr hPC);

    // ---- process creation ----

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool InitializeProcThreadAttributeList(IntPtr lpAttributeList, int dwAttributeCount, uint dwFlags, ref nint lpSize);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool UpdateProcThreadAttribute(IntPtr lpAttributeList, uint dwFlags, nuint attribute, IntPtr lpValue, nint cbSize, IntPtr lpPreviousValue, IntPtr lpReturnSize);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    public static partial void DeleteProcThreadAttributeList(IntPtr lpAttributeList);

    [LibraryImport("kernel32.dll", EntryPoint = "CreateProcessW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool CreateProcessW(
        string? lpApplicationName,
        char* lpCommandLine,
        IntPtr lpProcessAttributes,
        IntPtr lpThreadAttributes,
        [MarshalAs(UnmanagedType.Bool)] bool bInheritHandles,
        uint dwCreationFlags,
        char* lpEnvironment,
        string? lpCurrentDirectory,
        ref STARTUPINFOEXW lpStartupInfo,
        out PROCESS_INFORMATION lpProcessInformation);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    public static partial uint ResumeThread(IntPtr hThread);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool CloseHandle(IntPtr hObject);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool GetExitCodeProcess(SafeProcessHandle hProcess, out uint lpExitCode);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool TerminateProcess(SafeProcessHandle hProcess, uint uExitCode);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    public static partial SafeProcessHandle OpenProcess(uint dwDesiredAccess, [MarshalAs(UnmanagedType.Bool)] bool bInheritHandle, uint dwProcessId);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    public static partial uint WaitForSingleObject(SafeProcessHandle hHandle, uint dwMilliseconds);

    // ---- Job Objects ----

    [LibraryImport("kernel32.dll", EntryPoint = "CreateJobObjectW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    public static partial SafeJobHandle CreateJobObjectW(IntPtr lpJobAttributes, string? lpName);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool SetInformationJobObject(SafeJobHandle hJob, int jobObjectInformationClass, ref JOBOBJECT_EXTENDED_LIMIT_INFORMATION lpJobObjectInformation, uint cbJobObjectInformationLength);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool AssignProcessToJobObject(SafeJobHandle hJob, SafeProcessHandle hProcess);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool TerminateJobObject(SafeJobHandle hJob, uint uExitCode);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool IsProcessInJob(SafeProcessHandle processHandle, SafeJobHandle jobHandle, [MarshalAs(UnmanagedType.Bool)] out bool result);

    // ---- process snapshot ----

    [LibraryImport("kernel32.dll", SetLastError = true)]
    public static partial SafeFileHandle CreateToolhelp32Snapshot(uint dwFlags, uint th32ProcessID);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool Process32FirstW(SafeFileHandle hSnapshot, ref PROCESSENTRY32W lppe);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool Process32NextW(SafeFileHandle hSnapshot, ref PROCESSENTRY32W lppe);
}

/// <summary>A Job Object handle, closed with <c>CloseHandle</c>; closing the last handle kills the job's processes when the job is kill-on-close.</summary>
internal sealed class SafeJobHandle : SafeHandleZeroOrMinusOneIsInvalid
{
    /// <summary>Initializes a new instance of the <see cref="SafeJobHandle"/> class for the marshaller.</summary>
    public SafeJobHandle()
        : base(ownsHandle: true)
    {
    }

    /// <inheritdoc/>
    protected override bool ReleaseHandle() => NativeMethods.CloseHandle(handle);
}

/// <summary>A pseudo-console handle, closed with <c>ClosePseudoConsole</c>.</summary>
internal sealed class SafePseudoConsoleHandle : SafeHandleZeroOrMinusOneIsInvalid
{
    /// <summary>Initializes a new instance of the <see cref="SafePseudoConsoleHandle"/> class for the marshaller.</summary>
    public SafePseudoConsoleHandle()
        : base(ownsHandle: true)
    {
    }

    /// <summary>Initializes a new instance of the <see cref="SafePseudoConsoleHandle"/> class over a raw handle.</summary>
    /// <param name="handle">The handle from <c>CreatePseudoConsole</c>.</param>
    public SafePseudoConsoleHandle(IntPtr handle)
        : base(ownsHandle: true)
    {
        SetHandle(handle);
    }

    /// <inheritdoc/>
    protected override bool ReleaseHandle()
    {
        NativeMethods.ClosePseudoConsole(handle);
        return true;
    }
}

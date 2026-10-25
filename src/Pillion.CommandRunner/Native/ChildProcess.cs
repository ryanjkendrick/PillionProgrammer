using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Pillion.CommandRunner.Native;

/// <summary>
/// A process started on a pseudo-console with <c>CreateProcessW</c> and <c>PROC_THREAD_ATTRIBUTE_PSEUDOCONSOLE</c>. It is
/// created suspended so the caller can put it in its Job Objects before it runs a single instruction, then resumed.
/// </summary>
internal sealed class ChildProcess : IDisposable
{
    private readonly SafeProcessHandle _process;
    private IntPtr _thread;
    private int? _exitCode;

    private ChildProcess(SafeProcessHandle process, IntPtr thread, int processId)
    {
        _process = process;
        _thread = thread;
        ProcessId = processId;
    }

    /// <summary>Gets the process id.</summary>
    public int ProcessId { get; }

    /// <summary>Gets the process handle (for Job Object assignment and waits).</summary>
    public SafeProcessHandle Handle => _process;

    /// <summary>Gets a value indicating whether the process has exited.</summary>
    public bool HasExited
    {
        get
        {
            if (_exitCode is not null)
            {
                return true;
            }

            return NativeMethods.WaitForSingleObject(_process, 0) == NativeMethods.WAIT_OBJECT_0;
        }
    }

    /// <summary>
    /// Starts a process suspended on a pseudo-console.
    /// </summary>
    /// <param name="commandLine">The full command line (the program quoted as needed).</param>
    /// <param name="workingDirectory">The starting directory.</param>
    /// <param name="environment">The environment, or <see langword="null"/> to inherit the host's.</param>
    /// <param name="console">The pseudo-console the process is attached to.</param>
    /// <returns>The suspended process; call <see cref="Resume"/> once it is in its jobs.</returns>
    /// <exception cref="Win32Exception">The process could not be created.</exception>
    public static unsafe ChildProcess StartSuspended(
        string commandLine,
        string workingDirectory,
        IReadOnlyDictionary<string, string>? environment,
        PseudoConsole console)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(commandLine);
        ArgumentException.ThrowIfNullOrWhiteSpace(workingDirectory);
        ArgumentNullException.ThrowIfNull(console);

        nint size = 0;
        _ = NativeMethods.InitializeProcThreadAttributeList(IntPtr.Zero, 1, 0, ref size);
        var attributes = Marshal.AllocHGlobal(size);
        try
        {
            if (!NativeMethods.InitializeProcThreadAttributeList(attributes, 1, 0, ref size))
            {
                throw new Win32Exception(Marshal.GetLastPInvokeError(), "InitializeProcThreadAttributeList failed.");
            }

            try
            {
                if (!NativeMethods.UpdateProcThreadAttribute(
                    attributes,
                    0,
                    NativeMethods.PROC_THREAD_ATTRIBUTE_PSEUDOCONSOLE,
                    console.Handle,
                    IntPtr.Size,
                    IntPtr.Zero,
                    IntPtr.Zero))
                {
                    throw new Win32Exception(Marshal.GetLastPInvokeError(), "UpdateProcThreadAttribute failed.");
                }

                var startup = new NativeMethods.STARTUPINFOEXW
                {
                    StartupInfo = { cb = (uint)Marshal.SizeOf<NativeMethods.STARTUPINFOEXW>() },
                    lpAttributeList = attributes,
                };

                // CreateProcessW may write into the command line buffer, so it gets a mutable copy.
                var commandBuffer = (commandLine + '\0').ToCharArray();
                var environmentBlock = environment is null ? null : BuildEnvironmentBlock(environment);
                var flags = NativeMethods.EXTENDED_STARTUPINFO_PRESENT | NativeMethods.CREATE_SUSPENDED | NativeMethods.CREATE_UNICODE_ENVIRONMENT;

                fixed (char* command = commandBuffer)
                fixed (char* block = environmentBlock)
                {
                    if (!NativeMethods.CreateProcessW(
                        null,
                        command,
                        IntPtr.Zero,
                        IntPtr.Zero,
                        bInheritHandles: false,
                        flags,
                        block,
                        workingDirectory,
                        ref startup,
                        out var info))
                    {
                        throw new Win32Exception(Marshal.GetLastPInvokeError(), $"CreateProcess failed for: {commandLine}");
                    }

                    return new ChildProcess(new SafeProcessHandle(info.hProcess, ownsHandle: true), info.hThread, (int)info.dwProcessId);
                }
            }
            finally
            {
                NativeMethods.DeleteProcThreadAttributeList(attributes);
            }
        }
        finally
        {
            Marshal.FreeHGlobal(attributes);
        }
    }

    /// <summary>Lets the suspended process run.</summary>
    public void Resume()
    {
        if (_thread != IntPtr.Zero)
        {
            _ = NativeMethods.ResumeThread(_thread);
            _ = NativeMethods.CloseHandle(_thread);
            _thread = IntPtr.Zero;
        }
    }

    /// <summary>Waits for the process to exit.</summary>
    /// <param name="cancellationToken">Stops waiting (the process keeps running).</param>
    /// <returns>The exit code.</returns>
    public async Task<int> WaitForExitAsync(CancellationToken cancellationToken)
    {
        if (_exitCode is { } known)
        {
            return known;
        }

        using var waitHandle = new ProcessWaitHandle(_process);
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var registration = ThreadPool.RegisterWaitForSingleObject(
            waitHandle,
            static (state, _) => ((TaskCompletionSource)state!).TrySetResult(),
            completion,
            Timeout.Infinite,
            executeOnlyOnce: true);
        try
        {
            await completion.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            registration.Unregister(null);
        }

        return ExitCode;
    }

    /// <summary>Waits a bounded time for the process to exit.</summary>
    /// <param name="timeout">The time to wait.</param>
    /// <returns><see langword="true"/> when it exited within the time.</returns>
    public bool WaitForExit(TimeSpan timeout) =>
        NativeMethods.WaitForSingleObject(_process, (uint)Math.Clamp(timeout.TotalMilliseconds, 0, uint.MaxValue - 1)) == NativeMethods.WAIT_OBJECT_0;

    /// <summary>Gets the exit code of the exited process.</summary>
    /// <exception cref="InvalidOperationException">The process is still running.</exception>
    public int ExitCode
    {
        get
        {
            if (_exitCode is { } known)
            {
                return known;
            }

            if (!NativeMethods.GetExitCodeProcess(_process, out var code))
            {
                throw new Win32Exception(Marshal.GetLastPInvokeError(), "GetExitCodeProcess failed.");
            }

            if (code == NativeMethods.STILL_ACTIVE && !HasExited)
            {
                throw new InvalidOperationException("The process is still running.");
            }

            _exitCode = unchecked((int)code);
            return _exitCode.Value;
        }
    }

    /// <summary>Kills this process (not its children; the Job Object does that).</summary>
    public void Terminate()
    {
        if (!HasExited)
        {
            _ = NativeMethods.TerminateProcess(_process, 1);
        }
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        if (_thread != IntPtr.Zero)
        {
            _ = NativeMethods.CloseHandle(_thread);
            _thread = IntPtr.Zero;
        }

        _process.Dispose();
    }

    private static char[] BuildEnvironmentBlock(IReadOnlyDictionary<string, string> environment)
    {
        // Sorted case-insensitively, as CreateProcess documents, so the child's lookups work.
        var entries = environment
            .Where(pair => !string.IsNullOrEmpty(pair.Key) && !pair.Key.Contains('=', StringComparison.Ordinal))
            .OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase)
            .ToList();
        var length = 1;
        foreach (var (key, value) in entries)
        {
            length += key.Length + 1 + value.Length + 1;
        }

        var block = new char[length + 1];
        var position = 0;
        foreach (var (key, value) in entries)
        {
            key.CopyTo(0, block, position, key.Length);
            position += key.Length;
            block[position++] = '=';
            value.CopyTo(0, block, position, value.Length);
            position += value.Length;
            block[position++] = '\0';
        }

        block[position] = '\0';
        return block;
    }

    private sealed class ProcessWaitHandle : WaitHandle
    {
        public ProcessWaitHandle(SafeProcessHandle process)
        {
            SafeWaitHandle = new SafeWaitHandle(process.DangerousGetHandle(), ownsHandle: false);
        }
    }
}

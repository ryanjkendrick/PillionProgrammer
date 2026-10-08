using System.IO.Pipes;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Pillion.Host.Rpc;

/// <summary>
/// Identifies the process on the other end of a connected named pipe. Injectable so in-process tests (where the test
/// process is both ends) can simulate a foreign client.
/// </summary>
internal interface IPipeClientProcessIdProvider
{
    /// <summary>Gets the process id of the pipe's connected client.</summary>
    /// <param name="pipe">A connected server pipe.</param>
    /// <returns>The client's process id, or <see langword="null"/> if it cannot be determined.</returns>
    int? GetClientProcessId(NamedPipeServerStream pipe);
}

/// <summary>Uses the Win32 <c>GetNamedPipeClientProcessId</c> function.</summary>
internal sealed class NativePipeClientProcessIdProvider : IPipeClientProcessIdProvider
{
    /// <inheritdoc/>
    public int? GetClientProcessId(NamedPipeServerStream pipe)
    {
        ArgumentNullException.ThrowIfNull(pipe);

        if (!OperatingSystem.IsWindows())
        {
            return null;
        }

        return NativeMethods.GetNamedPipeClientProcessId(pipe.SafePipeHandle, out var processId)
            ? unchecked((int)processId)
            : null;
    }

    private static class NativeMethods
    {
        [DllImport("kernel32.dll", SetLastError = true)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool GetNamedPipeClientProcessId(SafePipeHandle pipe, out uint clientProcessId);
    }
}

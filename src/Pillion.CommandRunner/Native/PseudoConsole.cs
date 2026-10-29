using System.ComponentModel;
using System.Runtime.InteropServices;

namespace Pillion.CommandRunner.Native;

/// <summary>
/// A Windows pseudo-console (ConPTY, design section 19): the console a child process writes to as if it were a real
/// terminal. Its output arrives on <see cref="Output"/> as UTF-8 text with VT sequences, and what is written to
/// <see cref="Input"/> reaches the child as keyboard input.
/// </summary>
/// <remarks>
/// ConPTY keeps the write end of the output pipe open until the pseudo-console is closed, so a reader sees no end of
/// stream when the child exits; <see cref="Dispose"/> closes the pseudo-console, after which the reader gets its end of
/// stream. Closing can block until the pending output has been read, so the reader must keep draining while disposing.
/// </remarks>
internal sealed class PseudoConsole : IDisposable
{
    private readonly SafePseudoConsoleHandle _handle;
    private bool _disposed;

    private PseudoConsole(SafePseudoConsoleHandle handle, FileStream input, FileStream output, int columns, int rows)
    {
        _handle = handle;
        Input = input;
        Output = output;
        Columns = columns;
        Rows = rows;
    }

    /// <summary>Gets the stream that feeds the child's keyboard input (UTF-8).</summary>
    public FileStream Input { get; }

    /// <summary>Gets the stream carrying the child's rendered output (UTF-8 with VT sequences).</summary>
    public FileStream Output { get; }

    /// <summary>Gets the width in cells.</summary>
    public int Columns { get; private set; }

    /// <summary>Gets the height in rows.</summary>
    public int Rows { get; private set; }

    /// <summary>Gets the raw handle for the process attribute list.</summary>
    internal IntPtr Handle => _handle.DangerousGetHandle();

    /// <summary>Creates a pseudo-console of the given size.</summary>
    /// <param name="columns">The width in cells (1 to 32767).</param>
    /// <param name="rows">The height in rows (1 to 32767).</param>
    /// <returns>The pseudo-console.</returns>
    /// <exception cref="Win32Exception">The pipes or the pseudo-console could not be created.</exception>
    public static PseudoConsole Create(int columns, int rows)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(columns, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(rows, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(columns, short.MaxValue);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(rows, short.MaxValue);

        if (!NativeMethods.CreatePipe(out var inputRead, out var inputWrite, IntPtr.Zero, 0))
        {
            throw new Win32Exception(Marshal.GetLastPInvokeError(), "CreatePipe (input) failed.");
        }

        if (!NativeMethods.CreatePipe(out var outputRead, out var outputWrite, IntPtr.Zero, 0))
        {
            var error = Marshal.GetLastPInvokeError();
            inputRead.Dispose();
            inputWrite.Dispose();
            throw new Win32Exception(error, "CreatePipe (output) failed.");
        }

        var result = NativeMethods.CreatePseudoConsole(
            new NativeMethods.COORD { X = (short)columns, Y = (short)rows },
            inputRead,
            outputWrite,
            0,
            out var raw);

        // The pseudo-console duplicated the two ends it uses; this side keeps the other two.
        inputRead.Dispose();
        outputWrite.Dispose();
        if (result != 0)
        {
            inputWrite.Dispose();
            outputRead.Dispose();
            throw new Win32Exception(result, "CreatePseudoConsole failed.");
        }

        var handle = new SafePseudoConsoleHandle(raw);
        return new PseudoConsole(
            handle,
            new FileStream(inputWrite, FileAccess.Write, bufferSize: 0),
            new FileStream(outputRead, FileAccess.Read, bufferSize: 0),
            columns,
            rows);
    }

    /// <summary>Changes the size the child sees (it gets a window-size notification).</summary>
    /// <param name="columns">The new width.</param>
    /// <param name="rows">The new height.</param>
    /// <exception cref="Win32Exception">The resize failed.</exception>
    public void Resize(int columns, int rows)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(columns, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(rows, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(columns, short.MaxValue);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(rows, short.MaxValue);
        if (_disposed)
        {
            return;
        }

        var result = NativeMethods.ResizePseudoConsole(_handle, new NativeMethods.COORD { X = (short)columns, Y = (short)rows });
        if (result != 0)
        {
            throw new Win32Exception(result, "ResizePseudoConsole failed.");
        }

        Columns = columns;
        Rows = rows;
    }

    /// <summary>Closes the pseudo-console (<see cref="Close"/>) and then the output stream.</summary>
    public void Dispose()
    {
        Close();
        Output.Dispose();
    }

    /// <summary>
    /// Closes the input to the child (an end of file for a program reading stdin) and the pseudo-console, but leaves
    /// <see cref="Output"/> open so the reader can drain to its end of stream. Idempotent.
    /// </summary>
    public void Close()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        Input.Dispose();
        _handle.Dispose();
    }
}

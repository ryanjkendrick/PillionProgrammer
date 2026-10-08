using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO.Pipes;
using System.Text;

namespace Pillion.Host.Tests.TestSupport;

/// <summary>Launches the built Pillion.Host.exe the way the VSIX does: token on an inherited anonymous pipe.</summary>
internal sealed class HostProcess : IDisposable
{
    private readonly ConcurrentQueue<string> _output = new();

    private HostProcess(Process process)
    {
        Process = process;
    }

    public Process Process { get; }

    public string Output => string.Join(Environment.NewLine, _output);

    /// <summary>Starts the host.</summary>
    /// <param name="pipeName">The pipe name.</param>
    /// <param name="parentProcessId">The <c>--parent-pid</c> value.</param>
    /// <param name="logDirectory">The <c>--log-dir</c> value.</param>
    /// <param name="token">The token to write to the pipe, or <see langword="null"/> to close it without writing.</param>
    /// <returns>The running host.</returns>
    public static HostProcess Start(string pipeName, int parentProcessId, string logDirectory, string? token)
    {
        using var tokenPipe = new AnonymousPipeServerStream(PipeDirection.Out, HandleInheritability.Inheritable);
        var startInfo = new ProcessStartInfo(FindHostExecutable())
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        startInfo.ArgumentList.Add("--pipe");
        startInfo.ArgumentList.Add(pipeName);
        startInfo.ArgumentList.Add("--parent-pid");
        startInfo.ArgumentList.Add(parentProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture));
        startInfo.ArgumentList.Add("--token-handle");
        startInfo.ArgumentList.Add(tokenPipe.GetClientHandleAsString());
        startInfo.ArgumentList.Add("--log-dir");
        startInfo.ArgumentList.Add(logDirectory);

        var process = Process.Start(startInfo) ?? throw new InvalidOperationException("Pillion.Host.exe did not start.");
        var result = new HostProcess(process);
        process.OutputDataReceived += (_, e) => result.Collect(e.Data);
        process.ErrorDataReceived += (_, e) => result.Collect(e.Data);
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        tokenPipe.DisposeLocalCopyOfClientHandle();
        if (token is not null)
        {
            var bytes = Encoding.UTF8.GetBytes(token + "\n");
            tokenPipe.Write(bytes);
            tokenPipe.Flush();
        }

        return result;
    }

    public static string FindHostExecutable()
    {
        // The project reference copies the host (exe, runtimeconfig, deps) next to the tests.
        var local = Path.Combine(AppContext.BaseDirectory, "Pillion.Host.exe");
        if (File.Exists(local) && File.Exists(Path.Combine(AppContext.BaseDirectory, "Pillion.Host.runtimeconfig.json")))
        {
            return local;
        }

        // Fall back to the host's own output folder: tests/Pillion.Host.Tests/bin/<Configuration>/<TFM>/.
        var tfm = new DirectoryInfo(AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar));
        var configuration = tfm.Parent!;
        var root = configuration.Parent!.Parent!.Parent!.Parent!;
        var candidate = Path.Combine(root.FullName, "src", "Pillion.Host", "bin", configuration.Name, tfm.Name, "Pillion.Host.exe");
        return File.Exists(candidate)
            ? candidate
            : throw new FileNotFoundException("Pillion.Host.exe was not found; build the solution first.", candidate);
    }

    public void Dispose()
    {
        try
        {
            if (!Process.HasExited)
            {
                Process.Kill(entireProcessTree: true);
            }
        }
        catch (InvalidOperationException)
        {
            // Already exited.
        }

        Process.Dispose();
    }

    private void Collect(string? line)
    {
        if (line is not null)
        {
            _output.Enqueue(line);
        }
    }
}

using System.Diagnostics;
using Pillion.CommandRunner.Native;
using Pillion.CommandRunner.Shells;

namespace Pillion.CommandRunner.Tests;

/// <summary>Skips a test with a reason when no Visual Studio installation with <c>VsDevCmd.bat</c> is on the machine (CI runners and developer machines have one).</summary>
[AttributeUsage(AttributeTargets.Method)]
public sealed class VisualStudioFactAttribute : FactAttribute
{
    /// <summary>The newest installation, found once per test run.</summary>
    public static readonly Lazy<string?> Installation = new(() => VisualStudioLocator.FindNewestInstallation());

    public VisualStudioFactAttribute()
    {
        if (Installation.Value is null)
        {
            Skip = "No Visual Studio installation with Common7\\Tools\\VsDevCmd.bat was found on this machine (vswhere.exe).";
        }
    }
}

/// <summary>A temporary folder deleted on dispose.</summary>
internal sealed class TempDirectory : IDisposable
{
    public TempDirectory()
    {
        Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "pillion-cr-tests", Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(Path);
    }

    public string Path { get; }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(Path))
            {
                Directory.Delete(Path, recursive: true);
            }
        }
        catch (IOException)
        {
            // A file is still in use; the folder is under %TEMP%.
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}

/// <summary>Shared set-up for the component tests: sessions without the developer environment (fast) in a temporary folder.</summary>
internal static class TestSessions
{
    /// <summary>A generous bound for any wait in these tests; nothing waits this long when it works.</summary>
    public static readonly TimeSpan Wait = TimeSpan.FromSeconds(120);

    public static async Task<ICommandSession> CreateAsync(TempDirectory directory, Action<CommandSessionOptions>? configure = null, ShellKind shell = ShellKind.PowerShell7)
    {
        var options = new CommandSessionOptions(directory.Path)
        {
            Shell = shell,
            LoadDeveloperEnvironment = false,
            LogDirectory = Path.Combine(directory.Path, "logs"),
        };
        options.Environment["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1";
        options.Environment["DOTNET_NOLOGO"] = "1";
        configure?.Invoke(options);
        return await new CommandRunner().CreateSessionAsync(options);
    }

    /// <summary>A process and its descendants (Toolhelp keeps a dead parent's id on its children, so this works after a kill too).</summary>
    public static IReadOnlyList<int> TreeOf(int processId) => [processId, .. ProcessTree.GetDescendants(processId)];

    /// <summary>Waits until the session's running command has at least <paramref name="descendants"/> descendant processes, and returns the shell and its tree.</summary>
    public static async Task<IReadOnlyList<int>> WaitForTreeAsync(ICommandSession session, int descendants, TimeSpan timeout)
    {
        var watch = Stopwatch.StartNew();
        while (true)
        {
            if (session.CurrentProcessId is { } pid)
            {
                var tree = TreeOf(pid);
                if (tree.Count - 1 >= descendants)
                {
                    return tree;
                }
            }

            if (watch.Elapsed > timeout)
            {
                throw new TimeoutException($"Expected {descendants} descendant processes under the shell (pid {session.CurrentProcessId}), found {(session.CurrentProcessId is { } p ? TreeOf(p).Count - 1 : -1)} after {watch.Elapsed}.");
            }

            await Task.Delay(50);
        }
    }

    public static bool AllDead(IEnumerable<int> processIds) => processIds.All(id => !ProcessTree.IsAlive(id));

    /// <summary>Waits briefly for processes to disappear (a kill is signalled before the kernel tears the last one down).</summary>
    public static async Task<bool> WaitUntilDeadAsync(IEnumerable<int> processIds, TimeSpan timeout)
    {
        var ids = processIds.ToList();
        var watch = Stopwatch.StartNew();
        while (!AllDead(ids))
        {
            if (watch.Elapsed > timeout)
            {
                return false;
            }

            await Task.Delay(20);
        }

        return true;
    }

    /// <summary>Waits until a buffer holds a line matching the predicate (output is rendered asynchronously).</summary>
    public static async Task<bool> WaitForLineAsync(Pillion.CommandRunner.Terminal.ITerminalBuffer buffer, Func<string, bool> predicate, TimeSpan timeout)
    {
        var watch = Stopwatch.StartNew();
        while (!buffer.GetLinesToEnd(0).Any(line => predicate(line.Text)))
        {
            if (watch.Elapsed > timeout)
            {
                return false;
            }

            await Task.Delay(50);
        }

        return true;
    }

    public static int FreePort()
    {
        using var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        listener.Start();
        return ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;
    }
}

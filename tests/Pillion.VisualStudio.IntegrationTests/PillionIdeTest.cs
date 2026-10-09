using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using Microsoft.VisualStudio;
using Microsoft.VisualStudio.Extensibility.Testing;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using Xunit.Abstractions;
using Task = System.Threading.Tasks.Task;

namespace Pillion.VisualStudio.IntegrationTests;

/// <summary>
/// Base class for Pillion's Visual Studio integration tests. Each test runs on Visual Studio's UI thread inside the
/// Experimental Instance the framework launched. Adds helpers to open a scratch folder, wait for the Pillion package
/// and read the in-memory Activity Log, and writes that log to the results folder after every test.
/// </summary>
/// <remarks>
/// Types from Pillion.VisualStudio.Core and Pillion.Contracts are compile-time references only. Use them only after
/// <see cref="EnsurePackageLoadedAsync"/> (the package loads those assemblies), and only in methods that are called
/// afterwards, because the CLR resolves a method's types when it first runs it.
/// </remarks>
public abstract class PillionIdeTest : AbstractIdeIntegrationTest
{
    /// <summary>The Pillion package GUID (PackageGuids.PackageString in src/Pillion.VisualStudio).</summary>
    public static readonly Guid PackageGuid = new("992e3a80-c52f-4dcd-b4d9-5c78dc352352");

    /// <summary>The Pillion Chat tool window GUID (PackageGuids.ChatToolWindowString).</summary>
    public static readonly Guid ChatToolWindowGuid = new("3a84b873-8bad-4568-b3af-36f887cf71e5");

    /// <summary>The oldest Visual Studio the extension supports.</summary>
    public static readonly Version MinimumVisualStudioVersion = new(17, 14);

    private readonly List<string> _scratchFolders = [];

    static PillionIdeTest()
    {
        AppDomain.CurrentDomain.AssemblyResolve += ResolveLoadedPillionAssembly;
    }

    /// <summary>Initializes a new instance of the <see cref="PillionIdeTest"/> class.</summary>
    /// <param name="output">The xunit output for the test.</param>
    protected PillionIdeTest(ITestOutputHelper output)
    {
        Output = output;
    }

    /// <summary>Gets the test output.</summary>
    protected ITestOutputHelper Output { get; }

    /// <summary>
    /// Gets the folder for logs: <c>%XUNIT_LOGS%</c> when set (the framework puts its failure screenshots under it
    /// too), otherwise <c>xUnitResults</c> next to the test assembly.
    /// </summary>
    protected static string LogDirectory
    {
        get
        {
            var configured = Environment.GetEnvironmentVariable("XUNIT_LOGS");
            return string.IsNullOrEmpty(configured)
                ? Path.Combine(Path.GetDirectoryName(typeof(PillionIdeTest).Assembly.Location)!, "xUnitResults")
                : Path.GetFullPath(configured);
        }
    }

    /// <inheritdoc/>
    public override async Task DisposeAsync()
    {
        try
        {
            await WriteActivityLogAsync().ConfigureAwait(true);
        }
#pragma warning disable CA1031 // Saving the log is best effort; it must not fail or hide the test's own result.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            Output.WriteLine($"Could not save the Activity Log: {ex.GetType().Name}: {ex.Message}");
        }
        finally
        {
            await base.DisposeAsync().ConfigureAwait(true);
            foreach (var folder in _scratchFolders)
            {
                TryDeleteDirectory(folder);
            }
        }
    }

    /// <summary>Gets the running Visual Studio's version (for example 18.10.11209.10).</summary>
    protected Task<Version> GetVisualStudioVersionAsync() => TestServices.Shell.GetVersionAsync(HangMitigatingCancellationToken);

    /// <summary>Creates a scratch folder under <c>%TEMP%</c> with one C# file and opens it (File &gt; Open &gt; Folder).</summary>
    /// <returns>The folder path.</returns>
    protected async Task<string> OpenScratchFolderAsync()
    {
        var folder = Path.Combine(Path.GetTempPath(), "pillion-ide-tests", Guid.NewGuid().ToString("N").Substring(0, 12));
        Directory.CreateDirectory(folder);
        _scratchFolders.Add(folder);
        File.WriteAllText(
            Path.Combine(folder, "Program.cs"),
            "namespace Scratch;" + Environment.NewLine + Environment.NewLine
            + "internal static class Program" + Environment.NewLine + "{" + Environment.NewLine
            + "    private static void Main() { }" + Environment.NewLine + "}" + Environment.NewLine);

        await JoinableTaskFactory.SwitchToMainThreadAsync(HangMitigatingCancellationToken);
        var solution = await TestServices.Shell.GetRequiredGlobalServiceAsync<SVsSolution, IVsSolution7>(HangMitigatingCancellationToken);
        solution.OpenFolder(folder);
        await WaitUntilAsync(() => TestServices.SolutionExplorer.IsSolutionOpenAsync(HangMitigatingCancellationToken), TimeSpan.FromSeconds(60), "the scratch folder to open").ConfigureAwait(true);
        Output.WriteLine($"Opened folder {folder}");
        return folder;
    }

    /// <summary>Returns whether the Pillion package is loaded, without loading it.</summary>
    protected async Task<bool> IsPackageLoadedAsync()
    {
        await JoinableTaskFactory.SwitchToMainThreadAsync(HangMitigatingCancellationToken);
        var shell = await TestServices.Shell.GetRequiredGlobalServiceAsync<SVsShell, IVsShell>(HangMitigatingCancellationToken);
        var guid = PackageGuid;
        return ErrorHandler.Succeeded(shell.IsPackageLoaded(ref guid, out var package)) && package is not null;
    }

    /// <summary>Waits for the package to load by itself (it background-loads on SolutionExists and FolderOpened).</summary>
    /// <param name="timeout">How long to wait.</param>
    /// <returns>How long it took.</returns>
    protected async Task<TimeSpan> WaitForPackageLoadedAsync(TimeSpan timeout)
    {
        var clock = Stopwatch.StartNew();
        await WaitUntilAsync(IsPackageLoadedAsync, timeout, "the Pillion package to load").ConfigureAwait(true);
        return clock.Elapsed;
    }

    /// <summary>Opens a scratch folder when nothing is open and waits for the package (it loads on folder open).</summary>
    protected async Task EnsurePackageLoadedAsync()
    {
        if (!await TestServices.SolutionExplorer.IsSolutionOpenAsync(HangMitigatingCancellationToken).ConfigureAwait(true))
        {
            await OpenScratchFolderAsync().ConfigureAwait(true);
        }

        await WaitForPackageLoadedAsync(TimeSpan.FromSeconds(60)).ConfigureAwait(true);
    }

    /// <summary>
    /// Reads Visual Studio's in-memory Activity Log (the one written to ActivityLog.xml with <c>/log</c>). Empty when
    /// Visual Studio was started with <c>/log</c>, because the log then goes straight to the file.
    /// </summary>
    protected async Task<ActivityLog> GetActivityLogAsync()
    {
        await JoinableTaskFactory.SwitchToMainThreadAsync(HangMitigatingCancellationToken);
        var service = await TestServices.Shell.GetRequiredGlobalServiceAsync<SVsActivityLog, IVsActivityLog>(HangMitigatingCancellationToken);
        var text = service is IVsActivityLogDumper dumper ? dumper.GetActivityLogBuffer() : null;
        return ActivityLog.Parse(text);
    }

    /// <summary>Polls a condition every 250 ms (yielding the UI thread in between) until it holds or the timeout passes.</summary>
    /// <param name="condition">The condition.</param>
    /// <param name="timeout">How long to wait.</param>
    /// <param name="what">What is awaited, for the failure message.</param>
    protected async Task WaitUntilAsync(Func<Task<bool>> condition, TimeSpan timeout, string what)
    {
        var clock = Stopwatch.StartNew();
        while (!await condition().ConfigureAwait(true))
        {
            if (clock.Elapsed > timeout)
            {
                throw new TimeoutException($"Timed out after {timeout.TotalSeconds:0} s waiting for {what}.");
            }

            await Task.Delay(250, HangMitigatingCancellationToken).ConfigureAwait(true);
        }
    }

    /// <summary>
    /// Inside devenv.exe, resolves Pillion.VisualStudio.Core and Pillion.Contracts to the copies the installed extension
    /// loaded, so the test and the package share one IHostConnection type. The test output folder has no copies.
    /// </summary>
    private static Assembly? ResolveLoadedPillionAssembly(object? sender, ResolveEventArgs args)
    {
        var name = new AssemblyName(args.Name).Name;
        if (name is not ("Pillion.VisualStudio.Core" or "Pillion.Contracts"))
        {
            return null;
        }

        return AppDomain.CurrentDomain.GetAssemblies().FirstOrDefault(a => string.Equals(a.GetName().Name, name, StringComparison.Ordinal));
    }

    private static void TryDeleteDirectory(string folder)
    {
        try
        {
            Directory.Delete(folder, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Visual Studio may still hold the folder's .vs directory; %TEMP% is cleaned up eventually.
        }
    }

    private async Task WriteActivityLogAsync()
    {
        var log = await GetActivityLogAsync().ConfigureAwait(true);
        if (log.RawText.Length == 0)
        {
            return;
        }

        var version = await GetVisualStudioVersionAsync().ConfigureAwait(true);
        Directory.CreateDirectory(LogDirectory);
        var path = Path.Combine(LogDirectory, $"ActivityLog-VS{version.Major}.xml");
        File.WriteAllText(path, log.RawText);
    }
}

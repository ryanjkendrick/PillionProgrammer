using System;
using System.IO;
using System.Linq;
using EnvDTE;
using Microsoft.VisualStudio;
using Microsoft.VisualStudio.Shell.Interop;
using Pillion.VisualStudio.Core.Signals;
using Task = System.Threading.Tasks.Task;

namespace Pillion.VisualStudio.IntegrationTests;

/// <summary>
/// The IDE signals (work item 1.11): document events, build events, the Error List and Output pane queries, against a
/// scratch C# console project under %TEMP% with one compile error. A classic (non-SDK) project, so no restore is needed
/// and the build works offline.
/// </summary>
public sealed partial class PillionPackageTests
{
    private const string ScratchProjectGuid = "{3F2504E0-4F89-11D3-9A0C-0305E82C3301}";

    [IdeFact]
    [TestOrder(10)]
    public async Task IdeSignals_DocumentEvents_FireForOpenActivateSaveAndClose()
    {
        var folder = CreateScratchSolution(withCompileError: false);
        try
        {
            await OpenSolutionAsync(folder);
            await WaitForPackageLoadedAsync(TimeSpan.FromSeconds(60));
            await CheckDocumentEventsAsync(folder);
        }
        finally
        {
            await CloseSolutionAsync();
            TryDelete(folder);
        }
    }

    // Uses Pillion.VisualStudio.Core types, so it must not run (be compiled by the JIT) before the package has loaded them.
    private async Task CheckDocumentEventsAsync(string folder)
    {
        using var recording = await SignalRecording.StartAsync(TimeSpan.FromSeconds(60), HangMitigatingCancellationToken);
        var programPath = Path.Combine(folder, "Program.cs");

        await OpenFileAsync(Path.Combine(folder, "Program.cs"));
        await recording.WaitForAsync("DocumentOpened", TimeSpan.FromSeconds(30), HangMitigatingCancellationToken, s => SamePath(s.Path, programPath));
        await recording.WaitForAsync("ActiveDocumentChanged", TimeSpan.FromSeconds(30), HangMitigatingCancellationToken, s => SamePath(s.Path, programPath));
        Assert.True(SamePath(recording.Signals.ActiveDocumentPath, programPath), $"ActiveDocumentPath is {recording.Signals.ActiveDocumentPath}.");

        // Edit and save through the IDE's own document object.
        await JoinableTaskFactory.SwitchToMainThreadAsync(HangMitigatingCancellationToken);
        var dte = await TestServices.Shell.GetRequiredGlobalServiceAsync<SDTE, DTE>(HangMitigatingCancellationToken);
        var textDocument = (TextDocument)dte.ActiveDocument.Object("TextDocument");
        textDocument.EndPoint.CreateEditPoint().Insert("// edited by the integration test" + Environment.NewLine);
        dte.ActiveDocument.Save();
        await recording.WaitForAsync("DocumentSaved", TimeSpan.FromSeconds(30), HangMitigatingCancellationToken, s => SamePath(s.Path, programPath));

        dte.ActiveDocument.Close(vsSaveChanges.vsSaveChangesNo);
        await recording.WaitForAsync("DocumentClosed", TimeSpan.FromSeconds(30), HangMitigatingCancellationToken, s => SamePath(s.Path, programPath));

        foreach (var item in recording.Signals.RecentEvents)
        {
            Output.WriteLine(item.ToString());
        }

        Assert.Contains(recording.Signals.RecentEvents, e => e.Kind == "DocumentSaved");
    }

    [IdeFact]
    [TestOrder(11)]
    public async Task IdeSignals_BuildWithACompileError_RaisesBuildEvents_AndAnswersDiagnosticAndOutputQueries()
    {
        var folder = CreateScratchSolution(withCompileError: true);
        try
        {
            await OpenSolutionAsync(folder);
            await WaitForPackageLoadedAsync(TimeSpan.FromSeconds(60));
            await CheckBuildAndQueriesAsync(folder);
        }
        finally
        {
            await CloseSolutionAsync();
            TryDelete(folder);
        }
    }

    // Uses Pillion.VisualStudio.Core types, so it must not run (be compiled by the JIT) before the package has loaded them.
    private async Task CheckBuildAndQueriesAsync(string folder)
    {
        using var recording = await SignalRecording.StartAsync(TimeSpan.FromSeconds(60), HangMitigatingCancellationToken);
        Output.WriteLine("Diagnostics note: " + recording.Signals.DiagnosticsNote);
        await OpenFileAsync(Path.Combine(folder, "Program.cs"));
        await recording.WaitForAsync("ActiveDocumentChanged", TimeSpan.FromSeconds(30), HangMitigatingCancellationToken);

        Assert.False(recording.Signals.BuildActivity.IsBuilding);

        await JoinableTaskFactory.SwitchToMainThreadAsync(HangMitigatingCancellationToken);
        var dte = await TestServices.Shell.GetRequiredGlobalServiceAsync<SDTE, DTE>(HangMitigatingCancellationToken);
        dte.Solution.SolutionBuild.Build(true);

        var started = await recording.WaitForAsync("BuildStarted", TimeSpan.FromSeconds(30), HangMitigatingCancellationToken);
        var finished = await recording.WaitForAsync("BuildFinished", TimeSpan.FromSeconds(120), HangMitigatingCancellationToken);
        Output.WriteLine($"BuildFinished: success={finished.Build!.Success} projects={finished.Build.ProjectCount} duration={finished.Build.Duration.TotalSeconds:0.0}s");
        Output.WriteLine("Build pane after the build: " + await recording.Signals.GetOutputPaneTextAsync("Build", 3000, HangMitigatingCancellationToken));
        await JoinableTaskFactory.SwitchToMainThreadAsync(HangMitigatingCancellationToken);
        Output.WriteLine($"LastBuildInfo={dte.Solution.SolutionBuild.LastBuildInfo}; configuration={dte.Solution.SolutionBuild.ActiveConfiguration?.Name}; projects={dte.Solution.Projects.Count}");
        foreach (SolutionContext context in dte.Solution.SolutionBuild.ActiveConfiguration!.SolutionContexts)
        {
            Output.WriteLine($"  context {context.ProjectName}: ShouldBuild={context.ShouldBuild} config={context.ConfigurationName}|{context.PlatformName}");
        }

        Assert.True(started.Sequence < finished.Sequence, "BuildStarted must come before BuildFinished.");
        Assert.False(finished.Build.Success);
        Assert.True(finished.Build.ProjectCount >= 1, $"Project count {finished.Build.ProjectCount}.");
        Assert.True(finished.Build.Duration > TimeSpan.Zero);
        Assert.False(recording.Signals.BuildActivity.IsBuilding);
        await recording.WaitForAsync("BuildStateChanged(building)", TimeSpan.FromSeconds(10), HangMitigatingCancellationToken);
        await recording.WaitForAsync("BuildStateChanged(idle)", TimeSpan.FromSeconds(10), HangMitigatingCancellationToken);

        // The Error List (the developer's filters: the default shows Build + IntelliSense, entire solution).
        var all = await recording.WaitForSolutionDiagnosticAsync("CS0029", TimeSpan.FromSeconds(30), HangMitigatingCancellationToken);
        foreach (var diagnostic in all)
        {
            Output.WriteLine($"Solution: {diagnostic} [{diagnostic.Source}, project {diagnostic.Project}]");
        }

        var error = all.First(d => d.Code == "CS0029");
        Assert.Equal(IdeSeverity.Error, error.Severity);
        Assert.True(SamePath(error.File, Path.Combine(folder, "Program.cs")), $"File {error.File}.");
        Assert.Equal(7, error.Line);
        Assert.True(error.Column > 0);
        Assert.Equal("Scratch", error.Project);
        Assert.Contains("string", error.Message, StringComparison.OrdinalIgnoreCase);

        await recording.WaitForAsync("DiagnosticsChanged", TimeSpan.FromSeconds(15), HangMitigatingCancellationToken);

        // The active document: Roslyn's diagnostics merged with the Error List's entries for the file.
        var active = await recording.Signals.GetDiagnosticsAsync(DiagnosticScope.ActiveDocument, HangMitigatingCancellationToken);
        foreach (var diagnostic in active)
        {
            Output.WriteLine($"ActiveDocument: {diagnostic} [{diagnostic.Source}]");
        }

        Output.WriteLine("Diagnostics note now: " + recording.Signals.DiagnosticsNote);
        Assert.Contains(active, d => d.Code == "CS0029");

        // The Output window's Build pane.
        var build = await recording.WaitForOutputAsync("Build", "error", 4000, TimeSpan.FromSeconds(30), HangMitigatingCancellationToken);
        Output.WriteLine("Build pane (tail): " + build);
        Assert.True(build.Length <= 4000);
        Assert.Contains("CS0029", build, StringComparison.Ordinal);
        Assert.Null(await recording.Signals.GetOutputPaneTextAsync("No such pane", 100, HangMitigatingCancellationToken));
    }

    private static bool SamePath(string? a, string? b) =>
        a is not null && b is not null && string.Equals(Path.GetFullPath(a), Path.GetFullPath(b), StringComparison.OrdinalIgnoreCase);

    private static void TryDelete(string folder)
    {
        try
        {
            Directory.Delete(folder, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Visual Studio may still hold the folder; %TEMP% is cleaned up eventually.
        }
    }

    private async Task OpenSolutionAsync(string folder)
    {
        await JoinableTaskFactory.SwitchToMainThreadAsync(HangMitigatingCancellationToken);
        var dte = await TestServices.Shell.GetRequiredGlobalServiceAsync<SDTE, DTE>(HangMitigatingCancellationToken);
        dte.Solution.Open(Path.Combine(folder, "Scratch.sln"));
        await WaitUntilAsync(() => TestServices.SolutionExplorer.IsSolutionOpenAsync(HangMitigatingCancellationToken), TimeSpan.FromSeconds(60), "the scratch solution to open");

        // Projects load in the background; a build before they are loaded builds nothing and "succeeds".
        await WaitUntilAsync(
            async () =>
            {
                await JoinableTaskFactory.SwitchToMainThreadAsync(HangMitigatingCancellationToken);
                var vsSolution = await TestServices.Shell.GetRequiredGlobalServiceAsync<SVsSolution, IVsSolution>(HangMitigatingCancellationToken);
                await JoinableTaskFactory.SwitchToMainThreadAsync(HangMitigatingCancellationToken);
                var fullyLoaded = ErrorHandler.Succeeded(vsSolution.GetProperty((int)__VSPROPID4.VSPROPID_IsSolutionFullyLoaded, out var loaded)) && loaded is true;
                return fullyLoaded && dte.Solution.Projects.Count >= 1 && dte.Solution.SolutionBuild.SolutionConfigurations.Count >= 1;
            },
            TimeSpan.FromSeconds(60),
            "the scratch project to load");
        Output.WriteLine("Opened solution in " + folder);
    }

    private async Task OpenFileAsync(string path)
    {
        await JoinableTaskFactory.SwitchToMainThreadAsync(HangMitigatingCancellationToken);
        var dte = await TestServices.Shell.GetRequiredGlobalServiceAsync<SDTE, DTE>(HangMitigatingCancellationToken);
        _ = dte.ItemOperations.OpenFile(path);
    }

    private async Task CloseSolutionAsync()
    {
        try
        {
            await TestServices.SolutionExplorer.CloseSolutionAsync(HangMitigatingCancellationToken);
        }
#pragma warning disable CA1031 // Cleanup: the test's own result matters more.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            Output.WriteLine($"Could not close the solution: {ex.GetType().Name}: {ex.Message}");
        }
    }

    /// <summary>Writes a classic C# console project and its solution under %TEMP%; the one error is on line 7 of Program.cs.</summary>
    private static string CreateScratchSolution(bool withCompileError)
    {
        var folder = Path.Combine(Path.GetTempPath(), "pillion-ide-tests", "signals-" + Guid.NewGuid().ToString("N").Substring(0, 12));
        Directory.CreateDirectory(folder);
        var nl = Environment.NewLine;
        File.WriteAllText(
            Path.Combine(folder, "Program.cs"),
            "using System;" + nl + nl
            + "internal static class Program" + nl + "{" + nl
            + "    private static void Main()" + nl + "    {" + nl
            + (withCompileError ? "        int number = \"not a number\";" : "        int number = 1;") + nl
            + "        Console.WriteLine(number);" + nl + "    }" + nl + "}" + nl);
        WriteScratchProject(folder, ["Program.cs"]);
        return folder;
    }

    /// <summary>Writes <c>Scratch.csproj</c> (classic, .NET Framework 4.8, no restore needed) compiling the files, and <c>Scratch.sln</c>.</summary>
    private static void WriteScratchProject(string folder, string[] compileItems)
    {
        var nl = Environment.NewLine;
        File.WriteAllText(
            Path.Combine(folder, "Scratch.csproj"),
            "<Project ToolsVersion=\"15.0\" xmlns=\"http://schemas.microsoft.com/developer/msbuild/2003\">" + nl
            + "  <Import Project=\"$(MSBuildExtensionsPath)\\$(MSBuildToolsVersion)\\Microsoft.Common.props\" Condition=\"Exists('$(MSBuildExtensionsPath)\\$(MSBuildToolsVersion)\\Microsoft.Common.props')\" />" + nl
            + "  <PropertyGroup>" + nl
            + "    <Configuration Condition=\" '$(Configuration)' == '' \">Debug</Configuration>" + nl
            + "    <Platform Condition=\" '$(Platform)' == '' \">AnyCPU</Platform>" + nl
            + "    <ProjectGuid>" + ScratchProjectGuid + "</ProjectGuid>" + nl
            + "    <OutputType>Exe</OutputType>" + nl
            + "    <RootNamespace>Scratch</RootNamespace>" + nl
            + "    <AssemblyName>Scratch</AssemblyName>" + nl
            + "    <TargetFrameworkVersion>v4.8</TargetFrameworkVersion>" + nl
            + "  </PropertyGroup>" + nl
            // Visual Studio finds a project's configurations from these conditions; without one it skips the project's build.
            + "  <PropertyGroup Condition=\" '$(Configuration)|$(Platform)' == 'Debug|AnyCPU' \">" + nl
            + "    <DebugSymbols>true</DebugSymbols>" + nl
            + "    <DebugType>full</DebugType>" + nl
            + "    <Optimize>false</Optimize>" + nl
            + "    <OutputPath>bin\\Debug\\</OutputPath>" + nl
            + "  </PropertyGroup>" + nl
            + "  <ItemGroup>" + nl
            + "    <Reference Include=\"System\" />" + nl
            + "  </ItemGroup>" + nl
            + "  <ItemGroup>" + nl
            + string.Concat(compileItems.Select(item => "    <Compile Include=\"" + item + "\" />" + nl))
            + "  </ItemGroup>" + nl
            + "  <Import Project=\"$(MSBuildToolsPath)\\Microsoft.CSharp.targets\" />" + nl
            + "</Project>" + nl);
        File.WriteAllText(
            Path.Combine(folder, "Scratch.sln"),
            "Microsoft Visual Studio Solution File, Format Version 12.00" + nl
            + "# Visual Studio Version 17" + nl
            + "Project(\"{FAE04EC0-301F-11D3-BF4B-00C04F79EFBC}\") = \"Scratch\", \"Scratch.csproj\", \"" + ScratchProjectGuid + "\"" + nl
            + "EndProject" + nl
            + "Global" + nl
            + "\tGlobalSection(SolutionConfigurationPlatforms) = preSolution" + nl
            + "\t\tDebug|Any CPU = Debug|Any CPU" + nl
            + "\tEndGlobalSection" + nl
            + "\tGlobalSection(ProjectConfigurationPlatforms) = postSolution" + nl
            + "\t\t" + ScratchProjectGuid + ".Debug|Any CPU.ActiveCfg = Debug|Any CPU" + nl
            + "\t\t" + ScratchProjectGuid + ".Debug|Any CPU.Build.0 = Debug|Any CPU" + nl
            + "\tEndGlobalSection" + nl
            + "EndGlobal" + nl);
    }
}

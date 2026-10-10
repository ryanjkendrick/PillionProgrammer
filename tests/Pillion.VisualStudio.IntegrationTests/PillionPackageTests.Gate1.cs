using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using EnvDTE;
using Microsoft.VisualStudio;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using Microsoft.VisualStudio.Text.Editor;
using Newtonsoft.Json.Linq;
using Task = System.Threading.Tasks.Task;

namespace Pillion.VisualStudio.IntegrationTests;

/// <summary>
/// The Gate 1 performance scenarios (work item 1.18), run by <c>tools/gate1/Invoke-Gate1.ps1</c> in the Experimental
/// Instance with the real Pillion Host and local models in Ollama; skipped otherwise (<see cref="GateScenarioFactAttribute"/>).
/// They come after the package, host and mention tests of this class (one instance per run, in test order) and write their
/// figures as JSON to <c>%PILLION_GATE_OUT%</c>, named by test and Visual Studio version. The gate script writes the
/// Ollama provider into settings.json before the run and restores the file afterwards; the scenarios change only the
/// autocomplete routing and flag in it. The logic is in <see cref="GateScenarioChecks"/>.
/// </summary>
public sealed partial class PillionPackageTests
{
    /// <summary>The cadence of the typing scenario: about 80 ms a character, a pause at each line break.</summary>
    private static readonly TimeSpan CharacterDelay = TimeSpan.FromMilliseconds(80);

    /// <summary>The pause after each line break; long enough for the 200 ms debounce and a p95 of 800 ms.</summary>
    private static readonly TimeSpan LinePause = TimeSpan.FromMilliseconds(1200);

    /// <summary>The files of the gate scratch project besides the empty typing files.</summary>
    private static readonly string[] GateSourceFiles = ["Program.cs", "Orders.cs"];

    private static string? _gateFolder;

    private static string ProviderId => GateScenarioChecks.Setting("PILLION_GATE_PROVIDER", "ollama");

    private static string StrongModel => GateScenarioChecks.Setting("PILLION_GATE_STRONG_MODEL", "qwen2.5-coder:7b");

    private static string FastModel => GateScenarioChecks.Setting("PILLION_GATE_FAST_MODEL", "qwen2.5-coder:1.5b");

    [GateScenarioFact]
    [TestOrder(30)]
    public Task Gate1_Autocomplete_StrongModel_TypingScenario() => RunTypingPassAsync("autocomplete-strong", StrongModel, enabled: true, "TypeStrong.cs");

    [GateScenarioFact]
    [TestOrder(31)]
    public Task Gate1_Autocomplete_FastModel_TypingScenario() => RunTypingPassAsync("autocomplete-fast", FastModel, enabled: true, "TypeFast.cs");

    [GateScenarioFact]
    [TestOrder(32)]
    public Task Gate1_Keystrokes_AutocompleteOff_TypingScenario() => RunTypingPassAsync("autocomplete-off", model: null, enabled: false, "TypeOff.cs");

    /// <summary>
    /// The cloud rows (autocomplete on the Fast profile, chat first token): only when the gate script found a stored key for
    /// the provider it names in <c>PILLION_GATE_CLOUD_PROVIDER</c> (with the model in <c>PILLION_GATE_CLOUD_MODEL</c>);
    /// otherwise it writes that the rows are pending.
    /// </summary>
    [GateScenarioFact]
    [TestOrder(35)]
    public async Task Gate1_Cloud_FastAutocompleteAndChatFirstToken()
    {
        var provider = Environment.GetEnvironmentVariable("PILLION_GATE_CLOUD_PROVIDER");
        var model = Environment.GetEnvironmentVariable("PILLION_GATE_CLOUD_MODEL");
        var version = await GetVisualStudioVersionAsync();
        if (string.IsNullOrEmpty(provider) || string.IsNullOrEmpty(model))
        {
            Output.WriteLine("Cloud rows pending: no cloud provider with a stored key (tools/gate1/Invoke-Gate1.ps1 -CloudProviderId <id>).");
            GateScenarioChecks.WriteResult(GateOutput, $"cloud-VS{version.Major}", new JObject { ["status"] = "pending", ["reason"] = "no cloud provider with a stored key" });
            return;
        }

        await RunTypingPassAsync("autocomplete-cloud", model, enabled: true, "TypeCloud.cs", provider, "fast");
        var frame = await OpenChatWindowAsync();
        await JoinableTaskFactory.SwitchToMainThreadAsync(HangMitigatingCancellationToken);
        ErrorHandler.ThrowOnFailure(frame.GetProperty((int)__VSFPROPID.VSFPROPID_DocView, out var docView));
        var control = (docView as ToolWindowPane)?.Content as FrameworkElement ?? throw new InvalidOperationException("The Pillion Chat window has no content.");
        var chat = await GateScenarioChecks.ChatFirstTokenAsync(control, model, "In one short sentence: what is a hash map for?", 5, HangMitigatingCancellationToken, local: false);
        Output.WriteLine("Cloud chat: " + chat.ToString(Newtonsoft.Json.Formatting.None));
        await JoinableTaskFactory.SwitchToMainThreadAsync(HangMitigatingCancellationToken);
        _ = frame.CloseFrame((uint)__FRAMECLOSE.FRAMECLOSE_NoSave);
        GateScenarioChecks.WriteResult(GateOutput, $"cloud-VS{version.Major}", new JObject { ["status"] = "measured", ["provider"] = provider, ["model"] = model, ["chat"] = chat });
    }

    [GateScenarioFact]
    [TestOrder(33)]
    public async Task Gate1_InlineChat_PreviewFirstLine()
    {
        var started = DateTime.UtcNow;
        await OpenGateSolutionAsync();
        var view = await OpenGateDocumentAsync("Orders.cs");
        var runs = new JArray();
        try
        {
            foreach (var instruction in new[] { "extract the validation into a method", "/doc" })
            {
                for (var i = 0; i < 5; i++)
                {
                    // Lines 6 to 14 hold the Place method (see CreateGateSolution).
                    var run = await GateScenarioChecks.InlineChatOnceAsync(view, 6, 14, instruction, OpenInlineChatAsync, HangMitigatingCancellationToken);
                    run["run"] = i + 1;
                    runs.Add(run);
                    Output.WriteLine(run.ToString(Newtonsoft.Json.Formatting.None));
                }
            }
        }
        finally
        {
            await CloseGateDocumentAsync("Orders.cs");
        }

        var result = new JObject
        {
            ["test"] = nameof(Gate1_InlineChat_PreviewFirstLine),
            ["visualStudio"] = (await GetVisualStudioVersionAsync()).ToString(),
            ["startedUtc"] = started.ToString("o"),
            ["runs"] = runs,
        };
        Output.WriteLine("Wrote " + GateScenarioChecks.WriteResult(GateOutput, $"inline-chat-VS{(await GetVisualStudioVersionAsync()).Major}", result));
    }

    [GateScenarioFact]
    [TestOrder(34)]
    public async Task Gate1_Chat_FirstToken_Mentions_AndOutboundLog()
    {
        var started = DateTime.UtcNow;
        await OpenGateSolutionAsync();

        // @errors needs the compile error in the Error List: build the project (background analysis may cover open
        // documents only), as a developer would before asking about it.
        await JoinableTaskFactory.SwitchToMainThreadAsync(HangMitigatingCancellationToken);
        var dte = await TestServices.Shell.GetRequiredGlobalServiceAsync<SDTE, DTE>(HangMitigatingCancellationToken);
        await JoinableTaskFactory.SwitchToMainThreadAsync(HangMitigatingCancellationToken);
        dte.Solution!.SolutionBuild.Build(WaitForBuildToFinish: true);
        Output.WriteLine($"Built the gate project: {dte.Solution.SolutionBuild.LastBuildInfo} project(s) failed (one compile error is expected).");

        // @selection: the Place method selected in Orders.cs, the editor the chat window reads the selection from.
        var orders = await OpenGateDocumentAsync("Orders.cs");
        await JoinableTaskFactory.SwitchToMainThreadAsync(HangMitigatingCancellationToken);
        var snapshot = orders.TextSnapshot;
        orders.Selection.Select(new Microsoft.VisualStudio.Text.SnapshotSpan(snapshot.GetLineFromLineNumber(6).Start, snapshot.GetLineFromLineNumber(14).End), isReversed: false);

        var frame = await OpenChatWindowAsync();
        await JoinableTaskFactory.SwitchToMainThreadAsync(HangMitigatingCancellationToken);
        ErrorHandler.ThrowOnFailure(frame.GetProperty((int)__VSFPROPID.VSFPROPID_DocView, out var docView));
        var control = (docView as ToolWindowPane)?.Content as FrameworkElement ?? throw new InvalidOperationException("The Pillion Chat window has no content.");

        var chat = await GateScenarioChecks.ChatFirstTokenAsync(control, StrongModel, "In one short sentence: what is a hash map for?", 5, HangMitigatingCancellationToken);
        Output.WriteLine("Chat: " + chat.ToString(Newtonsoft.Json.Formatting.None));
        var mentions = await GateScenarioChecks.MentionsAndInspectorAsync(control, "Program.cs", StrongModel, HangMitigatingCancellationToken);
        Output.WriteLine("Mentions: " + mentions.ToString(Newtonsoft.Json.Formatting.None));

        // Every task class used during the run (autocomplete, inline edits, chat) has Outbound records in the host.
        var outbound = await GateScenarioChecks.OutboundByTaskClassAsync(DateTime.UtcNow.AddHours(-1), HangMitigatingCancellationToken);
        Output.WriteLine("Outbound: " + outbound.ToString(Newtonsoft.Json.Formatting.None));
        await JoinableTaskFactory.SwitchToMainThreadAsync(HangMitigatingCancellationToken);
        _ = frame.CloseFrame((uint)__FRAMECLOSE.FRAMECLOSE_NoSave);
        await CloseGateDocumentAsync("Orders.cs");

        var version = await GetVisualStudioVersionAsync();
        var result = new JObject
        {
            ["test"] = nameof(Gate1_Chat_FirstToken_Mentions_AndOutboundLog),
            ["visualStudio"] = version.ToString(),
            ["startedUtc"] = started.ToString("o"),
            ["chat"] = chat,
            ["mentions"] = mentions,
            ["outbound"] = outbound,
        };
        Output.WriteLine("Wrote " + GateScenarioChecks.WriteResult(GateOutput, $"chat-mentions-VS{version.Major}", result));
        Assert.True((bool)outbound["hasChat"]! && (bool)outbound["hasAutocomplete"]! && (bool)outbound["hasInlineEdit"]!, "An Outbound record is missing for a task class: " + outbound);
    }

    private static string GateOutput => GateScenarioChecks.OutputDirectory(LogDirectory);

    private async Task<IVsWindowFrame> OpenChatWindowAsync()
    {
        await TestServices.Shell.ExecuteCommandAsync("Tools.PillionOpenChat", HangMitigatingCancellationToken);
        IVsWindowFrame? frame = null;
        await WaitUntilAsync(
            async () =>
            {
                frame = await FindChatWindowAsync();
                await JoinableTaskFactory.SwitchToMainThreadAsync(HangMitigatingCancellationToken);
                return frame is not null && frame.IsVisible() == VSConstants.S_OK;
            },
            TimeSpan.FromSeconds(30),
            "the Pillion Chat window to be shown");
        return frame!;
    }

    /// <summary>
    /// One typing pass: routes autocomplete (or switches it off), warms the model up in a separate document, resets the
    /// extension's figures, types the snippet into an empty document of the scratch project and writes the figures.
    /// </summary>
    private async Task RunTypingPassAsync(string name, string? model, bool enabled, string file, string? providerId = null, string profile = "localFast")
    {
        providerId ??= ProviderId;
        await OpenGateSolutionAsync();
        var feature = await GateScenarioChecks.GetAutocompleteFeatureAsync(TimeSpan.FromSeconds(30), HangMitigatingCancellationToken);
        var configured = await GateScenarioChecks.ConfigureAutocompleteAsync(feature, providerId, model, enabled, HangMitigatingCancellationToken, profile);
        Output.WriteLine("Settings: " + configured);

        double? warm = null;
        if (enabled)
        {
            var warmView = await OpenGateDocumentAsync("Warm" + file);
            try
            {
                warm = await GateScenarioChecks.WarmUpAsync(warmView, TimeSpan.FromSeconds(60), HangMitigatingCancellationToken);
                Output.WriteLine($"Warm-up suggestion after {warm?.ToString("0", CultureInfo.InvariantCulture) ?? "(none within 60 s)"} ms.");
            }
            finally
            {
                await CloseGateDocumentAsync("Warm" + file);
            }
        }

        var view = await OpenGateDocumentAsync(file);
        await JoinableTaskFactory.SwitchToMainThreadAsync(HangMitigatingCancellationToken);
        GateScenarioChecks.PrepareEditor(view);
        JObject typing;
        JObject autocomplete;
        var hostBefore = await GateScenarioChecks.CaptureHostCountersAsync(HangMitigatingCancellationToken);
        var started = DateTime.UtcNow;
        try
        {
            GateScenarioChecks.ResetAutocompleteMetrics(feature);
            typing = await GateScenarioChecks.TypeSnippetAsync(view, new TypingOptions(CharacterDelay, LinePause, AcceptMatchingProposals: enabled), () => AcceptSuggestionAsync(view), HangMitigatingCancellationToken);
            await Task.Delay(TimeSpan.FromSeconds(2), HangMitigatingCancellationToken); // Let the last request finish.
            autocomplete = await GateScenarioChecks.DescribeAutocompleteAsync(feature, hostBefore, started, model, HangMitigatingCancellationToken);
        }
        finally
        {
            await CloseGateDocumentAsync(file);
        }

        var version = await GetVisualStudioVersionAsync();
        var result = new JObject
        {
            ["test"] = name,
            ["visualStudio"] = version.ToString(),
            ["model"] = model is null ? null : providerId + "/" + model,
            ["profile"] = profile,
            ["autocompleteEnabled"] = enabled,
            ["settings"] = configured,
            ["warmUpMs"] = warm,
            ["startedUtc"] = started.ToString("o"),
            ["typing"] = typing,
            ["autocomplete"] = autocomplete,
        };
        Output.WriteLine($"Typing: {typing.ToString(Newtonsoft.Json.Formatting.None)}");
        Output.WriteLine($"Autocomplete: {autocomplete.ToString(Newtonsoft.Json.Formatting.None)}");
        Output.WriteLine("Wrote " + GateScenarioChecks.WriteResult(GateOutput, $"{name}-VS{version.Major}", result));
        if (!enabled)
        {
            // Leave autocomplete on for whatever runs next.
            Output.WriteLine("Settings: " + await GateScenarioChecks.ConfigureAutocompleteAsync(feature, ProviderId, null, enabled: true, HangMitigatingCancellationToken));
        }
    }

    /// <summary>Accepts the displayed proposal as Tab does (Edit.AcceptSuggestion on 18.x; the Tab command on 17.x).</summary>
    private async Task AcceptSuggestionAsync(IWpfTextView view)
    {
        try
        {
            await TestServices.Shell.ExecuteCommandAsync("Edit.AcceptSuggestion", HangMitigatingCancellationToken);
        }
        catch (System.Runtime.InteropServices.COMException)
        {
            await JoinableTaskFactory.SwitchToMainThreadAsync(HangMitigatingCancellationToken);
            AutocompleteChecks.Exec(view, VSConstants.VSStd2KCmdID.TAB);
        }

        await JoinableTaskFactory.SwitchToMainThreadAsync(HangMitigatingCancellationToken);
    }

    /// <summary>Runs Pillion: Inline Chat by name, or by its command id when the shell does not know the name.</summary>
    private async Task OpenInlineChatAsync()
    {
        try
        {
            await TestServices.Shell.ExecuteCommandAsync("Tools.PillionInlineChat", HangMitigatingCancellationToken);
            return;
        }
        catch (System.Runtime.InteropServices.COMException ex)
        {
            Output.WriteLine($"Tools.PillionInlineChat by name: {ex.Message}");
        }

        await JoinableTaskFactory.SwitchToMainThreadAsync(HangMitigatingCancellationToken);
        var dispatcher = await TestServices.Shell.GetRequiredGlobalServiceAsync<SUIHostCommandDispatcher, Microsoft.VisualStudio.OLE.Interop.IOleCommandTarget>(HangMitigatingCancellationToken);
        var group = new Guid("444aebc2-1c36-43a9-ab09-2c2d4e04a11d");
        ErrorHandler.ThrowOnFailure(dispatcher.Exec(ref group, 0x0300, 0, IntPtr.Zero, IntPtr.Zero));
    }

    /// <summary>Opens the scratch solution of the gate run (created once per run), unless it is already the open one.</summary>
    private async Task OpenGateSolutionAsync()
    {
        _gateFolder ??= CreateGateSolution();
        await JoinableTaskFactory.SwitchToMainThreadAsync(HangMitigatingCancellationToken);
        var dte = await TestServices.Shell.GetRequiredGlobalServiceAsync<SDTE, DTE>(HangMitigatingCancellationToken);
        var solution = Path.Combine(_gateFolder, "Gate.sln");
        if (string.Equals(dte.Solution?.FullName, solution, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        if (await TestServices.SolutionExplorer.IsSolutionOpenAsync(HangMitigatingCancellationToken))
        {
            await TestServices.SolutionExplorer.CloseSolutionAsync(HangMitigatingCancellationToken);
        }

        await JoinableTaskFactory.SwitchToMainThreadAsync(HangMitigatingCancellationToken);
        dte.Solution!.Open(solution);
        await WaitUntilAsync(
            async () =>
            {
                await JoinableTaskFactory.SwitchToMainThreadAsync(HangMitigatingCancellationToken);
                var vsSolution = await TestServices.Shell.GetRequiredGlobalServiceAsync<SVsSolution, IVsSolution>(HangMitigatingCancellationToken);
                await JoinableTaskFactory.SwitchToMainThreadAsync(HangMitigatingCancellationToken);
                var fullyLoaded = ErrorHandler.Succeeded(vsSolution.GetProperty((int)__VSPROPID4.VSPROPID_IsSolutionFullyLoaded, out var loaded)) && loaded is true;
                return fullyLoaded && dte.Solution.Projects.Count >= 1;
            },
            TimeSpan.FromSeconds(60),
            "the gate scratch project to load");

        // The package loads in the background once a solution is open; Pillion.VisualStudio.Core is only there after that.
        await WaitForPackageLoadedAsync(TimeSpan.FromSeconds(60));
        await PillionHostChecks.WaitForReadyAsync(TimeSpan.FromSeconds(30), HangMitigatingCancellationToken);
        Output.WriteLine("Opened " + solution);
    }

    private async Task<IWpfTextView> OpenGateDocumentAsync(string file)
    {
        var path = Path.Combine(_gateFolder!, file);
        await JoinableTaskFactory.SwitchToMainThreadAsync(HangMitigatingCancellationToken);
        VsShellUtilities.OpenDocument(ServiceProvider.GlobalProvider, path);
        IWpfTextView? view = null;
        await WaitUntilAsync(
            async () =>
            {
                view = await TestServices.Editor.GetActiveTextViewAsync(HangMitigatingCancellationToken);
                await JoinableTaskFactory.SwitchToMainThreadAsync(HangMitigatingCancellationToken);
                return view.TextBuffer.Properties.TryGetProperty(typeof(Microsoft.VisualStudio.Text.ITextDocument), out Microsoft.VisualStudio.Text.ITextDocument document)
                    && string.Equals(document.FilePath, path, StringComparison.OrdinalIgnoreCase);
            },
            TimeSpan.FromSeconds(30),
            file + " to open");

        // The suggestion broker serves the focused view.
        await WaitUntilAsync(
            async () =>
            {
                await JoinableTaskFactory.SwitchToMainThreadAsync(HangMitigatingCancellationToken);
                _ = view!.VisualElement.Focus();
                return view.HasAggregateFocus;
            },
            TimeSpan.FromSeconds(10),
            file + " to get keyboard focus");
        return view!;
    }

    private async Task CloseGateDocumentAsync(string file)
    {
        await JoinableTaskFactory.SwitchToMainThreadAsync(HangMitigatingCancellationToken);
        var path = Path.Combine(_gateFolder!, file);
        if (VsShellUtilities.IsDocumentOpen(ServiceProvider.GlobalProvider, path, Guid.Empty, out _, out _, out var frame) && frame is not null)
        {
            _ = frame.CloseFrame((uint)__FRAMECLOSE.FRAMECLOSE_NoSave);
        }
    }

    /// <summary>
    /// A classic C# project for the scenarios: Program.cs with one compile error (for <c>@errors</c>), Orders.cs with the
    /// method the inline chat runs rewrite (lines 6 to 14), and empty files for the typing passes and their warm-ups.
    /// </summary>
    private static string CreateGateSolution()
    {
        var folder = Path.Combine(Path.GetTempPath(), "pillion-ide-tests", "gate1-" + Guid.NewGuid().ToString("N").Substring(0, 12));
        Directory.CreateDirectory(folder);
        var nl = Environment.NewLine;
        File.WriteAllText(
            Path.Combine(folder, "Program.cs"),
            "using System;" + nl + nl
            + "internal static class Program" + nl + "{" + nl
            + "    private static void Main()" + nl + "    {" + nl
            + "        int number = \"not a number\";" + nl
            + "        Console.WriteLine(number);" + nl + "    }" + nl + "}" + nl);
        File.WriteAllText(
            Path.Combine(folder, "Orders.cs"),
            string.Join(
                nl,
                "using System;",
                "using System.Collections.Generic;",
                string.Empty,
                "public class Orders",
                "{",
                "    private readonly List<Order> _store = new List<Order>();",
                "    public void Place(Order order)",
                "    {",
                "        if (order is null)",
                "        {",
                "            throw new ArgumentNullException(nameof(order));",
                "        }",
                string.Empty,
                "        _store.Add(order);",
                "    }",
                "}",
                string.Empty,
                "public class Order",
                "{",
                "}",
                string.Empty));
        var typingFiles = new[] { "TypeStrong.cs", "TypeFast.cs", "TypeOff.cs", "TypeCloud.cs", "WarmTypeStrong.cs", "WarmTypeFast.cs", "WarmTypeCloud.cs" };
        foreach (var file in typingFiles)
        {
            File.WriteAllText(Path.Combine(folder, file), string.Empty);
        }

        var compile = string.Empty;
        foreach (var file in GateSourceFiles.Concat(typingFiles))
        {
            compile += "    <Compile Include=\"" + file + "\" />" + nl;
        }

        const string ProjectGuid = "{5F1C7E2A-3B4D-4C6E-8F90-A1B2C3D4E5F6}";
        File.WriteAllText(
            Path.Combine(folder, "Gate.csproj"),
            "<Project ToolsVersion=\"15.0\" xmlns=\"http://schemas.microsoft.com/developer/msbuild/2003\">" + nl
            + "  <Import Project=\"$(MSBuildExtensionsPath)\\$(MSBuildToolsVersion)\\Microsoft.Common.props\" Condition=\"Exists('$(MSBuildExtensionsPath)\\$(MSBuildToolsVersion)\\Microsoft.Common.props')\" />" + nl
            + "  <PropertyGroup>" + nl
            + "    <Configuration Condition=\" '$(Configuration)' == '' \">Debug</Configuration>" + nl
            + "    <Platform Condition=\" '$(Platform)' == '' \">AnyCPU</Platform>" + nl
            + "    <ProjectGuid>" + ProjectGuid + "</ProjectGuid>" + nl
            + "    <OutputType>Exe</OutputType>" + nl
            + "    <RootNamespace>Scratch</RootNamespace>" + nl
            + "    <AssemblyName>Gate</AssemblyName>" + nl
            + "    <TargetFrameworkVersion>v4.8</TargetFrameworkVersion>" + nl
            + "  </PropertyGroup>" + nl
            + "  <PropertyGroup Condition=\" '$(Configuration)|$(Platform)' == 'Debug|AnyCPU' \">" + nl
            + "    <OutputPath>bin\\Debug\\</OutputPath>" + nl
            + "  </PropertyGroup>" + nl
            + "  <ItemGroup>" + nl
            + "    <Reference Include=\"System\" />" + nl
            + "    <Reference Include=\"System.Core\" />" + nl
            + "  </ItemGroup>" + nl
            + "  <ItemGroup>" + nl
            + compile
            + "  </ItemGroup>" + nl
            + "  <Import Project=\"$(MSBuildToolsPath)\\Microsoft.CSharp.targets\" />" + nl
            + "</Project>" + nl);
        File.WriteAllText(
            Path.Combine(folder, "Gate.sln"),
            "Microsoft Visual Studio Solution File, Format Version 12.00" + nl
            + "# Visual Studio Version 17" + nl
            + "Project(\"{FAE04EC0-301F-11D3-BF4B-00C04F79EFBC}\") = \"Gate\", \"Gate.csproj\", \"" + ProjectGuid + "\"" + nl
            + "EndProject" + nl
            + "Global" + nl
            + "\tGlobalSection(SolutionConfigurationPlatforms) = preSolution" + nl
            + "\t\tDebug|Any CPU = Debug|Any CPU" + nl
            + "\tEndGlobalSection" + nl
            + "\tGlobalSection(ProjectConfigurationPlatforms) = postSolution" + nl
            + "\t\t" + ProjectGuid + ".Debug|Any CPU.ActiveCfg = Debug|Any CPU" + nl
            + "\t\t" + ProjectGuid + ".Debug|Any CPU.Build.0 = Debug|Any CPU" + nl
            + "\tEndGlobalSection" + nl
            + "EndGlobal" + nl);
        return folder;
    }
}

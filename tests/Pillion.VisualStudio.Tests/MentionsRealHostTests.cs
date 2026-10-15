using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.Threading;
using Pillion.VisualStudio.Core;
using Pillion.VisualStudio.Core.Mentions;

namespace Pillion.VisualStudio.Tests;

// The tests await the view models' tasks; no main thread is involved.
#pragma warning disable VSTHRD003

/// <summary>
/// Mentions v2 (work item 2.13) over the real pipe with <c>tests/Pillion.FakeHost</c>: the picker asks the host for symbols
/// while <c>@symbol:</c> is typed and groups them, a picked symbol pins with its citation, and <c>@git</c>, <c>@output</c> and
/// <c>@docs</c> cross the contract with their qualifier, arguments and payload.
/// </summary>
[Collection(RealProcessTestGroup.Name)]
public sealed class MentionsRealHostTests : IDisposable
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);
    private static readonly WorkspaceFileList List = new([@"C:\fake"], [@"C:\fake\src\Program.cs", @"C:\fake\README.md"]);

    private readonly JoinableTaskContext _context = Task.Run(() => new JoinableTaskContext()).Result;
    private readonly string _logDirectory = Path.Combine(Path.GetTempPath(), "pillion-mention-tests-" + Guid.NewGuid().ToString("N"));
    private readonly PillionLog _log;
    private readonly ChatConversation _conversation = new();
    private readonly FakeEditorContext _editor = new();

    public MentionsRealHostTests() => _log = new PillionLog(_logDirectory);

    private static string FakeHostPath => typeof(MentionsRealHostTests).Assembly
        .GetCustomAttributes<AssemblyMetadataAttribute>()
        .Single(a => a.Key == "FakeHostPath").Value!;

    public void Dispose()
    {
        _context.Dispose();
        try
        {
            Directory.Delete(_logDirectory, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    [Fact]
    public async Task TypingAfterSymbol_ListsTheHostsSymbolsUnderTheirHeading_AndPickingOnePinsItWithItsCitation()
    {
        using var supervisor = await StartSupervisorAsync();
        using var vm = Create(supervisor);

        vm.UpdateInput("@symbol:AmbiguousOrder", 22);
        await vm.WhenSuggestionsDoneAsync().WithTimeout(Timeout);

        var symbols = vm.Picker.Items.Where(i => i.ItemKind == MentionPickerItemKind.Symbol).ToList();
        Assert.Equal(["AmbiguousOrder", "AmbiguousOrder"], symbols.Select(s => s.Label));
        Assert.Equal(["Fake.A.AmbiguousOrder, src/A/AmbiguousOrder.cs:4", "Fake.B.AmbiguousOrder, src/B/AmbiguousOrder.cs:9"], symbols.Select(s => s.Detail));
        Assert.All(vm.Picker.Items, i => Assert.Equal(ChatStrings.MentionGroupSymbols, i.Group));
        Assert.True(vm.Picker.Items[0].IsFirstInGroup);

        await vm.CommitAsync(symbols[1]).WithTimeout(Timeout);

        var pin = Assert.Single(_conversation.Pins);
        Assert.Equal("symbol", pin.Kind);
        Assert.Equal("AmbiguousOrder", pin.DisplayName);
        var citation = Assert.Single(pin.Resolution.Citations);
        Assert.Equal(@"C:\fake\src\AmbiguousOrder.cs", citation.FilePath);
    }

    [Fact]
    public async Task GitOutputAndDocs_CrossThePipe_WithTheirArgumentsAndPayload()
    {
        _editor.Panes["Debug"] = "Debug pane: thread exited";
        using var supervisor = await StartSupervisorAsync();
        using var vm = Create(supervisor);

        vm.UpdateInput("@git:log:3", 10);
        await vm.CommitAsync(Assert.Single(vm.Picker.Items)).WithTimeout(Timeout);
        vm.UpdateInput("@output:de", 10);
        await vm.CommitAsync(Assert.Single(vm.Picker.Items)).WithTimeout(Timeout);
        await vm.ResolveTextAsync("@docs:README").WithTimeout(Timeout);

        Assert.Equal(["Last 3 commits", "Output", "README.md"], _conversation.Pins.Select(p => p.DisplayName));
        Assert.Equal("Debug pane: thread exited", _conversation.Pins[1].Resolution.Content);
        Assert.Equal(["Debug"], _editor.PanesAsked);
        Assert.Single(_conversation.Pins[2].Resolution.Citations);
    }

    private MentionsViewModel Create(HostSupervisor supervisor) =>
        new(supervisor, _context.Factory, _conversation, new FakeWorkspaceFiles(List), _editor, _log) { SuggestionDelay = TimeSpan.Zero };

    private async Task<HostSupervisor> StartSupervisorAsync()
    {
        Assert.True(File.Exists(FakeHostPath), "Build tests/Pillion.FakeHost first: " + FakeHostPath);
        var launcher = new HostProcessLauncher(
            new HostLauncherOptions(FakeHostPath, typeof(HostSupervisor).Assembly.Location, HostLocalTargets.CreateNotImplemented())
            {
                LogDirectory = _logDirectory,
            },
            _log);
        var supervisor = new HostSupervisor(launcher, _context.Factory, _log, SystemClock.Instance, new RestartPolicy());
        supervisor.Start();
        await supervisor.WhenReadyAsync(CancellationToken.None).WithTimeout(Timeout);
        return supervisor;
    }
}

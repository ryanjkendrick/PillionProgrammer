using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.Threading;
using Pillion.Contracts;
using Pillion.VisualStudio.Core;

namespace Pillion.VisualStudio.Tests;

// The tests await the view models' tasks; no main thread is involved.
#pragma warning disable VSTHRD003

/// <summary>
/// The Index window's view model over the real pipe: <c>tests/Pillion.FakeHost</c> started with <c>PILLION_FAKEHOST_INDEX=ready</c>
/// serves the canned index of <c>C:\fake</c>, walks through the phases on rebuild, pauses and resumes, pages the excluded files
/// and lists the indexes (one of them an orphan whose root is gone). The <c>indexStatusChanged</c> notifications cross the
/// real JSON-RPC connection into the view model.
/// </summary>
[Collection(RealProcessTestGroup.Name)]
public sealed class IndexWindowRealHostTests : IDisposable
{
    private const string IndexVariable = "PILLION_FAKEHOST_INDEX";
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

    private readonly JoinableTaskContext _context = Task.Run(() => new JoinableTaskContext()).Result;
    private readonly string _logDirectory = Path.Combine(Path.GetTempPath(), "pillion-idx-tests-" + Guid.NewGuid().ToString("N"));
    private readonly PillionLog _log;
    private readonly List<string> _questions = [];

    public IndexWindowRealHostTests()
    {
        _log = new PillionLog(_logDirectory);
        Environment.SetEnvironmentVariable(IndexVariable, "ready");
    }

    private static string FakeHostPath => typeof(IndexWindowRealHostTests).Assembly
        .GetCustomAttributes<AssemblyMetadataAttribute>()
        .Single(a => a.Key == "FakeHostPath").Value!;

    public void Dispose()
    {
        Environment.SetEnvironmentVariable(IndexVariable, null);
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
    public async Task Open_ShowsTheCannedRoot_TheSecretBearingFilesFirst_AndTheOrphanIndex()
    {
        using var supervisor = await StartSupervisorAsync();
        using var vm = await OpenAsync(supervisor);

        var root = Assert.Single(vm.Roots);
        Assert.Equal(@"C:\fake", root.RootPath);
        Assert.Equal(IndexRole.Writer, root.Role);
        Assert.Equal("40 of 40", root.FilesText);
        Assert.Equal("320", root.ChunksText);
        Assert.Equal("bge-small-en-v1.5", root.ModelText);
        Assert.Equal("Idle (up to date)", vm.PhaseText);

        await Wait.UntilAsync(() => vm.Excluded.Items.Count == 6, "the excluded files");
        Assert.Equal([true, true, false, false, false, false], vm.Excluded.Items.Select(i => i.IsSecretBearing));
        Assert.Equal(".env", vm.Excluded.Items[0].RelativePath);
        Assert.Equal("Secret-bearing file", vm.Excluded.Items[0].KindText);

        await Wait.UntilAsync(() => vm.Folders.Items.Count == 2, "the index folders");
        var orphan = vm.Folders.Items.Single(i => i.IsStale);
        Assert.Equal(@"C:\gone\OldRepo", orphan.RootText);
        Assert.True(orphan.CanDelete);
    }

    [Fact]
    public async Task PauseAndResume_GoThroughTheHost_AndTheNotificationsUpdateTheHeader()
    {
        using var supervisor = await StartSupervisorAsync();
        using var vm = await OpenAsync(supervisor);

        await vm.PauseAsync().WithTimeout(Timeout);
        await Wait.UntilAsync(() => vm.IsPaused, "the paused status");
        Assert.Equal("Paused: you paused it", vm.PauseReasonText);
        Assert.True(vm.ResumeCommand.CanExecute(null));

        await vm.ResumeAsync().WithTimeout(Timeout);
        await Wait.UntilAsync(() => !vm.IsPaused, "the resumed status");
        Assert.Equal("Idle (up to date)", vm.PhaseText);
    }

    [Fact]
    public async Task Rebuild_WalksThePhasesThroughNotifications_AndEndsIdleWithTheRootIndexedAgain()
    {
        using var supervisor = await StartSupervisorAsync();
        using var vm = await OpenAsync(supervisor);
        var phases = new List<string>();
        vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(IndexWindowViewModel.PhaseText))
            {
                lock (phases)
                {
                    phases.Add(vm.PhaseText);
                }
            }
        };

        await vm.RebuildAsync(vm.Roots[0]).WithTimeout(Timeout);
        await Wait.UntilAsync(() => vm.IsIndexing, "the rebuild to start");
        await Wait.UntilAsync(() => !vm.IsIndexing && vm.PhaseText == "Idle (up to date)" && vm.Roots.Count == 1 && vm.Roots[0].IndexId.Length > 0, "the rebuild to finish", timeoutMs: 20000);

        lock (phases)
        {
            Assert.Contains("Embedding chunks", phases);
            Assert.Contains("Parsing and chunking", phases);
            Assert.Equal("Idle (up to date)", phases.Last());
        }

        Assert.Equal("40 of 40", vm.Roots[0].FilesText);
    }

    [Fact]
    public async Task DeletingTheOrphanIndex_AsksFirst_AndRemovesItFromTheList()
    {
        using var supervisor = await StartSupervisorAsync();
        using var vm = await OpenAsync(supervisor);
        await Wait.UntilAsync(() => vm.Folders.Items.Count == 2, "the index folders");
        var orphan = vm.Folders.Items.Single(i => i.IsStale);

        await vm.Folders.DeleteAsync(orphan).WithTimeout(Timeout);

        Assert.Single(_questions);
        Assert.Contains(@"C:\gone\OldRepo", _questions[0], StringComparison.Ordinal);
        Assert.Equal(@"C:\fake", Assert.Single(vm.Folders.Items).RootText);
        Assert.Equal(@"Deleted the index of C:\gone\OldRepo.", vm.ActionMessage);
    }

    private async Task<IndexWindowViewModel> OpenAsync(HostSupervisor supervisor)
    {
        var vm = new IndexWindowViewModel(
            supervisor,
            _context.Factory,
            new IndexWindowOptions
            {
                Log = _log,
                ApplyDelay = TimeSpan.Zero,
                Confirm = question =>
                {
                    _questions.Add(question);
                    return true;
                },
            });
        vm.Start();
        await Wait.UntilAsync(() => vm.Roots.Count == 1, "the first status", timeoutMs: 30000);
        return vm;
    }

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

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.VisualStudio.Threading;
using Pillion.Contracts;
using Pillion.VisualStudio.Core;

namespace Pillion.VisualStudio.Tests;

// The tests await the view models' tasks; no main thread is involved.
#pragma warning disable VSTHRD003

/// <summary>The Index window's view model (work item 2.15) against a scripted host: status updates, pause and resume, rebuild, delete, the excluded files and the index folders.</summary>
[Collection(UiThreadTimingsTests.GroupName)]
public sealed class IndexWindowTests : IDisposable
{
    private readonly JoinableTaskContext _context = Task.Run(() => new JoinableTaskContext()).Result;
    private readonly ScriptedContextService _host = new();
    private readonly InMemorySettingsService _settings = TestModels.Settings();
    private readonly FakeHostConnection _connection;
    private readonly List<string> _copied = [];
    private readonly List<string> _questions = [];
    private readonly List<string> _openedIgnore = [];
    private bool _answer = true;
    private IndexWindowViewModel? _vm;

    public IndexWindowTests()
    {
        _connection = new FakeHostConnection().With<IContextService>(_host).With<ISettingsService>(_settings);
        _host.StatusChanged = status => _ = _connection.RaiseIndexStatusChangedAsync(status);
        _host.Status = IndexData.Idle(IndexData.Root(IndexData.RootA));
    }

    public void Dispose()
    {
        _vm?.Dispose();
        _context.Dispose();
    }

    [Fact]
    public async Task Open_ReadsTheStatusTheIndexFoldersAndTheFirstRootsExclusionsOnce_AndNeverPolls()
    {
        SeedExcluded();
        _host.Indexes.Add(IndexData.Folder("id-alpha", IndexData.RootA, inUse: true, open: true));
        var vm = Create();

        vm.Start();
        await Wait.UntilAsync(() => vm.Excluded.Items.Count > 0 && vm.Folders.Items.Count > 0, "the first reads");
        await Task.Delay(300);

        Assert.Equal(1, _host.Count("GetIndexStatusAsync"));
        Assert.Equal(1, _host.Count("ListIndexesAsync"));
        Assert.Equal(1, _host.Count("ListExcludedFilesAsync"));
        Assert.Equal(IndexData.RootA, Assert.Single(vm.Roots).RootPath);
        Assert.Equal(IndexData.RootA, vm.Excluded.SelectedRoot?.RootPath);
        Assert.Equal("Idle (up to date)", vm.PhaseText);
        Assert.False(vm.IsProgressVisible);
        Assert.Contains("up to date", vm.SummaryText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ARootRow_ShowsPathRoleCountsDiskModelAndLastSweep()
    {
        _host.Status = IndexData.Idle(IndexData.Root(IndexData.RootA, files: 1234, chunks: 56789, bytes: 5_500_000, role: IndexRole.Reader));
        var vm = await StartAsync();

        var row = Assert.Single(vm.Roots);

        Assert.Equal(IndexData.RootA, row.RootPath);
        Assert.Equal(IndexRole.Reader, row.Role);
        Assert.StartsWith("Reader", row.RoleText, StringComparison.Ordinal);
        Assert.Equal("1,234 of 1,234", row.FilesText);
        Assert.Equal("56,789", row.ChunksText);
        Assert.Equal("5.2 MB", row.DiskText);
        Assert.Equal("bge-small-en-v1.5", row.ModelText);
        Assert.DoesNotContain("Never", row.LastSweepText, StringComparison.Ordinal);
        Assert.Contains(IndexData.RootA, row.AutomationName, StringComparison.Ordinal);
        Assert.Contains(IndexData.RootA, row.RebuildName, StringComparison.Ordinal);
        Assert.Contains(IndexData.RootA, row.DeleteName, StringComparison.Ordinal);
        Assert.Contains("1 root(s)", vm.TotalsText, StringComparison.Ordinal);
        Assert.Equal(string.Empty, vm.NoRootsText);
    }

    [Fact]
    public async Task Notifications_WalkThePhases_UpdateTheHeaderAndTheRowInPlace()
    {
        var vm = await StartAsync();
        var row = vm.Roots[0];
        var changes = new List<string?>();
        row.PropertyChanged += (_, e) => changes.Add(e.PropertyName);

        var steps = new (IndexPhase Phase, int Done, long Chunks, int Remaining, string Phase_Text)[]
        {
            (IndexPhase.Discovering, 0, 0, -1, "Discovering files"),
            (IndexPhase.Hashing, 10, 0, 3, "Hashing files"),
            (IndexPhase.Chunking, 20, 80, 2, "Parsing and chunking"),
            (IndexPhase.Embedding, 30, 160, 1, "Embedding chunks"),
            (IndexPhase.Storing, 40, 320, 0, "Storing"),
        };
        foreach (var step in steps)
        {
            var root = IndexData.Root(IndexData.RootA, files: step.Done, chunks: step.Chunks);
            await RaiseAsync(IndexData.Running(step.Phase, step.Done, 40, step.Chunks, step.Remaining, root));
            await Wait.UntilAsync(() => vm.PhaseText == step.Phase_Text, step.Phase_Text);

            Assert.True(vm.IsIndexing);
            Assert.True(vm.IsProgressVisible);
            Assert.Equal(step.Done / 40.0 * 100.0, vm.ProgressPercent, 3);
            Assert.Equal($"{step.Done} of 40 files, {step.Chunks} chunks", vm.ProgressText);
            Assert.Same(row, vm.Roots[0]);
            Assert.Equal(step.Remaining < 0 ? string.Empty : IndexFormat.Remaining(step.Remaining), vm.RemainingText);
        }

        Assert.Contains(nameof(IndexRootRowViewModel.ChunksText), changes);
        Assert.Contains(nameof(IndexRootRowViewModel.FilesText), changes);
        Assert.Single(vm.Roots);

        await RaiseAsync(IndexData.Idle(IndexData.Root(IndexData.RootA)));
        await Wait.UntilAsync(() => vm.PhaseText == "Idle (up to date)", "idle");
        Assert.False(vm.IsIndexing);
        Assert.False(vm.IsProgressVisible);
        Assert.Equal(string.Empty, vm.ProgressText);
    }

    [Fact]
    public async Task ABurstOfNotifications_IsAppliedAsOneBatch_AndTheLastStatusWins()
    {
        var vm = await StartAsync(applyDelay: TimeSpan.FromMilliseconds(120));
        var applied = 0;
        vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(IndexWindowViewModel.SummaryText))
            {
                applied++;
            }
        };

        for (var i = 1; i <= 40; i++)
        {
            await RaiseAsync(IndexData.Running(IndexPhase.Embedding, i, 40, i * 8, 5, IndexData.Root(IndexData.RootA, files: i)));
        }

        await Wait.UntilAsync(() => vm.ProgressText.StartsWith("40 of 40", StringComparison.Ordinal), "the last status");
        await Task.Delay(300);

        Assert.InRange(applied, 1, 8);
    }

    [Fact]
    public async Task Pause_CallsTheHost_ShowsThePausedReason_AndResumeUndoesIt()
    {
        var vm = await StartAsync();
        Assert.True(vm.PauseCommand.CanExecute(null));
        Assert.False(vm.ResumeCommand.CanExecute(null));

        await vm.PauseAsync();
        await Wait.UntilAsync(() => vm.IsPaused, "the paused status");

        Assert.True(_host.PauseCalls.TryPeek(out var first) && first);
        Assert.Equal("Paused: you paused it", vm.PauseReasonText);
        Assert.True(vm.HasPauseReason);
        Assert.False(vm.PauseCommand.CanExecute(null));
        Assert.True(vm.ResumeCommand.CanExecute(null));

        await vm.ResumeAsync();
        await Wait.UntilAsync(() => !vm.IsPaused, "the resumed status");

        Assert.Equal([true, false], _host.PauseCalls.ToArray());
        Assert.Equal(string.Empty, vm.PauseReasonText);
        Assert.True(vm.PauseCommand.CanExecute(null));
    }

    [Theory]
    [InlineData(IndexPauseReason.Build, "Paused: a build is running")]
    [InlineData(IndexPauseReason.Test, "Paused: tests are running")]
    [InlineData(IndexPauseReason.Debug, "Paused: the debugger is attached")]
    [InlineData(IndexPauseReason.Battery, "Paused: battery saver is on")]
    [InlineData(IndexPauseReason.Memory, "Paused: the memory limit was reached")]
    [InlineData(IndexPauseReason.LowDisk, "Paused: disk space is low")]
    public async Task ThePauseReasonsTheHostChooses_AreNamedInTheHeader_AndCanStillBePausedByTheUser(IndexPauseReason reason, string expected)
    {
        var vm = await StartAsync();

        await RaiseAsync(new IndexStatus { Phase = IndexPhase.Paused, IsPaused = true, PauseReason = reason, Roots = _host.Status.Roots });
        await Wait.UntilAsync(() => vm.IsPaused, "paused");

        Assert.Equal(expected, vm.PauseReasonText);
        Assert.Equal("Paused", vm.PhaseText);
        Assert.True(vm.PauseCommand.CanExecute(null));
        Assert.True(vm.ResumeCommand.CanExecute(null));
    }

    [Fact]
    public async Task APauseThatFails_IsShownAndTheStateStaysAsItWas()
    {
        _host.Failures["PauseIndexingAsync"] = new PillionRpcException(new PillionError { Code = PillionErrorCode.NotImplemented, Message = "Not implemented yet." });
        var vm = await StartAsync();

        await vm.PauseAsync();

        Assert.Contains("Could not pause indexing: Not implemented yet.", vm.ActionMessage, StringComparison.Ordinal);
        Assert.False(vm.IsPaused);
    }

    [Fact]
    public async Task Rebuild_AsksTheHostForTheRoot_AndTheProgressArrivesAsNotifications()
    {
        var vm = await StartAsync();
        var row = vm.Roots[0];
        Assert.True(row.RebuildCommand.CanExecute(null));

        await vm.RebuildAsync(row);
        await RaiseAsync(IndexData.Running(IndexPhase.Discovering, 0, 40, 0, -1, IndexData.Root(IndexData.RootA, files: 0, chunks: 0)));
        await Wait.UntilAsync(() => vm.IsIndexing, "the rebuild to show");

        Assert.Equal(IndexData.RootA, Assert.Single(_host.RebuildCalls));
        Assert.Equal($"Rebuild of {IndexData.RootA} started.", vm.ActionMessage);
        Assert.False(row.RebuildCommand.CanExecute(null));
        Assert.False(row.DeleteCommand.CanExecute(null));
        Assert.True(vm.IsProgressIndeterminate || vm.ProgressPercent == 0);

        await RaiseAsync(IndexData.Idle(IndexData.Root(IndexData.RootA)));
        await Wait.UntilAsync(() => !vm.IsIndexing, "the rebuild to finish");
        Assert.True(row.RebuildCommand.CanExecute(null));
    }

    [Fact]
    public async Task ARebuildTheHostRefuses_IsShownWithTheHostsReason()
    {
        _host.Failures["RebuildIndexAsync"] = new PillionRpcException(new PillionError { Code = PillionErrorCode.PolicyDenied, Message = "The indexing feature is switched off." });
        var vm = await StartAsync();

        await vm.RebuildAsync(vm.Roots[0]);

        Assert.Equal($"Could not rebuild {IndexData.RootA}: The indexing feature is switched off.", vm.ActionMessage);
    }

    [Fact]
    public async Task Delete_AsksFirst_AndCallsTheHostWithTheIndexIdOnlyOnYes()
    {
        _host.Status = IndexData.Idle(IndexData.Root(IndexData.RootA, id: "alpha-id"));
        _host.Indexes.Add(IndexData.Folder("alpha-id", IndexData.RootA, open: true, inUse: true));
        var vm = await StartAsync();
        var row = vm.Roots[0];

        _answer = false;
        await vm.DeleteAsync(row);

        Assert.Empty(_host.DeleteCalls);
        Assert.Single(_questions);
        Assert.Contains(IndexData.RootA, _questions[0], StringComparison.Ordinal);

        _answer = true;
        await vm.DeleteAsync(row);

        Assert.Equal("alpha-id", Assert.Single(_host.DeleteCalls));
        Assert.Equal($"Deleted the index of {IndexData.RootA}.", vm.ActionMessage);
    }

    [Fact]
    public async Task Delete_WithoutAConfirmationHandler_DeletesNothing()
    {
        var vm = await StartAsync(confirm: false);

        await vm.DeleteAsync(vm.Roots[0]);

        Assert.Empty(_host.DeleteCalls);
    }

    [Fact]
    public async Task ARootWithoutAnIndexYet_CanBeRebuiltButNotDeleted()
    {
        _host.Status = IndexData.Idle(new IndexRootStatus { RootPath = IndexData.RootB, Role = IndexRole.Unknown });
        var vm = await StartAsync();
        var row = vm.Roots[0];

        Assert.True(row.RebuildCommand.CanExecute(null));
        Assert.False(row.DeleteCommand.CanExecute(null));
        Assert.Equal("unknown", row.ModelText);
        Assert.StartsWith("Not open", row.RoleText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheRootsAreReconciled_RowsKeepTheirIdentity_AndAddedOrRemovedRootsAppearOrGo()
    {
        _host.Status = IndexData.Idle(IndexData.Root(IndexData.RootA), IndexData.Root(IndexData.RootB));
        var vm = await StartAsync();
        var a = vm.Roots[0];
        var b = vm.Roots[1];

        await RaiseAsync(IndexData.Idle(IndexData.Root(IndexData.RootB, chunks: 999)));
        await Wait.UntilAsync(() => vm.Roots.Count == 1, "one root");

        Assert.Same(b, vm.Roots[0]);
        Assert.Equal("999", b.ChunksText);

        await RaiseAsync(IndexData.Idle(IndexData.Root(IndexData.RootA), IndexData.Root(IndexData.RootB)));
        await Wait.UntilAsync(() => vm.Roots.Count == 2, "two roots");

        Assert.NotSame(a, vm.Roots[0]);
        Assert.Same(b, vm.Roots[1]);
    }

    [Fact]
    public async Task AFailedIndexer_ShowsItsLastError_AndAnEmptyHostShowsNoRoots()
    {
        var vm = await StartAsync();

        await RaiseAsync(new IndexStatus { Phase = IndexPhase.Failed, LastError = "Disk full while writing the index.", Roots = _host.Status.Roots });
        await Wait.UntilAsync(() => vm.HasLastError, "the error");

        Assert.Equal("Disk full while writing the index.", vm.LastError);
        Assert.Equal("Failed", vm.PhaseText);
        Assert.Contains("Disk full", vm.SummaryText, StringComparison.Ordinal);

        await RaiseAsync(new IndexStatus());
        await Wait.UntilAsync(() => !vm.HasRoots, "no roots");
        Assert.False(vm.HasLastError);
        Assert.NotEqual(string.Empty, vm.NoRootsText);
    }

    [Fact]
    public async Task ANotificationThatArrivesDuringTheOpeningRead_IsNotOverwrittenByIt()
    {
        var gate = new TaskCompletionSource<bool>();
        var slow = new GatedStatusService(_host, gate.Task);
        var connection = new FakeHostConnection().With<IContextService>(slow);
        var vm = new IndexWindowViewModel(connection, _context.Factory, new IndexWindowOptions { ApplyDelay = TimeSpan.Zero });
        _vm = vm;

        vm.Start();
        await Wait.UntilAsync(() => slow.Started, "the opening read to start");
        await connection.RaiseIndexStatusChangedAsync(IndexData.Running(IndexPhase.Embedding, 5, 10, 50, 1, IndexData.Root(IndexData.RootB)));
        await Wait.UntilAsync(() => vm.Roots.Count == 1 && vm.Roots[0].RootPath == IndexData.RootB, "the notification");
        gate.SetResult(true);
        await Wait.UntilAsync(() => _host.Count("GetIndexStatusAsync") >= 1 && vm.Folders.Items.Count >= 0 && vm.PhaseText == "Embedding chunks", "the read to finish");
        await Task.Delay(100);

        Assert.Equal(IndexData.RootB, vm.Roots[0].RootPath);
        Assert.Equal("Embedding chunks", vm.PhaseText);
    }

    [Fact]
    public async Task WhileTheHostIsNotReady_TheWindowSaysSo_DisablesTheButtons_AndLoadsWhenItIsReady()
    {
        var connection = new FakeHostConnection(HostState.Starting).With<IContextService>(_host);
        var vm = new IndexWindowViewModel(connection, _context.Factory, new IndexWindowOptions { ApplyDelay = TimeSpan.Zero });
        _vm = vm;

        vm.Start();
        await Task.Delay(100);

        Assert.True(vm.HasHostMessage);
        Assert.Contains("Host not ready", vm.HostMessage, StringComparison.Ordinal);
        Assert.False(vm.PauseCommand.CanExecute(null));
        Assert.False(vm.RefreshCommand.CanExecute(null));
        Assert.Equal(0, _host.Count("GetIndexStatusAsync"));

        await connection.SetStateAsync(HostState.Ready);
        await Wait.UntilAsync(() => vm.Roots.Count == 1, "the status after the host is ready");

        Assert.False(vm.HasHostMessage);
        Assert.True(vm.PauseCommand.CanExecute(null));
    }

    [Fact]
    public async Task AHostThatCannotReportTheIndex_ShowsWhyInsteadOfFailing()
    {
        _host.Failures["GetIndexStatusAsync"] = new PillionRpcException(new PillionError { Code = PillionErrorCode.NotImplemented, Message = "Index status is not implemented yet." });
        var connection = new FakeHostConnection().With<IContextService>(_host);
        var vm = new IndexWindowViewModel(connection, _context.Factory, new IndexWindowOptions { ApplyDelay = TimeSpan.Zero });
        _vm = vm;

        vm.Start();
        await Wait.UntilAsync(() => vm.HasLoadMessage, "the message");

        Assert.Equal("Could not read the index status: Index status is not implemented yet.", vm.LoadMessage);
        Assert.False(vm.HasStatus);
    }

    [Fact]
    public async Task WhileTheIndexWindowFlagIsOff_TheWindowIsDisabled_AndCommandsAreOff()
    {
        _settings.Values[ChatAvailability.EffectiveFeaturesKey] = "{\"chat\":true,\"indexWindow\":false}";
        using var availability = new ChatAvailability(_connection);
        await availability.RefreshAsync();
        var vm = new IndexWindowViewModel(_connection, _context.Factory, new IndexWindowOptions { ApplyDelay = TimeSpan.Zero, Availability = availability });
        _vm = vm;

        vm.Start();
        await Task.Delay(100);

        Assert.True(vm.IsWindowDisabled);
        Assert.Equal("The Index window is disabled by settings or policy", vm.DisabledMessage);
        Assert.False(vm.PauseCommand.CanExecute(null));

        _settings.Values[ChatAvailability.EffectiveFeaturesKey] = "{\"chat\":true,\"indexWindow\":true}";
        await availability.RefreshAsync();
        await Wait.UntilAsync(() => !vm.IsWindowDisabled, "the window to be enabled");
        Assert.True(vm.PauseCommand.CanExecute(null));
    }

    [Fact]
    public async Task Excluded_PagesTheList_SecretBearingFirstAndMarked_AndShowsWhichEntriesAreShown()
    {
        SeedExcluded();
        var vm = await StartAsync(pageSize: 2);
        await Wait.UntilAsync(() => vm.Excluded.Items.Count == 2, "page one");

        var excluded = vm.Excluded;
        Assert.Equal(5, excluded.TotalCount);
        Assert.True(excluded.HasMore);
        Assert.Equal("Showing 1-2 of 5", excluded.PageText);
        Assert.All(excluded.Items, i => Assert.True(i.IsSecretBearing));
        Assert.All(excluded.Items, i => Assert.Equal("Secret-bearing files (never indexed)", i.GroupName));
        Assert.Equal([".env", "src/appsettings.Development.json"], excluded.Items.Select(i => i.RelativePath));
        Assert.Contains("Secret-bearing file", excluded.Items[0].AutomationName, StringComparison.Ordinal);
        Assert.True(excluded.NextPageCommand.CanExecute(null));
        Assert.False(excluded.PreviousPageCommand.CanExecute(null));

        excluded.NextPageCommand.Execute(null);
        await Wait.UntilAsync(() => excluded.PageIndex == 1 && excluded.Items.Count == 2 && !excluded.IsLoading, "page two");

        Assert.Equal("Showing 3-4 of 5", excluded.PageText);
        Assert.All(excluded.Items, i => Assert.False(i.IsSecretBearing));
        Assert.All(excluded.Items, i => Assert.Equal("Other exclusions", i.GroupName));
        Assert.True(excluded.Items[0].IsDirectory);
        Assert.Equal("Folder", excluded.Items[0].KindText);
        Assert.True(excluded.PreviousPageCommand.CanExecute(null));

        excluded.NextPageCommand.Execute(null);
        await Wait.UntilAsync(() => excluded.PageIndex == 2 && excluded.Items.Count == 1 && !excluded.IsLoading, "page three");

        Assert.False(excluded.HasMore);
        Assert.False(excluded.NextPageCommand.CanExecute(null));
        Assert.Equal("Showing 5-5 of 5", excluded.PageText);
        Assert.Equal([0, 2, 4], _host.ExcludedRequests.Select(r => r.Offset));
        Assert.All(_host.ExcludedRequests, r => Assert.Equal(IndexData.RootA, r.RootPath));
        Assert.All(_host.ExcludedRequests, r => Assert.Equal(2, r.Limit));
    }

    [Fact]
    public async Task Excluded_SecretOnlyAsksTheHostForJustTheSecretBearingFiles()
    {
        SeedExcluded();
        var vm = await StartAsync();
        await Wait.UntilAsync(() => vm.Excluded.Items.Count == 5, "the list");

        vm.Excluded.SecretOnly = true;
        await Wait.UntilAsync(() => vm.Excluded.Items.Count == 2 && !vm.Excluded.IsLoading, "the secret-bearing files");

        Assert.True(_host.ExcludedRequests.Last().SecretOnly);
        Assert.Equal("Showing 1-2 of 2", vm.Excluded.PageText);
    }

    [Fact]
    public async Task Excluded_CopyPathPutsTheFullPathOnTheClipboard_AndOpenPillionIgnoreNamesTheRoot()
    {
        SeedExcluded();
        var vm = await StartAsync();
        await Wait.UntilAsync(() => vm.Excluded.Items.Count == 5, "the list");

        Assert.False(vm.Excluded.CopyPathCommand.CanExecute(null));
        vm.Excluded.SelectedItem = vm.Excluded.Items[1];
        Assert.True(vm.Excluded.CopyPathCommand.CanExecute(null));
        vm.Excluded.CopyPathCommand.Execute(null);
        vm.Excluded.OpenPillionIgnoreCommand.Execute(null);

        Assert.Equal(IndexData.RootA + @"\src\appsettings.Development.json", Assert.Single(_copied));
        Assert.Equal(IndexData.RootA, Assert.Single(_openedIgnore));
    }

    [Fact]
    public async Task Excluded_ChoosingAnotherRootLoadsItsFirstPage()
    {
        _host.Status = IndexData.Idle(IndexData.Root(IndexData.RootA), IndexData.Root(IndexData.RootB));
        SeedExcluded();
        var vm = await StartAsync();
        await Wait.UntilAsync(() => vm.Excluded.Items.Count == 5, "the first root's list");

        vm.Excluded.SelectedRoot = vm.Roots[1];
        await Wait.UntilAsync(() => _host.ExcludedRequests.Count == 2 && !vm.Excluded.IsLoading, "the second root's list");

        Assert.Equal(IndexData.RootB, _host.ExcludedRequests.Last().RootPath);
        Assert.Equal(0, _host.ExcludedRequests.Last().Offset);
    }

    [Fact]
    public async Task Excluded_IsReadAgainOnlyWhenTheChosenRootsExclusionCountsChange()
    {
        SeedExcluded();
        var vm = await StartAsync();
        await Wait.UntilAsync(() => vm.Excluded.Items.Count == 5, "the list");
        var reads = _host.Count("ListExcludedFilesAsync");

        await RaiseAsync(IndexData.Running(IndexPhase.Hashing, 10, 40, 0, 3, IndexData.Root(IndexData.RootA)));
        await RaiseAsync(IndexData.Running(IndexPhase.Chunking, 20, 40, 80, 2, IndexData.Root(IndexData.RootA)));
        await Task.Delay(300);
        Assert.Equal(reads, _host.Count("ListExcludedFilesAsync"));

        await RaiseAsync(IndexData.Idle(IndexData.Root(IndexData.RootA, excluded: 7, secret: 3)));
        await Wait.UntilAsync(() => _host.Count("ListExcludedFilesAsync") == reads + 1, "a new read");
    }

    [Fact]
    public async Task Excluded_AFailureIsShownOnTheTab()
    {
        _host.Failures["ListExcludedFilesAsync"] = new PillionRpcException(new PillionError { Code = PillionErrorCode.NotImplemented, Message = "Not implemented yet." });
        var vm = await StartAsync();
        await Wait.UntilAsync(() => vm.Excluded.HasMessage, "the message");

        Assert.Equal("Could not read the excluded files: Not implemented yet.", vm.Excluded.Message);
        Assert.Empty(vm.Excluded.Items);
    }

    [Fact]
    public async Task Folders_ListEveryIndexWithDiskUse_Staleness_AndUse()
    {
        _host.Indexes.Add(IndexData.Folder("alpha-id", IndexData.RootA, bytes: 2_097_152, inUse: true, open: true));
        _host.Indexes.Add(IndexData.Folder("old-id", @"C:\gone\Old", bytes: 4_200_000, rootExists: false));
        _host.Indexes.Add(IndexData.Folder("other-id", @"C:\elsewhere", bytes: 100, inUse: true));
        _host.Indexes.Add(IndexData.Folder("nostate-id", null, bytes: 0, rootExists: null));
        var vm = await StartAsync();
        await Wait.UntilAsync(() => vm.Folders.Items.Count == 4, "the folders");

        var rows = vm.Folders.Items.ToDictionary(r => r.IndexId);
        Assert.True(rows["old-id"].IsStale);
        Assert.Equal("Root is gone (stale)", rows["old-id"].RootExistsText);
        Assert.Equal("4.0 MB", rows["old-id"].DiskText);
        Assert.True(rows["old-id"].CanDelete);
        Assert.False(rows["alpha-id"].IsStale);
        Assert.Equal("Open in this Visual Studio", rows["alpha-id"].InUseText);
        Assert.True(rows["alpha-id"].CanDelete);
        Assert.Equal("In use by another running host", rows["other-id"].InUseText);
        Assert.False(rows["other-id"].CanDelete);
        Assert.False(rows["other-id"].DeleteCommand.CanExecute(null));
        Assert.Equal("(unknown root)", rows["nostate-id"].RootText);
        Assert.StartsWith("Root unknown", rows["nostate-id"].RootExistsText, StringComparison.Ordinal);
        Assert.Contains("4 index(es)", vm.Folders.Summary, StringComparison.Ordinal);
        Assert.Contains("1 stale", vm.Folders.Summary, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Folders_DeleteAStaleIndex_AsksFirst_DeletesIt_AndReadsTheListAgain()
    {
        _host.Indexes.Add(IndexData.Folder("old-id", @"C:\gone\Old", rootExists: false));
        _host.Indexes.Add(IndexData.Folder("alpha-id", IndexData.RootA, inUse: true, open: true));
        var vm = await StartAsync();
        await Wait.UntilAsync(() => vm.Folders.Items.Count == 2, "the folders");
        var stale = vm.Folders.Items.Single(i => i.IsStale);

        _answer = false;
        await vm.Folders.DeleteAsync(stale);
        Assert.Empty(_host.DeleteCalls);
        Assert.Equal(2, vm.Folders.Items.Count);

        _answer = true;
        await vm.Folders.DeleteAsync(stale);

        Assert.Equal("old-id", Assert.Single(_host.DeleteCalls));
        Assert.Equal(@"Deleted the index of C:\gone\Old.", vm.ActionMessage);
        Assert.Equal("alpha-id", Assert.Single(vm.Folders.Items).IndexId);
    }

    [Fact]
    public async Task Folders_ADeleteTheHostRefuses_ShowsItsReason_AndKeepsTheRow()
    {
        _host.Indexes.Add(IndexData.Folder("busy-id", @"C:\busy", inUse: true, open: true));
        _host.Failures["DeleteIndexAsync"] = new PillionRpcException(new PillionError { Code = PillionErrorCode.PolicyDenied, Message = "Another Visual Studio owns this index." });
        var vm = await StartAsync();
        await Wait.UntilAsync(() => vm.Folders.Items.Count == 1, "the folder");

        await vm.Folders.DeleteAsync(vm.Folders.Items[0]);

        Assert.Equal(@"Could not delete the index of C:\busy: Another Visual Studio owns this index.", vm.ActionMessage);
        Assert.Single(vm.Folders.Items);
        Assert.Contains("closed first", _questions.Last(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Folders_AreReadAgain_WhenARebuildFinishesOrTheRootsGetANewIndex()
    {
        var vm = await StartAsync();
        await Wait.UntilAsync(() => _host.Count("ListIndexesAsync") == 1, "the opening read");

        await RaiseAsync(IndexData.Running(IndexPhase.Storing, 40, 40, 320, 0, IndexData.Root(IndexData.RootA)));
        await Wait.UntilAsync(() => vm.IsIndexing, "indexing");
        Assert.Equal(1, _host.Count("ListIndexesAsync"));

        await RaiseAsync(IndexData.Idle(IndexData.Root(IndexData.RootA)));
        await Wait.UntilAsync(() => _host.Count("ListIndexesAsync") == 2, "a read after the run finished");

        await RaiseAsync(IndexData.Idle(IndexData.Root(IndexData.RootA, id: "new-id")));
        await Wait.UntilAsync(() => _host.Count("ListIndexesAsync") == 3, "a read after the index id changed");
    }

    [Fact]
    public async Task Dispose_StopsFollowingTheHost()
    {
        var vm = await StartAsync();
        vm.Dispose();

        await RaiseAsync(IndexData.Running(IndexPhase.Embedding, 5, 10, 50, 1, IndexData.Root(IndexData.RootB)));
        await Task.Delay(200);

        Assert.Equal(IndexData.RootA, Assert.Single(vm.Roots).RootPath);
    }

    private IndexWindowViewModel Create(TimeSpan? applyDelay = null, bool confirm = true, int pageSize = 200)
    {
        var options = new IndexWindowOptions
        {
            ApplyDelay = applyDelay ?? TimeSpan.Zero,
            ExcludedPageSize = pageSize,
            CopyText = _copied.Add,
            OpenPillionIgnore = _openedIgnore.Add,
        };
        if (confirm)
        {
            options.Confirm = question =>
            {
                _questions.Add(question);
                return _answer;
            };
        }

        _vm = new IndexWindowViewModel(_connection, _context.Factory, options);
        return _vm;
    }

    private async Task<IndexWindowViewModel> StartAsync(TimeSpan? applyDelay = null, bool confirm = true, int pageSize = 200)
    {
        var vm = Create(applyDelay, confirm, pageSize);
        vm.Start();
        await Wait.UntilAsync(() => vm.HasStatus, "the first status");
        await Wait.UntilAsync(() => !vm.Excluded.IsLoading && !vm.Folders.IsLoading, "the opening reads");
        return vm;
    }

    private Task RaiseAsync(IndexStatus status)
    {
        _host.Status = status;
        return _connection.RaiseIndexStatusChangedAsync(status);
    }

    private void SeedExcluded()
    {
        _host.Excluded.Add(IndexData.File(".env", "SecretBearing", secret: true));
        _host.Excluded.Add(IndexData.File("src/appsettings.Development.json", "SecretBearing", secret: true));
        _host.Excluded.Add(IndexData.File("bin", "BuiltIn", directory: true));
        _host.Excluded.Add(IndexData.File("obj", "BuiltIn", directory: true));
        _host.Excluded.Add(IndexData.File("package-lock.json", "LockFile"));
    }

    /// <summary>A context service whose first status read waits for the test.</summary>
    private sealed class GatedStatusService(ScriptedContextService inner, Task gate) : IContextService
    {
        private volatile bool _started;

        public bool Started => _started;

        public async Task<IndexStatus> GetIndexStatusAsync(System.Threading.CancellationToken cancellationToken)
        {
            _started = true;
            await gate.ConfigureAwait(false);
            return await inner.GetIndexStatusAsync(cancellationToken).ConfigureAwait(false);
        }

        public Task PauseIndexingAsync(bool pause, System.Threading.CancellationToken cancellationToken) => inner.PauseIndexingAsync(pause, cancellationToken);

        public Task<ExcludedFilesPage> ListExcludedFilesAsync(ExcludedFilesRequest request, System.Threading.CancellationToken cancellationToken) => inner.ListExcludedFilesAsync(request, cancellationToken);

        public Task RebuildIndexAsync(string rootPath, System.Threading.CancellationToken cancellationToken) => inner.RebuildIndexAsync(rootPath, cancellationToken);

        public Task<bool> DeleteIndexAsync(string indexId, System.Threading.CancellationToken cancellationToken) => inner.DeleteIndexAsync(indexId, cancellationToken);

        public Task<IndexInfo[]> ListIndexesAsync(System.Threading.CancellationToken cancellationToken) => inner.ListIndexesAsync(cancellationToken);

        public Task SetWorkspaceAsync(WorkspaceDescription workspace, System.Threading.CancellationToken cancellationToken) => inner.SetWorkspaceAsync(workspace, cancellationToken);

        public Task<SearchResult[]> SearchAsync(SearchRequest request, System.Threading.CancellationToken cancellationToken) => inner.SearchAsync(request, cancellationToken);

        public Task<SearchResponse> SearchDetailedAsync(SearchRequest request, System.Threading.CancellationToken cancellationToken) => inner.SearchDetailedAsync(request, cancellationToken);

        public Task<MentionResolution> ResolveMentionAsync(MentionRequest request, System.Threading.CancellationToken cancellationToken) => inner.ResolveMentionAsync(request, cancellationToken);
    }
}

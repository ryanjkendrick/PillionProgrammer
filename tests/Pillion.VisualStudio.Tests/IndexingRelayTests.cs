using System;
using System.Collections.Concurrent;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Pillion.Contracts;
using Pillion.VisualStudio.Core.Signals;
using StreamJsonRpc;
using StreamJsonRpc.Protocol;

namespace Pillion.VisualStudio.Tests;

/// <summary>An <see cref="IContextService"/> that records <see cref="SetWorkspaceAsync"/> and can be made to fail.</summary>
internal sealed class RecordingContextService : IContextService
{
    public ConcurrentQueue<WorkspaceDescription> Workspaces { get; } = new();

    public Func<WorkspaceDescription, Exception?> Fail { get; set; } = _ => null;

    public int Attempts;

    public Task SetWorkspaceAsync(WorkspaceDescription workspace, CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref Attempts);
        if (Fail(workspace) is { } error)
        {
            return Task.FromException(error);
        }

        Workspaces.Enqueue(workspace);
        return Task.CompletedTask;
    }

    public Task<SearchResult[]> SearchAsync(SearchRequest request, CancellationToken cancellationToken) => throw new NotSupportedException();

    public Task<MentionResolution> ResolveMentionAsync(MentionRequest request, CancellationToken cancellationToken) => throw new NotSupportedException();

    public Task<IndexStatus> GetIndexStatusAsync(CancellationToken cancellationToken) => throw new NotSupportedException();

    public Task PauseIndexingAsync(bool pause, CancellationToken cancellationToken) => throw new NotSupportedException();

    public Task<SearchResponse> SearchDetailedAsync(SearchRequest request, CancellationToken cancellationToken) => throw new NotSupportedException();

    public Task<ExcludedFilesPage> ListExcludedFilesAsync(ExcludedFilesRequest request, CancellationToken cancellationToken) => throw new NotSupportedException();

    public Task RebuildIndexAsync(string rootPath, CancellationToken cancellationToken) => throw new NotSupportedException();

    public Task<bool> DeleteIndexAsync(string indexId, CancellationToken cancellationToken) => throw new NotSupportedException();

    public Task<IndexInfo[]> ListIndexesAsync(CancellationToken cancellationToken) => throw new NotSupportedException();
}

/// <summary>A workspace source whose description and change events are driven by the test.</summary>
internal sealed class FakeWorkspaceSource : IWorkspaceDescriptionSource
{
    private WorkspaceDescription _current = new();
    private int _reads;

    public event EventHandler? Changed;

    public int Reads => Volatile.Read(ref _reads);

    public Func<Exception?> Fail { get; set; } = () => null;

    public void Set(WorkspaceDescription workspace, bool raise = true)
    {
        Volatile.Write(ref _current, workspace);
        if (raise)
        {
            Raise();
        }
    }

    public void Raise() => Changed?.Invoke(this, EventArgs.Empty);

    public Task<WorkspaceDescription> GetAsync(CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref _reads);
        return Fail() is { } error ? Task.FromException<WorkspaceDescription>(error) : Task.FromResult(Volatile.Read(ref _current));
    }
}

/// <summary>An activity source (the debugger's stand-in) driven by the test.</summary>
internal sealed class FakeActivitySource : IIdeActivitySource
{
    private int _current;

    public event EventHandler? Changed;

    public IdeActivity Current => (IdeActivity)Volatile.Read(ref _current);

    public void Set(IdeActivity activity)
    {
        Volatile.Write(ref _current, (int)activity);
        Changed?.Invoke(this, EventArgs.Empty);
    }
}

public sealed class IndexingRelayTests : IDisposable
{
    private static readonly WorkspaceDescription Solution = new()
    {
        SolutionPath = @"C:\src\App\App.sln",
        ProjectPaths = [@"C:\src\App\App\App.csproj", @"C:\src\App\Tests\Tests.csproj"],
    };

    private readonly SignalsClock _clock = new();
    private readonly IdeSignalsHub _hub;
    private readonly FakeWorkspaceSource _workspace = new();
    private readonly FakeActivitySource _debugger = new();
    private readonly RecordingContextService _context = new();
    private readonly FakeHostConnection _connection;
    private readonly IndexingRelay _relay;

    public IndexingRelayTests()
    {
        _hub = new IdeSignalsHub(_clock, null);
        _connection = new FakeHostConnection(HostState.Starting).With<IContextService>(_context);
        _workspace.Set(Solution, raise: false);
        _relay = new IndexingRelay(_connection, _hub, _workspace, _debugger, _clock, null);
    }

    public void Dispose()
    {
        _relay.Dispose();
        _hub.Dispose();
    }

    /// <summary>What StreamJsonRpc throws when a 1.2 host has no SetWorkspaceAsync (its constructor is internal).</summary>
    private static Exception MethodNotFound() => (Exception)Activator.CreateInstance(
        typeof(RemoteMethodNotFoundException),
        BindingFlags.Instance | BindingFlags.NonPublic,
        binder: null,
        args: ["method not found", "SetWorkspaceAsync", JsonRpcErrorCode.MethodNotFound, null, null],
        CultureInfo.InvariantCulture)!;

    private IdeActivity[] Activities => _connection.Notifications
        .Where(n => n.Method == IndexingRelay.IdeActivityChangedMethod)
        .Select(n => (IdeActivity)n.Argument!)
        .ToArray();

    /// <summary>Lets the hub raise its events, the clock's continuations run and the relay's pump drain.</summary>
    private async Task SettleAsync()
    {
        await _hub.WhenIdleAsync();
        await Task.Delay(30);
        await _relay.WhenIdleAsync();
    }

    private async Task ConnectAsync()
    {
        await _connection.SetStateAsync(HostState.Ready);
        await Wait.UntilAsync(() => Activities.Length >= 1 && _context.Attempts >= 1, "the relay sent the state after connecting");
        await SettleAsync();
    }

    [Fact]
    public async Task AfterConnecting_SendsTheActivitySetAndTheWorkspace_AndNothingBefore()
    {
        _relay.Start();
        await SettleAsync();
        Assert.Empty(_connection.Notifications);
        Assert.Equal(0, _workspace.Reads);

        await ConnectAsync();

        Assert.Equal([IdeActivity.None], Activities);
        var sent = Assert.Single(_context.Workspaces);
        Assert.Equal(Solution.SolutionPath, sent.SolutionPath);
        Assert.Equal(Solution.ProjectPaths, sent.ProjectPaths);
        Assert.Null(sent.FolderPath);
    }

    [Fact]
    public async Task AHostThatIsAlreadyReady_GetsTheStateOnStart()
    {
        await _connection.SetStateAsync(HostState.Degraded);
        _relay.Start();
        await Wait.UntilAsync(() => Activities.Length == 1 && _context.Workspaces.Count == 1, "the relay sent the state on start");
    }

    [Fact]
    public async Task BuildStartAndStop_AreForwardedAsTheBuildFlag_WithoutDuplicates()
    {
        _relay.Start();
        await ConnectAsync();

        _hub.NotifyBuildStarted();
        _hub.NotifyBuildStarted();
        await SettleAsync();
        Assert.Equal([IdeActivity.None, IdeActivity.Build], Activities);

        _hub.NotifyBuildFinished(success: true);
        _hub.NotifyBuildFinished(success: true);
        await SettleAsync();
        Assert.Equal([IdeActivity.None, IdeActivity.Build, IdeActivity.None], Activities);

        // The workspace did not change, so it is not sent again.
        Assert.Single(_context.Workspaces);
    }

    [Fact]
    public async Task TheDebugger_IsCombinedWithTheBuild_IntoOneSet()
    {
        _relay.Start();
        await ConnectAsync();

        _debugger.Set(IdeActivity.Debug);
        await SettleAsync();
        _hub.NotifyBuildStarted();
        await SettleAsync();
        _debugger.Set(IdeActivity.Debug); // A repeat: no change, nothing sent.
        await SettleAsync();
        _debugger.Set(IdeActivity.None);
        await SettleAsync();

        Assert.Equal([IdeActivity.None, IdeActivity.Debug, IdeActivity.Debug | IdeActivity.Build, IdeActivity.Build], Activities);
        Assert.Equal(IdeActivity.Build, _relay.CurrentActivity);
    }

    [Fact]
    public async Task AWorkspaceChange_IsSentAgain_OnceAfterABurstHasBeenQuietFor500Ms()
    {
        _relay.Start();
        await ConnectAsync();
        var readsAfterConnect = _workspace.Reads;

        var folder = new WorkspaceDescription { FolderPath = @"C:\src\Site" };
        for (var i = 0; i < 5; i++)
        {
            _workspace.Set(folder);
            _clock.Advance(TimeSpan.FromMilliseconds(100));
        }

        await SettleAsync();
        Assert.Equal(readsAfterConnect, _workspace.Reads);
        Assert.Single(_context.Workspaces);

        _clock.Advance(TimeSpan.FromMilliseconds(400));
        await Wait.UntilAsync(() => _context.Workspaces.Count == 2, "the new workspace was sent");
        await SettleAsync();
        Assert.Equal(readsAfterConnect + 1, _workspace.Reads);
        var sent = _context.Workspaces.Last();
        Assert.Equal(@"C:\src\Site", sent.FolderPath);
        Assert.Null(sent.SolutionPath);
        Assert.Empty(sent.ProjectPaths);

        // A change event that leaves the workspace as it was reads it again but sends nothing.
        _workspace.Raise();
        _clock.Advance(TimeSpan.FromMilliseconds(500));
        await Wait.UntilAsync(() => _workspace.Reads == readsAfterConnect + 2, "the workspace was read again");
        await SettleAsync();
        Assert.Equal(2, _context.Workspaces.Count);
    }

    [Fact]
    public async Task WhileTheHostIsNotConnected_NothingIsSentOrRead_AndTheNextConnectionGetsTheLatestState()
    {
        _relay.Start();
        await ConnectAsync();
        await _connection.SetStateAsync(HostState.Restarting, attempt: 1);
        await SettleAsync();
        var notifications = _connection.Notifications.Count;
        var reads = _workspace.Reads;

        _hub.NotifyBuildStarted();
        _debugger.Set(IdeActivity.Debug);
        _workspace.Set(new WorkspaceDescription { FolderPath = @"C:\src\Other" });
        _clock.Advance(TimeSpan.FromSeconds(10));
        await SettleAsync();
        Assert.Equal(notifications, _connection.Notifications.Count);
        Assert.Equal(reads, _workspace.Reads);
        Assert.Single(_context.Workspaces);

        await _connection.SetStateAsync(HostState.Starting);
        await _connection.SetStateAsync(HostState.Ready);
        await Wait.UntilAsync(() => _context.Workspaces.Count == 2, "the reconnected host got the workspace");
        await SettleAsync();
        Assert.Equal(IdeActivity.Build | IdeActivity.Debug, Activities.Last());
        Assert.Equal(@"C:\src\Other", _context.Workspaces.Last().FolderPath);
    }

    [Fact]
    public async Task ANewSession_GetsEverythingAgain_EvenWhenNothingChanged()
    {
        _relay.Start();
        await ConnectAsync();

        await _connection.SetStateAsync(HostState.Restarting, attempt: 1);
        await _connection.SetStateAsync(HostState.Ready);
        await Wait.UntilAsync(() => _context.Workspaces.Count == 2 && Activities.Length == 2, "the new session got the state");

        // Ready to Degraded is the same session: nothing is sent.
        await _connection.SetStateAsync(HostState.Degraded);
        await SettleAsync();
        Assert.Equal(2, _context.Workspaces.Count);
        Assert.Equal([IdeActivity.None, IdeActivity.None], Activities);
    }

    [Fact]
    public async Task AFailingSetWorkspace_IsSwallowed_AndTheActivityStillFlows()
    {
        _context.Fail = _ => new InvalidOperationException("index store is locked");
        _relay.Start();
        await _connection.SetStateAsync(HostState.Ready);
        await Wait.UntilAsync(() => _context.Attempts == 1 && Activities.Length == 1, "the relay tried to send the workspace");
        await SettleAsync();

        _hub.NotifyBuildStarted();
        await SettleAsync();
        Assert.Equal([IdeActivity.None, IdeActivity.Build], Activities);

        // The failed workspace was not recorded as sent, so the next change event tries again (and now succeeds).
        _context.Fail = _ => null;
        _workspace.Raise();
        _clock.Advance(TimeSpan.FromMilliseconds(500));
        await Wait.UntilAsync(() => _context.Workspaces.Count == 1, "the workspace was sent on the next change");
        Assert.Equal(2, _context.Attempts);
    }

    [Fact]
    public async Task AHostWithoutSetWorkspace_IsNotAskedAgainInTheSameSession()
    {
        _context.Fail = _ => MethodNotFound();
        _relay.Start();
        await _connection.SetStateAsync(HostState.Ready);
        await Wait.UntilAsync(() => _context.Attempts == 1, "the relay tried once");

        _workspace.Set(new WorkspaceDescription { FolderPath = @"C:\src\Site" });
        _clock.Advance(TimeSpan.FromMilliseconds(500));
        await SettleAsync();
        Assert.Equal(1, _context.Attempts);

        // A new session may be a newer host: ask again.
        await _connection.SetStateAsync(HostState.Restarting, attempt: 1);
        await _connection.SetStateAsync(HostState.Ready);
        await Wait.UntilAsync(() => _context.Attempts == 2, "the new session was asked");
    }

    [Fact]
    public async Task AFailingWorkspaceRead_IsSwallowed()
    {
        _workspace.Fail = () => new InvalidOperationException("solution is closing");
        _relay.Start();
        await _connection.SetStateAsync(HostState.Ready);
        await Wait.UntilAsync(() => _workspace.Reads == 1 && Activities.Length == 1, "the relay tried to read the workspace");
        await SettleAsync();
        Assert.Equal(0, _context.Attempts);
    }

    [Fact]
    public async Task AfterDispose_NothingIsSent()
    {
        _relay.Start();
        await ConnectAsync();
        _relay.Dispose();

        _hub.NotifyBuildStarted();
        _workspace.Set(new WorkspaceDescription { FolderPath = @"C:\src\Site" });
        _clock.Advance(TimeSpan.FromSeconds(1));
        await _connection.SetStateAsync(HostState.Restarting, attempt: 1);
        await _connection.SetStateAsync(HostState.Ready);
        await Task.Delay(50);

        Assert.Equal([IdeActivity.None], Activities);
        Assert.Single(_context.Workspaces);
    }

    [Fact]
    public async Task ProjectPaths_AreDeduplicatedAndSorted_AndADifferenceInOrderOrCaseIsNotAChange()
    {
        var a = new WorkspaceDescription { SolutionPath = @"C:\s.sln", ProjectPaths = [@"C:\b.csproj", @"C:\a.csproj"] };
        var b = new WorkspaceDescription { SolutionPath = @"c:\S.sln", ProjectPaths = [@"C:\A.csproj", @"C:\b.csproj", @"C:\b.csproj"] };
        _workspace.Set(a, raise: false);
        _relay.Start();
        await _connection.SetStateAsync(HostState.Ready);
        await Wait.UntilAsync(() => _context.Workspaces.Count == 1, "the workspace was sent");
        Assert.Equal([@"C:\a.csproj", @"C:\b.csproj"], _context.Workspaces.Single().ProjectPaths);

        _workspace.Set(b);
        _clock.Advance(TimeSpan.FromMilliseconds(500));
        await Wait.UntilAsync(() => _workspace.Reads == 2, "the workspace was read again");
        await SettleAsync();
        Assert.Single(_context.Workspaces);
    }
}

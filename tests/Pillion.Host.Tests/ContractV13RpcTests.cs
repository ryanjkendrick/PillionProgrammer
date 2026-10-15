using System.Collections.Concurrent;
using System.Threading.Channels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging.Abstractions;
using Pillion.Common;
using Pillion.Common.Secrets;
using Pillion.Common.Settings;
using Pillion.Contracts;
using Pillion.Host.Connectivity;
using Pillion.Host.Context;
using Pillion.Host.Tests.TestSupport;
using StreamJsonRpc;

namespace Pillion.Host.Tests;

/// <summary>
/// Contract 1.3 in Pillion Host, over the real pipe: the conversation service and the index methods are stubs until their
/// work items (2.2, 2.11, 2.15) land, and say so with typed errors; the status is honestly "no index"; the
/// <c>indexStatusChanged</c> notification reaches the client and is throttled to twice a second.
/// </summary>
public sealed class ContractV13RpcTests : IDisposable
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(10);

    private readonly TempDirectory _data = new();
    private readonly FakeFlags _flags = new();

    public void Dispose() => _data.Dispose();

    [Fact]
    public void NotificationName_IsIndexStatusChanged() =>
        Assert.Equal("indexStatusChanged", IndexStatusNotifier.NotificationName);

    [Fact]
    public async Task AOnePointTwoClient_IsAcceptedByAOnePointThreeHost_AndItsCallsStillAnswer()
    {
        await using var host = await StartAsync();
        await using var client = await host.ConnectAsync();

        var response = await client.Lifecycle
            .HandshakeAsync(TestRpcClient.CreateHandshake(host.Token, major: 1, minor: 2), CancellationToken.None)
            .WaitAsync(Wait);

        Assert.True(response.Accepted, response.Reason);
        Assert.Equal(1, response.ContractMajor);
        Assert.Equal(3, response.ContractMinor);
        var status = await client.Attach<IContextService>().GetIndexStatusAsync(CancellationToken.None).WaitAsync(Wait);
        Assert.False(status.IsIndexing);
    }

    [Fact]
    public async Task TheConversationService_IsNotImplementedYet_AndSaysWhichWorkItemAddsIt()
    {
        await using var host = await StartAsync();
        await using var client = await host.ConnectAndHandshakeAsync();
        var conversations = client.Attach<IConversationService>();

        var calls = new Func<Task>[]
        {
            () => conversations.ListConversationsAsync(new ConversationListRequest(), CancellationToken.None),
            () => conversations.GetConversationAsync("c", CancellationToken.None),
            () => conversations.CreateConversationAsync(new ConversationCreateRequest(), CancellationToken.None),
            () => conversations.RenameConversationAsync("c", "t", CancellationToken.None),
            () => conversations.DeleteConversationAsync("c", CancellationToken.None),
            () => conversations.DeleteAllConversationsAsync(CancellationToken.None),
            () => conversations.SetPinsAsync("c", [], CancellationToken.None),
        };

        foreach (var call in calls)
        {
            var error = await ErrorOfAsync(call);
            Assert.Equal(PillionErrorCode.NotImplemented, error.Code);
            Assert.Equal("work item 2.2", error.Details);
        }
    }

    [Fact]
    public async Task WithTheLocalStoreFlagOff_TheConversationServiceIsRefusedNamingTheFlag()
    {
        _flags.Disabled.Add(FeatureNames.LocalStore);
        await using var host = await StartAsync();
        await using var client = await host.ConnectAndHandshakeAsync();

        var error = await ErrorOfAsync(() => client.Attach<IConversationService>().ListConversationsAsync(new ConversationListRequest(), CancellationToken.None));

        Assert.Equal(PillionErrorCode.PolicyDenied, error.Code);
        Assert.Contains("localStore", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheIndex_IsReportedHonestlyAsAbsent_AndItsControlsAreNotImplementedYet()
    {
        await using var host = await StartAsync();
        await using var client = await host.ConnectAndHandshakeAsync();
        var context = client.Attach<IContextService>();

        var status = await context.GetIndexStatusAsync(CancellationToken.None).WaitAsync(Wait);
        Assert.Equal(IndexPhase.Idle, status.Phase);
        Assert.Equal(IndexPauseReason.None, status.PauseReason);
        Assert.Empty(status.Roots);
        Assert.Null(status.Progress);

        var search = await context.SearchDetailedAsync(new SearchRequest { Query = "pipe", Mode = SearchMode.Lexical }, CancellationToken.None).WaitAsync(Wait);
        Assert.Empty(search.Results);
        Assert.False(search.Degraded);

        foreach (var call in new Func<Task>[]
        {
            () => context.ListExcludedFilesAsync(new ExcludedFilesRequest { RootPath = _data.Path }, CancellationToken.None),
            () => context.RebuildIndexAsync(_data.Path, CancellationToken.None),
            () => context.DeleteIndexAsync("0123456789abcdef0123456789abcdef", CancellationToken.None),
            () => context.ListIndexesAsync(CancellationToken.None),
        })
        {
            var error = await ErrorOfAsync(call);
            Assert.Equal(PillionErrorCode.NotImplemented, error.Code);
            Assert.Equal("work item 2.15", error.Details);
        }
    }

    [Fact]
    public async Task APublishedStatus_ReachesTheClientAsIndexStatusChanged()
    {
        await using var host = await StartAsync();
        var notifications = Channel.CreateUnbounded<IndexStatus>();
        await using var client = await host.ConnectAndHandshakeAsync(rpc =>
            rpc.AddLocalRpcMethod(IndexStatusNotifier.NotificationName, new Action<IndexStatus>(s => notifications.Writer.TryWrite(s))));

        host.Services.GetRequiredService<IndexStatusNotifier>().Publish(new IndexStatus
        {
            IsIndexing = true,
            Phase = IndexPhase.Embedding,
            Roots = [new IndexRootStatus { RootPath = @"C:\src", IndexId = "0123456789abcdef0123456789abcdef", Role = IndexRole.Writer, ChunkCount = 40 }],
            Progress = new IndexProgress { FilesDone = 3, FilesTotal = 10, ChunksDone = 40, EstimatedRemainingSeconds = 12 },
        });

        var received = await notifications.Reader.ReadAsync().AsTask().WaitAsync(Wait);
        Assert.Equal(IndexPhase.Embedding, received.Phase);
        Assert.Equal(IndexRole.Writer, Assert.Single(received.Roots).Role);
        Assert.Equal(12, received.Progress!.EstimatedRemainingSeconds);
    }

    [Fact]
    public void TheNotifier_SendsAtOnce_ThenAtMostOncePerWindow_WithTheLatestStatus()
    {
        var time = new ManualTimeProvider();
        var sender = new RecordingSender();
        using var notifier = new IndexStatusNotifier(sender, time, NullLogger<IndexStatusNotifier>.Instance);

        notifier.Publish(new IndexStatus { Phase = IndexPhase.Discovering });
        Assert.Equal([IndexPhase.Discovering], sender.Phases);

        notifier.Publish(new IndexStatus { Phase = IndexPhase.Hashing });
        notifier.Publish(new IndexStatus { Phase = IndexPhase.Chunking });
        time.Advance(IndexStatusNotifier.MinimumInterval - TimeSpan.FromMilliseconds(1));
        Assert.Equal([IndexPhase.Discovering], sender.Phases);

        time.Advance(TimeSpan.FromMilliseconds(1));
        Assert.Equal([IndexPhase.Discovering, IndexPhase.Chunking], sender.Phases);

        // Nothing pending: the window ends quietly, and the next status after a quiet spell goes at once.
        time.Advance(IndexStatusNotifier.MinimumInterval * 4);
        Assert.Equal(2, sender.Phases.Count);
        notifier.Publish(new IndexStatus { Phase = IndexPhase.Idle });
        Assert.Equal([IndexPhase.Discovering, IndexPhase.Chunking, IndexPhase.Idle], sender.Phases);
        Assert.All(sender.Methods, m => Assert.Equal("indexStatusChanged", m));
    }

    [Fact]
    public void TheNotifier_SendsNothingAfterItIsDisposed()
    {
        var time = new ManualTimeProvider();
        var sender = new RecordingSender();
        var notifier = new IndexStatusNotifier(sender, time, NullLogger<IndexStatusNotifier>.Instance);
        notifier.Publish(new IndexStatus { Phase = IndexPhase.Discovering });
        notifier.Publish(new IndexStatus { Phase = IndexPhase.Hashing });

        notifier.Dispose();
        time.Advance(IndexStatusNotifier.MinimumInterval * 2);
        notifier.Publish(new IndexStatus { Phase = IndexPhase.Idle });

        Assert.Equal([IndexPhase.Discovering], sender.Phases);
    }

    private static async Task<PillionError> ErrorOfAsync(Func<Task> call)
    {
        var ex = await Assert.ThrowsAsync<RemoteInvocationException>(() => call().WaitAsync(Wait));
        return Assert.IsType<PillionError>(ex.DeserializedErrorData);
    }

    private Task<InProcessHost> StartAsync() => InProcessHost.StartAsync(services =>
    {
        services.Replace(ServiceDescriptor.Singleton(new PillionPaths(_data.Path)));
        services.Replace(ServiceDescriptor.Singleton<ISecretStore>(new InMemorySecretStore()));
        services.Replace(ServiceDescriptor.Singleton<IFeatureFlags>(_flags));
    });

    private sealed class FakeFlags : IFeatureFlags
    {
        public event EventHandler? Changed;

        public ConcurrentBag<string> Disabled { get; } = [];

        public bool IsEnabled(string feature) => !Disabled.Contains(feature);

        public void Raise() => Changed?.Invoke(this, EventArgs.Empty);
    }

    private sealed class RecordingSender : INotificationSender
    {
        private readonly List<(string Method, IndexStatus Status)> _sent = [];

        public event EventHandler? ClientConnected;

        public List<IndexPhase> Phases
        {
            get
            {
                lock (_sent)
                {
                    return [.. _sent.Select(s => s.Status.Phase)];
                }
            }
        }

        public List<string> Methods
        {
            get
            {
                lock (_sent)
                {
                    return [.. _sent.Select(s => s.Method)];
                }
            }
        }

        public Task<bool> TrySendAsync(string method, object? argument)
        {
            lock (_sent)
            {
                _sent.Add((method, (IndexStatus)argument!));
            }

            return Task.FromResult(true);
        }

        public void RaiseConnected() => ClientConnected?.Invoke(this, EventArgs.Empty);
    }
}

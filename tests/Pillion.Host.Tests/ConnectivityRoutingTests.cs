using System.Runtime.CompilerServices;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Pillion.Common;
using Pillion.Common.Connectivity;
using Pillion.Common.Secrets;
using Pillion.Common.Settings;
using Pillion.Contracts;
using Pillion.Host.Models;
using Pillion.Host.Tests.TestSupport;
using Pillion.ModelGateway;
using AiChatMessage = Microsoft.Extensions.AI.ChatMessage;
using AiChatRole = Microsoft.Extensions.AI.ChatRole;
using ConnectivityState = Pillion.Common.Connectivity.ConnectivityState;

namespace Pillion.Host.Tests;

/// <summary>
/// The composed host wires the connectivity monitor into the Model Gateway: routing skips what the monitor says is
/// unreachable, circuit-breaker failures feed the monitor, and both read Local-only from the same settings.
/// </summary>
public sealed class ConnectivityRoutingTests : IDisposable
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(10);

    private readonly TempDirectory _data = new();
    private readonly FakeSettings _settings = new();
    private readonly FakeProber _prober = new();

    public ConnectivityRoutingTests()
    {
        _settings.Settings.Providers.Add(new ProviderSettings { Id = "cloud", Kind = ProviderKind.OpenAi, Models = ["cloud-chat"] });
        _settings.Settings.Providers.Add(new ProviderSettings { Id = "local", Kind = ProviderKind.Ollama, Endpoint = "http://localhost:11434", Models = ["local-chat"] });
    }

    public void Dispose() => _data.Dispose();

    [Fact]
    public async Task TheHost_UsesTheConnectivityAdapters()
    {
        await using var host = await StartAsync();

        Assert.IsType<ConnectivityProviderReachability>(host.Services.GetRequiredService<IProviderReachability>());
        Assert.IsType<ConnectivityProviderHealthSink>(host.Services.GetRequiredService<IProviderHealthSink>());
        Assert.IsType<ConnectivityGatewaySettingsSource>(host.Services.GetRequiredService<IGatewaySettingsSource>());
    }

    [Fact]
    public async Task AnUnreachableCloudProvider_IsSkippedByRouting()
    {
        await using var host = await StartAsync();
        var monitor = await StartedMonitorAsync(host);
        var router = host.Services.GetRequiredService<IModelRouter>();

        // Reachable: Auto chat goes to the cloud (Balanced, online).
        await monitor.ProbeNowAsync(CancellationToken.None).WaitAsync(Wait);
        Assert.Equal(ConnectivityState.Online, monitor.State);
        Assert.Equal("cloud", router.Resolve(new RoutingRequest { TaskClass = TaskClass.Chat }).ProviderId);

        _prober.Reachable = false;
        await monitor.ProbeNowAsync(CancellationToken.None).WaitAsync(Wait);
        Assert.Equal(ConnectivityState.Offline, monitor.State);

        // Auto now chooses a local profile, and an explicit cloud profile skips the cloud provider as unreachable.
        var auto = router.Resolve(new RoutingRequest { TaskClass = TaskClass.Chat });
        Assert.Equal("local", auto.ProviderId);
        var balanced = router.Resolve(new RoutingRequest { TaskClass = TaskClass.Chat, Profile = Profile.Balanced });
        Assert.Equal("local", balanced.ProviderId);
        Assert.Contains("cloud/cloud-chat (unreachable)", balanced.Reason, StringComparison.Ordinal);

        // End to end: the chat reply names the local model that answered.
        await using var client = await host.ConnectAndHandshakeAsync();
        var models = client.Attach<IModelService>();
        var chunks = new List<ChatChunk>();
        await foreach (var chunk in models.ChatAsync(Request(Profile.Balanced), CancellationToken.None))
        {
            chunks.Add(chunk);
        }

        Assert.Equal("local: Hello", string.Concat(chunks.Select(c => c.Text)));
        Assert.All(chunks, c => Assert.Equal("local", c.ProviderId));
        Assert.Equal(0, _prober.CloudChats);
    }

    [Fact]
    public async Task ThreeCircuitBreakerFailures_MarkTheProviderUnreachableInTheMonitor()
    {
        await using var host = await StartAsync();
        var monitor = await StartedMonitorAsync(host);
        var breakers = host.Services.GetRequiredService<CircuitBreakerRegistry>();
        var reachability = host.Services.GetRequiredService<IProviderReachability>();

        breakers.RecordFailure("cloud");
        breakers.RecordFailure("cloud");
        Assert.True(monitor.IsProviderReachable("cloud"));
        Assert.True(reachability.IsReachable("cloud"));

        breakers.RecordFailure("cloud");
        Assert.Equal(CircuitState.Open, breakers.GetState("cloud"));
        Assert.False(monitor.IsProviderReachable("cloud"));
        Assert.Equal(ProbeErrorCategory.CircuitBreaker, monitor.Snapshot["cloud"].ErrorCategory);
        Assert.Equal(ConnectivityState.Offline, monitor.State);
        Assert.False(reachability.IsReachable("cloud"));

        // A local provider's failures never touch connectivity.
        for (var i = 0; i < 3; i++)
        {
            breakers.RecordFailure("local");
        }

        Assert.True(monitor.IsProviderReachable("local"));

        // A successful request clears the mark.
        breakers.RecordSuccess("cloud");
        Assert.True(monitor.IsProviderReachable("cloud"));
        Assert.Equal(ConnectivityState.Online, monitor.State);
    }

    [Fact]
    public async Task LocalOnly_IsTheSameForTheMonitorAndTheRouter()
    {
        await using var host = await StartAsync();
        var monitor = await StartedMonitorAsync(host);
        var router = host.Services.GetRequiredService<IModelRouter>();
        var reachability = host.Services.GetRequiredService<IProviderReachability>();
        var gatewaySettings = host.Services.GetRequiredService<IGatewaySettingsSource>();

        var probesBefore = _prober.ProbeCount;
        _settings.Update(s => s.Privacy.LocalOnly = true);

        Assert.Equal(ConnectivityState.LocalOnly, monitor.State);
        Assert.True(gatewaySettings.GetCurrent().LocalOnly);
        Assert.False(reachability.IsReachable("cloud"));
        Assert.True(reachability.IsReachable("local"));

        var routed = router.Resolve(new RoutingRequest { TaskClass = TaskClass.Chat, Profile = Profile.Frontier });
        Assert.Equal("local", routed.ProviderId);
        Assert.Contains("Local-only mode is on", routed.Reason, StringComparison.Ordinal);
        var denied = Assert.Throws<PillionRpcException>(() =>
            router.Resolve(new RoutingRequest { TaskClass = TaskClass.Chat, ModelId = "cloud/cloud-chat" }));
        Assert.Equal(PillionErrorCode.PolicyDenied, denied.Error.Code);
        await monitor.ProbeNowAsync(CancellationToken.None).WaitAsync(Wait);
        Assert.Equal(probesBefore, _prober.ProbeCount);
    }

    private static ChatRequest Request(Profile profile) => new()
    {
        RequestId = "req-route",
        Messages = [new Contracts.ChatMessage { Role = Contracts.ChatRole.User, Content = "Hello" }],
        Profile = profile,
    };

    /// <summary>Waits until the hosted service has started the monitor and it tracks the cloud provider.</summary>
    private static async Task<ConnectivityMonitor> StartedMonitorAsync(InProcessHost host)
    {
        var monitor = host.Services.GetRequiredService<ConnectivityMonitor>();
        var deadline = DateTime.UtcNow + Wait;
        while (!monitor.Snapshot.ContainsKey("cloud"))
        {
            Assert.True(DateTime.UtcNow < deadline, "the connectivity monitor did not start");
            await Task.Delay(10);
        }

        // Probe rounds never overlap, so this waits for the start-up round as well.
        await monitor.ProbeNowAsync(CancellationToken.None).WaitAsync(Wait);
        return monitor;
    }

    private Task<InProcessHost> StartAsync() => InProcessHost.StartAsync(services =>
    {
        services.Replace(ServiceDescriptor.Singleton(new PillionPaths(_data.Path)));
        services.Replace(ServiceDescriptor.Singleton<ISecretStore>(new InMemorySecretStore()));
        services.Replace(ServiceDescriptor.Singleton<IConnectivitySettingsProvider>(_settings));
        services.Replace(ServiceDescriptor.Singleton<IProviderProber>(_prober));
        services.Replace(ServiceDescriptor.Singleton<INetworkChangeSource>(new NoNetworkChanges()));
        services.AddSingleton<IModelProviderFactory>(new FakeFactory(ProviderKind.OpenAi, isLocal: false, _prober));
        services.AddSingleton<IModelProviderFactory>(new FakeFactory(ProviderKind.Ollama, isLocal: true, _prober));
    });

    private sealed class FakeSettings : IConnectivitySettingsProvider
    {
        public event EventHandler? Changed;

        public PillionSettings Settings { get; } = new();

        public EffectiveSettings GetEffectiveSettings() => EffectiveSettings.Resolve(Settings, policies: null);

        public void Update(Action<PillionSettings> edit)
        {
            edit(Settings);
            Changed?.Invoke(this, EventArgs.Empty);
        }
    }

    private sealed class FakeProber : IProviderProber
    {
        private int _probes;
        private int _cloudChats;

        public bool Reachable { get; set; } = true;

        public int ProbeCount => Volatile.Read(ref _probes);

        public int CloudChats => Volatile.Read(ref _cloudChats);

        public Task<ProbeResult> ProbeAsync(ProbeTarget target, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _probes);
            return Task.FromResult(Reachable ? ProbeResult.Success() : ProbeResult.Failure(ProbeErrorCategory.Connect));
        }

        public void ChatToCloud() => Interlocked.Increment(ref _cloudChats);
    }

    private sealed class NoNetworkChanges : INetworkChangeSource
    {
        public event EventHandler? Changed
        {
            add { }
            remove { }
        }
    }

    private sealed class FakeFactory(ProviderKind kind, bool isLocal, FakeProber prober) : IModelProviderFactory
    {
        public ProviderKind Kind => kind;

        public bool RequiresApiKey(ProviderSettings settings) => false;

        public IModelProvider Create(ProviderSettings settings, ProviderCreationContext context) =>
            new FakeProvider(settings.Id, kind, isLocal, prober);
    }

    private sealed class FakeProvider(string id, ProviderKind kind, bool isLocal, FakeProber prober) : IModelProvider
    {
        public string Id => id;

        public ProviderKind Kind => kind;

        public string DisplayName => id;

        public bool IsLocal => isLocal;

        public Task<IReadOnlyList<ProviderModel>> ListModelsAsync(CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<ProviderModel>>(
                [new ProviderModel(id, id + "-chat", id, new ModelCapabilityDescriptor { ContextWindow = 8192, IsLocal = isLocal })]);

        public IChatClient GetChatClient(string modelId) => new Client(this);

        public IFimClient? GetFimClient(string modelId) => null;

        public Task<ProviderProbeResult> ProbeAsync(CancellationToken cancellationToken) =>
            Task.FromResult(new ProviderProbeResult(true, "1", "fine"));

        public void Dispose()
        {
        }

        private sealed class Client(FakeProvider owner) : IChatClient
        {
            public Task<ChatResponse> GetResponseAsync(IEnumerable<AiChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) =>
                throw new NotSupportedException();

            public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
                IEnumerable<AiChatMessage> messages,
                ChatOptions? options = null,
                [EnumeratorCancellation] CancellationToken cancellationToken = default)
            {
                if (!owner.IsLocal)
                {
                    owner.Prober.ChatToCloud();
                }

                foreach (var piece in new[] { owner.Id, ": ", messages.Last().Text })
                {
                    await Task.Yield();
                    yield return new ChatResponseUpdate(AiChatRole.Assistant, piece);
                }
            }

            public object? GetService(Type serviceType, object? serviceKey = null) => null;

            public void Dispose()
            {
            }
        }

        public FakeProber Prober => prober;
    }
}

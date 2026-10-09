using System.Runtime.CompilerServices;
using System.Text.Json.Nodes;
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
using StreamJsonRpc;
using AiChatMessage = Microsoft.Extensions.AI.ChatMessage;
using AiChatRole = Microsoft.Extensions.AI.ChatRole;
using ConnectivityState = Pillion.Common.Connectivity.ConnectivityState;

namespace Pillion.Host.Tests;

/// <summary>What each feature flag does in the composed host (design: a flag gates every capability, a policy can switch it off).</summary>
public sealed class FeatureFlagTests : IDisposable
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(10);
    private static readonly string[] AllFlags =
        ["chat", "modelGateway", "connectivityProbes", "cloudProviders", "ollamaProvider", "openAICompatibleProvider", "anthropicProvider", "geminiProvider", "mistralProvider", "azureOpenAIProvider"];

    private readonly TempDirectory _data = new();
    private readonly FakeSettings _settings = new();
    private readonly FakeProber _prober = new();

    public FeatureFlagTests()
    {
        _settings.Settings.Providers.Add(new ProviderSettings { Id = "cloud", Kind = ProviderKind.OpenAi, Models = ["cloud-chat"] });
        _settings.Settings.Providers.Add(new ProviderSettings { Id = "local", Kind = ProviderKind.Ollama, Endpoint = "http://localhost:11434", Models = ["local-chat"] });
    }

    public void Dispose() => _data.Dispose();

    [Fact]
    public async Task TheHost_RegistersFeatureFlagsOverTheEffectiveSettings()
    {
        await using var host = await StartAsync();

        var flags = host.Services.GetRequiredService<IFeatureFlags>();

        Assert.IsType<SettingsFeatureFlags>(flags);
        Assert.All(AllFlags, name => Assert.True(flags.IsEnabled(name), name));
        _settings.Update(s => s.Features.Chat = false);
        Assert.False(flags.IsEnabled("chat"));
    }

    [Fact]
    public async Task EffectiveFeatures_ReportsEveryFlagOn_ByDefault()
    {
        await using var host = await StartAsync();
        await using var client = await host.ConnectAndHandshakeAsync();

        var json = await client.Attach<ISettingsService>().GetAsync("effective.features", CancellationToken.None).WaitAsync(Wait);

        var features = JsonNode.Parse(json!)!.AsObject();
        Assert.Equal(AllFlags, features.Select(p => p.Key));
        Assert.All(features, p => Assert.True(p.Value!.GetValue<bool>(), p.Key));
    }

    [Fact]
    public async Task EffectiveFeatures_IncludesPolicyRestrictions_TheUserSettingsDoNot()
    {
        await using var host = await StartAsync();
        await using var client = await host.ConnectAndHandshakeAsync();
        var settings = client.Attach<ISettingsService>();

        _settings.SetPolicies(RepositoryPolicy.Parse("""{ "disabledFeatures": ["chat", "cloudProviders"] }"""));

        var effective = JsonNode.Parse((await settings.GetAsync("effective.features", CancellationToken.None).WaitAsync(Wait))!)!.AsObject();
        Assert.False(effective["chat"]!.GetValue<bool>());
        Assert.False(effective["cloudProviders"]!.GetValue<bool>());
        Assert.True(effective["modelGateway"]!.GetValue<bool>());
        Assert.True(_settings.Settings.Features.Chat); // The user setting itself is untouched.
    }

    [Fact]
    public async Task EffectiveFeatures_IsReadOnly()
    {
        await using var host = await StartAsync();
        await using var client = await host.ConnectAndHandshakeAsync();
        var settings = client.Attach<ISettingsService>();

        await Assert.ThrowsAsync<RemoteInvocationException>(() => settings.SetAsync("effective.features", "{}", CancellationToken.None).WaitAsync(Wait));
        await Assert.ThrowsAsync<RemoteInvocationException>(() => settings.SetAsync("effective.features.chat", "false", CancellationToken.None).WaitAsync(Wait));
        await Assert.ThrowsAsync<RemoteInvocationException>(() => settings.SetAsync("Effective", "{}", CancellationToken.None).WaitAsync(Wait));
        Assert.Null(await settings.GetAsync("effective.other", CancellationToken.None).WaitAsync(Wait));
    }

    [Fact]
    public async Task ModelGatewayOff_FailsEveryModelCallWithPolicyDenied_AndOnAgainRestoresThem()
    {
        _settings.Settings.Features.ModelGateway = false;
        await using var host = await StartAsync();
        await using var client = await host.ConnectAndHandshakeAsync();
        var models = client.Attach<IModelService>();

        var list = await Assert.ThrowsAsync<RemoteInvocationException>(() => models.ListModelsAsync(CancellationToken.None).WaitAsync(Wait));
        var chat = await Assert.ThrowsAsync<RemoteInvocationException>(async () =>
        {
            await foreach (var chunk in models.ChatAsync(Request(Profile.Auto), CancellationToken.None))
            {
                _ = chunk;
            }
        });
        var complete = await Assert.ThrowsAsync<RemoteInvocationException>(async () =>
        {
            await foreach (var chunk in models.CompleteAsync(new CompletionRequest { RequestId = "c1", Prefix = "x" }, CancellationToken.None))
            {
                _ = chunk;
            }
        });

        foreach (var ex in new[] { list, chat, complete })
        {
            var error = Assert.IsType<PillionError>(ex.DeserializedErrorData);
            Assert.Equal(PillionErrorCode.PolicyDenied, error.Code);
            Assert.Contains("modelGateway", error.Message, StringComparison.Ordinal);
            Assert.Equal(ModelRpcService.GatewayDisabledMessage, error.Message);
        }

        // Cancelling stays harmless, and nothing reached a provider.
        await models.CancelAsync("nothing", CancellationToken.None).WaitAsync(Wait);
        Assert.Equal(0, _prober.CloudChats);

        _settings.Update(s => s.Features.ModelGateway = true);

        Assert.NotEmpty(await models.ListModelsAsync(CancellationToken.None).WaitAsync(Wait));
        var chunks = new List<ChatChunk>();
        await foreach (var chunk in models.ChatAsync(Request(Profile.LocalStrong), CancellationToken.None))
        {
            chunks.Add(chunk);
        }

        Assert.Equal("local: Hello", string.Concat(chunks.Select(c => c.Text)));
    }

    [Fact]
    public async Task APolicyThatDisablesTheModelGateway_StopsModelCalls()
    {
        await using var host = await StartAsync();
        await using var client = await host.ConnectAndHandshakeAsync();
        var models = client.Attach<IModelService>();
        Assert.NotEmpty(await models.ListModelsAsync(CancellationToken.None).WaitAsync(Wait));

        _settings.SetPolicies(RepositoryPolicy.Parse("""{ "disabledFeatures": ["modelGateway"] }"""));

        var ex = await Assert.ThrowsAsync<RemoteInvocationException>(() => models.ListModelsAsync(CancellationToken.None).WaitAsync(Wait));
        Assert.Equal(PillionErrorCode.PolicyDenied, Assert.IsType<PillionError>(ex.DeserializedErrorData).Code);
    }

    [Fact]
    public async Task ConnectivityProbesOff_StopsProbing_ButCloudProvidersStayRoutable()
    {
        _settings.Settings.Features.ConnectivityProbes = false;
        await using var host = await StartAsync();
        var monitor = host.Services.GetRequiredService<ConnectivityMonitor>();
        await WaitForMonitorAsync(monitor);
        await monitor.ProbeNowAsync(CancellationToken.None).WaitAsync(Wait);

        Assert.Equal(0, _prober.ProbeCount);
        Assert.Equal(ConnectivityState.Online, monitor.State);
        Assert.Empty(monitor.Snapshot);
        var router = host.Services.GetRequiredService<IModelRouter>();
        Assert.Equal("cloud", router.Resolve(new RoutingRequest { TaskClass = TaskClass.Chat }).ProviderId);

        // On again: the monitor probes the cloud provider.
        _settings.Update(s => s.Features.ConnectivityProbes = true);
        await monitor.ProbeNowAsync(CancellationToken.None).WaitAsync(Wait);
        Assert.True(_prober.ProbeCount >= 1);
        Assert.Contains("cloud", monitor.Snapshot.Keys);
    }

    [Fact]
    public async Task CloudProvidersOff_HidesEveryNonLocalProvider_AndProbesNothing()
    {
        _settings.Settings.Features.CloudProviders = false;
        await using var host = await StartAsync();
        var monitor = host.Services.GetRequiredService<ConnectivityMonitor>();
        await WaitForMonitorAsync(monitor);
        await monitor.ProbeNowAsync(CancellationToken.None).WaitAsync(Wait);
        var router = host.Services.GetRequiredService<IModelRouter>();
        var registry = host.Services.GetRequiredService<IProviderRegistry>();

        Assert.Equal(0, _prober.ProbeCount);
        Assert.Equal(ConnectivityState.Online, monitor.State);
        Assert.Null(registry.GetSnapshot().Find("cloud"));
        Assert.Contains(registry.GetSnapshot().Hidden, h => h.Settings.Id == "cloud" && h.Reason.Contains("cloudProviders", StringComparison.Ordinal));
        Assert.Equal("local", router.Resolve(new RoutingRequest { TaskClass = TaskClass.Chat }).ProviderId);
        Assert.Equal("local", router.Resolve(new RoutingRequest { TaskClass = TaskClass.Chat, Profile = Profile.Frontier }).ProviderId);

        await using var client = await host.ConnectAndHandshakeAsync();
        var listed = await client.Attach<IModelService>().ListModelsAsync(CancellationToken.None).WaitAsync(Wait);
        Assert.All(listed.Where(m => m.ProviderId == "cloud"), m => Assert.False(m.IsAvailable));
        Assert.Contains(listed, m => m.ProviderId == "local" && m.IsAvailable);

        _settings.Update(s => s.Features.CloudProviders = true);
        Assert.NotNull(registry.GetSnapshot().Find("cloud"));
    }

    [Fact]
    public async Task ProviderFactoryFlags_SkipTheRealOllamaAndOpenAIFactories()
    {
        // No fake factories here: the registry uses the real ones, which create providers without any network I/O.
        var secrets = new InMemorySecretStore();
        secrets.Set("cloud", "test-key-value");
        _settings.Settings.Features.OllamaProvider = false;
        await using var host = await InProcessHost.StartAsync(services =>
        {
            services.Replace(ServiceDescriptor.Singleton(new PillionPaths(_data.Path)));
            services.Replace(ServiceDescriptor.Singleton<ISecretStore>(secrets));
            services.Replace(ServiceDescriptor.Singleton<IConnectivitySettingsProvider>(_settings));
            services.Replace(ServiceDescriptor.Singleton<IProviderProber>(_prober));
            services.Replace(ServiceDescriptor.Singleton<INetworkChangeSource>(new NoNetworkChanges()));
        });
        var registry = host.Services.GetRequiredService<IProviderRegistry>();

        var snapshot = registry.GetSnapshot();
        Assert.Null(snapshot.Find("local"));
        Assert.Contains(snapshot.Hidden, h => h.Settings.Id == "local" && h.Reason.Contains("ollamaProvider", StringComparison.Ordinal));
        Assert.NotNull(snapshot.Find("cloud"));

        _settings.Update(s =>
        {
            s.Features.OllamaProvider = true;
            s.Features.OpenAICompatibleProvider = false;
        });
        snapshot = registry.GetSnapshot();
        Assert.NotNull(snapshot.Find("local"));
        Assert.Null(snapshot.Find("cloud"));
        Assert.Contains(snapshot.Hidden, h => h.Settings.Id == "cloud" && h.Reason.Contains("openAICompatibleProvider", StringComparison.Ordinal));
    }

    private static ChatRequest Request(Profile profile) => new()
    {
        RequestId = "req-flag",
        Messages = [new Contracts.ChatMessage { Role = Contracts.ChatRole.User, Content = "Hello" }],
        Profile = profile,
    };

    private static async Task WaitForMonitorAsync(ConnectivityMonitor monitor)
    {
        // The hosted service starts the monitor shortly after the host is ready; a started monitor reads the settings.
        var deadline = DateTime.UtcNow + Wait;
        while (true)
        {
            try
            {
                await monitor.ProbeNowAsync(CancellationToken.None).WaitAsync(Wait);
                if (monitor.IsProviderReachable("local"))
                {
                    return;
                }
            }
            catch (InvalidOperationException)
            {
                // Not started yet.
            }

            Assert.True(DateTime.UtcNow < deadline, "the connectivity monitor did not start");
            await Task.Delay(10);
        }
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
        private readonly List<RepositoryPolicy> _policies = [];

        public event EventHandler? Changed;

        public PillionSettings Settings { get; } = new();

        public EffectiveSettings GetEffectiveSettings() => EffectiveSettings.Resolve(Settings, _policies);

        public void Update(Action<PillionSettings> edit)
        {
            edit(Settings);
            Changed?.Invoke(this, EventArgs.Empty);
        }

        public void SetPolicies(params RepositoryPolicy[] policies)
        {
            _policies.Clear();
            _policies.AddRange(policies);
            Changed?.Invoke(this, EventArgs.Empty);
        }
    }

    private sealed class FakeProber : IProviderProber
    {
        private int _probes;
        private int _cloudChats;

        public int ProbeCount => Volatile.Read(ref _probes);

        public int CloudChats => Volatile.Read(ref _cloudChats);

        public Task<ProbeResult> ProbeAsync(ProbeTarget target, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _probes);
            return Task.FromResult(ProbeResult.Success());
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

        public string? FeatureFlag => kind == ProviderKind.Ollama ? FeatureNames.OllamaProvider : FeatureNames.OpenAICompatibleProvider;

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

        public IChatClient GetChatClient(string modelId) => new Client(this, prober);

        public IFimClient? GetFimClient(string modelId) => null;

        public Task<ProviderProbeResult> ProbeAsync(CancellationToken cancellationToken) =>
            Task.FromResult(new ProviderProbeResult(true, "1", "fine"));

        public void Dispose()
        {
        }

        private sealed class Client(FakeProvider owner, FakeProber prober) : IChatClient
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
                    prober.ChatToCloud();
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
    }
}

/// <summary>The feature flags and the effective.features key against the real settings store and policy registry.</summary>
public sealed class FeatureFlagStoreTests : IDisposable
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(10);

    private readonly TempDirectory _data = new();

    public void Dispose() => _data.Dispose();

    [Fact]
    public async Task FeaturesSetThroughTheSettingsService_ReachTheEffectiveKeyAndTheGateway()
    {
        await using var host = await StartAsync();
        await using var client = await host.ConnectAndHandshakeAsync();
        var settings = client.Attach<ISettingsService>();
        var models = client.Attach<IModelService>();

        // A fresh settings file has the features section with everything on.
        var raw = JsonNode.Parse((await settings.GetAsync("features", CancellationToken.None).WaitAsync(Wait))!)!.AsObject();
        Assert.Equal(FeatureNames.All.Count, raw.Count);
        Assert.All(raw, p => Assert.True(p.Value!.GetValue<bool>(), p.Key));
        Assert.NotEmpty(await models.ListModelsAsync(CancellationToken.None).WaitAsync(Wait));

        await settings.SetAsync("features.modelGateway", "false", CancellationToken.None).WaitAsync(Wait);

        Assert.Equal("false", await settings.GetAsync("features.modelGateway", CancellationToken.None).WaitAsync(Wait));
        var effective = JsonNode.Parse((await settings.GetAsync("effective.features", CancellationToken.None).WaitAsync(Wait))!)!.AsObject();
        Assert.False(effective["modelGateway"]!.GetValue<bool>());
        await Assert.ThrowsAsync<RemoteInvocationException>(() => models.ListModelsAsync(CancellationToken.None).WaitAsync(Wait));

        await settings.SetAsync("features.modelGateway", "true", CancellationToken.None).WaitAsync(Wait);
        Assert.NotEmpty(await models.ListModelsAsync(CancellationToken.None).WaitAsync(Wait));
    }

    [Fact]
    public async Task RepositoryPolicies_AreAppliedToTheEffectiveKey_ButNotToTheStoredSettings()
    {
        await using var host = await StartAsync();
        await using var client = await host.ConnectAndHandshakeAsync();
        var settings = client.Attach<ISettingsService>();
        var policies = host.Services.GetRequiredService<RepositoryPolicyRegistry>();

        policies.SetPolicies([RepositoryPolicy.Parse("""{ "disabledFeatures": ["chat", "connectivityProbes"] }""")]);

        var effective = JsonNode.Parse((await settings.GetAsync("effective.features", CancellationToken.None).WaitAsync(Wait))!)!.AsObject();
        Assert.False(effective["chat"]!.GetValue<bool>());
        Assert.False(effective["connectivityProbes"]!.GetValue<bool>());
        Assert.True(effective["modelGateway"]!.GetValue<bool>());
        Assert.Equal("true", await settings.GetAsync("features.chat", CancellationToken.None).WaitAsync(Wait));

        policies.SetPolicies([]);
        effective = JsonNode.Parse((await settings.GetAsync("effective.features", CancellationToken.None).WaitAsync(Wait))!)!.AsObject();
        Assert.True(effective["chat"]!.GetValue<bool>());
    }

    private Task<InProcessHost> StartAsync() => InProcessHost.StartAsync(services =>
    {
        services.Replace(ServiceDescriptor.Singleton(new PillionPaths(_data.Path)));
        services.Replace(ServiceDescriptor.Singleton<ISecretStore>(new InMemorySecretStore()));
        services.Replace(ServiceDescriptor.Singleton<INetworkChangeSource>(new NoChanges()));
        services.AddSingleton<IModelProviderFactory>(new LocalFactory());
        services.AddSingleton<ProvidersSeed>();
        services.AddHostedService(sp => sp.GetRequiredService<ProvidersSeed>());
    });

    private sealed class NoChanges : INetworkChangeSource
    {
        public event EventHandler? Changed
        {
            add { }
            remove { }
        }
    }

    /// <summary>Writes one local provider into the settings before the gateway first reads them.</summary>
    private sealed class ProvidersSeed(SettingsStore store) : Microsoft.Extensions.Hosting.IHostedService
    {
        public Task StartAsync(CancellationToken cancellationToken)
        {
            store.SetJson("providers", """[{"id":"local","kind":"ollama","displayName":"Local","endpoint":"http://localhost:11434","models":["local-chat"],"enabled":true}]""");
            return Task.CompletedTask;
        }

        public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private sealed class LocalFactory : IModelProviderFactory
    {
        public ProviderKind Kind => ProviderKind.Ollama;

        public string? FeatureFlag => FeatureNames.OllamaProvider;

        public bool RequiresApiKey(ProviderSettings settings) => false;

        public IModelProvider Create(ProviderSettings settings, ProviderCreationContext context) => new Provider(settings.Id);

        private sealed class Provider(string id) : IModelProvider
        {
            public string Id => id;

            public ProviderKind Kind => ProviderKind.Ollama;

            public string DisplayName => id;

            public bool IsLocal => true;

            public Task<IReadOnlyList<ProviderModel>> ListModelsAsync(CancellationToken cancellationToken) =>
                Task.FromResult<IReadOnlyList<ProviderModel>>(
                    [new ProviderModel(id, "local-chat", "Local chat", new ModelCapabilityDescriptor { ContextWindow = 8192, IsLocal = true })]);

            public IChatClient GetChatClient(string modelId) => throw new NotSupportedException();

            public IFimClient? GetFimClient(string modelId) => null;

            public Task<ProviderProbeResult> ProbeAsync(CancellationToken cancellationToken) =>
                Task.FromResult(new ProviderProbeResult(true, "1", "fine"));

            public void Dispose()
            {
            }
        }
    }
}

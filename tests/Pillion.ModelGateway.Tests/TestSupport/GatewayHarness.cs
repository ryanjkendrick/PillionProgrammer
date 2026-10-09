using Pillion.Common.Hardware;
using Pillion.Common.Secrets;
using Pillion.Common.Settings;
using Pillion.Contracts;

namespace Pillion.ModelGateway.Tests.TestSupport;

/// <summary>
/// A gateway wired with fake providers: <c>local</c> (kind Ollama, local) and <c>cloud</c> (kind OpenAI-compatible,
/// cloud) by default. Tests adjust <see cref="Settings"/> before the first request, or call <see cref="Reload"/>.
/// </summary>
internal sealed class GatewayHarness : IDisposable
{
    public GatewayHarness(bool withCloud = true, bool withLocal = true, Action<ModelGatewayOptions>? configure = null, GpuTier gpuTier = GpuTier.Gpu8GB)
    {
        Options = new ModelGatewayOptions
        {
            RetryBaseDelay = TimeSpan.FromMilliseconds(1),
            RetryMaxDelay = TimeSpan.FromMilliseconds(5),
        };
        configure?.Invoke(Options);

        Settings = new PillionSettings();
        if (withLocal)
        {
            Local = new FakeProvider("local", isLocal: true, ProviderKind.Ollama);
            Local.Models = [Local.Model("qwen2.5-coder:1.5b", fim: true), Local.Model("qwen2.5-coder:14b", fim: true)];
            LocalFactory.Providers[Local.Id] = Local;
            Settings.Providers.Add(new ProviderSettings { Id = "local", Kind = ProviderKind.Ollama });
        }

        if (withCloud)
        {
            Cloud = new FakeProvider("cloud", isLocal: false, ProviderKind.OpenAiCompatible);
            Cloud.Models = [Cloud.Model("cloud-fast", fim: true), Cloud.Model("cloud-balanced", vision: true), Cloud.Model("cloud-frontier", vision: true)];
            CloudFactory.Providers[Cloud.Id] = Cloud;
            Settings.Providers.Add(new ProviderSettings { Id = "cloud", Kind = ProviderKind.OpenAiCompatible, Models = ["cloud-balanced", "cloud-fast", "cloud-frontier"] });
            Settings.Routing.Profiles[Profile.Fast] = "cloud/cloud-fast";
            Settings.Routing.Profiles[Profile.Balanced] = "cloud/cloud-balanced";
            Settings.Routing.Profiles[Profile.Frontier] = "cloud/cloud-frontier";
        }

        Source = new StaticGatewaySettingsSource(Settings);
        Registry = new ProviderRegistry(Source, Secrets, [LocalFactory, CloudFactory], ModelCatalog.Default);
        Breakers = new CircuitBreakerRegistry(Options, Clock, Health);
        Limiter = new ConcurrencyLimiter(Options);
        Cache = new AutocompleteCache(Options);
        OutboundLog = new InMemoryOutboundLog(Options);
        Router = new ModelRouter(Registry, ModelCatalog.Default, Inventory, Reachability, Breakers, new NoCostCaps());
        Gateway = new ModelGateway(
            Registry,
            Router,
            Inventory,
            ModelCatalog.Default,
            PromptTemplates.Default,
            Cache,
            Breakers,
            Limiter,
            OutboundLog,
            Reachability,
            new NoCostCaps(),
            Progress,
            Options,
            Clock,
            gpuTierDetector: new FixedGpuTierDetector(gpuTier));
    }

    public ModelGatewayOptions Options { get; }

    public PillionSettings Settings { get; }

    public FakeProvider? Local { get; }

    public FakeProvider? Cloud { get; }

    public FakeProviderFactory LocalFactory { get; } = new(ProviderKind.Ollama);

    public FakeProviderFactory CloudFactory { get; } = new(ProviderKind.OpenAiCompatible);

    public InMemorySecretStore Secrets { get; } = new();

    public StaticGatewaySettingsSource Source { get; }

    public ProviderRegistry Registry { get; }

    public ModelInventory Inventory { get; } = new();

    public FakeReachability Reachability { get; } = new();

    public ManualClock Clock { get; } = new();

    public RecordingHealthSink Health { get; } = new();

    public RecordingProgressSink Progress { get; } = new();

    public CircuitBreakerRegistry Breakers { get; }

    public ConcurrencyLimiter Limiter { get; }

    public AutocompleteCache Cache { get; }

    public InMemoryOutboundLog OutboundLog { get; }

    public ModelRouter Router { get; }

    public ModelGateway Gateway { get; }

    /// <summary>Applies changes made to <see cref="Settings"/>.</summary>
    public void Reload() => Source.Update(Settings);

    /// <summary>Fills the inventory as if the providers had been listed.</summary>
    public void ListAll()
    {
        if (Local is not null)
        {
            Inventory.Update(Local.Id, Local.Models);
        }

        if (Cloud is not null)
        {
            Inventory.Update(Cloud.Id, Cloud.Models);
        }
    }

    public static ChatRequest ChatRequest(string text, string? modelId = null, Profile profile = Profile.Auto, string requestId = "r1") => new()
    {
        RequestId = requestId,
        Messages = [new Contracts.ChatMessage { Role = ChatRole.User, Content = text }],
        ModelId = modelId,
        Profile = profile,
    };

    public static CompletionRequest CompletionRequest(string prefix = "int Add(int a, int b) => ", string suffix = ";", string requestId = "c1") => new()
    {
        RequestId = requestId,
        Prefix = prefix,
        Suffix = suffix,
        LanguageId = "csharp",
        TaskClass = TaskClass.Autocomplete,
    };

    public void Dispose() => Registry.Dispose();
}

/// <summary>An <see cref="IGpuTierDetector"/> that reports a fixed tier.</summary>
internal sealed class FixedGpuTierDetector(GpuTier tier) : IGpuTierDetector
{
    public int Calls;

    public GpuInfo Detect()
    {
        Interlocked.Increment(ref Calls);
        return new GpuInfo(tier, null, "fake", "test");
    }
}

using Pillion.Common.Secrets;
using Pillion.Common.Settings;
using Pillion.Contracts;
using Pillion.ModelGateway.Providers.Ollama;
using Pillion.ModelGateway.Providers.OpenAICompatible;
using Pillion.ModelGateway.Tests.TestSupport;

namespace Pillion.ModelGateway.Tests;

/// <summary>The feature flags decide which providers the registry builds and the router can reach.</summary>
public sealed class FeatureFlagTests
{
    [Fact]
    public void TheRealFactories_NameTheirFeatureFlag()
    {
        var secrets = new InMemorySecretStore();

        Assert.Equal(FeatureNames.OllamaProvider, new OllamaProviderFactory().FeatureFlag);
        Assert.Equal(FeatureNames.OpenAICompatibleProvider, new OpenAICompatibleProviderFactory(secrets).FeatureFlag);
        Assert.Equal(FeatureNames.OpenAICompatibleProvider, new OpenAIProviderFactory(secrets).FeatureFlag);
    }

    [Fact]
    public void WithTheFlagsOn_TheRealFactoriesBuildTheirProviders()
    {
        var settings = RealProviderSettings();
        var secrets = new InMemorySecretStore();
        secrets.Set("openai", "test-key-value");
        using var registry = new ProviderRegistry(new StaticGatewaySettingsSource(settings), secrets, RealFactories(secrets), ModelCatalog.Default);

        var snapshot = registry.GetSnapshot();

        Assert.NotNull(snapshot.Find("ollama"));
        Assert.NotNull(snapshot.Find("lmstudio"));
        Assert.NotNull(snapshot.Find("openai"));
        Assert.Empty(snapshot.Hidden);
    }

    [Fact]
    public void OllamaProviderOff_SkipsTheOllamaFactory_AndNothingElse()
    {
        var settings = RealProviderSettings();
        settings.Features.OllamaProvider = false;
        var secrets = new InMemorySecretStore();
        secrets.Set("openai", "test-key-value");
        using var registry = new ProviderRegistry(new StaticGatewaySettingsSource(settings), secrets, RealFactories(secrets), ModelCatalog.Default);

        var snapshot = registry.GetSnapshot();

        Assert.Null(snapshot.Find("ollama"));
        var hidden = Assert.Single(snapshot.Hidden);
        Assert.Equal("ollama", hidden.Settings.Id);
        Assert.Contains("ollamaProvider", hidden.Reason, StringComparison.Ordinal);
        Assert.NotNull(snapshot.Find("lmstudio"));
        Assert.NotNull(snapshot.Find("openai"));
    }

    [Fact]
    public void OpenAICompatibleProviderOff_SkipsBothOpenAIFactories()
    {
        var settings = RealProviderSettings();
        settings.Features.OpenAICompatibleProvider = false;
        var secrets = new InMemorySecretStore();
        secrets.Set("openai", "test-key-value");
        using var registry = new ProviderRegistry(new StaticGatewaySettingsSource(settings), secrets, RealFactories(secrets), ModelCatalog.Default);

        var snapshot = registry.GetSnapshot();

        Assert.NotNull(snapshot.Find("ollama"));
        Assert.Null(snapshot.Find("lmstudio"));
        Assert.Null(snapshot.Find("openai"));
        Assert.Equal(["lmstudio", "openai"], snapshot.Hidden.Select(h => h.Settings.Id).Order(StringComparer.Ordinal));
        Assert.All(snapshot.Hidden, h => Assert.Contains("openAICompatibleProvider", h.Reason, StringComparison.Ordinal));
    }

    [Fact]
    public void AFactoryFlag_CanBeSwitchedOffByAPolicy_EvenWhenTheKeyIsStored()
    {
        using var harness = new GatewayHarness(withCloud: false, withLocal: false);
        var factory = new FakeProviderFactory(ProviderKind.Anthropic, requiresKey: true) { FeatureFlag = "someFeature" };
        factory.Providers["claude"] = new FakeProvider("claude", isLocal: false, ProviderKind.Anthropic);
        harness.Settings.Providers.Add(new ProviderSettings { Id = "claude", Kind = ProviderKind.Anthropic });
        harness.Secrets.Set("claude", "test-key-value");
        harness.Reload();
        using var registry = new ProviderRegistry(harness.Source, harness.Secrets, [factory], ModelCatalog.Default);

        Assert.NotNull(registry.GetSnapshot().Find("claude"));

        // A policy can disable any feature name, including one this version has no setting for.
        var source = new PolicySource(harness.Settings, RepositoryPolicy.Parse("""{ "disabledFeatures": ["someFeature"] }"""));
        using var restricted = new ProviderRegistry(source, harness.Secrets, [factory], ModelCatalog.Default);

        var snapshot = restricted.GetSnapshot();
        Assert.Null(snapshot.Find("claude"));
        Assert.Contains("someFeature", Assert.Single(snapshot.Hidden).Reason, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CloudProvidersOff_HidesEveryNonLocalProvider_FromRoutingAndListing()
    {
        using var harness = new GatewayHarness();
        harness.ListAll();
        Assert.Equal("cloud", harness.Router.Resolve(new RoutingRequest { TaskClass = TaskClass.Chat, Profile = Profile.Frontier }).ProviderId);

        harness.Settings.Features.CloudProviders = false;
        harness.Reload();

        var snapshot = harness.Registry.GetSnapshot();
        Assert.Null(snapshot.Find("cloud"));
        Assert.NotNull(snapshot.Find("local"));
        var hidden = Assert.Single(snapshot.Hidden);
        Assert.Equal("cloud", hidden.Settings.Id);
        Assert.Contains("cloudProviders", hidden.Reason, StringComparison.Ordinal);

        // Auto and a cloud profile both end up on the local provider.
        Assert.Equal("local", harness.Router.Resolve(new RoutingRequest { TaskClass = TaskClass.Chat }).ProviderId);
        Assert.Equal("local", harness.Router.Resolve(new RoutingRequest { TaskClass = TaskClass.Chat, Profile = Profile.Frontier }).ProviderId);

        // Naming a cloud model is refused with the reason.
        var refused = Assert.Throws<PillionRpcException>(() =>
            harness.Router.Resolve(new RoutingRequest { TaskClass = TaskClass.Chat, ModelId = "cloud/cloud-balanced" }));
        Assert.Equal(PillionErrorCode.ProviderUnavailable, refused.Error.Code);
        Assert.Contains("cloudProviders", refused.Error.Message, StringComparison.Ordinal);

        // Listing still shows the cloud models, but not as usable, and never contacts the provider.
        var contactsBefore = harness.Cloud!.ListCalls;
        var models = await harness.Gateway.ListModelsAsync(CancellationToken.None);
        Assert.All(models.Where(m => m.ProviderId == "cloud"), m => Assert.False(m.IsAvailable));
        Assert.Contains(models, m => m.ProviderId == "local" && m.IsAvailable);
        Assert.Equal(contactsBefore, harness.Cloud.ListCalls);

        // Turned back on, the cloud provider is routable again.
        harness.Settings.Features.CloudProviders = true;
        harness.Reload();
        Assert.NotNull(harness.Registry.GetSnapshot().Find("cloud"));
    }

    [Fact]
    public void CloudProvidersOff_KeepsAnOpenAICompatibleProviderOnLoopback()
    {
        var settings = RealProviderSettings();
        settings.Features.CloudProviders = false;
        var secrets = new InMemorySecretStore();
        secrets.Set("openai", "test-key-value");
        using var registry = new ProviderRegistry(new StaticGatewaySettingsSource(settings), secrets, RealFactories(secrets), ModelCatalog.Default);

        var snapshot = registry.GetSnapshot();

        Assert.NotNull(snapshot.Find("ollama"));
        Assert.NotNull(snapshot.Find("lmstudio")); // http://localhost:1234 is local.
        Assert.Null(snapshot.Find("openai"));
        Assert.Contains("cloudProviders", Assert.Single(snapshot.Hidden).Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void APolicyThatDisablesCloudProviders_AppliesToTheRegistry()
    {
        using var harness = new GatewayHarness();
        var source = new PolicySource(harness.Settings, RepositoryPolicy.Parse("""{ "disabledFeatures": ["cloudProviders"] }"""));
        using var registry = new ProviderRegistry(source, harness.Secrets, [harness.LocalFactory, harness.CloudFactory], ModelCatalog.Default);

        var snapshot = registry.GetSnapshot();

        Assert.Null(snapshot.Find("cloud"));
        Assert.NotNull(snapshot.Find("local"));
        Assert.True(harness.Settings.Features.CloudProviders); // The user setting itself is untouched.
    }

    private static PillionSettings RealProviderSettings()
    {
        var settings = new PillionSettings();
        settings.Providers.Add(new ProviderSettings { Id = "ollama", Kind = ProviderKind.Ollama });
        settings.Providers.Add(new ProviderSettings { Id = "lmstudio", Kind = ProviderKind.OpenAiCompatible, Endpoint = "http://localhost:1234/v1", Models = ["local-model"] });
        settings.Providers.Add(new ProviderSettings { Id = "openai", Kind = ProviderKind.OpenAi, Models = ["gpt-4o-mini"] });
        return settings;
    }

    private static IModelProviderFactory[] RealFactories(ISecretStore secrets) =>
        [new OllamaProviderFactory(), new OpenAICompatibleProviderFactory(secrets), new OpenAIProviderFactory(secrets)];

    private sealed class PolicySource(PillionSettings settings, params RepositoryPolicy[] policies) : IGatewaySettingsSource
    {
        public event EventHandler? Changed
        {
            add { }
            remove { }
        }

        public EffectiveSettings GetCurrent() => EffectiveSettings.Resolve(settings, policies);
    }
}

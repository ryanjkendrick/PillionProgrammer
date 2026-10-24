using Pillion.Common.Secrets;
using Pillion.Common.Settings;
using Pillion.Contracts;
using Pillion.ModelGateway.Providers.Bundled;
using Pillion.ModelGateway.Tests.TestSupport;

namespace Pillion.ModelGateway.Tests.Embeddings.Bundled;

/// <summary>
/// The bundled provider in routing: the registry adds it without a settings entry, the Embedding task class tries it first
/// (the catalog's provisional embeddingProviderOrder), a configured model still wins, and the feature flag removes it.
/// </summary>
public class BundledRoutingTests
{
    private static readonly RoutingRequest Embedding = new() { TaskClass = TaskClass.Embedding };

    [Fact]
    public void LocalOnly_WithNoOllamaEmbeddingModelConfigured_RoutesEmbeddingToTheBundledModel()
    {
        using var harness = Harness(h => h.Settings.Privacy.LocalOnly = true, ollamaEmbeddings: false);

        var routed = harness.Router.Resolve(Embedding);

        Assert.Equal("bundled/bge-small-en-v1.5", routed.QualifiedId);
        Assert.True(routed.IsLocal);
        Assert.Equal(Profile.LocalFast, routed.Profile);
    }

    [Fact]
    public void TheBundledModel_ComesFirst_EvenWhenOllamaHasAnEmbeddingModel_WhichStaysTheFallback()
    {
        using var harness = Harness(h => h.Settings.Privacy.LocalOnly = true);

        var routed = harness.Router.Resolve(Embedding);

        Assert.Equal("bundled/bge-small-en-v1.5", routed.QualifiedId);
        Assert.Equal("local/qwen3-embedding:0.6b", Assert.Single(routed.Fallbacks).QualifiedId);
    }

    [Fact]
    public void AnEmbeddingModelNamedByRoutingProfiles_WinsOverTheBundledModel()
    {
        using var harness = Harness(h => h.Settings.Routing.Profiles[Profile.LocalFast] = "local/nomic-embed-text");

        Assert.Equal("local/nomic-embed-text", harness.Router.Resolve(Embedding).QualifiedId);
    }

    [Fact]
    public void AnExplicitModelId_CanNameTheImplicitProvider()
    {
        using var harness = Harness();

        var routed = harness.Router.Resolve(Embedding with { ModelId = "bundled/multilingual-e5-small" });

        Assert.Equal("bundled", routed.ProviderId);
        Assert.Equal("multilingual-e5-small", routed.ModelId);
    }

    [Fact]
    public void WithTheFeatureOff_TheBundledProviderIsHidden_AndOllamaServesEmbeddings()
    {
        using var harness = Harness(h => h.Settings.Features.BundledEmbeddings = false);

        var routed = harness.Router.Resolve(Embedding);

        Assert.Equal("local/qwen3-embedding:0.6b", routed.QualifiedId);
        Assert.Contains(harness.Registry.GetSnapshot().Hidden, h => h.Settings.Id == "bundled" && h.Reason.Contains("bundledEmbeddings", StringComparison.Ordinal));
    }

    [Fact]
    public void WhenTheBundledModelIsNotInstalled_OllamaServesEmbeddings()
    {
        using var harness = Harness(h => h.Inventory.Update("bundled", []));

        Assert.Equal("local/qwen3-embedding:0.6b", harness.Router.Resolve(Embedding).QualifiedId);
    }

    [Fact]
    public void ChatNeverRoutesToTheBundledProvider()
    {
        using var harness = Harness(h => h.Settings.Privacy.LocalOnly = true);

        var routed = harness.Router.Resolve(TaskClass.Chat, GatewayHarness.ChatRequest("hi"));

        Assert.NotEqual("bundled", routed.ProviderId);
        Assert.DoesNotContain(routed.Fallbacks, f => f.ProviderId == "bundled");
    }

    [Fact]
    public void TheRegistry_AddsTheBundledProvider_AfterTheConfiguredOnes_WithoutASettingsEntry()
    {
        using var runtime = new BundledModelRuntime(ModelManifest.Default, new BundledModelOptions { InstallModelsDirectory = Path.GetTempPath(), UserModelsDirectory = null });
        var settings = new PillionSettings();
        settings.Providers.Add(new ProviderSettings { Id = "ollama", Kind = ProviderKind.Ollama });
        using var registry = new ProviderRegistry(new StaticGatewaySettingsSource(settings), new InMemorySecretStore(), [new BundledProviderFactory(runtime)], ModelCatalog.Default);

        var snapshot = registry.GetSnapshot();

        var provider = Assert.IsType<BundledEmbeddingProvider>(Assert.Single(snapshot.Providers));
        Assert.Equal("bundled", provider.Id);
        Assert.True(provider.IsLocal);
        Assert.Equal(ProviderKind.Bundled, snapshot.FindSettings("bundled")!.Kind);
        Assert.Equal(["ollama", "bundled"], snapshot.ProviderIds);
        Assert.DoesNotContain(settings.Providers, p => p.Kind == ProviderKind.Bundled);
    }

    [Fact]
    public void AConfiguredBundledEntry_ReplacesTheImplicitOne_AndCanDisableIt()
    {
        using var runtime = new BundledModelRuntime(ModelManifest.Default, new BundledModelOptions { InstallModelsDirectory = Path.GetTempPath(), UserModelsDirectory = null });
        var settings = new PillionSettings();
        settings.Providers.Add(new ProviderSettings { Id = "onnx", Kind = ProviderKind.Bundled, Enabled = false });
        using var registry = new ProviderRegistry(new StaticGatewaySettingsSource(settings), new InMemorySecretStore(), [new BundledProviderFactory(runtime)], ModelCatalog.Default);

        var snapshot = registry.GetSnapshot();

        Assert.Empty(snapshot.Providers);
        Assert.Equal("onnx", Assert.Single(snapshot.Hidden).Settings.Id);
        Assert.Equal(["onnx"], snapshot.ProviderIds);
    }

    [Fact]
    public void TheCatalog_PutsTheBundledKindFirstForEmbeddings()
    {
        Assert.Equal([ProviderKind.Bundled], ModelCatalog.Default.EmbeddingProviderOrder);
        Assert.Equal(0, ModelCatalog.Default.GetEmbeddingProviderPriority(ProviderKind.Bundled));
        Assert.Equal(int.MaxValue, ModelCatalog.Default.GetEmbeddingProviderPriority(ProviderKind.Ollama));
    }

    private static GatewayHarness Harness(Action<GatewayHarness>? configure = null, bool ollamaEmbeddings = true)
    {
        var bundled = new FakeProvider("bundled", isLocal: true, ProviderKind.Bundled);
        bundled.Models =
        [
            bundled.Model(TestModels.Bge, chat: false, embeddings: true, context: BundledEmbeddingProvider.TruncatingContextWindow),
            bundled.Model(TestModels.E5, chat: false, embeddings: true, context: BundledEmbeddingProvider.TruncatingContextWindow),
        ];
        var factory = new FakeProviderFactory(ProviderKind.Bundled) { FeatureFlag = FeatureNames.BundledEmbeddings };
        factory.Providers[bundled.Id] = bundled;

        var harness = new GatewayHarness(extraFactories: [factory]);
        if (ollamaEmbeddings)
        {
            harness.Local!.Models.Add(harness.Local.Model("qwen3-embedding:0.6b", chat: false, embeddings: true, context: 32768));
            harness.Local.Models.Add(harness.Local.Model("nomic-embed-text", chat: false, embeddings: true, context: 8192));
        }

        harness.ListAll();
        harness.Inventory.Update(bundled.Id, bundled.Models);
        configure?.Invoke(harness);
        harness.Reload();
        return harness;
    }
}

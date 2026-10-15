using Pillion.Common.Settings;
using Pillion.Contracts;
using Pillion.ModelGateway.Tests.TestSupport;

namespace Pillion.ModelGateway.Tests.Embeddings;

/// <summary>
/// Routing of the Embedding task class: policy, then the cloud switch (off by default), connectivity, capability, cost, then
/// the explicit model id.
/// </summary>
public class EmbeddingRoutingTests
{
    private const string CloudModel = "text-embedding-3-small";
    private const string Qwen = "qwen3-embedding:0.6b";
    private const string Nomic = "nomic-embed-text";

    private static readonly RoutingRequest Embedding = new() { TaskClass = TaskClass.Embedding };

    /// <summary>A harness whose local provider has nomic-embed-text and qwen3-embedding:0.6b and whose cloud provider has an embedding model.</summary>
    private static GatewayHarness Harness(Action<GatewayHarness>? configure = null, bool localEmbeddings = true, bool cloudEmbeddings = true)
    {
        var harness = new GatewayHarness();
        if (localEmbeddings)
        {
            harness.Local!.Models.Add(harness.Local.Model("nomic-embed-text", chat: false, embeddings: true, context: 8192));
            harness.Local.Models.Add(harness.Local.Model("qwen3-embedding:0.6b", chat: false, embeddings: true, context: 32768));
        }

        if (cloudEmbeddings)
        {
            harness.Cloud!.Models.Add(harness.Cloud.Model(CloudModel, chat: false, embeddings: true, context: 8191));
            harness.Settings.Providers.Single(p => p.Id == "cloud").Models.Add(CloudModel);
        }

        configure?.Invoke(harness);
        harness.ListAll();
        harness.Reload();
        return harness;
    }

    [Fact]
    public void CloudSwitchForEmbedding_IsOffByDefault()
    {
        Assert.False(new CloudSwitchSettings().Embedding);
        Assert.False(ModelRouter.IsCloudAllowed(new CloudSwitchSettings(), TaskClass.Embedding));
        Assert.True(ModelRouter.IsCloudAllowed(new CloudSwitchSettings { Embedding = true }, TaskClass.Embedding));
    }

    [Fact]
    public void Auto_PrefersTheCatalogsFirstInstalledEmbeddingModel_OnTheLocalProvider()
    {
        using var harness = Harness();

        var routed = harness.Router.Resolve(Embedding);

        Assert.Equal("local/qwen3-embedding:0.6b", routed.QualifiedId);
        Assert.Equal(Profile.LocalFast, routed.Profile);
        Assert.True(routed.IsLocal);
        Assert.All(routed.Fallbacks, f => Assert.True(f.IsLocal));
    }

    [Fact]
    public void Auto_FallsBackToTheNextCatalogModel_WhenTheFirstIsNotInstalled()
    {
        using var harness = Harness(h => h.Local!.Models.RemoveAll(m => m.ModelId == "qwen3-embedding:0.6b"));

        Assert.Equal("local/nomic-embed-text", harness.Router.Resolve(Embedding).QualifiedId);
    }

    [Fact]
    public void Auto_UsesAnyInstalledEmbeddingModel_TheCatalogDoesNotName()
    {
        using var harness = Harness(h =>
        {
            h.Local!.Models.RemoveAll(m => m.ModelId is "qwen3-embedding:0.6b" or "nomic-embed-text");
            h.Local.Models.Add(h.Local.Model("mxbai-embed-large", chat: false, embeddings: true));
        });

        Assert.Equal("local/mxbai-embed-large", harness.Router.Resolve(Embedding).QualifiedId);
    }

    [Fact]
    public void WhenNoEmbeddingModelIsInstalled_RoutingFailsWithModelNotInstalled()
    {
        using var harness = Harness(localEmbeddings: false, cloudEmbeddings: false);

        var ex = Assert.Throws<PillionRpcException>(() => harness.Router.Resolve(Embedding));

        Assert.Equal(PillionErrorCode.ModelNotInstalled, ex.Error.Code);
    }

    [Fact]
    public void RoutingProfiles_NamingAChatModel_DoNotApplyToEmbeddings()
    {
        using var harness = Harness(h =>
        {
            h.Settings.Routing.Profiles[Profile.LocalFast] = "local/qwen2.5-coder:1.5b";
            h.Settings.Routing.Profiles[Profile.LocalStrong] = "qwen2.5-coder:14b";
        });

        Assert.Equal("local/qwen3-embedding:0.6b", harness.Router.Resolve(Embedding).QualifiedId);
    }

    [Fact]
    public void RoutingProfiles_NamingAnEmbeddingModel_AreHonoured()
    {
        using var harness = Harness(h => h.Settings.Routing.Profiles[Profile.LocalFast] = "local/nomic-embed-text");

        Assert.Equal("local/nomic-embed-text", harness.Router.Resolve(Embedding).QualifiedId);
    }

    // Policy and the cloud switch.
    [Fact]
    public void WithTheSwitchOff_ACloudProfileStillEndsOnTheLocalModel()
    {
        using var harness = Harness();

        var routed = harness.Router.Resolve(Embedding with { Profile = Profile.Fast });

        Assert.Equal("local/qwen3-embedding:0.6b", routed.QualifiedId);
        Assert.Contains("the cloud switch for Embedding is off", routed.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void WithTheSwitchOff_ACloudEmbeddingModelByName_IsPolicyDenied()
    {
        using var harness = Harness();

        var ex = Assert.Throws<PillionRpcException>(() => harness.Router.Resolve(Embedding with { ModelId = "cloud/" + CloudModel }));

        Assert.Equal(PillionErrorCode.PolicyDenied, ex.Error.Code);
        Assert.Contains("cloud switch for Embedding is off", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void WithTheSwitchOn_AutoStaysLocal()
    {
        using var harness = Harness(h => h.Settings.CloudSwitches.Embedding = true);

        var routed = harness.Router.Resolve(Embedding);

        Assert.Equal("local/qwen3-embedding:0.6b", routed.QualifiedId);
        Assert.DoesNotContain(routed.Fallbacks, f => !f.IsLocal);
    }

    [Fact]
    public void WithTheSwitchOn_ACloudProfileRoutesToTheCloudEmbeddingModel_WithTheLocalModelsAsFallbacks()
    {
        using var harness = Harness(h => h.Settings.CloudSwitches.Embedding = true);

        var routed = harness.Router.Resolve(Embedding with { Profile = Profile.Fast });

        Assert.Equal("cloud/" + CloudModel, routed.QualifiedId);
        Assert.False(routed.IsLocal);
        Assert.Contains(routed.Fallbacks, f => f.QualifiedId == "local/qwen3-embedding:0.6b");
    }

    [Fact]
    public void WithTheSwitchOn_RoutingTasksEmbedding_CanNameACloudProfile()
    {
        using var harness = Harness(h =>
        {
            h.Settings.CloudSwitches.Embedding = true;
            h.Settings.Routing.Tasks[TaskClass.Embedding] = Profile.Balanced;
        });

        Assert.Equal("cloud/" + CloudModel, harness.Router.Resolve(Embedding).QualifiedId);
    }

    [Fact]
    public void WithTheSwitchOn_ACloudEmbeddingModelByName_IsAllowed()
    {
        using var harness = Harness(h => h.Settings.CloudSwitches.Embedding = true);

        var routed = harness.Router.Resolve(Embedding with { ModelId = "cloud/" + CloudModel });

        Assert.Equal("cloud/" + CloudModel, routed.QualifiedId);
    }

    [Fact]
    public void LocalOnly_BeatsTheSwitch_ForProfilesAndForNamedModels()
    {
        using var harness = Harness(h =>
        {
            h.Settings.CloudSwitches.Embedding = true;
            h.Settings.Privacy.LocalOnly = true;
        });

        var viaProfile = harness.Router.Resolve(Embedding with { Profile = Profile.Frontier });
        var ex = Assert.Throws<PillionRpcException>(() => harness.Router.Resolve(Embedding with { ModelId = "cloud/" + CloudModel }));

        Assert.True(viaProfile.IsLocal);
        Assert.All(viaProfile.Fallbacks, f => Assert.True(f.IsLocal));
        Assert.Equal(PillionErrorCode.PolicyDenied, ex.Error.Code);
        Assert.Contains("Local-only", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void LocalOnly_WithOnlyACloudEmbeddingModel_IsPolicyDenied()
    {
        using var harness = Harness(h =>
        {
            h.Settings.CloudSwitches.Embedding = true;
            h.Settings.Privacy.LocalOnly = true;
        }, localEmbeddings: false);

        var ex = Assert.Throws<PillionRpcException>(() => harness.Router.Resolve(Embedding with { Profile = Profile.Fast }));

        Assert.True(ex.Error.Code is PillionErrorCode.PolicyDenied or PillionErrorCode.ModelNotInstalled, ex.Error.Code.ToString());
    }

    // Capability.
    [Fact]
    public void WithTheSwitchOn_ACloudProviderWithoutEmbeddingModels_IsSkipped()
    {
        using var harness = Harness(h => h.Settings.CloudSwitches.Embedding = true, cloudEmbeddings: false);

        var routed = harness.Router.Resolve(Embedding with { Profile = Profile.Fast });

        Assert.True(routed.IsLocal);
        Assert.Contains("fell back", routed.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void ACloudModelThatDoesNotEmbed_IsFilteredByCapability()
    {
        // The catalog prefers text-embedding-3-small, but this server lists it as a model without the embeddings capability.
        using var harness = Harness(
            h =>
            {
                h.Settings.CloudSwitches.Embedding = true;
                h.Cloud!.Models.RemoveAll(m => m.ModelId == CloudModel);
                h.Cloud.Models.Add(h.Cloud.Model(CloudModel, chat: true, embeddings: false));
            },
            cloudEmbeddings: false);
        harness.Settings.Providers.Single(p => p.Id == "cloud").Models.Add(CloudModel);
        harness.Reload();

        var routed = harness.Router.Resolve(Embedding with { Profile = Profile.Fast });

        Assert.True(routed.IsLocal);
        Assert.Contains("cloud/" + CloudModel + " (no embeddings support)", routed.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void ModelsWithoutTheEmbeddingsCapability_AreNeverPickedByProfile()
    {
        using var harness = Harness(h =>
        {
            h.Local!.Models.RemoveAll(m => m.ModelId is Qwen or Nomic);
            h.Local.Models.Add(h.Local.Model(Qwen, chat: true, embeddings: false));
        }, localEmbeddings: false, cloudEmbeddings: false);

        var ex = Assert.Throws<PillionRpcException>(() => harness.Router.Resolve(Embedding));

        Assert.Equal(PillionErrorCode.ProviderUnavailable, ex.Error.Code);
        Assert.Contains("no embeddings support", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AnInputLongerThanTheModelsContext_SkipsThatModel()
    {
        using var harness = Harness(h =>
        {
            h.Local!.Models.RemoveAll(m => m.ModelId == "qwen3-embedding:0.6b");
            h.Local.Models[^1] = h.Local.Model("nomic-embed-text", chat: false, embeddings: true, context: 2048);
        });

        var ex = Assert.Throws<PillionRpcException>(() => harness.Router.Resolve(Embedding with { EstimatedPromptTokens = 5_000 }));

        Assert.Equal(PillionErrorCode.ContextOverflow, ex.Error.Code);
    }

    // Connectivity.
    [Fact]
    public void AnUnreachableLocalProvider_WithTheSwitchOn_FallsToTheCloudOnlyWhenAProfileAllowsIt()
    {
        using var harness = Harness(h =>
        {
            h.Settings.CloudSwitches.Embedding = true;
            h.Reachability.Unreachable.Add("local");
        });

        var viaProfile = harness.Router.Resolve(Embedding with { Profile = Profile.Fast });
        var auto = Assert.Throws<PillionRpcException>(() => harness.Router.Resolve(Embedding));

        Assert.Equal("cloud/" + CloudModel, viaProfile.QualifiedId);
        Assert.Equal(PillionErrorCode.ProviderUnavailable, auto.Error.Code);
    }
}

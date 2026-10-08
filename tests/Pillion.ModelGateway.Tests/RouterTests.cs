using Pillion.Contracts;
using Pillion.ModelGateway.Tests.TestSupport;

namespace Pillion.ModelGateway.Tests;

public class RouterTests
{
    /// <summary>One routing case: how to set up the harness, what to ask, and what must come back.</summary>
    internal sealed record Case(
        string Name,
        Action<GatewayHarness> Setup,
        RoutingRequest Request,
        string? ExpectedModel,
        Profile? ExpectedProfile = null,
        PillionErrorCode? ExpectedError = null)
    {
        public override string ToString() => Name;
    }

    private static readonly RoutingRequest Chat = new() { TaskClass = TaskClass.Chat };
    private static readonly RoutingRequest Agent = new() { TaskClass = TaskClass.Agent };
    private static readonly RoutingRequest Autocomplete = new() { TaskClass = TaskClass.Autocomplete };
    private static readonly RoutingRequest Vision = new() { TaskClass = TaskClass.Vision };

    private static readonly Case[] Cases =
    [
        // Auto policy, online.
        new("chat online -> Balanced", _ => { }, Chat, "cloud/cloud-balanced", Profile.Balanced),
        new("inline edit online -> Balanced", _ => { }, Chat with { TaskClass = TaskClass.InlineEdit }, "cloud/cloud-balanced", Profile.Balanced),
        new("agent online, agent switch on -> Frontier", h => h.Settings.CloudSwitches.Agent = true, Agent, "cloud/cloud-frontier", Profile.Frontier),
        new("vision online -> Frontier", h => h.ListAll(), Vision, "cloud/cloud-frontier", Profile.Frontier),
        new("autocomplete with a local FIM model -> LocalFast", _ => { }, Autocomplete, "local/qwen2.5-coder:1.5b", Profile.LocalFast),
        new(
            "autocomplete without a local FIM model, cloud switch on -> Fast",
            h =>
            {
                h.Local!.Models = [h.Local.Model("gemma4:e2b")];
                h.Settings.CloudSwitches.Autocomplete = true;
                h.ListAll();
            },
            Autocomplete,
            "cloud/cloud-fast",
            Profile.Fast),
        new(
            "embedding is always local",
            h => { h.Local!.Models.Add(h.Local.Model("nomic-embed-text", chat: false, embeddings: true)); h.ListAll(); },
            new RoutingRequest { TaskClass = TaskClass.Embedding },
            "local/nomic-embed-text",
            Profile.LocalFast),

        // Rule 1: policy.
        new("local-only: chat -> LocalStrong", h => h.Settings.Privacy.LocalOnly = true, Chat, "local/qwen2.5-coder:14b", Profile.LocalStrong),
        new("local-only: requested cloud profile still goes local", h => h.Settings.Privacy.LocalOnly = true, Chat with { Profile = Profile.Frontier }, "local/qwen2.5-coder:14b", Profile.LocalStrong),
        new("local-only, no local provider -> PolicyDenied", h => { h.Settings.Privacy.LocalOnly = true; h.Settings.Providers.RemoveAll(p => p.Id == "local"); }, Chat, null, ExpectedError: PillionErrorCode.PolicyDenied),

        // Rule 2: cloud switches.
        new("chat switch off -> LocalStrong", h => h.Settings.CloudSwitches.Chat = false, Chat, "local/qwen2.5-coder:14b", Profile.LocalStrong),
        new("agent switch off by default -> LocalStrong", _ => { }, Agent, "local/qwen2.5-coder:14b", Profile.LocalStrong),
        new("autocomplete without local FIM, switch off -> ModelNotInstalled", h => { h.Local!.Models = [h.Local.Model("gemma4:e2b")]; h.ListAll(); }, Autocomplete, null, ExpectedError: PillionErrorCode.ModelNotInstalled),

        // Rule 3: connectivity.
        new("cloud unreachable -> chat goes LocalStrong (offline)", h => h.Reachability.Unreachable.Add("cloud"), Chat, "local/qwen2.5-coder:14b", Profile.LocalStrong),
        new("requested Balanced, cloud unreachable -> falls back to LocalStrong", h => h.Reachability.Unreachable.Add("cloud"), Chat with { Profile = Profile.Balanced }, "local/qwen2.5-coder:14b", Profile.LocalStrong),
        new("everything unreachable -> ProviderUnavailable", h => { h.Reachability.Unreachable.Add("cloud"); h.Reachability.Unreachable.Add("local"); }, Chat, null, ExpectedError: PillionErrorCode.ProviderUnavailable),
        new(
            "open circuit counts as unreachable",
            h =>
            {
                for (var i = 0; i < 3; i++)
                {
                    h.Breakers.RecordFailure("cloud");
                }
            },
            Chat,
            "local/qwen2.5-coder:14b",
            Profile.LocalStrong),

        // Rule 4: capability.
        new(
            "vision offline, local model without vision -> no route",
            h => { h.Reachability.Unreachable.Add("cloud"); h.ListAll(); },
            Vision,
            null,
            ExpectedError: PillionErrorCode.ProviderUnavailable),
        new(
            "prompt larger than the Balanced model's context -> next profile",
            h => { h.Cloud!.Models[1] = h.Cloud.Model("cloud-balanced", context: 4096); h.ListAll(); },
            Chat with { EstimatedPromptTokens = 10_000 },
            "cloud/cloud-fast",
            Profile.Fast),
        new(
            "catalog model not installed -> installed local model",
            h => { h.Local!.Models = [h.Local.Model("qwen2.5-coder:7b", fim: true)]; h.Settings.CloudSwitches.Chat = false; h.ListAll(); },
            Chat,
            "local/qwen2.5-coder:7b",
            Profile.LocalStrong),

        // Settings.
        new("routing.profiles overrides the catalog", h => { h.Settings.Privacy.LocalOnly = true; h.Settings.Routing.Profiles[Profile.LocalStrong] = "qwen2.5-coder:32b"; }, Chat, "local/qwen2.5-coder:32b", Profile.LocalStrong),
        new("routing.tasks pins a profile", h => h.Settings.Routing.Tasks[TaskClass.Chat] = Profile.LocalFast, Chat, "local/qwen2.5-coder:1.5b", Profile.LocalFast),

        // Rule 6: the explicit model id wins.
        new("explicit local model wins over Auto", _ => { }, Chat with { ModelId = "local/qwen2.5-coder:1.5b" }, "local/qwen2.5-coder:1.5b", Profile.LocalFast),
        new("explicit bare id resolves its provider", h => h.ListAll(), Chat with { ModelId = "cloud-frontier" }, "cloud/cloud-frontier", Profile.Frontier),
        new("explicit model wins over the capability check", h => { h.Settings.CloudSwitches.Autocomplete = true; h.ListAll(); }, Autocomplete with { ModelId = "cloud/cloud-balanced" }, "cloud/cloud-balanced", Profile.Balanced),
        new("explicit cloud model with its task switch off -> PolicyDenied", h => h.ListAll(), Autocomplete with { ModelId = "cloud/cloud-fast" }, null, ExpectedError: PillionErrorCode.PolicyDenied),
        new("explicit cloud model under local-only -> PolicyDenied", h => h.Settings.Privacy.LocalOnly = true, Chat with { ModelId = "cloud/cloud-balanced" }, null, ExpectedError: PillionErrorCode.PolicyDenied),
        new("explicit model of an unknown provider id falls to a bare id", _ => { }, Chat with { ModelId = "elsewhere/model" }, "local/elsewhere/model", Profile.LocalStrong),
    ];

    public static TheoryData<string> CaseNames => new(Cases.Select(c => c.Name));

    [Theory]
    [MemberData(nameof(CaseNames))]
    public void Resolve_FollowsTheRoutingRules(string name)
    {
        var testCase = Cases.Single(c => c.Name == name);
        using var harness = new GatewayHarness();
        testCase.Setup(harness);
        harness.Reload();

        if (testCase.ExpectedError is { } code)
        {
            var ex = Assert.Throws<PillionRpcException>(() => harness.Router.Resolve(testCase.Request));
            Assert.Equal(code, ex.Error.Code);
            return;
        }

        var routed = harness.Router.Resolve(testCase.Request);
        Assert.Equal(testCase.ExpectedModel, routed.QualifiedId);
        Assert.Equal(testCase.ExpectedProfile, routed.Profile);
        Assert.False(string.IsNullOrWhiteSpace(routed.Reason));
    }

    [Fact]
    public void Resolve_LocalOnly_PutsNoCloudModelInTheFallbackChain()
    {
        using var harness = new GatewayHarness();
        harness.Settings.Privacy.LocalOnly = true;
        harness.Reload();

        var routed = harness.Router.Resolve(new RoutingRequest { TaskClass = TaskClass.Chat, Profile = Profile.Frontier });

        Assert.True(routed.IsLocal);
        Assert.All(routed.Fallbacks, f => Assert.True(f.IsLocal));
        Assert.Contains("Local-only", routed.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void Resolve_Balanced_FallsBackToTheNextCloudProfileThenLocal()
    {
        using var harness = new GatewayHarness();

        var routed = harness.Router.Resolve(new RoutingRequest { TaskClass = TaskClass.Chat });

        Assert.Equal(
            ["cloud/cloud-fast", "local/qwen2.5-coder:14b", "local/qwen2.5-coder:1.5b"],
            routed.Fallbacks.Select(f => f.QualifiedId));
    }

    [Fact]
    public void Resolve_ExplicitModel_KeepsTheAutoChainAsFallbacks()
    {
        using var harness = new GatewayHarness();

        var routed = harness.Router.Resolve(new RoutingRequest { TaskClass = TaskClass.Chat, ModelId = "local/qwen2.5-coder:1.5b" });

        Assert.Equal("local/qwen2.5-coder:1.5b", routed.QualifiedId);
        Assert.Equal("cloud/cloud-balanced", routed.Fallbacks[0].QualifiedId);
        Assert.DoesNotContain(routed.Fallbacks, f => f.QualifiedId == routed.QualifiedId);
    }

    [Fact]
    public void Resolve_NoProviders_ExplainsHowToAddOne()
    {
        using var harness = new GatewayHarness(withCloud: false, withLocal: false);

        var ex = Assert.Throws<PillionRpcException>(() => harness.Router.Resolve(new RoutingRequest { TaskClass = TaskClass.Chat }));

        Assert.Equal(PillionErrorCode.ProviderUnavailable, ex.Error.Code);
        Assert.Contains("No model provider is configured", ex.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(Profile.Frontier, TaskClass.Chat, new[] { Profile.Frontier, Profile.Balanced, Profile.LocalStrong, Profile.LocalFast })]
    [InlineData(Profile.Balanced, TaskClass.Chat, new[] { Profile.Balanced, Profile.Fast, Profile.LocalStrong, Profile.LocalFast })]
    [InlineData(Profile.Fast, TaskClass.Autocomplete, new[] { Profile.Fast, Profile.Balanced, Profile.LocalFast, Profile.LocalStrong })]
    [InlineData(Profile.LocalStrong, TaskClass.Chat, new[] { Profile.LocalStrong, Profile.LocalFast })]
    [InlineData(Profile.LocalFast, TaskClass.Autocomplete, new[] { Profile.LocalFast, Profile.LocalStrong })]
    public void FallbackChain_IsNextProfileInLocalityThenLocal(Profile profile, TaskClass taskClass, Profile[] expected)
    {
        Assert.Equal(expected, ModelRouter.FallbackChain(profile, taskClass));
    }
}

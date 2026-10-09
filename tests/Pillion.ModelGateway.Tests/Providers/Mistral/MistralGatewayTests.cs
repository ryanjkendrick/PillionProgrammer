using System.Net;
using Pillion.Common.Settings;
using Pillion.Contracts;
using Pillion.ModelGateway.Providers.Mistral;
using Pillion.ModelGateway.Tests.Providers.Cloud;
using Pillion.ModelGateway.Tests.TestSupport;

namespace Pillion.ModelGateway.Tests.Providers.Mistral;

/// <summary>The Mistral provider behind the real gateway, router, registry and redaction.</summary>
public sealed class MistralGatewayTests
{
    private static CloudRig Rig(FakeCloudHandler handler, string? key = MistralProviderTests.Key, Action<PillionSettings>? configure = null) =>
        new(handler, MistralProviderTests.Settings("mistral"), (secrets, h) => new MistralProviderFactory(secrets, () => h), key, configure);

    [Fact]
    public async Task Chat_IsRoutedToMistral_WhenTheCloudSwitchIsOnAndAKeyExists()
    {
        using var rig = Rig(MistralProviderTests.Defaults());

        var chunks = await rig.ChatAsync("Say hello");

        Assert.Equal("Hello from Pillion", string.Concat(chunks.Select(c => c.Text)));
        Assert.All(chunks, c => Assert.Equal("mistral", c.ProviderId));
        Assert.All(chunks, c => Assert.Equal("mistral-small-latest", c.ModelId)); // Balanced.
        Assert.True(chunks[^1].IsFinal);
        Assert.Null(chunks[^1].Error);
        Assert.Equal(18, chunks[^1].PromptTokens);
        Assert.Equal(6, chunks[^1].CompletionTokens);
        var record = Assert.Single(rig.OutboundLog.GetRecent());
        Assert.False(record.IsLocal);
        Assert.Equal(OutboundOutcome.Succeeded, record.Outcome);
    }

    [Theory]
    [InlineData(Profile.Fast, "codestral-latest")]
    [InlineData(Profile.Balanced, "mistral-small-latest")]
    [InlineData(Profile.Frontier, "mistral-medium-3-5")]
    public async Task EachCloudProfile_UsesTheCatalogDefault(Profile profile, string expected)
    {
        using var rig = Rig(MistralProviderTests.Defaults());

        var chunks = await rig.ChatAsync("Say hello", profile: profile);

        Assert.All(chunks, c => Assert.Equal(expected, c.ModelId));
        Assert.Equal(expected, rig.Handler.RequestsTo("v1/chat/completions").Single().Body!["model"]!.GetValue<string>());
    }

    [Fact]
    public async Task Autocomplete_WithTheCloudSwitchOn_GoesThroughTheFimEndpoint_OnACodestralModel()
    {
        using var rig = Rig(MistralProviderTests.Defaults(), configure: settings => settings.CloudSwitches.Autocomplete = true);
        var request = new CompletionRequest
        {
            RequestId = "c1",
            Prefix = "int Add(int a, int b) => ",
            Suffix = ";",
            LanguageId = "csharp",
            TaskClass = TaskClass.Autocomplete,
        };

        var chunks = await rig.Gateway.CompleteAsync(request, CancellationToken.None).CollectAsync();

        Assert.Equal("a + b", string.Concat(chunks.Select(c => c.Text)));
        Assert.All(chunks, c => Assert.Equal("mistral", c.ProviderId));
        Assert.All(chunks, c => Assert.Equal("codestral-latest", c.ModelId));
        var fim = Assert.Single(rig.Handler.RequestsTo("v1/fim/completions"));
        Assert.Equal("codestral-latest", fim.Body!["model"]!.GetValue<string>());
        Assert.Empty(rig.Handler.RequestsTo("v1/chat/completions"));
    }

    [Fact]
    public async Task Autocomplete_WithTheCloudSwitchOff_SendsNothing()
    {
        using var rig = Rig(MistralProviderTests.Defaults());
        var request = new CompletionRequest { RequestId = "c1", Prefix = "x", Suffix = "y", LanguageId = "csharp", TaskClass = TaskClass.Autocomplete };

        await Assert.ThrowsAnyAsync<Exception>(() => rig.Gateway.CompleteAsync(request, CancellationToken.None).CollectAsync());

        Assert.Empty(rig.Handler.Requests);
    }

    [Fact]
    public async Task Provider_WithoutAKey_IsHiddenFromRouting_AndNothingIsSent()
    {
        using var rig = Rig(MistralProviderTests.Defaults(), key: null);

        await Assert.ThrowsAsync<PillionRpcException>(() => rig.ChatAsync("Say hello"));

        var snapshot = rig.Registry.GetSnapshot();
        Assert.Empty(snapshot.Providers);
        Assert.Contains("No API key", Assert.Single(snapshot.Hidden).Reason, StringComparison.Ordinal);
        Assert.Empty(rig.Handler.Requests);
        Assert.False((await rig.Gateway.TestProviderAsync("mistral", CancellationToken.None)).IsSuccess);
    }

    [Fact]
    public async Task Chat_WithTheCloudSwitchOff_IsRefusedByPolicy_AndNothingIsSent()
    {
        using var rig = Rig(MistralProviderTests.Defaults(), configure: settings => settings.CloudSwitches.Chat = false);

        var ex = await Assert.ThrowsAsync<PillionRpcException>(() => rig.ChatAsync("Say hello"));

        Assert.Equal(PillionErrorCode.PolicyDenied, ex.Error.Code);
        Assert.Empty(rig.Handler.Requests);
    }

    [Fact]
    public async Task TheFeatureFlagOff_SkipsTheFactory()
    {
        using var rig = Rig(MistralProviderTests.Defaults(), configure: settings => settings.Features.MistralProvider = false);

        await Assert.ThrowsAsync<PillionRpcException>(() => rig.ChatAsync("Say hello"));

        Assert.Contains("mistralProvider", Assert.Single(rig.Registry.GetSnapshot().Hidden).Reason, StringComparison.Ordinal);
        Assert.Empty(rig.Handler.Requests);
    }

    [Fact]
    public async Task Chat_A429ThenSuccess_IsRetriedByTheGateway()
    {
        var handler = MistralProviderTests.Defaults();
        var calls = 0;
        handler.On("POST v1/chat/completions", _ => Interlocked.Increment(ref calls) == 1
            ? FakeCloudHandler.Respond(HttpStatusCode.TooManyRequests, FakeCloudHandler.Fixture("Mistral", "error-429.json"))
            : FakeCloudHandler.Respond(HttpStatusCode.OK, FakeCloudHandler.Fixture("Mistral", "chat-stream.sse"), "text/event-stream"));
        using var rig = Rig(handler);

        var chunks = await rig.ChatAsync("Say hello");

        Assert.Equal("Hello from Pillion", string.Concat(chunks.Select(c => c.Text)));
        Assert.Equal(2, handler.RequestsTo("v1/chat/completions").Count());
        Assert.Null(chunks[^1].Error);
    }

    [Fact]
    public async Task Chat_ARejectedKey_IsNotRetried_AndTheMessageDoesNotContainTheKey()
    {
        var handler = MistralProviderTests.Defaults().Fail("POST v1/chat/completions", HttpStatusCode.Unauthorized, "Mistral", "error-401.json");
        using var rig = Rig(handler);

        var ex = await Assert.ThrowsAsync<PillionRpcException>(() => rig.ChatAsync("Say hello"));

        Assert.Equal(PillionErrorCode.ProviderUnavailable, ex.Error.Code);
        Assert.DoesNotContain(MistralProviderTests.Key, ex.Error.Message, StringComparison.Ordinal);

        // One try on the Balanced model and one on the Fast model; no retries.
        Assert.Equal(["mistral-small-latest", "codestral-latest"], handler.Requests.Select(r => r.Body!["model"]!.GetValue<string>()));
    }

    [Fact]
    public async Task Chat_ContextLengthError_ReachesTheClientAsContextOverflow()
    {
        var handler = MistralProviderTests.Defaults().Fail("POST v1/chat/completions", HttpStatusCode.BadRequest, "Mistral", "error-context-length.json");
        using var rig = Rig(handler);

        var ex = await Assert.ThrowsAsync<PillionRpcException>(() => rig.ChatAsync("Say hello"));

        Assert.Equal(PillionErrorCode.ContextOverflow, ex.Error.Code);
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task ListModels_ThroughTheGateway_MarksCodestralAsFim()
    {
        using var rig = Rig(MistralProviderTests.Defaults());

        var models = await rig.Gateway.ListModelsAsync(CancellationToken.None);

        Assert.Contains(models, m => m.Id == "mistral/codestral-latest" && m.Capabilities.HasFlag(ModelCapabilities.Fim) && m.IsAvailable && !m.IsLocal);
        Assert.Contains(models, m => m.Id == "mistral/mistral-small-latest" && !m.Capabilities.HasFlag(ModelCapabilities.Fim));
    }

    [Fact]
    public async Task TestProvider_ThroughTheGateway_ReportsTheProbe()
    {
        using var rig = Rig(MistralProviderTests.Defaults());

        var result = await rig.Gateway.TestProviderAsync("mistral", CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Contains("accepted", result.Message, StringComparison.Ordinal);
    }
}

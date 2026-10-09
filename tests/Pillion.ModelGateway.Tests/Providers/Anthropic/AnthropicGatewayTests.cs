using System.Net;
using Pillion.Common.Settings;
using Pillion.Contracts;
using Pillion.ModelGateway.Providers.Anthropic;
using Pillion.ModelGateway.Tests.Providers.Cloud;

namespace Pillion.ModelGateway.Tests.Providers.Anthropic;

/// <summary>The Anthropic provider behind the real gateway, router, registry and redaction.</summary>
public sealed class AnthropicGatewayTests
{
    private static CloudRig Rig(FakeCloudHandler handler, string? key = AnthropicProviderTests.Key, Action<PillionSettings>? configure = null) =>
        new(handler, AnthropicProviderTests.Settings("claude"), (secrets, h) => new AnthropicProviderFactory(secrets, () => h), key, configure);

    [Fact]
    public async Task Chat_IsRoutedToClaude_WhenTheCloudSwitchIsOnAndAKeyExists()
    {
        using var rig = Rig(AnthropicProviderTests.Defaults());

        var chunks = await rig.ChatAsync("Say hello");

        Assert.Equal("Hello from Pillion", string.Concat(chunks.Select(c => c.Text)));
        Assert.All(chunks, c => Assert.Equal("claude", c.ProviderId));

        // Auto chat is Balanced, which the catalog maps to Sonnet.
        Assert.All(chunks, c => Assert.Equal("claude-sonnet-5-5", c.ModelId));
        Assert.True(chunks[^1].IsFinal);
        Assert.Null(chunks[^1].Error);
        Assert.Equal(30, chunks[^1].PromptTokens);
        Assert.Equal(15, chunks[^1].CompletionTokens);
        var record = Assert.Single(rig.OutboundLog.GetRecent());
        Assert.False(record.IsLocal);
        Assert.Equal("claude", record.ProviderId);
        Assert.Equal(OutboundOutcome.Succeeded, record.Outcome);
    }

    [Theory]
    [InlineData(Profile.Fast, "claude-haiku-5-5")]
    [InlineData(Profile.Balanced, "claude-sonnet-5-5")]
    [InlineData(Profile.Frontier, "claude-opus-5-5")]
    public async Task EachCloudProfile_UsesTheCatalogDefault(Profile profile, string expected)
    {
        using var rig = Rig(AnthropicProviderTests.Defaults());

        var chunks = await rig.ChatAsync("Say hello", profile: profile);

        Assert.All(chunks, c => Assert.Equal(expected, c.ModelId));
        Assert.Equal(expected, rig.Handler.RequestsTo("v1/messages").Single().Body!["model"]!.GetValue<string>());
    }

    [Fact]
    public async Task Provider_WithoutAKey_IsHiddenFromRouting_AndNothingIsSent()
    {
        using var rig = Rig(AnthropicProviderTests.Defaults(), key: null);

        await Assert.ThrowsAsync<PillionRpcException>(() => rig.ChatAsync("Say hello"));

        var snapshot = rig.Registry.GetSnapshot();
        Assert.Empty(snapshot.Providers);
        Assert.Contains("No API key", Assert.Single(snapshot.Hidden).Reason, StringComparison.Ordinal);
        Assert.Empty(rig.Handler.Requests);
        var test = await rig.Gateway.TestProviderAsync("claude", CancellationToken.None);
        Assert.False(test.IsSuccess);
        Assert.Contains("No API key", test.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Chat_WithTheCloudSwitchOff_IsRefusedByPolicy_AndNothingIsSent()
    {
        using var rig = Rig(AnthropicProviderTests.Defaults(), configure: settings => settings.CloudSwitches.Chat = false);

        var ex = await Assert.ThrowsAsync<PillionRpcException>(() => rig.ChatAsync("Say hello"));

        Assert.Equal(PillionErrorCode.PolicyDenied, ex.Error.Code);
        Assert.Empty(rig.Handler.Requests);
    }

    [Fact]
    public async Task TheFeatureFlagOff_SkipsTheFactory()
    {
        using var rig = Rig(AnthropicProviderTests.Defaults(), configure: settings => settings.Features.AnthropicProvider = false);

        await Assert.ThrowsAsync<PillionRpcException>(() => rig.ChatAsync("Say hello"));

        var hidden = Assert.Single(rig.Registry.GetSnapshot().Hidden);
        Assert.Contains("anthropicProvider", hidden.Reason, StringComparison.Ordinal);
        Assert.Empty(rig.Handler.Requests);
    }

    [Fact]
    public async Task Chat_A529ThenSuccess_IsRetriedByTheGateway()
    {
        var handler = AnthropicProviderTests.Defaults();
        var calls = 0;
        handler.On("POST v1/messages", _ => Interlocked.Increment(ref calls) == 1
            ? FakeCloudHandler.Respond((HttpStatusCode)529, FakeCloudHandler.Fixture("Anthropic", "error-529.json"))
            : FakeCloudHandler.Respond(HttpStatusCode.OK, FakeCloudHandler.Fixture("Anthropic", "chat-stream.sse"), "text/event-stream"));
        using var rig = Rig(handler);

        var chunks = await rig.ChatAsync("Say hello");

        Assert.Equal("Hello from Pillion", string.Concat(chunks.Select(c => c.Text)));
        Assert.Equal(2, handler.RequestsTo("v1/messages").Count());
        Assert.Null(chunks[^1].Error);
    }

    [Fact]
    public async Task Chat_ARejectedKey_IsNotRetried_AndTheMessageDoesNotContainTheKey()
    {
        var handler = AnthropicProviderTests.Defaults().Fail("POST v1/messages", HttpStatusCode.Unauthorized, "Anthropic", "error-401.json");
        using var rig = Rig(handler);

        var ex = await Assert.ThrowsAsync<PillionRpcException>(() => rig.ChatAsync("Say hello"));

        Assert.Equal(PillionErrorCode.ProviderUnavailable, ex.Error.Code);
        Assert.DoesNotContain(AnthropicProviderTests.Key, ex.Error.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("sk-ant", ex.Error.Message, StringComparison.Ordinal);

        // Never retried on the same model; the gateway still offers the Fast model as the next candidate.
        Assert.Equal(["claude-sonnet-5-5", "claude-haiku-5-5"], handler.Requests.Select(r => r.Body!["model"]!.GetValue<string>()));
    }

    [Fact]
    public async Task Chat_ContextLengthError_ReachesTheClientAsContextOverflow()
    {
        var handler = AnthropicProviderTests.Defaults().Fail("POST v1/messages", HttpStatusCode.BadRequest, "Anthropic", "error-context-length.json");
        using var rig = Rig(handler);

        var ex = await Assert.ThrowsAsync<PillionRpcException>(() => rig.ChatAsync("Say hello"));

        Assert.Equal(PillionErrorCode.ContextOverflow, ex.Error.Code);
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task Chat_AnUnknownModel_ReachesTheClientAsModelNotInstalled()
    {
        var handler = AnthropicProviderTests.Defaults().Fail("POST v1/messages", HttpStatusCode.NotFound, "Anthropic", "error-404-model.json");
        using var rig = Rig(handler);

        var ex = await Assert.ThrowsAsync<PillionRpcException>(() => rig.ChatAsync("Say hello", "claude/claude-imaginary-9"));

        Assert.Equal(PillionErrorCode.ModelNotInstalled, ex.Error.Code);
    }

    [Fact]
    public async Task ListModels_ThroughTheGateway_NamesTheProviderOnEveryModel()
    {
        using var rig = Rig(AnthropicProviderTests.Defaults());

        var models = await rig.Gateway.ListModelsAsync(CancellationToken.None);

        Assert.Contains(models, m => m.Id == "claude/claude-sonnet-5-5" && m.ProviderId == "claude" && !m.IsLocal && m.IsAvailable);
        Assert.All(models, m => Assert.False(m.Capabilities.HasFlag(ModelCapabilities.Fim)));
    }

    [Fact]
    public async Task TestProvider_ThroughTheGateway_ReportsTheProbe()
    {
        using var rig = Rig(AnthropicProviderTests.Defaults());

        var result = await rig.Gateway.TestProviderAsync("claude", CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Contains("accepted", result.Message, StringComparison.Ordinal);
    }
}

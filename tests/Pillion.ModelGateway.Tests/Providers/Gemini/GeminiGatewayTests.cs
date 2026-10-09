using System.Net;
using Pillion.Common.Settings;
using Pillion.Contracts;
using Pillion.ModelGateway.Providers.Gemini;
using Pillion.ModelGateway.Tests.Providers.Cloud;

namespace Pillion.ModelGateway.Tests.Providers.Gemini;

/// <summary>The Gemini provider behind the real gateway, router, registry and redaction.</summary>
public sealed class GeminiGatewayTests
{
    private static CloudRig Rig(FakeCloudHandler handler, string? key = GeminiProviderTests.Key, Action<PillionSettings>? configure = null) =>
        new(handler, GeminiProviderTests.Settings("gem"), (secrets, h) => new GeminiProviderFactory(secrets, () => h), key, configure);

    [Fact]
    public async Task Chat_IsRoutedToGemini_WhenTheCloudSwitchIsOnAndAKeyExists()
    {
        using var rig = Rig(GeminiProviderTests.Defaults());

        var chunks = await rig.ChatAsync("Say hello");

        Assert.Equal("Hello from Pillion", string.Concat(chunks.Select(c => c.Text)));
        Assert.All(chunks, c => Assert.Equal("gem", c.ProviderId));
        Assert.All(chunks, c => Assert.Equal("gemini-3.8-flash", c.ModelId)); // Balanced.
        Assert.True(chunks[^1].IsFinal);
        Assert.Null(chunks[^1].Error);
        Assert.Equal(21, chunks[^1].PromptTokens);
        Assert.Equal(3, chunks[^1].CompletionTokens);
        var record = Assert.Single(rig.OutboundLog.GetRecent());
        Assert.False(record.IsLocal);
        Assert.Equal("gem", record.ProviderId);
        Assert.Equal(OutboundOutcome.Succeeded, record.Outcome);
    }

    [Theory]
    [InlineData(Profile.Fast, "gemini-3.5-flash-lite")]
    [InlineData(Profile.Balanced, "gemini-3.8-flash")]
    [InlineData(Profile.Frontier, "gemini-3.1-pro-preview")]
    public async Task EachCloudProfile_UsesTheCatalogDefault(Profile profile, string expected)
    {
        var handler = new FakeCloudHandler().Serve($"POST v1beta/models/{expected}:streamGenerateContent", "Gemini", "chat-stream.sse");
        using var rig = Rig(handler);

        var chunks = await rig.ChatAsync("Say hello", profile: profile);

        Assert.All(chunks, c => Assert.Equal(expected, c.ModelId));
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task Provider_WithoutAKey_IsHiddenFromRouting_AndNothingIsSent()
    {
        using var rig = Rig(GeminiProviderTests.Defaults(), key: null);

        await Assert.ThrowsAsync<PillionRpcException>(() => rig.ChatAsync("Say hello"));

        var snapshot = rig.Registry.GetSnapshot();
        Assert.Empty(snapshot.Providers);
        Assert.Contains("No API key", Assert.Single(snapshot.Hidden).Reason, StringComparison.Ordinal);
        Assert.Empty(rig.Handler.Requests);
        Assert.False((await rig.Gateway.TestProviderAsync("gem", CancellationToken.None)).IsSuccess);
    }

    [Fact]
    public async Task Chat_WithTheCloudSwitchOff_IsRefusedByPolicy_AndNothingIsSent()
    {
        using var rig = Rig(GeminiProviderTests.Defaults(), configure: settings => settings.CloudSwitches.Chat = false);

        var ex = await Assert.ThrowsAsync<PillionRpcException>(() => rig.ChatAsync("Say hello"));

        Assert.Equal(PillionErrorCode.PolicyDenied, ex.Error.Code);
        Assert.Empty(rig.Handler.Requests);
    }

    [Fact]
    public async Task TheFeatureFlagOff_SkipsTheFactory()
    {
        using var rig = Rig(GeminiProviderTests.Defaults(), configure: settings => settings.Features.GeminiProvider = false);

        await Assert.ThrowsAsync<PillionRpcException>(() => rig.ChatAsync("Say hello"));

        Assert.Contains("geminiProvider", Assert.Single(rig.Registry.GetSnapshot().Hidden).Reason, StringComparison.Ordinal);
        Assert.Empty(rig.Handler.Requests);
    }

    [Fact]
    public async Task Chat_A503ThenSuccess_IsRetriedByTheGateway()
    {
        var handler = GeminiProviderTests.Defaults();
        var calls = 0;
        handler.On(GeminiProviderTests.StreamRoute, _ => Interlocked.Increment(ref calls) == 1
            ? FakeCloudHandler.Respond(HttpStatusCode.ServiceUnavailable, FakeCloudHandler.Fixture("Gemini", "error-503.json"))
            : FakeCloudHandler.Respond(HttpStatusCode.OK, FakeCloudHandler.Fixture("Gemini", "chat-stream.sse"), "text/event-stream"));
        using var rig = Rig(handler);

        var chunks = await rig.ChatAsync("Say hello");

        Assert.Equal("Hello from Pillion", string.Concat(chunks.Select(c => c.Text)));
        Assert.Equal(2, handler.Requests.Count);
        Assert.Null(chunks[^1].Error);
    }

    [Fact]
    public async Task Chat_ARejectedKey_IsNotRetried_AndTheMessageDoesNotContainTheKey()
    {
        var handler = GeminiProviderTests.Defaults()
            .Fail(GeminiProviderTests.StreamRoute, HttpStatusCode.BadRequest, "Gemini", "error-api-key-invalid.json")
            .Fail("POST v1beta/models/gemini-3.5-flash-lite:streamGenerateContent", HttpStatusCode.BadRequest, "Gemini", "error-api-key-invalid.json");
        using var rig = Rig(handler);

        var ex = await Assert.ThrowsAsync<PillionRpcException>(() => rig.ChatAsync("Say hello"));

        Assert.Equal(PillionErrorCode.ProviderUnavailable, ex.Error.Code);
        Assert.DoesNotContain(GeminiProviderTests.Key, ex.Error.Message, StringComparison.Ordinal);

        // One try on the Balanced model and one on the Fast model; no retries.
        Assert.Equal(2, handler.Requests.Count);
    }

    [Fact]
    public async Task Chat_ContextLengthError_ReachesTheClientAsContextOverflow()
    {
        var handler = GeminiProviderTests.Defaults().Fail(GeminiProviderTests.StreamRoute, HttpStatusCode.BadRequest, "Gemini", "error-context-length.json");
        using var rig = Rig(handler);

        var ex = await Assert.ThrowsAsync<PillionRpcException>(() => rig.ChatAsync("Say hello"));

        Assert.Equal(PillionErrorCode.ContextOverflow, ex.Error.Code);
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task Chat_AnUnknownModel_ReachesTheClientAsModelNotInstalled()
    {
        var handler = new FakeCloudHandler().Fail("POST v1beta/models/gemini-imaginary-9:streamGenerateContent", HttpStatusCode.NotFound, "Gemini", "error-404-model.json");
        using var rig = Rig(handler);

        var ex = await Assert.ThrowsAsync<PillionRpcException>(() => rig.ChatAsync("Say hello", "gem/gemini-imaginary-9"));

        Assert.Equal(PillionErrorCode.ModelNotInstalled, ex.Error.Code);
    }

    [Fact]
    public async Task ListModels_ThroughTheGateway_NamesTheProviderOnEveryModel()
    {
        using var rig = Rig(GeminiProviderTests.Defaults());

        var models = await rig.Gateway.ListModelsAsync(CancellationToken.None);

        Assert.Contains(models, m => m.Id == "gem/gemini-3.8-flash" && m.ProviderId == "gem" && !m.IsLocal && m.IsAvailable);
        Assert.Contains(models, m => m.Id == "gem/gemini-embedding-001" && m.Capabilities == ModelCapabilities.Embeddings);
    }

    [Fact]
    public async Task TestProvider_ThroughTheGateway_ReportsTheProbe()
    {
        using var rig = Rig(GeminiProviderTests.Defaults());

        var result = await rig.Gateway.TestProviderAsync("gem", CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Contains("accepted", result.Message, StringComparison.Ordinal);
    }
}

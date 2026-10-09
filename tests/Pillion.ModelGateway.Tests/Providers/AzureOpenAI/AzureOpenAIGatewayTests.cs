using System.Net;
using Pillion.Common.Settings;
using Pillion.Contracts;
using Pillion.ModelGateway.Providers.AzureOpenAI;
using Pillion.ModelGateway.Tests.Providers.Cloud;

namespace Pillion.ModelGateway.Tests.Providers.AzureOpenAI;

/// <summary>The Azure OpenAI provider behind the real gateway, router, registry and redaction.</summary>
public sealed class AzureOpenAIGatewayTests
{
    private static CloudRig Rig(
        FakeCloudHandler handler,
        string? key = AzureOpenAIProviderTests.Key,
        ITokenCredentialProvider? credentials = null,
        Action<PillionSettings>? configure = null) =>
        new(
            handler,
            AzureOpenAIProviderTests.Settings("azure"),
            (secrets, h) => new AzureOpenAIProviderFactory(secrets, credentials, () => h),
            key,
            configure);

    [Fact]
    public async Task Chat_IsRoutedToTheDeployment_WhenTheCloudSwitchIsOnAndAKeyExists()
    {
        using var rig = Rig(AzureOpenAIProviderTests.Defaults());

        var chunks = await rig.ChatAsync("Say hello");

        Assert.Equal("Hello from Azure", string.Concat(chunks.Select(c => c.Text)));
        Assert.All(chunks, c => Assert.Equal("azure", c.ProviderId));
        Assert.All(chunks, c => Assert.Equal("prod-chat", c.ModelId));
        Assert.True(chunks[^1].IsFinal);
        Assert.Null(chunks[^1].Error);
        Assert.Equal(19, chunks[^1].PromptTokens);
        Assert.Equal(3, chunks[^1].CompletionTokens);
        var record = Assert.Single(rig.OutboundLog.GetRecent());
        Assert.False(record.IsLocal);
        Assert.Equal(OutboundOutcome.Succeeded, record.Outcome);
        Assert.Equal(AzureOpenAIProviderTests.Key, Assert.Single(rig.Handler.Requests).Header("api-key"));
    }

    [Fact]
    public async Task Chat_IsRoutedWithEntraId_WhenNoKeyIsStoredButTheHostSuppliesACredentialProvider()
    {
        using var rig = Rig(AzureOpenAIProviderTests.Defaults(), key: null, credentials: new StaticTokenCredentialProvider(AzureOpenAIProviderTests.Token));

        var chunks = await rig.ChatAsync("Say hello");

        Assert.Equal("Hello from Azure", string.Concat(chunks.Select(c => c.Text)));
        var request = Assert.Single(rig.Handler.Requests);
        Assert.Equal("Bearer " + AzureOpenAIProviderTests.Token, request.Header("authorization"));
        Assert.Null(request.Header("api-key"));
    }

    [Fact]
    public async Task Provider_WithNeitherAKeyNorACredentialProvider_IsHiddenFromRouting_AndNothingIsSent()
    {
        using var rig = Rig(AzureOpenAIProviderTests.Defaults(), key: null);

        await Assert.ThrowsAsync<PillionRpcException>(() => rig.ChatAsync("Say hello"));

        var snapshot = rig.Registry.GetSnapshot();
        Assert.Empty(snapshot.Providers);
        Assert.Contains("No API key", Assert.Single(snapshot.Hidden).Reason, StringComparison.Ordinal);
        Assert.Empty(rig.Handler.Requests);
        Assert.False((await rig.Gateway.TestProviderAsync("azure", CancellationToken.None)).IsSuccess);
    }

    [Fact]
    public async Task Provider_WithoutAnEndpoint_IsHiddenWithAReason()
    {
        using var rig = new CloudRig(
            AzureOpenAIProviderTests.Defaults(),
            AzureOpenAIProviderTests.Settings("azure", endpoint: string.Empty),
            (secrets, h) => new AzureOpenAIProviderFactory(secrets, null, () => h),
            AzureOpenAIProviderTests.Key);

        await Assert.ThrowsAsync<PillionRpcException>(() => rig.ChatAsync("Say hello"));

        Assert.Contains("needs an endpoint", Assert.Single(rig.Registry.GetSnapshot().Hidden).Reason, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Chat_WithTheCloudSwitchOff_IsRefusedByPolicy_AndNothingIsSent()
    {
        using var rig = Rig(AzureOpenAIProviderTests.Defaults(), configure: settings => settings.CloudSwitches.Chat = false);

        var ex = await Assert.ThrowsAsync<PillionRpcException>(() => rig.ChatAsync("Say hello"));

        Assert.Equal(PillionErrorCode.PolicyDenied, ex.Error.Code);
        Assert.Empty(rig.Handler.Requests);
    }

    [Fact]
    public async Task TheFeatureFlagOff_SkipsTheFactory()
    {
        using var rig = Rig(AzureOpenAIProviderTests.Defaults(), configure: settings => settings.Features.AzureOpenAIProvider = false);

        await Assert.ThrowsAsync<PillionRpcException>(() => rig.ChatAsync("Say hello"));

        Assert.Contains("azureOpenAIProvider", Assert.Single(rig.Registry.GetSnapshot().Hidden).Reason, StringComparison.Ordinal);
        Assert.Empty(rig.Handler.Requests);
    }

    [Fact]
    public async Task Chat_A429ThenSuccess_IsRetriedByTheGateway()
    {
        var handler = AzureOpenAIProviderTests.Defaults();
        var calls = 0;
        handler.On(AzureOpenAIProviderTests.ChatRoute, _ => Interlocked.Increment(ref calls) == 1
            ? FakeCloudHandler.Respond(HttpStatusCode.TooManyRequests, FakeCloudHandler.Fixture("AzureOpenAI", "error-429.json"))
            : FakeCloudHandler.Respond(HttpStatusCode.OK, FakeCloudHandler.Fixture("AzureOpenAI", "chat-stream.sse"), "text/event-stream"));
        using var rig = Rig(handler);

        var chunks = await rig.ChatAsync("Say hello");

        Assert.Equal("Hello from Azure", string.Concat(chunks.Select(c => c.Text)));
        Assert.Equal(2, handler.RequestsTo("openai/deployments/prod-chat/chat/completions").Count());
        Assert.Null(chunks[^1].Error);
    }

    [Fact]
    public async Task Chat_ARejectedKey_IsNotRetried_AndTheMessageDoesNotContainTheKey()
    {
        var handler = AzureOpenAIProviderTests.Defaults().Fail(AzureOpenAIProviderTests.ChatRoute, HttpStatusCode.Unauthorized, "AzureOpenAI", "error-401.json");
        using var rig = Rig(handler);

        var ex = await Assert.ThrowsAsync<PillionRpcException>(() => rig.ChatAsync("Say hello"));

        Assert.Equal(PillionErrorCode.ProviderUnavailable, ex.Error.Code);
        Assert.DoesNotContain(AzureOpenAIProviderTests.Key, ex.Error.Message, StringComparison.Ordinal);
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task Chat_ContextLengthError_ReachesTheClientAsContextOverflow()
    {
        var handler = AzureOpenAIProviderTests.Defaults().Fail(AzureOpenAIProviderTests.ChatRoute, HttpStatusCode.BadRequest, "AzureOpenAI", "error-context-length.json");
        using var rig = Rig(handler);

        var ex = await Assert.ThrowsAsync<PillionRpcException>(() => rig.ChatAsync("Say hello"));

        Assert.Equal(PillionErrorCode.ContextOverflow, ex.Error.Code);
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task Chat_AMissingDeployment_ReachesTheClientAsModelNotInstalled()
    {
        var handler = AzureOpenAIProviderTests.Defaults().Fail(AzureOpenAIProviderTests.ChatRoute, HttpStatusCode.NotFound, "AzureOpenAI", "error-deployment-not-found.json");
        using var rig = Rig(handler);

        var ex = await Assert.ThrowsAsync<PillionRpcException>(() => rig.ChatAsync("Say hello", "azure/prod-chat"));

        Assert.Equal(PillionErrorCode.ModelNotInstalled, ex.Error.Code);
    }

    [Fact]
    public async Task ListModels_ThroughTheGateway_ReturnsTheConfiguredDeployments()
    {
        using var rig = Rig(AzureOpenAIProviderTests.Defaults());

        var models = await rig.Gateway.ListModelsAsync(CancellationToken.None);

        var model = Assert.Single(models, m => m.ProviderId == "azure");
        Assert.Equal("azure/prod-chat", model.Id);
        Assert.True(model.IsAvailable);
        Assert.False(model.IsLocal);
    }

    [Fact]
    public async Task TestProvider_ThroughTheGateway_ReportsTheProbe()
    {
        using var rig = Rig(AzureOpenAIProviderTests.Defaults());

        var result = await rig.Gateway.TestProviderAsync("azure", CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Contains("accepted", result.Message, StringComparison.Ordinal);
    }
}

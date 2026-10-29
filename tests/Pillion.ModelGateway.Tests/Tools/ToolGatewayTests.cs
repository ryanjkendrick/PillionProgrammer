using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.AI;
using Pillion.Contracts;
using Pillion.ModelGateway.Providers.Tools;
using Pillion.ModelGateway.Tests.TestSupport;
using AiChatMessage = Microsoft.Extensions.AI.ChatMessage;
using AiChatRole = Microsoft.Extensions.AI.ChatRole;

namespace Pillion.ModelGateway.Tests.Tools;

/// <summary>
/// <see cref="IModelGateway.ChatWithToolsAsync"/> and <see cref="IModelGateway.ResolveToolRoute"/> through the real
/// router, registry, redaction and Outbound log, with scripted providers.
/// </summary>
public sealed class ToolGatewayTests
{
    private const string LocalModel = "local/qwen2.5-coder:14b";

    private static GatewayHarness Harness(ToolCallingSupport local = ToolCallingSupport.Native, ToolCallingSupport cloud = ToolCallingSupport.Native)
    {
        var harness = new GatewayHarness();
        harness.Local!.Models = [harness.Local.Model("qwen2.5-coder:1.5b", fim: true), harness.Local.Model("qwen2.5-coder:14b", toolCalling: local)];
        harness.Cloud!.Models = [harness.Cloud.Model("cloud-fast", fim: true), harness.Cloud.Model("cloud-balanced", vision: true), harness.Cloud.Model("cloud-frontier", vision: true, toolCalling: cloud)];
        harness.ListAll();
        return harness;
    }

    private static ToolChatRequest Request(IReadOnlyList<AiChatMessage>? messages = null, string? modelId = LocalModel, params AITool[] tools) => new()
    {
        RequestId = "t1",
        Messages = messages ?? TestTools.Question(),
        Tools = tools.Length == 0 ? [TestTools.GetWeather] : tools,
        TaskClass = TaskClass.Agent,
        ModelId = modelId,
    };

    private static IAsyncEnumerable<ChatResponseUpdate> WeatherCall(IReadOnlyList<AiChatMessage> messages, ChatOptions? options, CancellationToken ct) =>
        FakeProvider.Updates(
            ct,
            new ChatResponseUpdate(AiChatRole.Assistant, "Checking."),
            FakeProvider.CallUpdate("call_9", "get_weather", """{"city":"Paris","unit":"celsius"}"""),
            FakeProvider.FinalUpdate(ChatFinishReason.ToolCalls, 300, 20));

    [Fact]
    public async Task ATurn_YieldsText_ThenTheCompleteCall_ThenTheFinalUpdate()
    {
        using var harness = Harness();
        harness.Local!.ChatUpdates = WeatherCall;

        var updates = await harness.Gateway.ChatWithToolsAsync(Request(), CancellationToken.None).CollectAsync();

        Assert.Equal(3, updates.Count);
        Assert.All(updates, u => Assert.Equal("local", u.ProviderId));
        Assert.All(updates, u => Assert.Equal("qwen2.5-coder:14b", u.ModelId));
        Assert.All(updates, u => Assert.Equal(ToolTransport.Native, u.Transport));
        Assert.Equal("Checking.", updates[0].Text);
        var call = Assert.IsType<FunctionCallContent>(updates[1].ToolCall);
        Assert.Equal("call_9", call.CallId);
        Assert.Equal("get_weather", call.Name);
        Assert.Equal("Paris", TestTools.Argument(call, "city"));
        Assert.True(updates[2].IsFinal);
        Assert.Equal(ChatFinishReason.ToolCalls, updates[2].FinishReason);
        Assert.Equal(300, updates[2].PromptTokens);
        Assert.Equal(20, updates[2].CompletionTokens);
        Assert.Null(updates[2].Error);

        // The declarations went to the provider; the gateway invoked nothing.
        var options = Assert.Single(harness.Local.ReceivedOptions);
        var tool = Assert.Single(options!.Tools!);
        Assert.Equal("get_weather", tool.Name);
        Assert.Equal(Profile.LocalStrong, options.AdditionalProperties![GatewayOptionKeys.Profile]);
    }

    [Fact]
    public async Task TheOutboundRecord_NamesTheToolsOfferedAndCalled_AndTheirSizes_NeverTheirContents()
    {
        using var harness = Harness();
        harness.Local!.ChatUpdates = WeatherCall;
        var messages = TestTools.RoundTrip(result: "{\"temperature_c\":18}");

        await harness.Gateway.ChatWithToolsAsync(Request(messages), CancellationToken.None).CollectAsync();

        var record = Assert.Single(harness.OutboundLog.GetRecent());
        Assert.Equal(TaskClass.Agent, record.TaskClass);
        Assert.True(record.IsLocal);
        Assert.Equal(OutboundOutcome.Succeeded, record.Outcome);
        Assert.Equal(["get_weather"], record.ToolsOffered);
        Assert.Equal(["get_weather"], record.ToolCalls);
        Assert.Equal(Encoding.UTF8.GetByteCount("""{"city":"Paris","unit":"celsius"}"""), record.ToolArgumentBytes);
        Assert.Equal(Encoding.UTF8.GetByteCount("{\"temperature_c\":18}"), record.ToolResultBytes);
        Assert.Equal(20, record.CompletionTokens);
        Assert.True(record.EstimatedPromptTokens > 0); // The estimate, as for ChatAsync; the reported count goes to the final update.
    }

    [Fact]
    public async Task AModelWithoutNativeTools_GetsNoDeclarations_AndTheTurnSaysSo()
    {
        using var harness = Harness(local: ToolCallingSupport.None);
        harness.Local!.ChatUpdates = (_, _, ct) => FakeProvider.Updates(ct, new ChatResponseUpdate(AiChatRole.Assistant, "<tool_call>...</tool_call>"), FakeProvider.FinalUpdate(ChatFinishReason.Stop, 10, 5));

        var route = harness.Gateway.ResolveToolRoute(Request());
        var updates = await harness.Gateway.ChatWithToolsAsync(Request(), CancellationToken.None).CollectAsync();

        Assert.Equal(ToolCallingSupport.None, route.ToolCalling);
        Assert.Equal("local/qwen2.5-coder:14b", route.QualifiedId);
        Assert.All(updates, u => Assert.Equal(ToolTransport.None, u.Transport));
        Assert.Null(Assert.Single(harness.Local.ReceivedOptions)!.Tools);
        Assert.Empty(Assert.Single(harness.OutboundLog.GetRecent()).ToolsOffered);
    }

    [Fact]
    public async Task ResolveToolRoute_FollowsTheAgentCloudSwitch_AndLocalOnly()
    {
        using var harness = Harness();

        // Agent is off for the cloud by default: Auto routes locally, and an explicit cloud model is refused by policy.
        var local = harness.Gateway.ResolveToolRoute(Request(modelId: null));
        Assert.Equal("local", local.ProviderId);
        Assert.True(local.IsLocal);
        var denied = Assert.Throws<PillionRpcException>(() => harness.Gateway.ResolveToolRoute(Request(modelId: "cloud/cloud-frontier")));
        Assert.Equal(PillionErrorCode.PolicyDenied, denied.Error.Code);
        await Assert.ThrowsAsync<PillionRpcException>(() => harness.Gateway.ChatWithToolsAsync(Request(modelId: "cloud/cloud-frontier"), CancellationToken.None).CollectAsync());
        Assert.Equal(0, harness.Cloud!.ChatCalls);

        harness.Settings.CloudSwitches.Agent = true;
        harness.Reload();
        var cloud = harness.Gateway.ResolveToolRoute(Request(modelId: null));
        Assert.Equal("cloud/cloud-frontier", cloud.QualifiedId);
        Assert.Equal(ToolCallingSupport.Native, cloud.ToolCalling);
        Assert.Contains("Frontier", cloud.Reason, StringComparison.Ordinal);

        harness.Settings.Privacy.LocalOnly = true;
        harness.Reload();
        var localOnly = harness.Gateway.ResolveToolRoute(Request(modelId: null));
        Assert.True(localOnly.IsLocal);
    }

    [Fact]
    public async Task ToolArgumentsAndResults_BoundForACloudModel_AreRedacted_AndRestoredInTheCallThatComesBack()
    {
        using var harness = Harness();
        harness.Settings.CloudSwitches.Agent = true;
        harness.Reload();
        var secret = TestSecrets.AwsAccessKey();
        harness.Cloud!.ChatUpdates = (messages, _, ct) =>
        {
            // The model echoes the (redacted) result it was given into a tool argument.
            var received = messages.SelectMany(m => m.Contents).OfType<FunctionResultContent>().Single();
            var echoed = JsonSerializer.Serialize(new { content = ToolResults.Text(received.Result) });
            return FakeProvider.Updates(ct, FakeProvider.CallUpdate("call_2", "create_file", echoed), FakeProvider.FinalUpdate(ChatFinishReason.ToolCalls, 50, 10));
        };
        var messages = TestTools.RoundTrip(result: $"token={secret}", callArgumentsJson: JsonSerializer.Serialize(new { city = $"Paris {secret}" }));

        var updates = await harness.Gateway.ChatWithToolsAsync(Request(messages, "cloud/cloud-frontier"), CancellationToken.None).CollectAsync();

        var sent = Assert.Single(harness.Cloud.ReceivedChats);
        var sentCall = sent.SelectMany(m => m.Contents).OfType<FunctionCallContent>().Single();
        var sentResult = sent.SelectMany(m => m.Contents).OfType<FunctionResultContent>().Single();
        Assert.DoesNotContain(secret, ToolJson.ArgumentsJson(sentCall), StringComparison.Ordinal);
        Assert.Contains("<redacted:", ToolJson.ArgumentsJson(sentCall), StringComparison.Ordinal);
        Assert.DoesNotContain(secret, ToolResults.Text(sentResult.Result), StringComparison.Ordinal);
        Assert.Contains("<redacted:", ToolResults.Text(sentResult.Result), StringComparison.Ordinal);

        var call = updates.Single(u => u.ToolCall is not null).ToolCall!;
        Assert.Equal($"token={secret}", TestTools.Argument(call, "content"));
        var record = Assert.Single(harness.OutboundLog.GetRecent());
        Assert.False(record.IsLocal);
        Assert.True(record.RedactionCount >= 1);
    }

    [Fact]
    public async Task AToolResultOver16KB_IsCappedBeforeTransport_AndTheLogRecordsWhatWasSent()
    {
        using var harness = Harness();
        harness.Local!.ChatUpdates = (_, _, ct) => FakeProvider.Updates(ct, new ChatResponseUpdate(AiChatRole.Assistant, "Done."), FakeProvider.FinalUpdate(ChatFinishReason.Stop, 5000, 2));
        var messages = TestTools.RoundTrip(result: new string('a', 40_000));

        var updates = await harness.Gateway.ChatWithToolsAsync(Request(messages), CancellationToken.None).CollectAsync();

        var sent = Assert.Single(harness.Local.ReceivedChats).SelectMany(m => m.Contents).OfType<FunctionResultContent>().Single();
        var text = Assert.IsType<string>(sent.Result);
        Assert.True(Encoding.UTF8.GetByteCount(text) <= ToolResults.MaxBytes);
        Assert.Contains("[truncated by Pillion:", text, StringComparison.Ordinal);
        Assert.EndsWith("of 40000 bytes shown; ask for the rest with read_more]", text, StringComparison.Ordinal);
        var record = Assert.Single(harness.OutboundLog.GetRecent());
        Assert.Equal(Encoding.UTF8.GetByteCount(text), record.ToolResultBytes);
        Assert.Equal("Done.", updates[0].Text);

        // The caller's messages are not changed.
        Assert.Equal(40_000, ToolResults.Text(messages[^1].Contents.OfType<FunctionResultContent>().Single().Result).Length);
    }

    [Fact]
    public async Task AProviderThatRefusesTools_DowngradesTheModel_EndsTheTurn_AndLaterTurnsSendNone()
    {
        using var harness = Harness();
        var refused = 0;
        harness.Local!.ChatUpdates = (_, options, ct) =>
        {
            if (options?.Tools is { Count: > 0 })
            {
                Interlocked.Increment(ref refused);
                throw new ToolsNotSupportedException("The model 'qwen2.5-coder:14b' or its server does not accept tool definitions.");
            }

            return FakeProvider.Updates(ct, new ChatResponseUpdate(AiChatRole.Assistant, "text protocol"), FakeProvider.FinalUpdate(ChatFinishReason.Stop, 10, 3));
        };

        var first = await harness.Gateway.ChatWithToolsAsync(Request(), CancellationToken.None).CollectAsync();

        var only = Assert.Single(first);
        Assert.True(only.IsFinal);
        Assert.Equal(ToolTransport.Downgraded, only.Transport);
        Assert.NotNull(only.Error);
        Assert.True(only.ErrorIsRetryable);
        Assert.Contains("text protocol", only.Error!.RetryHint, StringComparison.Ordinal);
        Assert.Equal(1, refused);
        Assert.NotNull(harness.Inventory.ToolCallingDowngradeReason("local", "qwen2.5-coder:14b"));
        Assert.Equal(ToolCallingSupport.None, harness.Inventory.Find("local", "qwen2.5-coder:14b")!.Capabilities.ToolCalling);
        Assert.Equal(ToolCallingSupport.None, harness.Gateway.ResolveToolRoute(Request()).ToolCalling);
        var record = Assert.Single(harness.OutboundLog.GetRecent());
        Assert.Equal(OutboundOutcome.Failed, record.Outcome);
        Assert.Equal(["get_weather"], record.ToolsOffered);

        // The next turn goes without tools; the downgrade survives a fresh listing; the descriptor loses the Tools flag.
        var second = await harness.Gateway.ChatWithToolsAsync(Request(), CancellationToken.None).CollectAsync();
        Assert.Equal("text protocol", second[0].Text);
        Assert.All(second, u => Assert.Equal(ToolTransport.None, u.Transport));
        Assert.Equal(1, refused);
        var listed = await harness.Gateway.ListModelsAsync(CancellationToken.None);
        Assert.False(listed.Single(m => m.Id == LocalModel).Capabilities.HasFlag(ModelCapabilities.Tools));
        Assert.Equal(ToolCallingSupport.None, harness.Inventory.Find("local", "qwen2.5-coder:14b")!.Capabilities.ToolCalling);
        Assert.DoesNotContain(harness.Health.Events, e => e.StartsWith("failed:", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ATurnCutOffBeforeAnyTextOrCall_IsFinalWithLength_NotAnError()
    {
        // A reasoning model that spends its max_tokens thinking (Gemma 4 on LM Studio) answers with nothing but a finish reason.
        using var harness = Harness();
        harness.Local!.ChatUpdates = (_, _, ct) => FakeProvider.Updates(ct, FakeProvider.FinalUpdate(ChatFinishReason.Length, 350, 32));

        var updates = await harness.Gateway.ChatWithToolsAsync(Request() with { MaxOutputTokens = 32, Temperature = null }, CancellationToken.None).CollectAsync();

        var only = Assert.Single(updates);
        Assert.True(only.IsFinal);
        Assert.Null(only.Error);
        Assert.Equal(ChatFinishReason.Length, only.FinishReason);
        Assert.Equal(ToolTransport.Native, only.Transport);
        Assert.Equal(32, only.CompletionTokens);
        Assert.Equal(32, Assert.Single(harness.Local.ReceivedOptions)!.MaxOutputTokens);
        Assert.Null(Assert.Single(harness.Local.ReceivedOptions)!.Temperature);
        Assert.Equal(OutboundOutcome.Succeeded, Assert.Single(harness.OutboundLog.GetRecent()).Outcome);
    }

    [Fact]
    public async Task MalformedToolArguments_EndTheTurnWithARetryableError()
    {
        using var harness = Harness();
        harness.Local!.ChatUpdates = (_, _, ct) => TextThenMalformed(ct);

        var updates = await harness.Gateway.ChatWithToolsAsync(Request(), CancellationToken.None).CollectAsync();

        Assert.Equal("Sure.", updates[0].Text);
        var final = updates[^1];
        Assert.True(final.IsFinal);
        Assert.NotNull(final.Error);
        Assert.True(final.ErrorIsRetryable);
        Assert.Contains("not a valid JSON object", final.Error!.Message, StringComparison.Ordinal);
        Assert.Equal(OutboundOutcome.Failed, Assert.Single(harness.OutboundLog.GetRecent()).Outcome);
        Assert.DoesNotContain(harness.Health.Events, e => e.StartsWith("failed:", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ChatAsync_IsUnchanged_AndSendsNoTools()
    {
        using var harness = Harness();

        var chunks = await harness.Gateway.ChatAsync(GatewayHarness.ChatRequest("hi", LocalModel), TaskClass.Chat, CancellationToken.None).CollectAsync();

        Assert.Equal("Hello from fake", string.Concat(chunks.Select(c => c.Text)));
        Assert.Null(Assert.Single(harness.Local!.ReceivedOptions)!.Tools);
        var record = Assert.Single(harness.OutboundLog.GetRecent());
        Assert.Empty(record.ToolsOffered);
        Assert.Empty(record.ToolCalls);
        Assert.Equal(0, record.ToolResultBytes);
    }

    [Fact]
    public async Task ARequestWithoutMessages_IsRejected()
    {
        using var harness = Harness();

        await Assert.ThrowsAsync<ArgumentException>(() => harness.Gateway.ChatWithToolsAsync(new ToolChatRequest { RequestId = "x", Messages = [] }, CancellationToken.None).CollectAsync());
    }

    private static async IAsyncEnumerable<ChatResponseUpdate> TextThenMalformed([EnumeratorCancellation] CancellationToken ct)
    {
        await Task.Yield();
        yield return new ChatResponseUpdate(AiChatRole.Assistant, "Sure.");
        ct.ThrowIfCancellationRequested();
        throw ToolJson.Malformed("get_weather", "fake", null);
    }
}

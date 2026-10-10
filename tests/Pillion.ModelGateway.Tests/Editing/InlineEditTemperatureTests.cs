using Pillion.Contracts;
using Pillion.ModelGateway.Tests.TestSupport;

namespace Pillion.ModelGateway.Tests.Editing;

public sealed class InlineEditTemperatureTests
{
    [Fact]
    public async Task InlineEdits_UseTheLowInlineTemperature_AndChatKeepsTheProviderDefault()
    {
        using var harness = new GatewayHarness();
        harness.Settings.Privacy.LocalOnly = true;
        harness.Reload();

        await harness.Gateway.ChatAsync(GatewayHarness.ChatRequest("Edit"), TaskClass.InlineEdit, CancellationToken.None).CollectAsync();
        await harness.Gateway.ChatAsync(GatewayHarness.ChatRequest("Hi"), TaskClass.Chat, CancellationToken.None).CollectAsync();

        Assert.True(harness.Local!.ReceivedOptions.TryDequeue(out var inline));
        Assert.Equal(0.2f, inline!.Temperature);
        Assert.True(harness.Local.ReceivedOptions.TryDequeue(out var chat));
        Assert.Null(chat!.Temperature);
        Assert.Equal(TaskClass.InlineEdit, harness.OutboundLog.GetRecent()[0].TaskClass);
    }
}

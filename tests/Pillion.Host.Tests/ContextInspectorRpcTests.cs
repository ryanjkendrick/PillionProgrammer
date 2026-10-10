using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Pillion.Common;
using Pillion.Common.Secrets;
using Pillion.Common.Settings;
using Pillion.Contracts;
using Pillion.Host.Models;
using Pillion.Host.Tests.TestSupport;
using Pillion.ModelGateway;
using AiChatMessage = Microsoft.Extensions.AI.ChatMessage;
using AiChatRole = Microsoft.Extensions.AI.ChatRole;

namespace Pillion.Host.Tests;

/// <summary>
/// The Context Inspector's preview and the Outbound log read method (work item 1.13) through the real pipe, with a fake
/// provider behind the gateway that records every request it is sent.
/// </summary>
public sealed class ContextInspectorRpcTests : IDisposable
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(10);

    private readonly TempDirectory _data = new();
    private readonly RecordingProvider _provider = new();

    public void Dispose() => _data.Dispose();

    [Fact]
    public async Task PreviewContextAsync_ReportsEveryItem_InPromptOrder_WithoutCallingAModel()
    {
        await using var host = await StartAsync(contextWindow: 16384);
        await using var client = await host.ConnectAndHandshakeAsync();
        var diagnostics = client.Attach<IDiagnosticsService>();
        var request = Request(
            "preview-1",
            [Pin("Calc.cs", "public static class Calc { }"), Pin("errors", "error CS0029: Cannot convert (Program.cs:3)", kind: "errors")]);
        request.CurrentFile = @"C:\src\Program.cs";
        request.CurrentFileRegion = "var total = Calc.Add(1, 2);";

        var report = await diagnostics.PreviewContextAsync(request, CancellationToken.None).WaitAsync(Wait);

        Assert.Equal("fake-chat:1b", report.ModelId);
        Assert.Equal("fake", report.ProviderId);
        Assert.True(report.IsLocal);
        Assert.True(report.IsAssembled);
        Assert.False(report.Redacts);
        Assert.Equal(0, report.RedactionCount);
        Assert.Equal(16384, report.ContextWindow);
        Assert.Equal(4000, report.OutputReserve);
        Assert.Equal(["system", "pin", "pin", "currentFile", "query"], report.Items.Select(i => i.Kind));
        Assert.Equal(["Calc.cs", "errors"], report.Items.Where(i => i.Kind == "pin").Select(i => i.Name));
        Assert.Equal([-1, 0, 1, -1, -1], report.Items.Select(i => i.PinIndex));
        Assert.Equal("Program.cs", report.Items[3].Name);
        Assert.Equal("Your message", report.Items[4].Name);
        Assert.All(report.Items, i => Assert.False(i.Truncated || i.Dropped));
        Assert.All(report.Items, i => Assert.True(i.Tokens > 0, i.Name));
        Assert.Equal(report.Items.Sum(i => i.Tokens), report.TotalTokens);
        Assert.True(report.TotalTokens <= report.BudgetTokens + report.Items[0].Tokens);

        // Nothing was sent to the provider and nothing was recorded.
        Assert.Empty(_provider.Received);
        Assert.Empty(host.Services.GetRequiredService<IOutboundLog>().GetRecent());
    }

    [Fact]
    public async Task PreviewContextAsync_WithATinyBudget_ReportsATruncatedAndADroppedPin()
    {
        await using var host = await StartAsync(contextWindow: 1024, options: new ContextAssemblerOptions { PinCapFraction = 1.0, MinItemTokens = 300 });
        await using var client = await host.ConnectAndHandshakeAsync();
        var big = string.Join("\n", Enumerable.Range(0, 400).Select(i => $"// line {i} of a file far larger than the budget"));
        var request = Request("preview-tiny", [Pin("Big.cs", big), Pin("Second.cs", big)]);

        var report = await client.Attach<IDiagnosticsService>().PreviewContextAsync(request, CancellationToken.None).WaitAsync(Wait);

        Assert.Equal(1024, report.ContextWindow);
        Assert.Equal(256, report.OutputReserve);
        var pins = report.Items.Where(i => i.Kind == "pin").ToList();
        Assert.Equal(2, pins.Count);
        Assert.True(pins[0].Truncated);
        Assert.False(pins[0].Dropped);
        Assert.True(pins[0].Tokens < pins[0].OriginalTokens);
        Assert.False(string.IsNullOrEmpty(pins[0].Note));
        Assert.True(pins[1].Dropped);
        Assert.Equal(0, pins[1].Tokens);
        Assert.Equal(1, pins[1].PinIndex);
        Assert.Contains("no room", pins[1].Note, StringComparison.OrdinalIgnoreCase);
        Assert.True(report.TotalTokens <= report.ContextWindow - report.OutputReserve);
    }

    [Fact]
    public async Task PreviewContextAsync_CountsTheSecretsRedactionWouldReplace_WhenRedactionApplies()
    {
        await using var host = await StartAsync(contextWindow: 16384, redactForLocal: true);
        await using var client = await host.ConnectAndHandshakeAsync();
        var request = Request("preview-secret", [Pin("appsettings.json", "{ \"aws\": \"AKIAQ3MZ7KX2PL5RW4TB\" }"), Pin("Calc.cs", "class Calc { }")]);

        var report = await client.Attach<IDiagnosticsService>().PreviewContextAsync(request, CancellationToken.None).WaitAsync(Wait);

        Assert.True(report.Redacts);
        Assert.Equal(1, report.RedactionCount);
        Assert.Equal(1, report.Items.Single(i => i.Name == "appsettings.json").RedactionCount);
        Assert.Equal(0, report.Items.Single(i => i.Name == "Calc.cs").RedactionCount);
    }

    [Fact]
    public async Task PreviewContextAsync_ReportsTheOlderHistoryThatSendingWouldSummarise()
    {
        await using var host = await StartAsync(contextWindow: 2048);
        await using var client = await host.ConnectAndHandshakeAsync();
        var turn = string.Join(" ", Enumerable.Repeat("words that fill the window", 60));
        var messages = new List<Contracts.ChatMessage>();
        for (var i = 0; i < 6; i++)
        {
            messages.Add(new Contracts.ChatMessage { Role = i % 2 == 0 ? Contracts.ChatRole.User : Contracts.ChatRole.Assistant, Content = turn });
        }

        messages.Add(new Contracts.ChatMessage { Role = Contracts.ChatRole.User, Content = "And now?" });
        var request = Request("preview-history", [Pin("Calc.cs", "class Calc { }")]);
        request.Messages = messages;
        request.ConversationId = "conv-1";

        var report = await client.Attach<IDiagnosticsService>().PreviewContextAsync(request, CancellationToken.None).WaitAsync(Wait);

        var summary = Assert.Single(report.Items, i => i.Kind == "summary");
        Assert.Contains("at most", summary.Note, StringComparison.Ordinal);
        Assert.True(summary.Tokens > 0);
        Assert.Empty(_provider.Received);
    }

    [Fact]
    public async Task GetOutboundRecordsAsync_ReturnsTheRecordsNewestFirst_WithoutAnyText()
    {
        await using var host = await StartAsync(contextWindow: 16384);
        await using var client = await host.ConnectAndHandshakeAsync();
        var models = client.Attach<IModelService>();
        foreach (var id in new[] { "first", "second", "third" })
        {
            await foreach (var chunk in models.ChatAsync(Request(id, id == "second" ? [Pin("Calc.cs", "class Calc { }")] : []), CancellationToken.None))
            {
                _ = chunk;
            }
        }

        var diagnostics = client.Attach<IDiagnosticsService>();
        var all = await diagnostics.GetOutboundRecordsAsync(0, CancellationToken.None).WaitAsync(Wait);
        var two = await diagnostics.GetOutboundRecordsAsync(2, CancellationToken.None).WaitAsync(Wait);

        Assert.Equal(["third", "second", "first"], all.Select(r => r.RequestId));
        Assert.Equal(["third", "second"], two.Select(r => r.RequestId));
        var second = all[1];
        Assert.Equal(TaskClass.Chat, second.TaskClass);
        Assert.Equal("fake", second.ProviderId);
        Assert.Equal("fake-chat:1b", second.ModelId);
        Assert.True(second.IsLocal);
        Assert.Equal("Succeeded", second.Outcome);
        Assert.Null(second.ErrorCode);
        Assert.True(second.PromptTokens > 0);
        Assert.Equal(3, second.ContextItems); // System prompt, the pin, the message.
        Assert.Equal(0, all[0].ContextItems);
        Assert.Equal(DateTimeKind.Utc, second.TimestampUtc.Kind);
        Assert.True(all[0].TimestampUtc >= all[2].TimestampUtc);
    }

    [Fact]
    public async Task BothMethods_AreRefused_WhenTheOutboundLogViewFeatureIsOff()
    {
        var features = new FeatureSettings { OutboundLogView = false };
        await using var host = await StartAsync(contextWindow: 16384, features: features);
        await using var client = await host.ConnectAndHandshakeAsync();
        var diagnostics = client.Attach<IDiagnosticsService>();

        var records = await Assert.ThrowsAnyAsync<Exception>(() => diagnostics.GetOutboundRecordsAsync(10, CancellationToken.None).WaitAsync(Wait));
        var preview = await Assert.ThrowsAnyAsync<Exception>(() => diagnostics.PreviewContextAsync(Request("x", []), CancellationToken.None).WaitAsync(Wait));

        Assert.Contains("outboundLogView", records.Message, StringComparison.Ordinal);
        Assert.Contains("outboundLogView", preview.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ToDtos_CapsAtTheDefaultCount_AndKeepsTheErrorCode()
    {
        var at = new DateTimeOffset(2026, 10, 10, 8, 0, 0, TimeSpan.FromHours(1));
        var records = Enumerable.Range(0, 250)
            .Select(i => new OutboundRecord(at.AddSeconds(i), "r" + i, "p", "m", TaskClass.Autocomplete, false, 10, 2, 1, TimeSpan.FromMilliseconds(1500), OutboundOutcome.Failed, PillionErrorCode.ProviderUnavailable, CacheHit: true))
            .ToList();

        var dtos = DiagnosticsRpcService.ToDtos(records, 0);

        Assert.Equal(DiagnosticsRpcService.DefaultOutboundCount, dtos.Length);
        Assert.Equal("r249", dtos[0].RequestId);
        Assert.Equal(new DateTime(2026, 10, 10, 7, 4, 9, DateTimeKind.Utc), dtos[0].TimestampUtc);
        Assert.Equal(PillionErrorCode.ProviderUnavailable, dtos[0].ErrorCode);
        Assert.Equal("Failed", dtos[0].Outcome);
        Assert.Equal(1500, dtos[0].DurationMs);
        Assert.Equal(1, dtos[0].RedactionCount);
        Assert.True(dtos[0].CacheHit);
        Assert.False(dtos[0].IsLocal);
    }

    private static MentionResolution Pin(string name, string content, string kind = "file") => new()
    {
        Mention = "@" + name,
        IsResolved = true,
        Kind = kind,
        DisplayName = name,
        FilePath = kind == "file" ? @"C:\src\" + name : null,
        Content = content,
        TokenCount = content.Length / 4,
    };

    private static ChatRequest Request(string id, MentionResolution[] pins) => new()
    {
        RequestId = id,
        Profile = Profile.Auto,
        Pins = pins,
        Messages = [new Contracts.ChatMessage { Role = Contracts.ChatRole.User, Content = "Explain this." }],
    };

    private Task<InProcessHost> StartAsync(int contextWindow, ContextAssemblerOptions? options = null, bool redactForLocal = false, FeatureSettings? features = null) =>
        InProcessHost.StartAsync(services =>
        {
            var settings = new PillionSettings();
            settings.Providers.Add(new ProviderSettings { Id = "fake", Kind = ProviderKind.Mistral, Models = ["fake-chat:1b"] });
            settings.Privacy.RedactForLocalProviders = redactForLocal;
            _provider.ContextWindow = contextWindow;

            services.Replace(ServiceDescriptor.Singleton(new PillionPaths(_data.Path)));
            services.Replace(ServiceDescriptor.Singleton<ISecretStore>(new InMemorySecretStore()));
            services.Replace(ServiceDescriptor.Singleton<IGatewaySettingsSource>(new StaticGatewaySettingsSource(settings)));
            services.AddSingleton<IModelProviderFactory>(new FakeFactory(_provider));
            if (options is not null)
            {
                services.Replace(ServiceDescriptor.Singleton(options));
            }

            if (features is not null)
            {
                services.Replace(ServiceDescriptor.Singleton<IFeatureFlags>(new FixedFlags(features)));
            }

            // The inventory gives the preview the provider's window, as a model listing would.
            var inventory = new ModelInventory();
            inventory.Update("fake", _provider.ListModelsAsync(CancellationToken.None).Result);
            services.Replace(ServiceDescriptor.Singleton(inventory));
        });

    private sealed class FixedFlags(FeatureSettings features) : IFeatureFlags
    {
        public event EventHandler? Changed
        {
            add { }
            remove { }
        }

        public bool IsEnabled(string feature) => features.IsEnabled(feature);
    }

    private sealed class FakeFactory(RecordingProvider provider) : IModelProviderFactory
    {
        public ProviderKind Kind => ProviderKind.Mistral;

        public bool RequiresApiKey(ProviderSettings settings) => false;

        public IModelProvider Create(ProviderSettings settings, ProviderCreationContext context) => provider;
    }

    private sealed class RecordingProvider : IModelProvider
    {
        public ConcurrentQueue<List<AiChatMessage>> Received { get; } = new();

        public int ContextWindow { get; set; } = 16384;

        public string Id => "fake";

        public ProviderKind Kind => ProviderKind.Mistral;

        public string DisplayName => "Fake";

        public bool IsLocal => true;

        public Task<IReadOnlyList<ProviderModel>> ListModelsAsync(CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<ProviderModel>>(
                [new ProviderModel("fake", "fake-chat:1b", "Fake chat", new ModelCapabilityDescriptor { ContextWindow = ContextWindow, IsLocal = true }, "sha256:abc")]);

        public IChatClient GetChatClient(string modelId) => new Client(this);

        public IFimClient? GetFimClient(string modelId) => null;

        public Task<ProviderProbeResult> ProbeAsync(CancellationToken cancellationToken) =>
            Task.FromResult(new ProviderProbeResult(true, "1", "Fake is fine."));

        public void Dispose()
        {
        }

        private sealed class Client(RecordingProvider owner) : IChatClient
        {
            public Task<ChatResponse> GetResponseAsync(IEnumerable<AiChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) =>
                throw new NotSupportedException();

            public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
                IEnumerable<AiChatMessage> messages,
                ChatOptions? options = null,
                [EnumeratorCancellation] CancellationToken cancellationToken = default)
            {
                owner.Received.Enqueue([.. messages]);
                foreach (var piece in new[] { "se", "en" })
                {
                    await Task.Yield();
                    yield return new ChatResponseUpdate(AiChatRole.Assistant, piece);
                }
            }

            public object? GetService(Type serviceType, object? serviceKey = null) => null;

            public void Dispose()
            {
            }
        }
    }
}

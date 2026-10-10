using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Pillion.Common;
using Pillion.Common.Secrets;
using Pillion.Common.Settings;
using Pillion.Contracts;
using Pillion.Host.Models;
using Pillion.Host.Tests.TestSupport;
using Pillion.ModelGateway;

namespace Pillion.Host.Tests;

/// <summary>The diagnostics service through the real pipe server, with fake Outbound records and a fixed clock.</summary>
public sealed class DiagnosticsRpcTests : IDisposable
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(10);
    private static readonly DateTimeOffset Now = new(2026, 10, 9, 10, 0, 0, TimeSpan.Zero);

    private readonly TempDirectory _data = new();

    public void Dispose() => _data.Dispose();

    [Fact]
    public async Task GetAutocompleteCountersAsync_ReportsTheCountersAndTheLastFiveMinutesOfOutboundRecords()
    {
        var counters = new AutocompleteCounters();
        for (var i = 0; i < 7; i++)
        {
            counters.Request();
        }

        for (var i = 0; i < 5; i++)
        {
            counters.ModelRequest();
        }

        counters.Failed();
        counters.Cancelled();
        counters.Cancelled();
        var outbound = new FakeOutbound(Records());
        await using var host = await StartAsync(counters, outbound);
        await using var client = await host.ConnectAndHandshakeAsync();

        var report = await client.Attach<IDiagnosticsService>().GetAutocompleteCountersAsync(CancellationToken.None).WaitAsync(Wait);

        Assert.Equal(7, report.Requests);
        Assert.Equal(5, report.ModelRequests);
        Assert.Equal(1, report.Failed);
        Assert.Equal(2, report.Cancelled);
        Assert.Equal(0, report.CacheHits);
        Assert.Equal(0, report.HitRate);
        Assert.Equal(300, report.WindowSeconds);

        // Seven autocomplete records within five minutes: four answered, one failed, two served from the cache.
        // The old one and the chat one are left out.
        Assert.Equal(7, report.WindowRequests);
        Assert.Equal(2, report.WindowCacheHits);
        Assert.Equal(400, report.FirstTokenP50Ms);
        Assert.Equal(800, report.FirstTokenP95Ms);
        Assert.Equal(500, report.TotalP50Ms);
        Assert.Equal(900, report.TotalP95Ms);
        Assert.Equal(2, report.PromptCountedRequests);
        Assert.Equal(1700, report.PromptTokensCounted);
        Assert.Equal(3, report.PromptEstimatedRequests);
        Assert.Equal(1900, report.PromptTokensEstimated);
        Assert.Equal(1000, report.PromptTokensMax);
    }

    [Fact]
    public async Task GetAutocompleteCountersAsync_WithNothingRecorded_ReportsZerosAndNoPercentiles()
    {
        await using var host = await StartAsync(new AutocompleteCounters(), new FakeOutbound([]));
        await using var client = await host.ConnectAndHandshakeAsync();

        var report = await client.Attach<IDiagnosticsService>().GetAutocompleteCountersAsync(CancellationToken.None).WaitAsync(Wait);

        Assert.Equal(0, report.Requests);
        Assert.Equal(0, report.WindowRequests);
        Assert.Null(report.FirstTokenP50Ms);
        Assert.Null(report.FirstTokenP95Ms);
        Assert.Null(report.TotalP50Ms);
        Assert.Null(report.TotalP95Ms);
        Assert.Equal(0, report.PromptTokensMax);
    }

    [Fact]
    public void Build_UsesARecordExactlyFiveMinutesOld_ButNotOlder()
    {
        var edge = Record("edge", Now - TimeSpan.FromMinutes(5), 100, 100, 700);
        var older = Record("older", Now - TimeSpan.FromMinutes(5) - TimeSpan.FromMilliseconds(1), 100, 100, 700);

        var report = DiagnosticsRpcService.Build(new AutocompleteCounters().Snapshot(), [older, edge], Now);

        Assert.Equal(1, report.WindowRequests);
    }

    private static List<OutboundRecord> Records() =>
    [
        // Answered by the model: first token 200 to 800 ms, total 300 to 900 ms.
        Record("a1", Now - TimeSpan.FromSeconds(10), firstToken: 200, total: 300, prompt: 700, counted: true),
        Record("a2", Now - TimeSpan.FromMinutes(1), firstToken: 400, total: 500, prompt: 1000, counted: true),
        Record("a3", Now - TimeSpan.FromMinutes(2), firstToken: 600, total: 700, prompt: 600),
        Record("a4", Now - TimeSpan.FromMinutes(4), firstToken: 800, total: 900, prompt: 800),

        // A failed request: counts in the window and in the prompt figures, but not in the latencies.
        Record("f1", Now - TimeSpan.FromMinutes(3), firstToken: null, total: 50, prompt: 500, outcome: OutboundOutcome.Failed),

        // Two served from the cache: nothing was sent.
        Record("h1", Now - TimeSpan.FromSeconds(30), firstToken: null, total: 1, prompt: 0, cacheHit: true),
        Record("h2", Now - TimeSpan.FromSeconds(20), firstToken: null, total: 1, prompt: 0, cacheHit: true),

        // Outside the window, and another task class.
        Record("old", Now - TimeSpan.FromMinutes(10), firstToken: 9000, total: 9500, prompt: 2000),
        Record("chat", Now - TimeSpan.FromSeconds(5), firstToken: 1, total: 1, prompt: 5000, taskClass: TaskClass.Chat),
    ];

    private static OutboundRecord Record(
        string id,
        DateTimeOffset at,
        int? firstToken,
        int total,
        int prompt,
        bool counted = false,
        bool cacheHit = false,
        OutboundOutcome outcome = OutboundOutcome.Succeeded,
        TaskClass taskClass = TaskClass.Autocomplete) => new(
            at,
            id,
            "fake",
            "qwen2.5-coder:1.5b",
            taskClass,
            IsLocal: true,
            prompt,
            CompletionTokens: 10,
            RedactionCount: 0,
            TimeSpan.FromMilliseconds(total),
            outcome,
            ErrorCode: null,
            counted,
            cacheHit,
            firstToken is { } ms ? TimeSpan.FromMilliseconds(ms) : null);

    private Task<InProcessHost> StartAsync(AutocompleteCounters counters, IOutboundLog outbound) => InProcessHost.StartAsync(services =>
    {
        services.Replace(ServiceDescriptor.Singleton(new PillionPaths(_data.Path)));
        services.Replace(ServiceDescriptor.Singleton<ISecretStore>(new InMemorySecretStore()));
        services.Replace(ServiceDescriptor.Singleton<IGatewaySettingsSource>(new StaticGatewaySettingsSource(new PillionSettings())));
        services.Replace(ServiceDescriptor.Singleton(counters));
        services.Replace(ServiceDescriptor.Singleton(outbound));
        services.Replace(ServiceDescriptor.Singleton(new DiagnosticsRpcService(counters, outbound, new FixedTime(Now))));
    });

    private sealed class FixedTime(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private sealed class FakeOutbound(List<OutboundRecord> records) : IOutboundLog
    {
        public void Record(OutboundRecord record) => records.Add(record);

        public IReadOnlyList<OutboundRecord> GetRecent() => [.. records];
    }
}

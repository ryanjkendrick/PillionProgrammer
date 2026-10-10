using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Pillion.Common;
using Pillion.Common.Hardware;
using Pillion.Common.Secrets;
using Pillion.Common.Settings;
using Pillion.Contracts;
using Pillion.Host.Models;
using Pillion.Host.Tests.TestSupport;
using Pillion.ModelGateway;
using StreamJsonRpc;

namespace Pillion.Host.Tests;

/// <summary>The autocomplete service through the real <see cref="ModelRpcService"/> over the in-process pipe, with a fake FIM provider.</summary>
public sealed class AutocompleteRpcTests : IDisposable
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(10);

    private readonly TempDirectory _data = new();
    private readonly FakeFimProvider _provider = new();
    private readonly FakeFlags _flags = new();

    public void Dispose() => _data.Dispose();

    [Fact]
    public async Task CompleteAsync_StreamsABudgetedSingleLineCompletion_AndRecordsIt()
    {
        await using var host = await StartAsync(GpuTier.CpuOnly);
        await using var client = await host.ConnectAndHandshakeAsync();
        var models = client.Attach<IModelService>();

        var chunks = await CollectAsync(models.CompleteAsync(Request("c1", LongPrefix() + "    return a ", ";\n}\n"), CancellationToken.None));

        Assert.Equal("+ b;", string.Concat(chunks.Select(c => c.Text)));
        Assert.All(chunks, c => Assert.Equal("c1", c.RequestId));
        Assert.All(chunks, c => Assert.Equal("qwen2.5-coder:1.5b", c.ModelId));
        var final = chunks[^1];
        Assert.True(final.IsFinal);
        Assert.InRange(final.PromptTokens, 700, AutocompleteBudget.CpuPromptTokens);

        var fim = Assert.Single(_provider.Received);
        Assert.StartsWith("// File: Calc.cs\n", fim.Prefix, StringComparison.Ordinal);
        Assert.EndsWith("    return a ", fim.Prefix, StringComparison.Ordinal);
        Assert.Equal("\n", fim.StopSequences[0]);

        var record = Assert.Single(host.Services.GetRequiredService<IOutboundLog>().GetRecent());
        Assert.Equal(TaskClass.Autocomplete, record.TaskClass);
        Assert.Equal(final.PromptTokens, record.EstimatedPromptTokens);
        Assert.True(record.IsLocal);
        Assert.False(record.CacheHit);
        Assert.NotNull(record.TimeToFirstToken);
        Assert.Equal(0, record.RedactionCount);
    }

    [Fact]
    public async Task CompleteAsync_SecondKeystrokeIntoTheSuggestion_IsServedFromTheCache()
    {
        await using var host = await StartAsync(GpuTier.Gpu8GB);
        await using var client = await host.ConnectAndHandshakeAsync();
        var models = client.Attach<IModelService>();

        await CollectAsync(models.CompleteAsync(Request("c1", "return a ", ";"), CancellationToken.None));
        var second = await CollectAsync(models.CompleteAsync(Request("c2", "return a +", ";"), CancellationToken.None));

        Assert.Equal(" b;", Assert.Single(second).Text);
        Assert.Single(_provider.Received);
        var counters = host.Services.GetRequiredService<AutocompleteCounters>().Snapshot();
        Assert.Equal(2, counters.Requests);
        Assert.Equal(1, counters.TypeThroughHits);
        Assert.Equal(0.5, counters.HitRate);
    }

    [Fact]
    public async Task Diagnostics_ReportsTheGatewayCountersAndTheOutboundWindow_AfterRealRequests()
    {
        await using var host = await StartAsync(GpuTier.Gpu8GB);
        await using var client = await host.ConnectAndHandshakeAsync();
        var models = client.Attach<IModelService>();
        await CollectAsync(models.CompleteAsync(Request("c1", "return a ", ";"), CancellationToken.None));
        await CollectAsync(models.CompleteAsync(Request("c2", "return a +", ";"), CancellationToken.None));

        var counters = await client.Attach<IDiagnosticsService>().GetAutocompleteCountersAsync(CancellationToken.None).WaitAsync(Wait);

        Assert.Equal(2, counters.Requests);
        Assert.Equal(1, counters.TypeThroughHits);
        Assert.Equal(1, counters.CacheMisses);
        Assert.Equal(0.5, counters.HitRate);
        Assert.Equal(1, counters.ModelRequests);
        Assert.Equal(300, counters.WindowSeconds);
        Assert.Equal(2, counters.WindowRequests);
        Assert.Equal(1, counters.WindowCacheHits);
        Assert.NotNull(counters.FirstTokenP50Ms);
        Assert.NotNull(counters.TotalP95Ms);
        Assert.Equal(1, counters.PromptCountedRequests + counters.PromptEstimatedRequests);
        Assert.True(counters.PromptTokensMax > 0);
    }

    [Fact]
    public async Task CompleteAsync_WithAutocompleteOff_FailsWithPolicyDenied()
    {
        _flags.Disabled.Add(FeatureNames.Autocomplete);
        await using var host = await StartAsync(GpuTier.Gpu8GB);
        await using var client = await host.ConnectAndHandshakeAsync();
        var models = client.Attach<IModelService>();

        var ex = await Assert.ThrowsAsync<RemoteInvocationException>(() => CollectAsync(models.CompleteAsync(Request("c1", "x", string.Empty), CancellationToken.None)));

        var error = Assert.IsType<PillionError>(ex.DeserializedErrorData);
        Assert.Equal(PillionErrorCode.PolicyDenied, error.Code);
        Assert.Equal(ModelRpcService.AutocompleteDisabledMessage, error.Message);
        Assert.Empty(_provider.Received);

        // Chat is not affected by the autocomplete flag.
        Assert.NotEmpty(await models.ListModelsAsync(CancellationToken.None).WaitAsync(Wait));

        _flags.Disabled.Clear();
        Assert.NotEmpty(await CollectAsync(models.CompleteAsync(Request("c2", "return a ", ";"), CancellationToken.None)));
    }

    private static async Task<List<CompletionChunk>> CollectAsync(IAsyncEnumerable<CompletionChunk> stream)
    {
        var list = new List<CompletionChunk>();
        using var cts = new CancellationTokenSource(Wait);
        await foreach (var chunk in stream.WithCancellation(cts.Token))
        {
            list.Add(chunk);
        }

        return list;
    }

    private static string LongPrefix()
    {
        var builder = new System.Text.StringBuilder("public static class Calc\n{\n");
        for (var i = 0; i < 400; i++)
        {
            builder.Append("    public static int F").Append(i).Append("(int a, int b) => a * ").Append(i).Append(" + b;\n");
        }

        return builder.Append("    public static int Add(int a, int b)\n    {\n").ToString();
    }

    private static CompletionRequest Request(string id, string prefix, string suffix) => new()
    {
        RequestId = id,
        Prefix = prefix,
        Suffix = suffix,
        LanguageId = "csharp",
        FilePath = @"C:\src\Calc.cs",
        CaretLine = prefix.Count(c => c == '\n'),
        TaskClass = TaskClass.Autocomplete,
    };

    private Task<InProcessHost> StartAsync(GpuTier tier) => InProcessHost.StartAsync(services =>
    {
        var settings = new PillionSettings();
        settings.Providers.Add(new ProviderSettings { Id = "fake", Kind = ProviderKind.Mistral, Models = ["qwen2.5-coder:1.5b"] });
        settings.Routing.Profiles[Profile.LocalFast] = "fake/qwen2.5-coder:1.5b";

        services.Replace(ServiceDescriptor.Singleton(new PillionPaths(_data.Path)));
        services.Replace(ServiceDescriptor.Singleton<ISecretStore>(new InMemorySecretStore()));
        services.Replace(ServiceDescriptor.Singleton<IGatewaySettingsSource>(new StaticGatewaySettingsSource(settings)));
        services.Replace(ServiceDescriptor.Singleton<IFeatureFlags>(_flags));
        services.Replace(ServiceDescriptor.Singleton<IGpuTierDetector>(new FixedTier(tier)));
        services.AddSingleton<IModelProviderFactory>(new FakeFactory(_provider));
    });

    private sealed class FixedTier(GpuTier tier) : IGpuTierDetector
    {
        public GpuInfo Detect() => new(tier, null, "fake", "test");
    }

    private sealed class FakeFlags : IFeatureFlags
    {
        public event EventHandler? Changed;

        public ConcurrentBag<string> Disabled { get; } = [];

        public bool IsEnabled(string feature) => !Disabled.Contains(feature);

        public void Raise() => Changed?.Invoke(this, EventArgs.Empty);
    }

    private sealed class FakeFactory(FakeFimProvider provider) : IModelProviderFactory
    {
        public ProviderKind Kind => ProviderKind.Mistral;

        public bool RequiresApiKey(ProviderSettings settings) => false;

        public IModelProvider Create(ProviderSettings settings, ProviderCreationContext context) => provider;
    }

    private sealed class FakeFimProvider : IModelProvider
    {
        public ConcurrentQueue<FimRequest> Received { get; } = new();

        public string Id => "fake";

        public ProviderKind Kind => ProviderKind.Mistral;

        public string DisplayName => "Fake";

        public bool IsLocal => true;

        public Task<IReadOnlyList<ProviderModel>> ListModelsAsync(CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<ProviderModel>>(
                [new ProviderModel("fake", "qwen2.5-coder:1.5b", "Fake coder", new ModelCapabilityDescriptor { ContextWindow = 32768, Fim = true, IsLocal = true }, "sha256:abc")]);

        public IChatClient GetChatClient(string modelId) => throw new NotSupportedException();

        public IFimClient? GetFimClient(string modelId) => new Client(this);

        public Task<ProviderProbeResult> ProbeAsync(CancellationToken cancellationToken) =>
            Task.FromResult(new ProviderProbeResult(true, "1", "Fake is fine."));

        public void Dispose()
        {
        }

        private sealed class Client(FakeFimProvider owner) : IFimClient
        {
            public async IAsyncEnumerable<string> CompleteAsync(FimRequest request, [EnumeratorCancellation] CancellationToken cancellationToken)
            {
                owner.Received.Enqueue(request);
                foreach (var piece in new[] { "+ ", "b;", "\n", "}" })
                {
                    await Task.Yield();
                    cancellationToken.ThrowIfCancellationRequested();
                    yield return piece;
                }
            }
        }
    }
}

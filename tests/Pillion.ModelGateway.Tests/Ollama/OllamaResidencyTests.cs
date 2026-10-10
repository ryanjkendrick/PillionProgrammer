using System.Globalization;
using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using Pillion.Common.Hardware;
using Pillion.Common.Settings;
using Pillion.Contracts;
using Pillion.ModelGateway.Providers.Ollama;
using Pillion.ModelGateway.Tests.TestSupport;
using AiChatMessage = Microsoft.Extensions.AI.ChatMessage;
using AiChatRole = Microsoft.Extensions.AI.ChatRole;

namespace Pillion.ModelGateway.Tests.Ollama;

/// <summary>
/// A stateful stand-in for an Ollama server that keeps one runner per model and context size and, like the real one,
/// cannot start a second runner of a model beside a resident one when the GPU is full: that request waits forever.
/// </summary>
internal sealed class FakeOllamaServer
{
    private const double MiB = 1024 * 1024;
    private readonly object _lock = new();
    private readonly ManualClock _clock;

    public FakeOllamaServer(ManualClock clock)
    {
        _clock = clock;
        Handler.On("GET api/ps", (_, _) => FakeOllamaHandler.Respond(HttpStatusCode.OK, PsJson()));
        Handler.OnAsync("POST api/generate", HandleGenerateAsync);
        Handler.OnAsync("POST api/chat", HandleChatAsync);
    }

    public FakeOllamaHandler Handler { get; } = FakeOllamaHandler.WithDefaults();

    /// <summary>Gets or sets a value indicating whether Ollama starts a second runner of a model with another context size.</summary>
    public bool SecondInstanceFits { get; set; }

    /// <summary>Gets or sets a value indicating whether an unload request is accepted but the runner stays (a request keeps it busy).</summary>
    public bool UnloadNeverCompletes { get; set; }

    public List<Runner> Runners { get; } = [];

    /// <summary>Puts a runner on the server as if another client had loaded it.</summary>
    public void Load(string model, int context, string keepAlive)
    {
        lock (_lock)
        {
            Runners.Add(new Runner(model, context, keepAlive, ExpiryOf(keepAlive)));
        }
    }

    /// <summary>Describes the requests the provider sent, without the <c>/api/ps</c> polls.</summary>
    public List<string> Transcript() => Handler.Requests
        .Where(r => r.Path != "api/ps" && r.Path != "api/version")
        .Select(r =>
        {
            var body = r.Body!;
            var context = body["options"]?["num_ctx"]?.GetValue<int>();
            var keepAlive = KeepAliveOf(body);
            if (r.Path == "api/chat")
            {
                return $"chat@{context}/{keepAlive}";
            }

            var prompt = body["prompt"]?.GetValue<string>() ?? string.Empty;
            return prompt.Length > 0 ? $"generate@{context}/{keepAlive}"
                : keepAlive == "0" ? "unload"
                : $"load@{context}/{keepAlive}";
        })
        .ToList();

    public int PsRequests => Handler.Requests.Count(r => r.Path == "api/ps");

    private static string KeepAliveOf(JsonObject body) => body["keep_alive"] switch
    {
        JsonValue value when value.TryGetValue<int>(out var seconds) => seconds.ToString(CultureInfo.InvariantCulture),
        JsonValue value => value.GetValue<string>(),
        _ => "(none)",
    };

    private static long BytesAt(string model, int context) =>
        (long)((model.Contains("1.5b", StringComparison.Ordinal) ? 1050 + (28.0 * context / 1024)
            : model.Contains("14b", StringComparison.Ordinal) ? 8800 + (192.0 * context / 1024)
            : model.StartsWith("gemma", StringComparison.Ordinal) ? 1600 + (28.0 * context / 1024)
            : 4300 + (56.0 * context / 1024)) * MiB);

    private DateTimeOffset? ExpiryOf(string keepAlive) => keepAlive switch
    {
        "-1" => null,
        "0" => _clock.GetUtcNow(),
        _ when keepAlive.EndsWith('m') => _clock.GetUtcNow().AddMinutes(double.Parse(keepAlive[..^1], CultureInfo.InvariantCulture)),
        _ => _clock.GetUtcNow().AddMinutes(5),
    };

    private string PsJson()
    {
        lock (_lock)
        {
            var models = new JsonArray();
            foreach (var runner in Runners)
            {
                var bytes = BytesAt(runner.Model, runner.Context);
                models.Add(new JsonObject
                {
                    ["name"] = runner.Model,
                    ["model"] = runner.Model,
                    ["size"] = bytes,
                    ["digest"] = "abc",
                    ["expires_at"] = runner.ExpiresAt is { } at ? at.ToString("O", CultureInfo.InvariantCulture) : "2319-01-19T05:51:01.647710507Z",
                    ["size_vram"] = bytes,
                    ["context_length"] = runner.Context,
                });
            }

            return new JsonObject { ["models"] = models }.ToJsonString();
        }
    }

    private async Task<HttpResponseMessage> HandleGenerateAsync(HttpRequestMessage request, JsonObject? body, CancellationToken cancellationToken)
    {
        var model = body!["model"]!.GetValue<string>();
        var prompt = body["prompt"]?.GetValue<string>() ?? string.Empty;
        var keepAlive = KeepAliveOf(body);
        if (prompt.Length == 0 && keepAlive == "0")
        {
            if (!UnloadNeverCompletes)
            {
                lock (_lock)
                {
                    Runners.RemoveAll(r => r.Model == model);
                }
            }

            return FakeOllamaHandler.Respond(HttpStatusCode.OK, """{"model":"m","response":"","done":true,"done_reason":"unload"}""");
        }

        await AdmitAsync(model, body["options"]?["num_ctx"]?.GetValue<int>() ?? 4096, keepAlive, cancellationToken);
        return prompt.Length == 0
            ? FakeOllamaHandler.Respond(HttpStatusCode.OK, """{"model":"m","response":"","done":true,"done_reason":"load"}""")
            : FakeOllamaHandler.Respond(HttpStatusCode.OK, FakeOllamaHandler.Fixture("generate-qwen-coder.ndjson"), "application/x-ndjson");
    }

    private async Task<HttpResponseMessage> HandleChatAsync(HttpRequestMessage request, JsonObject? body, CancellationToken cancellationToken)
    {
        await AdmitAsync(body!["model"]!.GetValue<string>(), body["options"]?["num_ctx"]?.GetValue<int>() ?? 4096, KeepAliveOf(body), cancellationToken);
        return FakeOllamaHandler.Respond(HttpStatusCode.OK, FakeOllamaHandler.Fixture("chat-gemma4.ndjson"), "application/x-ndjson");
    }

    private async Task AdmitAsync(string model, int context, string keepAlive, CancellationToken cancellationToken)
    {
        var blocked = false;
        lock (_lock)
        {
            var runner = Runners.FindIndex(r => r.Model == model && r.Context == context);
            if (runner >= 0)
            {
                Runners[runner] = Runners[runner] with { KeepAlive = keepAlive, ExpiresAt = ExpiryOf(keepAlive) };
            }
            else if (Runners.Any(r => r.Model == model) && !SecondInstanceFits)
            {
                blocked = true;
            }
            else
            {
                Runners.Add(new Runner(model, context, keepAlive, ExpiryOf(keepAlive)));
            }
        }

        if (blocked)
        {
            // What the real server does: the request waits for memory that never comes.
            await Task.Delay(Timeout.Infinite, cancellationToken);
        }
    }

    internal sealed record Runner(string Model, int Context, string KeepAlive, DateTimeOffset? ExpiresAt);
}

public class OllamaResidencyTests
{
    private const string Coder = "qwen2.5-coder:7b";

    private static ChatOptions Chat(Profile profile) => new()
    {
        AdditionalProperties = new AdditionalPropertiesDictionary { [GatewayOptionKeys.Profile] = profile, [GatewayOptionKeys.RequestId] = "req-1" },
    };

    private static Task<List<ChatResponseUpdate>> ChatAsync(OllamaProvider provider, string text, Profile profile = Profile.LocalStrong, string model = Coder) =>
        provider.GetChatClient(model).GetStreamingResponseAsync([new AiChatMessage(AiChatRole.User, text)], Chat(profile)).CollectAsync();

    private static Task<List<string>> CompleteAsync(OllamaProvider provider, string model = Coder) =>
        provider.GetFimClient(model)!.CompleteAsync(new FimRequest { Prefix = "int Add(int a, int b) => ", Suffix = ";", MaxTokens = 32, Profile = Profile.LocalFast }, CancellationToken.None).CollectAsync();

    /// <summary>A prompt of about 8,000 tokens by the gateway's estimate, so that with the output reserve it needs a 8,192-token context.</summary>
    private static string LongPrompt() => new('x', 30_000);

    /// <summary>A provider over a stateful fake server, with the residency services a registry would give it.</summary>
    private sealed class Rig : IDisposable
    {
        public Rig(GpuTier tier, string? completionModel = Coder, Func<string, int?>? overrides = null, string? extensionJson = null, ModelCatalog? catalog = null)
        {
            Server = new FakeOllamaServer(Clock);
            var settings = new ProviderSettings { Id = "ollama", Kind = ProviderKind.Ollama };
            if (extensionJson is not null)
            {
                settings.ExtensionData = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(extensionJson);
            }

            var runtime = new ProviderRuntime(Progress, () => tier, overrides ?? (_ => null), Clock, Map);
            Provider = (OllamaProvider)new OllamaProviderFactory(() => Server.Handler).Create(
                settings,
                new ProviderCreationContext(null, catalog ?? ModelCatalog.Default, NullLoggerFactory.Instance, completionModel, runtime));
        }

        public ManualClock Clock { get; } = new();

        public RecordingProgressSink Progress { get; } = new();

        public LocalResidencyMap Map { get; } = new();

        public FakeOllamaServer Server { get; }

        public OllamaProvider Provider { get; }

        public void Dispose() => Provider.Dispose();
    }

    [Fact]
    public async Task ResidentContext_IsReused_WhenTheRequestNeedsLess_WhateverItsProfileDefaultIs()
    {
        using var rig = new Rig(GpuTier.Gpu8GB);
        rig.Server.Load(Coder, 4096, "-1");

        var updates = await ChatAsync(rig.Provider, "Reply with exactly: hello there", Profile.LocalStrong);
        await CompleteAsync(rig.Provider);

        Assert.Equal("hello there", string.Concat(updates.Select(u => u.Text)));
        Assert.Equal(["chat@4096/-1", "generate@4096/-1"], rig.Server.Transcript());
        var runner = Assert.Single(rig.Server.Runners);
        Assert.Equal(4096, runner.Context);
    }

    [Fact]
    public async Task LargerNeed_On8Gb_ReleasesTheResidentInstance_ReloadsOnce_AndPinsItAgain()
    {
        using var rig = new Rig(GpuTier.Gpu8GB);
        rig.Server.Load(Coder, 4096, "-1");

        var updates = await ChatAsync(rig.Provider, LongPrompt(), Profile.LocalStrong);

        Assert.NotEmpty(updates);
        Assert.Equal(["unload", "load@8192/-1", "chat@8192/-1"], rig.Server.Transcript());
        var runner = Assert.Single(rig.Server.Runners);
        Assert.Equal((8192, "-1"), (runner.Context, runner.KeepAlive));

        // The pin moved with it: autocomplete now uses the 8,192-token instance and nothing is reloaded.
        await CompleteAsync(rig.Provider);
        await ChatAsync(rig.Provider, "short", Profile.LocalStrong);
        Assert.Equal(["unload", "load@8192/-1", "chat@8192/-1", "generate@8192/-1", "chat@8192/-1"], rig.Server.Transcript());

        var messages = rig.Progress.Reports.Select(p => p.Message).ToList();
        Assert.Contains(messages, m => m.StartsWith("Unloading qwen2.5-coder:7b (4,096-token context)", StringComparison.Ordinal));
        Assert.Contains(messages, m => m.StartsWith("Loading qwen2.5-coder:7b with 8,192 tokens of context", StringComparison.Ordinal));
        Assert.All(rig.Progress.Reports, p => Assert.Equal(("ollama", "req-1", Coder), (p.ProviderId, p.RequestId, p.ModelId)));
    }

    [Fact]
    public async Task LargerNeed_OnAChatModel_ReloadsWithTheChatKeepAlive()
    {
        using var rig = new Rig(GpuTier.Gpu8GB, completionModel: "qwen2.5-coder:1.5b");
        rig.Server.Load(Coder, 4096, "30m");

        await ChatAsync(rig.Provider, LongPrompt(), Profile.LocalStrong);

        Assert.Equal(["unload", "load@8192/30m", "chat@8192/30m"], rig.Server.Transcript());
    }

    [Fact]
    public async Task LargerNeed_On16Gb_LetsOllamaLoadASecondInstance_AndKeepsTheFirst()
    {
        using var rig = new Rig(GpuTier.Gpu16GB);
        rig.Server.SecondInstanceFits = true;
        rig.Server.Load(Coder, 4096, "-1");

        await ChatAsync(rig.Provider, LongPrompt(), Profile.LocalStrong);

        Assert.Equal(["load@8192/-1", "chat@8192/-1"], rig.Server.Transcript());
        Assert.Equal([4096, 8192], rig.Server.Runners.Select(r => r.Context).Order());

        // A small request still goes to the smaller instance, which the autocomplete model is pinned in.
        await CompleteAsync(rig.Provider);
        Assert.Equal("generate@4096/-1", rig.Server.Transcript()[^1]);
    }

    [Fact]
    public async Task LargerNeed_WhenTheEstimateSaysASecondInstanceDoesNotFit_ReleasesFirstEvenOn16Gb()
    {
        // 16 GB tier, but another model already takes 10 GiB of it: 15 - 10 - 4.4 leaves too little for a second 7B.
        using var rig = new Rig(GpuTier.Gpu16GB);
        rig.Server.Load(Coder, 4096, "-1");
        rig.Server.Load("qwen2.5-coder:14b", 8192, "30m");

        await ChatAsync(rig.Provider, LongPrompt(), Profile.LocalStrong);

        Assert.Equal(["unload", "load@8192/-1", "chat@8192/-1"], rig.Server.Transcript());
        Assert.Contains(rig.Server.Runners, r => r.Model == "qwen2.5-coder:14b");
    }

    [Fact]
    public async Task SharedModel_On8Gb_IsLoadedAtTheRecommendedMaximum_NotTheLargestProfileDefault()
    {
        using var rig = new Rig(GpuTier.Gpu8GB);

        await ChatAsync(rig.Provider, "hello", Profile.LocalStrong);
        await CompleteAsync(rig.Provider);

        Assert.Equal(["load@4096/-1", "chat@4096/-1", "generate@4096/-1"], rig.Server.Transcript());
    }

    [Fact]
    public async Task SharedModel_On16Gb_IsLoadedAtTheLargestProfileDefault_BecauseItFitsTwice()
    {
        using var rig = new Rig(GpuTier.Gpu16GB);

        await ChatAsync(rig.Provider, "hello", Profile.LocalStrong);
        await CompleteAsync(rig.Provider);

        Assert.Equal(["load@8192/-1", "chat@8192/-1", "generate@8192/-1"], rig.Server.Transcript());
    }

    [Fact]
    public async Task ResidentContextSetting_ChoosesTheSizeTheModelIsLoadedAt()
    {
        using var rig = new Rig(GpuTier.Gpu8GB, overrides: id => id == Coder ? 6144 : null);

        await CompleteAsync(rig.Provider);
        await ChatAsync(rig.Provider, "hello", Profile.LocalStrong);

        Assert.Equal(["load@6144/-1", "generate@6144/-1", "chat@6144/-1"], rig.Server.Transcript());
    }

    [Fact]
    public async Task ResidentContextSetting_StillGrowsForARequestThatNeedsMore()
    {
        using var rig = new Rig(GpuTier.Gpu8GB, overrides: id => id == Coder ? 4096 : null);
        rig.Server.Load(Coder, 4096, "-1");

        await ChatAsync(rig.Provider, LongPrompt(), Profile.LocalStrong);

        Assert.Equal(["unload", "load@8192/-1", "chat@8192/-1"], rig.Server.Transcript());
    }

    [Fact]
    public void ResidentContextSetting_IsFoundByModelId_IgnoringCaseAndTheLatestTag()
    {
        var overrides = new Dictionary<string, int> { ["Qwen2.5-Coder:7B"] = 8192, ["gemma4:latest"] = 16384, ["bad"] = 0 };

        Assert.Equal(8192, ProviderRegistry.FindResidentContext(overrides, "qwen2.5-coder:7b"));
        Assert.Equal(16384, ProviderRegistry.FindResidentContext(overrides, "gemma4"));
        Assert.Null(ProviderRegistry.FindResidentContext(overrides, "bad"));
        Assert.Null(ProviderRegistry.FindResidentContext(overrides, "other"));
        Assert.Null(ProviderRegistry.FindResidentContext(null, "gemma4"));
        Assert.Empty(new PillionSettings().Models.ResidentContext);
    }

    [Fact]
    public async Task ASecondInstanceThatNeverStarts_FailsWithinTheLoadTimeout_NamingWhatIsResident()
    {
        using var rig = new Rig(GpuTier.Gpu16GB, extensionJson: """{ "loadTimeoutSeconds": 0.3 }""");
        rig.Server.SecondInstanceFits = false; // the estimate says it fits, Ollama disagrees
        rig.Server.Load(Coder, 4096, "-1");
        var watch = System.Diagnostics.Stopwatch.StartNew();

        var ex = await Assert.ThrowsAsync<ProviderException>(() => ChatAsync(rig.Provider, LongPrompt(), Profile.LocalStrong));

        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(10), $"{watch.Elapsed}");
        Assert.Equal(PillionErrorCode.ProviderUnavailable, ex.Code);
        Assert.False(ex.IsProviderFault);
        Assert.Contains("did not finish loading qwen2.5-coder:7b with 8,192 tokens of context within 0.3 s", ex.Message, StringComparison.Ordinal);
        Assert.Contains("OLLAMA_MAX_LOADED_MODELS", ex.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("chat@", string.Concat(rig.Server.Transcript()), StringComparison.Ordinal);
        Assert.Contains(rig.Progress.Reports, p => p.Message.StartsWith("Loading", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ALoadThatStallsBehindAnotherModel_ListsTheResidentModelsInTheMessage()
    {
        using var rig = new Rig(GpuTier.Gpu24GBPlus, extensionJson: """{ "loadTimeoutSeconds": 0.3 }""");
        rig.Server.Load("qwen2.5-coder:14b", 8192, "-1");
        rig.Server.Load(Coder, 4096, "-1");

        var ex = await Assert.ThrowsAsync<ProviderException>(() => ChatAsync(rig.Provider, LongPrompt(), Profile.LocalStrong));

        Assert.Contains("These models are resident: qwen2.5-coder:14b", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnUnloadThatNeverCompletes_FailsWithinTheReleaseTimeout()
    {
        using var rig = new Rig(GpuTier.Gpu8GB, extensionJson: """{ "releaseTimeoutSeconds": 0.5 }""");
        rig.Server.UnloadNeverCompletes = true;
        rig.Server.Load(Coder, 4096, "-1");

        var ex = await Assert.ThrowsAsync<ProviderException>(() => ChatAsync(rig.Provider, LongPrompt(), Profile.LocalStrong));

        Assert.Equal(PillionErrorCode.ProviderUnavailable, ex.Code);
        Assert.Contains("did not unload qwen2.5-coder:7b within 0.5 s", ex.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("chat@", string.Concat(rig.Server.Transcript()), StringComparison.Ordinal);
    }

    [Fact]
    public async Task AServerThatDoesNotAnswerPs_FailsTheRequestInsteadOfWaiting()
    {
        using var rig = new Rig(GpuTier.Gpu8GB);
        rig.Server.Handler.OnAsync("GET api/ps", async (_, _, ct) =>
        {
            await Task.Delay(Timeout.Infinite, ct);
            return new HttpResponseMessage();
        });
        // The 10 s limit on /api/ps is a constant, so this test waits for it.
        var ex = await Assert.ThrowsAsync<ProviderException>(() => ChatAsync(rig.Provider, "hello"));

        Assert.Equal(PillionErrorCode.ProviderUnavailable, ex.Code);
        Assert.Contains("did not answer /api/ps within 10 s", ex.Message, StringComparison.Ordinal);
        Assert.Empty(rig.Server.Transcript());
    }

    [Fact]
    public async Task ACancelledRequest_StopsWaitingForTheLoad()
    {
        using var rig = new Rig(GpuTier.Gpu16GB);
        rig.Server.Load(Coder, 4096, "-1");
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(300));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => rig.Provider.GetChatClient(Coder)
            .GetStreamingResponseAsync([new AiChatMessage(AiChatRole.User, LongPrompt())], Chat(Profile.LocalStrong), cts.Token).CollectAsync(cts.Token));
    }

    private const string BigWindowCatalog = """
        { "version": 1, "profiles": {}, "families": [
          { "match": "bigctx*", "contextWindow": 131072, "chat": true, "fim": true,
            "residency": { "vramMiB": 4300, "kvMiBPer1kTokens": 56, "recommendedContext": { "8gb": 4096 } } },
          { "match": "*" } ] }
        """;

    [Fact]
    public async Task ARequestBeyondWhatTheTierCanHold_FailsWithContextOverflow_AndLoadsNothing()
    {
        using var rig = new Rig(GpuTier.Gpu8GB, completionModel: null, catalog: ModelCatalog.Parse(BigWindowCatalog));

        var ex = await Assert.ThrowsAsync<ProviderException>(() => ChatAsync(rig.Provider, new string('x', 240_000), Profile.LocalStrong, model: "bigctx:7b"));

        // 8 GB tier: 7 GiB usable, 4,300 MiB of weights, 56 MiB per 1,024 tokens: 52,224 tokens at most.
        Assert.Equal(PillionErrorCode.ContextOverflow, ex.Code);
        Assert.False(ex.IsProviderFault);
        Assert.Contains("52,224", ex.Message, StringComparison.Ordinal);
        Assert.Contains("8 GB GPU tier", ex.Message, StringComparison.Ordinal);
        Assert.Empty(rig.Server.Transcript());
    }

    [Fact]
    public async Task ARequestAtTheTiersLimit_LoadsTheLargestContextThatFits()
    {
        using var rig = new Rig(GpuTier.Gpu8GB, completionModel: null, catalog: ModelCatalog.Parse(BigWindowCatalog));

        await ChatAsync(rig.Provider, new string('x', 180_000), Profile.LocalStrong, model: "bigctx:7b");

        Assert.Equal(["load@52224/30m", "chat@52224/30m"], rig.Server.Transcript());
    }

    [Fact]
    public async Task Ps_IsReadAtMostEvery30Seconds_AndParsedIntoTheResidencyMap()
    {
        using var rig = new Rig(GpuTier.Gpu8GB);
        rig.Server.Load(Coder, 4096, "-1");
        rig.Server.Load("gemma4:e2b", 4096, "30m");

        await CompleteAsync(rig.Provider);
        await CompleteAsync(rig.Provider);
        await rig.Map.RefreshAsync(force: false, CancellationToken.None);
        Assert.Equal(1, rig.Server.PsRequests);

        rig.Clock.Advance(TimeSpan.FromSeconds(29));
        await CompleteAsync(rig.Provider);
        Assert.Equal(1, rig.Server.PsRequests);

        rig.Clock.Advance(TimeSpan.FromSeconds(2));
        var snapshot = await rig.Map.RefreshAsync(force: false, CancellationToken.None);
        Assert.Equal(2, rig.Server.PsRequests);

        // An explicit refresh asks again.
        await rig.Map.RefreshAsync(force: true, CancellationToken.None);
        Assert.Equal(3, rig.Server.PsRequests);

        Assert.Equal(GpuTier.Gpu8GB, snapshot.Tier);
        Assert.Equal(2, snapshot.Models.Count);
        var coder = snapshot.Models.Single(m => m.ModelId == Coder);
        Assert.Equal(("ollama", 4096, "-1", true, null), (coder.ProviderId, coder.ContextLength, coder.KeepAlive, coder.IsPinned, coder.ExpiresAt));
        Assert.Equal((long)((4300 + 224) * 1024.0 * 1024), coder.VramBytes);
        var gemma = snapshot.Models.Single(m => m.ModelId == "gemma4:e2b");
        Assert.Equal(("30m", false), (gemma.KeepAlive, gemma.IsPinned));
        Assert.NotNull(gemma.ExpiresAt);
        Assert.Equal(snapshot.Models.Sum(m => m.VramBytes), snapshot.TotalVramBytes);
        Assert.Equal(coder, snapshot.Models[0]); // largest first
    }

    [Fact]
    public async Task Ps_ParsesNanosecondTimestampsAndOlderServersWithoutContextLength()
    {
        using var rig = new Rig(GpuTier.Gpu8GB);
        rig.Server.Handler.Json("GET api/ps", """
            { "models": [
              { "name": "qwen2.5-coder:7b", "model": "qwen2.5-coder:7b", "size": 5000000000, "size_vram": 4900000000, "expires_at": "2319-01-19T05:51:01.647710507Z" },
              { "name": "gemma4:e2b", "size": 1700000000, "size_vram": 0, "expires_at": "2026-10-08T12:07:30.123456789+00:00", "context_length": 8192 } ] }
            """);

        var snapshot = await rig.Map.RefreshAsync(force: true, CancellationToken.None);

        var coder = snapshot.Models.Single(m => m.ModelId == Coder);
        Assert.Null(coder.ContextLength);
        Assert.Equal("-1", coder.KeepAlive);
        Assert.Null(coder.ExpiresAt);
        var gemma = snapshot.Models.Single(m => m.ModelId == "gemma4:e2b");
        Assert.Equal(8192, gemma.ContextLength);
        Assert.Equal(0, gemma.VramBytes);
        Assert.Equal("8m", gemma.KeepAlive); // 7 min 30 s left, rounded up
        Assert.Equal(new DateTimeOffset(2026, 10, 8, 12, 7, 30, TimeSpan.Zero).AddTicks(1234567), gemma.ExpiresAt);

        // The server did not say what the resident instance was loaded with, so the profile default is sent, as before.
        await ChatAsync(rig.Provider, "hello", Profile.LocalStrong);
        Assert.Equal(["chat@8192/-1"], rig.Server.Transcript());
    }

    [Fact]
    public async Task AServerWithoutPs_GetsRequestsExactlyAsBefore()
    {
        var handler = FakeOllamaHandler.WithDefaults();
        var settings = new ProviderSettings { Id = "ollama", Kind = ProviderKind.Ollama };
        using var provider = (OllamaProvider)new OllamaProviderFactory(() => handler).Create(settings, new ProviderCreationContext(null, ModelCatalog.Default, NullLoggerFactory.Instance, "qwen2.5-coder:1.5b"));

        await CompleteAsync(provider, "qwen2.5-coder:1.5b");
        await ChatAsync(provider, "x", Profile.LocalFast, "gemma4:e2b");

        Assert.Single(handler.BodiesFor("api/generate"));
        Assert.Equal(4096, handler.BodiesFor("api/chat").Single()["options"]!["num_ctx"]!.GetValue<int>());
        Assert.Single(handler.Requests, r => r.Path == "api/ps"); // asked once, then remembered as unsupported
    }

    [Fact]
    public async Task TheMap_ForgetsAProviderThatIsDisposed()
    {
        var rig = new Rig(GpuTier.Gpu8GB);
        rig.Server.Load(Coder, 4096, "-1");
        Assert.Single((await rig.Map.RefreshAsync(force: true, CancellationToken.None)).Models);

        rig.Dispose();

        Assert.Empty(rig.Map.GetSnapshot().Models);
    }

    [Fact]
    public async Task TheRegistry_GivesProvidersTheTierTheOverridesTheProgressSinkAndTheMap()
    {
        var clock = new ManualClock();
        var server = new FakeOllamaServer(clock);
        var settings = new PillionSettings();
        settings.Providers.Add(new ProviderSettings { Id = "ollama", Kind = ProviderKind.Ollama });
        settings.Models.GpuTier = GpuTierSetting.Gpu16GB;
        settings.Models.ResidentContext["Qwen2.5-Coder:7B"] = 6144;
        var progress = new RecordingProgressSink();
        var map = new LocalResidencyMap();
        using var registry = new ProviderRegistry(
            new StaticGatewaySettingsSource(settings),
            new Common.Secrets.InMemorySecretStore(),
            [new OllamaProviderFactory(() => server.Handler)],
            ModelCatalog.Default,
            progress: progress,
            time: clock,
            residency: map);
        var provider = (OllamaProvider)registry.GetSnapshot().Find("ollama")!;

        // The catalog routes Local-fast to the 1.5B model, so the 7B is a plain chat model here (30m, not pinned).
        await ChatAsync(provider, "hello", Profile.LocalStrong);
        var snapshot = await map.RefreshAsync(force: false, CancellationToken.None);

        Assert.Equal(["load@6144/30m", "chat@6144/30m"], server.Transcript());
        var resident = Assert.Single(snapshot.Models);
        Assert.Equal((Coder, 6144, "ollama"), (resident.ModelId, resident.ContextLength, resident.ProviderId));
        Assert.Equal(GpuTier.Gpu16GB, snapshot.Tier);
        Assert.Contains(progress.Reports, p => p.Message.StartsWith("Loading", StringComparison.Ordinal));

        // Disposing the registry disposes its providers, which leave the map.
        registry.Dispose();
        Assert.Empty(map.GetSnapshot().Models);
    }

    [Fact]
    public void EstimatesAndRecommendations_ComeFromTheCatalog()
    {
        var seven = ModelCatalog.Default.GetResidency("qwen2.5-coder:7b")!;
        Assert.Equal(4300 + 224, seven.EstimateMiB(4096));
        Assert.Equal(4096, seven.RecommendedContextFor(GpuTier.Gpu8GB, 0));
        Assert.Equal(16384, seven.RecommendedContextFor(GpuTier.Gpu16GB, 0));
        Assert.Equal(1234, seven.RecommendedContextFor((GpuTier)99, 1234));
        Assert.Equal(0, seven.MaxContextWithin(4000));
        Assert.Equal(1024, seven.MaxContextWithin(4300 + 56));

        Assert.NotNull(ModelCatalog.Default.GetResidency("library/qwen2.5-coder:14b"));
        Assert.Null(ModelCatalog.Default.GetResidency("gemma4:e2b"));
        Assert.Equal(1024 * 7, OllamaResidency.TierBudgetMiB(GpuTier.Gpu8GB));
        Assert.Null(OllamaResidency.TierBudgetMiB(GpuTier.CpuOnly));
        int[] sizes = [1, 2048, 2049, 4097, 8192, 9000];
        Assert.Equal([2048, 2048, 4096, 8192, 8192, 16384], sizes.Select(OllamaResidency.RoundUpContext));
    }
}

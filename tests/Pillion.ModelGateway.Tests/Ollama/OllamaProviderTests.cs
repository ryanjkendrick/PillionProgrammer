using System.Net;
using System.Text.Json;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using Pillion.Common.Settings;
using Pillion.Contracts;
using Pillion.ModelGateway.Providers.Ollama;
using Pillion.ModelGateway.Tests.TestSupport;
using AiChatMessage = Microsoft.Extensions.AI.ChatMessage;
using AiChatRole = Microsoft.Extensions.AI.ChatRole;

namespace Pillion.ModelGateway.Tests.Ollama;

public class OllamaProviderTests
{
    private static OllamaProvider Create(FakeOllamaHandler handler, string endpoint = "", string? autocompleteModel = null, string? extensionJson = null)
    {
        var settings = new ProviderSettings { Id = "ollama", Kind = ProviderKind.Ollama, Endpoint = endpoint };
        if (extensionJson is not null)
        {
            settings.ExtensionData = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(extensionJson);
        }

        var factory = new OllamaProviderFactory(() => handler);
        return (OllamaProvider)factory.Create(settings, new ProviderCreationContext(null, ModelCatalog.Default, NullLoggerFactory.Instance, autocompleteModel));
    }

    private static ChatOptions Options(Profile profile) => new()
    {
        AdditionalProperties = new AdditionalPropertiesDictionary { [GatewayOptionKeys.Profile] = profile },
    };

    [Fact]
    public void Defaults_LocalhostEndpoint_IsLocal_AndNeedsNoKey()
    {
        using var provider = Create(FakeOllamaHandler.WithDefaults());

        Assert.Equal(new Uri("http://localhost:11434/"), provider.Options.Endpoint);
        Assert.True(provider.IsLocal);
        Assert.Equal(ProviderKind.Ollama, provider.Kind);
        Assert.False(new OllamaProviderFactory().RequiresApiKey(new ProviderSettings()));
    }

    [Fact]
    public void RemoteEndpoint_IsNotLocal()
    {
        using var provider = Create(FakeOllamaHandler.WithDefaults(), "http://gpu-box.example:11434");

        Assert.False(provider.IsLocal);
        Assert.Equal(new Uri("http://gpu-box.example:11434/"), provider.Options.Endpoint);
    }

    [Fact]
    public void InvalidEndpoint_IsRejectedAtCreation()
    {
        Assert.Throws<UriFormatException>(() => Create(FakeOllamaHandler.WithDefaults(), "ftp://nope"));
    }

    [Fact]
    public async Task ListModels_ReadsTagsAndShow_IntoCapabilities()
    {
        var handler = FakeOllamaHandler.WithDefaults();
        using var provider = Create(handler);

        var models = await provider.ListModelsAsync(CancellationToken.None);

        Assert.Equal(["qwen3-embedding:0.6b", "gemma4:e2b", "qwen2.5-coder:1.5b"], models.Select(m => m.ModelId));
        var gemma = models.Single(m => m.ModelId == "gemma4:e2b");
        Assert.True(gemma.Capabilities.Chat);
        Assert.True(gemma.Capabilities.Vision);
        Assert.False(gemma.Capabilities.Fim);
        Assert.Equal(ToolCallingSupport.Native, gemma.Capabilities.ToolCalling);
        Assert.Equal(131072, gemma.Capabilities.ContextWindow);
        Assert.Equal("sha256:7fbdbf8f5e45a75bb122155ed546e765b4d9c53a1285f62fd9f506baa1c5a47e", gemma.Digest);
        Assert.True(gemma.Capabilities.IsLocal);

        var coder = models.Single(m => m.ModelId == "qwen2.5-coder:1.5b");
        Assert.True(coder.Capabilities.Fim);
        Assert.Equal(32768, coder.Capabilities.ContextWindow);

        var embedding = models.Single(m => m.ModelId == "qwen3-embedding:0.6b");
        Assert.True(embedding.Capabilities.Embeddings);
        Assert.False(embedding.Capabilities.Chat);

        var descriptor = gemma.ToDescriptor(isAvailable: true);
        Assert.Equal("ollama/gemma4:e2b", descriptor.Id);
        Assert.Equal(gemma.Digest, descriptor.Digest);

        // Show results are cached by digest.
        await provider.ListModelsAsync(CancellationToken.None);
        Assert.Equal(3, handler.Requests.Count(r => r.Path == "api/show"));
        Assert.Single(handler.Requests, r => r.Path == "api/version");
    }

    [Fact]
    public async Task ListModels_DeduplicatesByName_KeepingTheFirstEntryTheCurrentDigest()
    {
        // The duplicate fixture lists gemma4:e2b twice (current digest 7fbdbf... first, an older digest 0b1c2d... second)
        // and llama3.2 both as llama3.2:latest and llama3.2.
        var handler = FakeOllamaHandler.WithDefaults();
        handler.Json("GET api/tags", FakeOllamaHandler.Fixture("tags-duplicates.json"));
        var knownShow = handler.RouteFor("POST api/show");
        handler.On("POST api/show", (request, body) => body?["model"]?.GetValue<string>() is "llama3.2:latest" or "llama3.2"
            ? FakeOllamaHandler.Respond(HttpStatusCode.OK, FakeOllamaHandler.Fixture("show-qwen-coder.json"))
            : knownShow(request, body));
        using var provider = Create(handler);

        var models = await provider.ListModelsAsync(CancellationToken.None);

        Assert.Equal(["qwen3-embedding:0.6b", "gemma4:e2b", "qwen2.5-coder:1.5b", "llama3.2:latest"], models.Select(m => m.ModelId));
        Assert.Single(models, m => m.ModelId == "gemma4:e2b");
        Assert.Equal("sha256:7fbdbf8f5e45a75bb122155ed546e765b4d9c53a1285f62fd9f506baa1c5a47e", models.Single(m => m.ModelId == "gemma4:e2b").Digest);
        Assert.StartsWith("sha256:a80c4f17", models.Single(m => m.ModelId == "llama3.2:latest").Digest, StringComparison.Ordinal);

        // The dropped duplicates are not even inspected.
        Assert.DoesNotContain(handler.BodiesFor("api/show"), b => b["name"]?.GetValue<string>() == "llama3.2");
        Assert.Equal(4, handler.Requests.Count(r => r.Path == "api/show"));
    }

    [Fact]
    public async Task Chat_StreamsNdjson_WithNumCtxAndKeepAlivePerProfile()
    {
        var handler = FakeOllamaHandler.WithDefaults();
        using var provider = Create(handler);
        var client = provider.GetChatClient("gemma4:e2b");

        var updates = await client.GetStreamingResponseAsync([new AiChatMessage(AiChatRole.User, "Reply with exactly: hello there")], Options(Profile.LocalStrong)).CollectAsync();

        Assert.Equal("hello there", string.Concat(updates.Select(u => u.Text)));
        Assert.All(updates, u => Assert.Equal("gemma4:e2b", u.ModelId));
        var usage = updates[^1].Contents.OfType<UsageContent>().Single().Details;
        Assert.Equal(22, usage.InputTokenCount);
        Assert.Equal(3, usage.OutputTokenCount);
        Assert.Equal(ChatFinishReason.Stop, updates[^1].FinishReason);

        var body = handler.BodiesFor("api/chat").Single();
        Assert.Equal("gemma4:e2b", body["model"]!.GetValue<string>());
        Assert.True(body["stream"]!.GetValue<bool>());
        Assert.Equal("30m", body["keep_alive"]!.GetValue<string>());
        Assert.Equal(8192, body["options"]!["num_ctx"]!.GetValue<int>());
        Assert.Equal("user", body["messages"]![0]!["role"]!.GetValue<string>());
    }

    [Fact]
    public async Task Chat_LocalFastProfile_Uses4096()
    {
        var handler = FakeOllamaHandler.WithDefaults();
        using var provider = Create(handler);

        await provider.GetChatClient("gemma4:e2b").GetStreamingResponseAsync([new AiChatMessage(AiChatRole.User, "x")], Options(Profile.LocalFast)).CollectAsync();

        Assert.Equal(4096, handler.BodiesFor("api/chat").Single()["options"]!["num_ctx"]!.GetValue<int>());
    }

    [Fact]
    public async Task Chat_NumCtxCanBeOverriddenInSettings()
    {
        var handler = FakeOllamaHandler.WithDefaults();
        using var provider = Create(handler, extensionJson: """{ "numCtx": { "localStrong": 16384 } }""");

        await provider.GetChatClient("gemma4:e2b").GetStreamingResponseAsync([new AiChatMessage(AiChatRole.User, "x")], Options(Profile.LocalStrong)).CollectAsync();

        Assert.Equal(16384, handler.BodiesFor("api/chat").Single()["options"]!["num_ctx"]!.GetValue<int>());
        Assert.Equal(4096, provider.Options.ContextSizeFor(Profile.LocalFast));
    }

    [Fact]
    public async Task Chat_OnTheAutocompleteModel_KeepsItResident()
    {
        var handler = FakeOllamaHandler.WithDefaults();
        using var provider = Create(handler, autocompleteModel: "qwen2.5-coder:1.5b");

        await provider.GetChatClient("qwen2.5-coder:1.5b").GetStreamingResponseAsync([new AiChatMessage(AiChatRole.User, "x")], Options(Profile.LocalFast)).CollectAsync();

        Assert.Equal(-1, handler.BodiesFor("api/chat").Single()["keep_alive"]!.GetValue<int>());
    }

    [Fact]
    public async Task Fim_UsesGenerateWithSuffix_RawFalse_AndKeepAliveMinusOne()
    {
        var handler = FakeOllamaHandler.WithDefaults();
        using var provider = Create(handler);
        var client = provider.GetFimClient("qwen2.5-coder:1.5b")!;

        var pieces = await client.CompleteAsync(
            new FimRequest { Prefix = "int Add(int a, int b) => ", Suffix = ";\n", MaxTokens = 32, Temperature = 0.2f, StopSequences = ["\n\n"], Profile = Profile.LocalFast },
            CancellationToken.None).CollectAsync();

        Assert.Equal("a + b", string.Concat(pieces));
        var body = handler.BodiesFor("api/generate").Single();
        Assert.Equal("int Add(int a, int b) => ", body["prompt"]!.GetValue<string>());
        Assert.Equal(";\n", body["suffix"]!.GetValue<string>());
        Assert.False(body["raw"]!.GetValue<bool>());
        Assert.Equal(-1, body["keep_alive"]!.GetValue<int>());
        Assert.Equal(4096, body["options"]!["num_ctx"]!.GetValue<int>());
        Assert.Equal(32, body["options"]!["num_predict"]!.GetValue<int>());
        Assert.Equal("\n\n", body["options"]!["stop"]![0]!.GetValue<string>());

        // Once FIM has used a model, chat on it keeps it resident too.
        await provider.GetChatClient("qwen2.5-coder:1.5b").GetStreamingResponseAsync([new AiChatMessage(AiChatRole.User, "x")]).CollectAsync();
        Assert.Equal(-1, handler.BodiesFor("api/chat").Single()["keep_alive"]!.GetValue<int>());
    }

    [Fact]
    public async Task Fim_AtTheEndOfTheDocument_SendsALineBreakAsTheSuffix()
    {
        // Ollama's templates take the FIM path only when the suffix is not empty; an empty one turns the prompt into a
        // chat turn, which the instruct-tuned coder models answered in prose or behind a fence at Gate 1.
        var handler = FakeOllamaHandler.WithDefaults();
        using var provider = Create(handler);
        var client = provider.GetFimClient("qwen2.5-coder:1.5b")!;

        await client.CompleteAsync(new FimRequest { Prefix = "int Add(int a, int b) => ", Suffix = string.Empty, Profile = Profile.LocalFast }, CancellationToken.None).CollectAsync();

        var body = handler.BodiesFor("api/generate").Single();
        Assert.Equal("\n", body["suffix"]!.GetValue<string>());
        Assert.Equal("\n", global::Pillion.ModelGateway.Providers.Ollama.OllamaFimClient.SuffixToSend(null));
        Assert.Equal(" ", global::Pillion.ModelGateway.Providers.Ollama.OllamaFimClient.SuffixToSend(" "));
    }

    [Fact]
    public async Task Fim_ModelWithoutInsert_HasNoFimClientOnceListed()
    {
        using var provider = Create(FakeOllamaHandler.WithDefaults());
        Assert.NotNull(provider.GetFimClient("gemma4:e2b")); // unknown until listed

        await provider.ListModelsAsync(CancellationToken.None);

        Assert.Null(provider.GetFimClient("gemma4:e2b"));
        Assert.NotNull(provider.GetFimClient("qwen2.5-coder:1.5b"));
    }

    [Fact]
    public async Task Fim_ServerSaysNoInsert_IsNotAProviderFault()
    {
        var handler = FakeOllamaHandler.WithDefaults();
        handler.On("POST api/generate", (_, _) => FakeOllamaHandler.Respond(HttpStatusCode.BadRequest, FakeOllamaHandler.Fixture("error-no-insert.json")));
        using var provider = Create(handler);

        var ex = await Assert.ThrowsAsync<ProviderException>(() => provider.GetFimClient("gemma4:e2b")!.CompleteAsync(new FimRequest { Prefix = "a" }, CancellationToken.None).CollectAsync());

        Assert.Equal(PillionErrorCode.ProviderUnavailable, ex.Code);
        Assert.False(ex.IsProviderFault);
        Assert.Contains("fill-in-the-middle", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task VersionTooOld_IsRejected()
    {
        var handler = FakeOllamaHandler.WithDefaults();
        handler.Json("GET api/version", FakeOllamaHandler.Fixture("version-too-old.json"));
        using var provider = Create(handler);

        var ex = await Assert.ThrowsAsync<ProviderException>(() => provider.GetChatClient("gemma4:e2b").GetStreamingResponseAsync([new AiChatMessage(AiChatRole.User, "x")]).CollectAsync());
        Assert.Equal(PillionErrorCode.ProviderUnavailable, ex.Code);
        Assert.Contains("0.5.13", ex.Message, StringComparison.Ordinal);
        Assert.Contains("0.6.4", ex.Message, StringComparison.Ordinal);
        Assert.Empty(handler.BodiesFor("api/chat"));

        var probe = await provider.ProbeAsync(CancellationToken.None);
        Assert.False(probe.IsReachable);
        Assert.Contains("older than the minimum", probe.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("0.40.1", 0, 40, 1)]
    [InlineData("0.6.4-rc1", 0, 6, 4)]
    [InlineData("v0.12.0", 0, 12, 0)]
    public void ParseVersion_HandlesSuffixes(string text, int major, int minor, int build)
    {
        Assert.Equal(new Version(major, minor, build), OllamaProvider.ParseVersion(text));
    }

    [Fact]
    public async Task Probe_ReportsTheVersion()
    {
        using var provider = Create(FakeOllamaHandler.WithDefaults());

        var probe = await provider.ProbeAsync(CancellationToken.None);

        Assert.True(probe.IsReachable);
        Assert.Equal("0.40.1", probe.Version);
        Assert.Contains("Ollama 0.40.1", probe.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ModelMissing_MapsToModelNotInstalled()
    {
        var handler = FakeOllamaHandler.WithDefaults();
        handler.On("POST api/chat", (_, _) => FakeOllamaHandler.Respond(HttpStatusCode.NotFound, FakeOllamaHandler.Fixture("error-model-not-found.json")));
        using var provider = Create(handler);

        var ex = await Assert.ThrowsAsync<ProviderException>(() => provider.GetChatClient("nope:1b").GetStreamingResponseAsync([new AiChatMessage(AiChatRole.User, "x")]).CollectAsync());

        Assert.Equal(PillionErrorCode.ModelNotInstalled, ex.Code);
        Assert.Contains("ollama pull nope:1b", ex.Message, StringComparison.Ordinal);
        Assert.False(ex.IsProviderFault);
    }

    [Fact]
    public async Task ConnectionRefused_MapsToProviderUnavailable()
    {
        var handler = FakeOllamaHandler.WithDefaults();
        handler.RefuseConnections = true;
        using var provider = Create(handler);

        var ex = await Assert.ThrowsAsync<ProviderException>(() => provider.ListModelsAsync(CancellationToken.None));

        Assert.Equal(PillionErrorCode.ProviderUnavailable, ex.Code);
        Assert.True(ex.IsProviderFault);
        Assert.Contains("not running", ex.Message, StringComparison.Ordinal);

        var probe = await provider.ProbeAsync(CancellationToken.None);
        Assert.False(probe.IsReachable);
    }

    [Theory]
    [InlineData("input length exceeds the context length")]
    [InlineData("the prompt is too long for the context window")]
    public void ContextErrors_MapToContextOverflow(string message)
    {
        var ex = OllamaHttp.MapError(HttpStatusCode.BadRequest, message, "m");

        Assert.Equal(PillionErrorCode.ContextOverflow, ex.Code);
        Assert.False(ex.IsProviderFault);
    }

    [Fact]
    public async Task ErrorMidStream_EndsTheStreamWithAProviderException()
    {
        var handler = FakeOllamaHandler.WithDefaults();
        handler.Json("POST api/chat", FakeOllamaHandler.Fixture("chat-error-midstream.ndjson"), "application/x-ndjson");
        using var provider = Create(handler);
        var received = new List<string>();

        var ex = await Assert.ThrowsAsync<ProviderException>(async () =>
        {
            await foreach (var update in provider.GetChatClient("gemma4:e2b").GetStreamingResponseAsync([new AiChatMessage(AiChatRole.User, "x")]))
            {
                received.Add(update.Text);
            }
        });

        Assert.Equal(["hello"], received);
        Assert.Contains("unexpected EOF", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ThroughTheGateway_ChatChunksNameTheOllamaModel()
    {
        var handler = FakeOllamaHandler.WithDefaults();
        var settings = new PillionSettings();
        settings.Providers.Add(new ProviderSettings { Id = "ollama", Kind = ProviderKind.Ollama });
        var source = new StaticGatewaySettingsSource(settings);
        var options = new ModelGatewayOptions();
        using var registry = new ProviderRegistry(source, new Common.Secrets.InMemorySecretStore(), [new OllamaProviderFactory(() => handler)], ModelCatalog.Default);
        var inventory = new ModelInventory();
        var breakers = new CircuitBreakerRegistry(options);
        var router = new ModelRouter(registry, ModelCatalog.Default, inventory, new AlwaysReachable(), breakers, new NoCostCaps());
        var gateway = new ModelGateway(registry, router, inventory, ModelCatalog.Default, PromptTemplates.Default, new AutocompleteCache(options), breakers, new ConcurrencyLimiter(options), new InMemoryOutboundLog(options), new AlwaysReachable(), new NoCostCaps(), new NoProgressSink(), options);

        // Listing tells the router that only gemma4 can chat, so Auto picks it although the catalog prefers qwen2.5-coder:14b.
        var models = await gateway.ListModelsAsync(CancellationToken.None);
        Assert.Contains(models, m => m.Id == "ollama/gemma4:e2b" && m.IsAvailable);

        var chunks = await gateway.ChatAsync(GatewayHarness.ChatRequest("Reply with exactly: hello there"), TaskClass.Chat, CancellationToken.None).CollectAsync();

        Assert.Equal("hello there", string.Concat(chunks.Select(c => c.Text)));
        Assert.All(chunks, c => Assert.Equal("gemma4:e2b", c.ModelId));
        Assert.All(chunks, c => Assert.Equal("ollama", c.ProviderId));
        Assert.Equal(22, chunks[^1].PromptTokens);
        Assert.Equal(3, chunks[^1].CompletionTokens);

        // Autocomplete goes to the installed FIM model.
        var completion = await gateway.CompleteAsync(GatewayHarness.CompletionRequest(), CancellationToken.None).CollectAsync();
        Assert.Equal("a + b", string.Concat(completion.Select(c => c.Text)));
        Assert.All(completion, c => Assert.Equal("qwen2.5-coder:1.5b", c.ModelId));
    }
}

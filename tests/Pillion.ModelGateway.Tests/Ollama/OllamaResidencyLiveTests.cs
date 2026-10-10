using System.Diagnostics;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using Pillion.Common.Hardware;
using Pillion.Common.Settings;
using Pillion.Contracts;
using Pillion.ModelGateway.Providers.Ollama;
using Pillion.ModelGateway.Tests.TestSupport;
using Xunit.Abstractions;
using AiChatMessage = Microsoft.Extensions.AI.ChatMessage;
using AiChatRole = Microsoft.Extensions.AI.ChatRole;

namespace Pillion.ModelGateway.Tests.Ollama;

/// <summary>
/// A fact for a test that needs the GPU to itself: it unloads and reloads the model it names. Skipped when Ollama does not
/// answer, the model is not installed, or another model is resident when the tests are discovered (other work shares the
/// server; evicting its models mid-run would fail it).
/// </summary>
public sealed class LiveOllamaExclusiveModelFactAttribute : FactAttribute
{
    public LiveOllamaExclusiveModelFactAttribute(string model)
    {
        if (!LiveOllama.IsReachable)
        {
            Skip = $"Ollama is not answering at {LiveOllama.Endpoint}api/version.";
        }
        else if (!AutocompleteLiveTests.IsInstalled(model))
        {
            Skip = $"{model} is not installed.";
        }
        else if (OtherResidentModels(model) is { Count: > 0 } others)
        {
            Skip = $"Other models are resident in Ollama ({string.Join(", ", others)}); this test needs the GPU to itself.";
        }
    }

    private static List<string> OtherResidentModels(string model)
    {
        try
        {
            using var http = new HttpClient { BaseAddress = new Uri(LiveOllama.Endpoint), Timeout = TimeSpan.FromSeconds(3) };
            using var ps = JsonDocument.Parse(http.GetStringAsync("api/ps").GetAwaiter().GetResult());
            return [.. ps.RootElement.GetProperty("models").EnumerateArray().Select(m => m.GetProperty("name").GetString()!).Where(n => !string.Equals(n, model, StringComparison.OrdinalIgnoreCase))];
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException or KeyNotFoundException)
        {
            return [];
        }
    }
}

/// <summary>
/// The 8 GB scenario of design section 6 against the real server: autocomplete keeps <c>qwen2.5-coder:7b</c> resident at
/// 4,096 tokens with <c>keep_alive</c> -1, and chat at the Local-strong profile (8,192 by default) used to wait for ever for a
/// second runner. Run it on a machine whose GPU is free of other Ollama models:
/// <c>dotnet test --filter Category=Live --logger "console;verbosity=detailed"</c>. Whatever was resident when a test
/// started (the model, its context, its keep-alive) is restored afterwards.
/// </summary>
[Trait("Category", "Live")]
[Collection("Ollama GPU")]
public sealed class OllamaResidencyLiveTests(ITestOutputHelper output)
{
    private const string Coder = AutocompleteLiveTests.Large;

    private static readonly TimeSpan Minute = TimeSpan.FromSeconds(60);

    [LiveOllamaExclusiveModelFact(Coder)]
    public async Task ChatAtLocalStrong_BesideThePinnedCoder_SharesTheRunner_AndCompletesWithinAMinute()
    {
        using var live = await LiveRig.StartAsync(output);
        if (live is null)
        {
            return;
        }

        await live.PinAsync(4096);

        var watch = Stopwatch.StartNew();
        using var cts = new CancellationTokenSource(Minute);
        var reply = await live.ChatAsync("Reply with exactly the word: pillion", Profile.LocalStrong, cts.Token);
        watch.Stop();

        output.WriteLine($"Chat at Local-strong beside the pinned coder: {watch.Elapsed.TotalSeconds:F1} s, reply [{reply}]");
        Assert.False(string.IsNullOrWhiteSpace(reply));
        Assert.True(watch.Elapsed < Minute);

        var instance = Assert.Single(await live.ResidentAsync(Coder));
        Assert.Equal(4096, instance.Context);
        Assert.True(instance.Pinned);
        Assert.DoesNotContain(live.Messages, m => m.StartsWith("Unloading", StringComparison.Ordinal));
    }

    [LiveOllamaExclusiveModelFact(Coder)]
    public async Task ChatThatNeeds8192_ReloadsTheCoderOnce_AndAutocompleteStillWorks()
    {
        using var live = await LiveRig.StartAsync(output);
        if (live is null)
        {
            return;
        }

        await live.PinAsync(4096);

        var watch = Stopwatch.StartNew();
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(3));
        var reply = await live.ChatAsync(LongQuestion(), Profile.LocalStrong, cts.Token);
        watch.Stop();

        output.WriteLine($"Chat that needs 8,192 tokens: {watch.Elapsed.TotalSeconds:F1} s, reply [{reply}]");
        foreach (var message in live.Messages)
        {
            output.WriteLine("  progress: " + message);
        }

        Assert.False(string.IsNullOrWhiteSpace(reply));
        Assert.True(watch.Elapsed < new OllamaOptions().LoadTimeout + Minute, $"{watch.Elapsed}");
        Assert.Contains(live.Messages, m => m.StartsWith("Unloading qwen2.5-coder:7b (4,096-token context)", StringComparison.Ordinal));
        var instance = Assert.Single(await live.ResidentAsync(Coder));
        Assert.Equal(8192, instance.Context);
        Assert.True(instance.Pinned, "the completion model is pinned again after the reload");

        // Autocomplete uses the instance that is there and does not reload it.
        var before = live.Messages.Count;
        watch.Restart();
        var completion = await live.CompleteAsync(cts.Token);
        output.WriteLine($"Autocomplete after the reload: {watch.Elapsed.TotalMilliseconds:F0} ms, [{completion}]");
        Assert.False(string.IsNullOrWhiteSpace(completion));
        Assert.Equal(before, live.Messages.Count);
        Assert.Equal(8192, Assert.Single(await live.ResidentAsync(Coder)).Context);
    }

    private static string LongQuestion() =>
        string.Concat(Enumerable.Repeat("The quick brown fox jumps over the lazy dog while the editor waits for a suggestion. ", 350))
        + "\n\nIgnore the text above and reply with exactly the word: ok";

    private readonly record struct Instance(int? Context, bool Pinned);

    /// <summary>A provider on the real server with the 8 GB tier, and the means to set the server up and put it back.</summary>
    private sealed class LiveRig : IDisposable
    {
        private readonly ITestOutputHelper _output;
        private readonly HttpClient _http = new() { BaseAddress = new Uri(LiveOllama.Endpoint), Timeout = TimeSpan.FromSeconds(90) };
        private readonly RecordingProgressSink _progress = new();
        private readonly (int? Context, string KeepAlive)? _before;
        private OllamaProvider? _provider;

        private LiveRig(ITestOutputHelper output, (int? Context, string KeepAlive)? before)
        {
            _output = output;
            _before = before;
        }

        public IReadOnlyList<string> Messages => [.. _progress.Reports.Select(p => p.Message)];

        /// <summary>
        /// Starts a rig, or returns <see langword="null"/> (and says so in the output) when another model has become resident since
        /// discovery: the 7B cannot be reloaded beside it on this GPU, and evicting somebody else's model is not the test's call.
        /// </summary>
        public static async Task<LiveRig?> StartAsync(ITestOutputHelper output)
        {
            var probe = new HttpClient { BaseAddress = new Uri(LiveOllama.Endpoint), Timeout = TimeSpan.FromSeconds(10) };
            using (probe)
            {
                using var all = JsonDocument.Parse(await probe.GetStringAsync("api/ps"));
                var others = all.RootElement.GetProperty("models").EnumerateArray().Select(m => m.GetProperty("name").GetString()!).Where(n => !string.Equals(n, Coder, StringComparison.OrdinalIgnoreCase)).ToList();
                if (others.Count > 0)
                {
                    output.WriteLine($"NOT RUN: {string.Join(", ", others)} became resident; this test needs the GPU to itself. Run it again when Ollama is idle.");
                    return null;
                }

                var resident = await ReadAsync(probe, Coder);
                var rig = new LiveRig(output, resident.Count > 0 ? (resident[0].Context, resident[0].Pinned ? "-1" : "30m") : null);
                output.WriteLine(resident.Count > 0
                    ? $"Found {Coder} resident at {resident[0].Context} tokens, pinned {resident[0].Pinned}."
                    : $"{Coder} was not resident.");
                return rig;
            }
        }

        public async Task PinAsync(int context)
        {
            await UnloadAsync();
            await LoadAsync(context, "-1");
            _output.WriteLine($"Pinned {Coder} at {context} tokens as autocomplete does.");

            // A provider that knows nothing yet: it learns what is resident from /api/ps, as the host's does after a restart.
            _provider?.Dispose();
            var runtime = new ProviderRuntime(_progress, () => GpuTier.Gpu8GB, _ => null, TimeProvider.System);
            _provider = (OllamaProvider)new OllamaProviderFactory().Create(
                new ProviderSettings { Id = "ollama", Kind = ProviderKind.Ollama, Endpoint = LiveOllama.Endpoint },
                new ProviderCreationContext(null, ModelCatalog.Default, NullLoggerFactory.Instance, Coder, runtime));
        }

        public async Task<string> ChatAsync(string text, Profile profile, CancellationToken cancellationToken)
        {
            var updates = await _provider!.GetChatClient(Coder).GetStreamingResponseAsync(
                [new AiChatMessage(AiChatRole.User, text)],
                new ChatOptions
                {
                    Temperature = 0,
                    MaxOutputTokens = 16,
                    AdditionalProperties = new AdditionalPropertiesDictionary { [GatewayOptionKeys.Profile] = profile },
                },
                cancellationToken).CollectAsync(cancellationToken);
            return string.Concat(updates.Select(u => u.Text)).Trim();
        }

        public async Task<string> CompleteAsync(CancellationToken cancellationToken)
        {
            var pieces = await _provider!.GetFimClient(Coder)!.CompleteAsync(
                new FimRequest { Prefix = "def add(a, b):\n    return ", Suffix = "\n\nprint(add(1, 2))\n", MaxTokens = 16, Temperature = 0, Profile = Profile.LocalFast },
                cancellationToken).CollectAsync(cancellationToken);
            return string.Concat(pieces);
        }

        public async Task<List<Instance>> ResidentAsync(string model) => await ReadAsync(_http, model);

        public void Dispose()
        {
            _provider?.Dispose();
            try
            {
                RestoreAsync().GetAwaiter().GetResult();
            }
            finally
            {
                _http.Dispose();
            }
        }

        private static async Task<List<Instance>> ReadAsync(HttpClient http, string model)
        {
            using var ps = JsonDocument.Parse(await http.GetStringAsync("api/ps"));
            return
            [
                .. ps.RootElement.GetProperty("models").EnumerateArray()
                    .Where(m => string.Equals(m.GetProperty("name").GetString(), model, StringComparison.OrdinalIgnoreCase))
                    .Select(m => new Instance(
                        m.TryGetProperty("context_length", out var c) ? c.GetInt32() : null,
                        m.TryGetProperty("expires_at", out var e) && DateTimeOffset.TryParse(e.GetString()![..Math.Min(e.GetString()!.Length, 19)] + "Z", out var at) && at.Year >= 2100)),
            ];
        }

        private async Task RestoreAsync()
        {
            await UnloadAsync();
            if (_before is { } before)
            {
                await LoadAsync(before.Context ?? 4096, before.KeepAlive);
                _output.WriteLine($"Restored {Coder} at {before.Context} tokens, keep_alive {before.KeepAlive}.");
            }
            else
            {
                _output.WriteLine($"{Coder} was not resident when the test started; left unloaded.");
            }
        }

        private async Task UnloadAsync()
        {
            using var response = await _http.PostAsJsonAsync("api/generate", new JsonObject { ["model"] = Coder, ["prompt"] = string.Empty, ["stream"] = false, ["keep_alive"] = 0 });
            response.EnsureSuccessStatusCode();
            var deadline = Stopwatch.StartNew();
            while ((await ReadAsync(_http, Coder)).Count > 0)
            {
                Assert.True(deadline.Elapsed < TimeSpan.FromSeconds(30), $"{Coder} did not unload.");
                await Task.Delay(200);
            }
        }

        private async Task LoadAsync(int context, string keepAlive)
        {
            var body = new JsonObject
            {
                ["model"] = Coder,
                ["prompt"] = string.Empty,
                ["stream"] = false,
                ["options"] = new JsonObject { ["num_ctx"] = context },
            };
            OllamaProvider.SetKeepAlive(body, keepAlive);
            using var response = await _http.PostAsJsonAsync("api/generate", body);
            response.EnsureSuccessStatusCode();
        }
    }
}

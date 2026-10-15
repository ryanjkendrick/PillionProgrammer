using System.Globalization;
using System.Net.Http.Json;
using System.Text.Json;
using Pillion.ModelGateway.Providers.Bundled;

namespace Pillion.Eval.Models;

/// <summary>How an evaluated model is pinned (design section 15: local models by digest).</summary>
internal sealed record ModelIdentityInfo
{
    /// <summary>Gets the qualified model id.</summary>
    public required string QualifiedId { get; init; }

    /// <summary>Gets what the hash is (<c>sha256 of model_quantized.onnx</c>, <c>ollama digest</c>).</summary>
    public required string HashKind { get; init; }

    /// <summary>Gets the hash or digest.</summary>
    public required string Hash { get; init; }

    /// <summary>Gets where the model came from (the manifest's pinned URL, or the Ollama server).</summary>
    public string? Source { get; init; }

    /// <summary>Gets the licence, when the manifest names one.</summary>
    public string? License { get; init; }

    /// <summary>Gets details (quantisation, parameter size, file size) as text.</summary>
    public string? Details { get; init; }
}

/// <summary>Finds, downloads and identifies the evaluated models.</summary>
internal static class ModelIdentity
{
    /// <summary>Makes sure a bundled-provider model is on disk: the build copies the bundled one; others are downloaded once, from the manifest's pinned URLs, into the harness's models folder.</summary>
    /// <param name="model">The model.</param>
    /// <param name="modelsDirectory">The folder that receives downloads.</param>
    /// <param name="download">Whether a missing model may be downloaded.</param>
    /// <param name="log">Receives progress.</param>
    /// <param name="cancellationToken">Cancels the download.</param>
    /// <returns>A task.</returns>
    public static async Task EnsureBundledAsync(ModelSpec model, string modelsDirectory, bool download, TextWriter log, CancellationToken cancellationToken)
    {
        var manifestModel = ModelManifest.Default.Find(model.ModelId)
            ?? throw new InvalidOperationException($"The model manifest has no model '{model.ModelId}'.");
        using var runtime = new BundledModelRuntime(ModelManifest.Default, new BundledModelOptions
        {
            InstallModelsDirectory = Path.Combine(AppContext.BaseDirectory, "models"),
            UserModelsDirectory = modelsDirectory,
            ExecutionProvider = BundledExecutionProvider.Cpu,
        });
        if (runtime.Locate(manifestModel.Id, cancellationToken) is not null)
        {
            return;
        }

        if (!download)
        {
            throw new InvalidOperationException($"{model.QualifiedId} is not on disk (looked in the build output and {modelsDirectory}) and --no-download was given.");
        }

        var size = manifestModel.RequiredFiles.Sum(f => f.Size);
        await log.WriteLineAsync(string.Create(CultureInfo.InvariantCulture, $"  downloading {model.QualifiedId} ({size / 1048576.0:0} MB) into {modelsDirectory} from the manifest's pinned URLs...")).ConfigureAwait(false);
        var downloader = new ModelDownloadService(ModelManifest.Default, modelsDirectory);
        var lastPercent = -10;
        var progress = new SyncProgress<ModelDownloadProgress>(p =>
        {
            var percent = p.TotalBytes > 0 ? (int)(p.BytesReceived * 100 / p.TotalBytes) : 0;
            if (percent >= lastPercent + 10 || p.BytesReceived == p.TotalBytes)
            {
                lastPercent = percent;
                log.WriteLine(string.Create(CultureInfo.InvariantCulture, $"    {p.File}: {percent}%"));
            }
        });
        await downloader.DownloadAsync(manifestModel.Id, progress, includeOptional: false, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Identifies a bundled-provider model by the SHA-256 of its graph (verified against the manifest when the runtime located it).</summary>
    /// <param name="model">The model.</param>
    /// <param name="runtime">The runtime that loaded it.</param>
    /// <returns>The identity.</returns>
    public static ModelIdentityInfo DescribeBundled(ModelSpec model, BundledModelRuntime runtime)
    {
        var located = runtime.Locate(model.ModelId) ?? throw new InvalidOperationException($"{model.QualifiedId} is not on disk.");
        var graph = located.Model.Find(ModelFileRole.Model)!;
        return new ModelIdentityInfo
        {
            QualifiedId = model.QualifiedId,
            HashKind = "sha256 of " + graph.Path + " (verified against the manifest)",
            Hash = graph.Sha256,
            Source = graph.Url.ToString(),
            License = located.Model.License,
            Details = string.Create(CultureInfo.InvariantCulture, $"{located.Model.Dimensions} dimensions, {located.Model.Pooling} pooling, {located.Model.Tokenizer}, {graph.Size:N0} bytes, from {located.Directory}, execution provider {runtime.ExecutionProviderOf(model.ModelId)?.ToString() ?? "not loaded"}"),
        };
    }

    /// <summary>Identifies an Ollama model by the digest <c>/api/tags</c> reports.</summary>
    /// <param name="model">The model.</param>
    /// <param name="endpoint">The Ollama endpoint.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The identity.</returns>
    /// <exception cref="InvalidOperationException">Ollama does not answer or does not have the model.</exception>
    public static async Task<ModelIdentityInfo> DescribeOllamaAsync(ModelSpec model, string endpoint, CancellationToken cancellationToken)
    {
        using var http = new HttpClient { BaseAddress = new Uri(endpoint), Timeout = TimeSpan.FromSeconds(10) };
        JsonElement tags;
        try
        {
            tags = await http.GetFromJsonAsync<JsonElement>("api/tags", cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            throw new InvalidOperationException($"Ollama does not answer at {endpoint}api/tags: {ex.Message}", ex);
        }

        foreach (var entry in tags.GetProperty("models").EnumerateArray())
        {
            var name = entry.GetProperty("name").GetString() ?? string.Empty;
            if (SameOllamaModel(name, model.ModelId))
            {
                var details = entry.TryGetProperty("details", out var d) ? d : default;
                string Detail(string key) => details.ValueKind == JsonValueKind.Object && details.TryGetProperty(key, out var v) ? v.ToString() : "?";
                return new ModelIdentityInfo
                {
                    QualifiedId = model.QualifiedId,
                    HashKind = "ollama digest",
                    Hash = entry.GetProperty("digest").GetString() ?? "?",
                    Source = endpoint + " (" + name + ")",
                    Details = string.Create(CultureInfo.InvariantCulture, $"{Detail("family")}, {Detail("parameter_size")}, {Detail("quantization_level")}, {entry.GetProperty("size").GetInt64():N0} bytes"),
                };
            }
        }

        throw new InvalidOperationException($"Ollama at {endpoint} does not have {model.ModelId}; pull it with 'ollama pull {model.ModelId}'.");
    }

    /// <summary>Lists the models Ollama has loaded now.</summary>
    /// <param name="endpoint">The Ollama endpoint.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The model names, or empty when Ollama does not answer.</returns>
    public static async Task<IReadOnlyList<string>> OllamaResidentAsync(string endpoint, CancellationToken cancellationToken)
    {
        try
        {
            using var http = new HttpClient { BaseAddress = new Uri(endpoint), Timeout = TimeSpan.FromSeconds(10) };
            var ps = await http.GetFromJsonAsync<JsonElement>("api/ps", cancellationToken).ConfigureAwait(false);
            return [.. ps.GetProperty("models").EnumerateArray().Select(m => m.GetProperty("name").GetString() ?? string.Empty)];
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException or KeyNotFoundException)
        {
            return [];
        }
    }

    /// <summary>Describes where Ollama holds a model now (its context size and how much of it is in GPU memory), as <c>/api/ps</c> reports it.</summary>
    /// <param name="model">The model.</param>
    /// <param name="endpoint">The Ollama endpoint.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The description, or a note that the model is not loaded or Ollama did not answer.</returns>
    public static async Task<string> OllamaPlacementAsync(ModelSpec model, string endpoint, CancellationToken cancellationToken)
    {
        try
        {
            using var http = new HttpClient { BaseAddress = new Uri(endpoint), Timeout = TimeSpan.FromSeconds(10) };
            var ps = await http.GetFromJsonAsync<JsonElement>("api/ps", cancellationToken).ConfigureAwait(false);
            foreach (var entry in ps.GetProperty("models").EnumerateArray())
            {
                if (SameOllamaModel(entry.GetProperty("name").GetString() ?? string.Empty, model.ModelId))
                {
                    var size = entry.GetProperty("size").GetInt64();
                    var vram = entry.GetProperty("size_vram").GetInt64();
                    var context = entry.TryGetProperty("context_length", out var c) ? c.GetInt32() : 0;
                    return string.Create(CultureInfo.InvariantCulture, $"loaded at num_ctx {context}, {vram / 1048576.0:N0} of {size / 1048576.0:N0} MiB in GPU memory ({(vram == 0 ? "CPU" : vram >= size ? "GPU" : "split")})");
                }
            }

            return "not loaded when checked";
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException or KeyNotFoundException or InvalidOperationException)
        {
            return "placement unknown (" + ex.Message + ")";
        }
    }

    /// <summary>Unloads an Ollama embedding model (<c>/api/embed</c> with <c>keep_alive</c> 0), leaving other models alone.</summary>
    /// <param name="model">The model.</param>
    /// <param name="endpoint">The Ollama endpoint.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>A task.</returns>
    public static async Task UnloadOllamaAsync(ModelSpec model, string endpoint, CancellationToken cancellationToken)
    {
        try
        {
            using var http = new HttpClient { BaseAddress = new Uri(endpoint), Timeout = TimeSpan.FromSeconds(30) };
            using var response = await http.PostAsJsonAsync("api/embed", new { model = model.ModelId, input = Array.Empty<string>(), keep_alive = 0 }, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            // Best effort: Ollama unloads it after its keep-alive anyway.
        }
    }

    /// <summary>Compares Ollama model names, treating no tag as <c>:latest</c>.</summary>
    /// <param name="a">One name.</param>
    /// <param name="b">The other.</param>
    /// <returns>Whether they name the same model.</returns>
    public static bool SameOllamaModel(string a, string b)
    {
        static string Tagged(string name) => name.Contains(':', StringComparison.Ordinal) ? name : name + ":latest";
        return string.Equals(Tagged(a), Tagged(b), StringComparison.OrdinalIgnoreCase);
    }

    private sealed class SyncProgress<T>(Action<T> report) : IProgress<T>
    {
        public void Report(T value) => report(value);
    }
}

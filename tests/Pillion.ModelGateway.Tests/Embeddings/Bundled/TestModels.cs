using Pillion.Common.Hardware;
using Pillion.ModelGateway.Providers.Bundled;

namespace Pillion.ModelGateway.Tests.Embeddings.Bundled;

/// <summary>
/// Finds the real model files for the tests that need them: the bundled model in the build output (the build downloads and
/// verifies it into <c>models\</c>), else a shared download folder under %TEMP% that <see cref="ModelDownloadService"/> fills
/// from the manifest's URLs, the same path the host uses for models on request.
/// </summary>
internal static class TestModels
{
    public const string Bge = "bge-small-en-v1.5";
    public const string E5 = "multilingual-e5-small";
    public const string Jina = "jina-embeddings-v2-base-code";

    /// <summary>The strings the reference token ids were taken for, with the Hugging Face tokenizers library (0.22.1) on each model's tokenizer.json.</summary>
    public static readonly string[] ReferenceTexts =
    [
        "Hello world.",
        "public async Task<EmbeddingResult> EmbedAsync(EmbeddingRequest request, CancellationToken cancellationToken)",
        "def parse_config(path: str) -> dict:\n    return json.load(open(path))",
        "Café naïve résumé über",
        "你好世界 means hello world",
        "if (x != null && y >= 10) { return x?.Value ?? 0; }",
        "getHTTPResponseCode snake_case_name kebab-case-name",
    ];

    private static readonly SemaphoreSlim DownloadGate = new(1, 1);

    public static string BuildOutputRoot => Path.Combine(AppContext.BaseDirectory, "models");

    public static string DownloadRoot => Path.Combine(Path.GetTempPath(), "pillion-model-tests", "models");

    /// <summary>Gets the root holding a verified copy of the bundled model in the build output; fails the test when the build skipped it.</summary>
    public static string RequireBuildOutput()
    {
        var model = ModelManifest.Default.Find(Bge)!;
        Assert.True(
            new BundledModelLocator([BuildOutputRoot]).Locate(model, CancellationToken.None) is not null,
            $"The bundled model is not in the build output ({BuildOutputRoot}). Build without -p:PillionSkipModelDownload=true.");
        return BuildOutputRoot;
    }

    /// <summary>Gets a root holding a verified copy of a model, downloading it through <see cref="ModelDownloadService"/> when needed.</summary>
    public static async Task<string> EnsureAsync(string modelId)
    {
        var model = ModelManifest.Default.Find(modelId)!;
        if (new BundledModelLocator([BuildOutputRoot]).Locate(model, CancellationToken.None) is not null)
        {
            return BuildOutputRoot;
        }

        await DownloadGate.WaitAsync();
        try
        {
            await new ModelDownloadService(ModelManifest.Default, DownloadRoot).DownloadAsync(modelId);
            return DownloadRoot;
        }
        finally
        {
            DownloadGate.Release();
        }
    }

    /// <summary>Creates a runtime over one models folder, with nothing released while idle.</summary>
    public static BundledModelRuntime Runtime(string root, BundledExecutionProvider executionProvider = BundledExecutionProvider.Cpu, Action<BundledModelOptions>? configure = null)
    {
        var options = new BundledModelOptions
        {
            InstallModelsDirectory = root,
            UserModelsDirectory = null,
            ExecutionProvider = executionProvider,
            IdleUnloadAfter = TimeSpan.Zero,
        };
        configure?.Invoke(options);
        return new BundledModelRuntime(ModelManifest.Default, options, () => GpuTier.Gpu8GB);
    }
}

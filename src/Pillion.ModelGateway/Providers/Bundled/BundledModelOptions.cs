namespace Pillion.ModelGateway.Providers.Bundled;

/// <summary>Which ONNX Runtime execution provider the bundled models run on.</summary>
public enum BundledExecutionProvider
{
    /// <summary>DirectML when the GPU tier (the <c>models.gpuTier</c> setting, else the detected tier) reports a GPU, else CPU.</summary>
    Auto,

    /// <summary>Always the CPU.</summary>
    Cpu,

    /// <summary>DirectML whatever the tier, falling back to the CPU when it cannot start.</summary>
    DirectML,
}

/// <summary>Tunables of the bundled embedding provider (work item 2.7). The defaults follow design sections 7 and 13.</summary>
public sealed class BundledModelOptions
{
    /// <summary>
    /// Gets or sets the folder of the models shipped with the extension: <c>models</c> beside the host executable (in the VSIX,
    /// <c>Host\models</c>). Searched first.
    /// </summary>
    public string InstallModelsDirectory { get; set; } = Path.Combine(AppContext.BaseDirectory, "models");

    /// <summary>
    /// Gets or sets the folder of the models downloaded on request (<c>%LOCALAPPDATA%\PillionProgrammer\models</c>), searched
    /// after <see cref="InstallModelsDirectory"/>; <see langword="null"/> disables it.
    /// </summary>
    public string? UserModelsDirectory { get; set; } = DefaultUserModelsDirectory();

    /// <summary>
    /// Gets or sets the ONNX Runtime threads for one inference. Default: half the logical processors, at least one (design
    /// section 13: indexing uses at most half the cores; the host lowers its own priority).
    /// </summary>
    public int IntraOpThreads { get; set; } = Math.Max(1, Environment.ProcessorCount / 2);

    /// <summary>Gets or sets which execution provider to use. Default <see cref="BundledExecutionProvider.Auto"/>.</summary>
    public BundledExecutionProvider ExecutionProvider { get; set; } = BundledExecutionProvider.Auto;

    /// <summary>Gets or sets the DirectML adapter index (0 is the default adapter). Default 0.</summary>
    public int DirectMLDeviceId { get; set; }

    /// <summary>
    /// Gets or sets the longest input in tokens any model reads, special tokens included, whatever its own limit. Jina's code
    /// model accepts 8,192 tokens, but attention grows with the square of the length; the chunker never makes chunks that long.
    /// Default 2,048.
    /// </summary>
    public int MaxSequenceTokens { get; set; } = 2048;

    /// <summary>
    /// Gets or sets the attention budget of one inference: texts per run times the square of the padded length. A batch is
    /// split into runs that stay within it, so that memory does not grow with the square of a long input (design section 13,
    /// 1.5 GB while indexing). Default 32 x 512 x 512: a full batch of 32 at the bundled model's 512 tokens in one run.
    /// </summary>
    public long MaxAttentionCells { get; set; } = 32L * 512 * 512;

    /// <summary>Gets or sets how long a loaded model may sit unused before its session is released (the host's idle budget is 150 MB). Default 5 minutes; <see cref="TimeSpan.Zero"/> never releases.</summary>
    public TimeSpan IdleUnloadAfter { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>Gets the default <see cref="UserModelsDirectory"/>: <c>%LOCALAPPDATA%\PillionProgrammer\models</c>, or <see langword="null"/> when there is no local application data folder.</summary>
    /// <returns>The folder.</returns>
    public static string? DefaultUserModelsDirectory()
    {
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        return string.IsNullOrEmpty(localAppData) ? null : Path.Combine(localAppData, "PillionProgrammer", "models");
    }
}

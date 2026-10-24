namespace Pillion.Eval.Models;

/// <summary>An embedding model the harness evaluates: <c>bundled/&lt;manifest id&gt;</c> or <c>ollama/&lt;model&gt;</c>.</summary>
/// <param name="Provider">The provider kind (<see cref="Bundled"/> or <see cref="Ollama"/>), which is also the provider id the harness configures.</param>
/// <param name="ModelId">The model id within the provider.</param>
internal sealed record ModelSpec(string Provider, string ModelId)
{
    /// <summary>The bundled ONNX provider.</summary>
    public const string Bundled = "bundled";

    /// <summary>A local Ollama server.</summary>
    public const string Ollama = "ollama";

    /// <summary>Gets the qualified id the gateway uses (<c>provider/model</c>).</summary>
    public string QualifiedId => Provider + "/" + ModelId;

    /// <summary>Gets a folder-safe name for output files.</summary>
    public string FileSafeName => string.Concat(QualifiedId.Select(c => char.IsLetterOrDigit(c) || c is '-' or '.' ? c : '_'));

    /// <summary>Parses a model spec.</summary>
    /// <param name="text">For example <c>bundled/bge-small-en-v1.5</c> or <c>ollama/qwen3-embedding:0.6b</c>.</param>
    /// <returns>The spec.</returns>
    /// <exception cref="FormatException">The text is not <c>bundled/…</c> or <c>ollama/…</c>.</exception>
    public static ModelSpec Parse(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var slash = text.IndexOf('/', StringComparison.Ordinal);
        if (slash <= 0 || slash == text.Length - 1)
        {
            throw new FormatException($"'{text}' is not a model spec: use bundled/<model> or ollama/<model>.");
        }

        var provider = text[..slash].Trim().ToLowerInvariant();
        var model = text[(slash + 1)..].Trim();
        if (provider is not (Bundled or Ollama))
        {
            throw new FormatException($"'{text}': the provider must be '{Bundled}' or '{Ollama}'.");
        }

        return new ModelSpec(provider, model);
    }

    /// <inheritdoc/>
    public override string ToString() => QualifiedId;
}

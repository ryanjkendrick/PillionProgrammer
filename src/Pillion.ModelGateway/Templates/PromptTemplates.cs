using System.Text.Json;
using System.Text.RegularExpressions;

namespace Pillion.ModelGateway;

/// <summary>A fill-in-the-middle token format for one model family.</summary>
/// <param name="Family">The family name, for example <c>qwen2.5-coder</c>.</param>
/// <param name="Match">The model id pattern (<c>*</c> is a wildcard).</param>
/// <param name="Template">The prompt template with <c>{prefix}</c> and <c>{suffix}</c> markers.</param>
/// <param name="StopSequences">Stop sequences for the family.</param>
public sealed record FimFormat(string Family, string Match, string Template, IReadOnlyList<string> StopSequences)
{
    /// <summary>Builds the raw prompt for a server that does not apply the template itself.</summary>
    /// <param name="prefix">The text before the caret.</param>
    /// <param name="suffix">The text after the caret.</param>
    /// <returns>The prompt.</returns>
    public string Render(string prefix, string suffix)
    {
        ArgumentNullException.ThrowIfNull(prefix);
        ArgumentNullException.ThrowIfNull(suffix);

        // Single pass so a prefix containing "{suffix}" is not expanded.
        return Regex.Replace(Template, @"\{prefix\}|\{suffix\}", m => m.Value == "{prefix}" ? prefix : suffix, RegexOptions.CultureInvariant);
    }
}

/// <summary>
/// The prompt templates shipped as data under <c>Templates/</c> (design section 6): system prompt variants and FIM
/// token formats per family. Ollama applies its own FIM template through <c>suffix</c>, so the formats are only data
/// until an adapter for a raw server needs them.
/// </summary>
public sealed class PromptTemplates
{
    private readonly Dictionary<string, string> _systemPrompts;
    private readonly List<(Regex Pattern, FimFormat Format)> _fimFormats;

    private PromptTemplates(Dictionary<string, string> systemPrompts, List<(Regex, FimFormat)> fimFormats, int defaultMaxTokens, float defaultTemperature, IReadOnlyList<string> defaultStops)
    {
        _systemPrompts = systemPrompts;
        _fimFormats = fimFormats;
        DefaultFimMaxTokens = defaultMaxTokens;
        DefaultFimTemperature = defaultTemperature;
        DefaultFimStopSequences = defaultStops;
    }

    /// <summary>Gets the templates shipped in this assembly.</summary>
    public static PromptTemplates Default { get; } = LoadEmbedded();

    /// <summary>Gets the plain chat system prompt (phase 0 uses only this variant).</summary>
    public string ChatSystemPrompt => GetSystemPrompt("chat");

    /// <summary>Gets the default number of tokens for a FIM completion when the request does not set one.</summary>
    public int DefaultFimMaxTokens { get; }

    /// <summary>Gets the default FIM sampling temperature.</summary>
    public float DefaultFimTemperature { get; }

    /// <summary>Gets the stop sequences added to every FIM request.</summary>
    public IReadOnlyList<string> DefaultFimStopSequences { get; }

    /// <summary>Gets the FIM formats in match order.</summary>
    public IReadOnlyList<FimFormat> FimFormats => _fimFormats.Select(f => f.Format).ToList();

    /// <summary>Gets a system prompt variant by name (<c>chat</c>, <c>chatToolCapable</c>, <c>chatTextProtocol</c>).</summary>
    /// <param name="name">The variant name.</param>
    /// <returns>The prompt text.</returns>
    /// <exception cref="KeyNotFoundException">There is no such variant.</exception>
    public string GetSystemPrompt(string name) =>
        _systemPrompts.TryGetValue(name, out var prompt) ? prompt : throw new KeyNotFoundException($"There is no system prompt named '{name}'.");

    /// <summary>Finds the FIM format for a model.</summary>
    /// <param name="modelId">The model id.</param>
    /// <returns>The format, or <see langword="null"/> when no family matches.</returns>
    public FimFormat? FindFimFormat(string modelId)
    {
        ArgumentNullException.ThrowIfNull(modelId);
        var name = modelId[(modelId.LastIndexOf('/') + 1)..];
        return _fimFormats.FirstOrDefault(f => f.Pattern.IsMatch(name)).Format;
    }

    /// <summary>Parses the two template documents.</summary>
    /// <param name="systemPromptsJson">The contents of <c>system-prompts.json</c>.</param>
    /// <param name="fimFormatsJson">The contents of <c>fim-formats.json</c>.</param>
    /// <returns>The templates.</returns>
    /// <exception cref="FormatException">A document is not valid.</exception>
    public static PromptTemplates Parse(string systemPromptsJson, string fimFormatsJson)
    {
        try
        {
            using var prompts = JsonDocument.Parse(systemPromptsJson);
            var systemPrompts = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var property in prompts.RootElement.GetProperty("prompts").EnumerateObject())
            {
                systemPrompts[property.Name] = property.Value.GetString() ?? string.Empty;
            }

            if (!systemPrompts.ContainsKey("chat"))
            {
                throw new FormatException("system-prompts.json must define the 'chat' prompt.");
            }

            using var fim = JsonDocument.Parse(fimFormatsJson);
            var formats = new List<(Regex, FimFormat)>();
            foreach (var entry in fim.RootElement.GetProperty("formats").EnumerateArray())
            {
                var match = entry.GetProperty("match").GetString()!;
                var format = new FimFormat(
                    entry.GetProperty("family").GetString()!,
                    match,
                    entry.GetProperty("template").GetString()!,
                    entry.GetProperty("stop").EnumerateArray().Select(s => s.GetString()!).ToList());
                var regex = new Regex("^" + Regex.Escape(match).Replace("\\*", ".*", StringComparison.Ordinal) + "$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
                formats.Add((regex, format));
            }

            var defaults = fim.RootElement.GetProperty("defaults");
            return new PromptTemplates(
                systemPrompts,
                formats,
                defaults.GetProperty("maxTokens").GetInt32(),
                defaults.GetProperty("temperature").GetSingle(),
                defaults.GetProperty("stop").EnumerateArray().Select(s => s.GetString()!).ToList());
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException)
        {
            throw new FormatException($"A prompt template file is not valid: {ex.Message}", ex);
        }
    }

    private static PromptTemplates LoadEmbedded() =>
        Parse(ReadResource("Pillion.ModelGateway.Templates.system-prompts.json"), ReadResource("Pillion.ModelGateway.Templates.fim-formats.json"));

    private static string ReadResource(string name)
    {
        using var stream = typeof(PromptTemplates).Assembly.GetManifestResourceStream(name)
            ?? throw new InvalidOperationException($"The embedded resource {name} is missing.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}

using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Pillion.Common.Settings;
using Pillion.Contracts;

namespace Pillion.ModelGateway;

/// <summary>
/// The model catalog (design section 6): the default model id for each profile per provider kind, and capability
/// descriptors for known model families. Loaded from the embedded <c>catalog.json</c>; the catalog is data, not code.
/// <c>routing.profiles</c> in settings overrides the defaults.
/// </summary>
public sealed class ModelCatalog
{
    private const string ResourceName = "Pillion.ModelGateway.catalog.json";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    private readonly Dictionary<ProviderKind, Dictionary<Profile, string>> _profiles;
    private readonly List<(Regex Pattern, string Match, ModelCapabilityDescriptor Descriptor)> _families;

    private ModelCatalog(Dictionary<ProviderKind, Dictionary<Profile, string>> profiles, List<(Regex, string, ModelCapabilityDescriptor)> families)
    {
        _profiles = profiles;
        _families = families;
    }

    /// <summary>Gets the catalog shipped in this assembly.</summary>
    public static ModelCatalog Default { get; } = LoadEmbedded();

    /// <summary>Gets the family patterns in match order (for diagnostics and tests).</summary>
    public IReadOnlyList<string> FamilyPatterns => _families.Select(f => f.Match).ToList();

    /// <summary>Parses a catalog document.</summary>
    /// <param name="json">The catalog JSON.</param>
    /// <returns>The catalog.</returns>
    /// <exception cref="FormatException">The document is not a valid catalog.</exception>
    public static ModelCatalog Parse(string json)
    {
        ArgumentNullException.ThrowIfNull(json);

        CatalogDocument document;
        try
        {
            document = JsonSerializer.Deserialize<CatalogDocument>(json, JsonOptions)
                ?? throw new FormatException("The model catalog is empty.");
        }
        catch (JsonException ex)
        {
            throw new FormatException($"The model catalog is not valid JSON: {ex.Message}", ex);
        }

        var profiles = new Dictionary<ProviderKind, Dictionary<Profile, string>>();
        foreach (var (kindName, map) in document.Profiles ?? [])
        {
            if (!ProviderKinds.TryParse(kindName, out var kind))
            {
                throw new FormatException($"The model catalog names an unknown provider kind '{kindName}'.");
            }

            var byProfile = new Dictionary<Profile, string>();
            foreach (var (profileName, modelId) in map ?? [])
            {
                if (!Enum.TryParse<Profile>(profileName.Replace("-", string.Empty, StringComparison.Ordinal), ignoreCase: true, out var profile) || profile == Profile.Auto)
                {
                    throw new FormatException($"The model catalog names an unknown profile '{profileName}'.");
                }

                if (!string.IsNullOrWhiteSpace(modelId))
                {
                    byProfile[profile] = modelId;
                }
            }

            profiles[kind] = byProfile;
        }

        var families = new List<(Regex, string, ModelCapabilityDescriptor)>();
        foreach (var family in document.Families ?? [])
        {
            if (string.IsNullOrWhiteSpace(family.Match))
            {
                throw new FormatException("Every model family in the catalog needs a 'match' pattern.");
            }

            families.Add((ToRegex(family.Match), family.Match, family.ToDescriptor()));
        }

        if (families.Count == 0 || families[^1].Item2 != "*")
        {
            throw new FormatException("The last model family in the catalog must be the '*' default.");
        }

        return new ModelCatalog(profiles, families);
    }

    /// <summary>Gets the catalog's default model for a profile and provider kind.</summary>
    /// <param name="kind">The provider kind.</param>
    /// <param name="profile">The profile.</param>
    /// <returns>The model id, or <see langword="null"/> when the catalog names none.</returns>
    public string? GetDefaultModel(ProviderKind kind, Profile profile) =>
        _profiles.TryGetValue(kind, out var map) && map.TryGetValue(profile, out var model) ? model : null;

    /// <summary>Gets the capability descriptor of the first family whose pattern matches the model id.</summary>
    /// <param name="modelId">The provider's model id; an Ollama namespace such as <c>library/</c> or a registry host is ignored.</param>
    /// <param name="isLocal">Whether the provider serving it is local (local models cost nothing).</param>
    /// <returns>The descriptor.</returns>
    public ModelCapabilityDescriptor GetCapabilities(string modelId, bool isLocal)
    {
        ArgumentNullException.ThrowIfNull(modelId);

        var name = modelId;
        var slash = name.LastIndexOf('/');
        if (slash >= 0)
        {
            name = name[(slash + 1)..];
        }

        foreach (var (pattern, _, descriptor) in _families)
        {
            if (pattern.IsMatch(name))
            {
                return isLocal
                    ? descriptor with { IsLocal = true, InputCostPerMillionTokens = 0, OutputCostPerMillionTokens = 0 }
                    : descriptor with { IsLocal = false };
            }
        }

        return ModelCapabilityDescriptor.Unknown with { IsLocal = isLocal };
    }

    private static ModelCatalog LoadEmbedded()
    {
        using var stream = typeof(ModelCatalog).Assembly.GetManifestResourceStream(ResourceName)
            ?? throw new InvalidOperationException($"The embedded resource {ResourceName} is missing.");
        using var reader = new StreamReader(stream);
        return Parse(reader.ReadToEnd());
    }

    private static Regex ToRegex(string glob) =>
        new("^" + Regex.Escape(glob).Replace("\\*", ".*", StringComparison.Ordinal) + "$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private sealed class CatalogDocument
    {
        public int Version { get; set; }

        public Dictionary<string, Dictionary<string, string>?>? Profiles { get; set; }

        public List<FamilyEntry>? Families { get; set; }
    }

    private sealed class FamilyEntry
    {
        public string Match { get; set; } = string.Empty;

        public int ContextWindow { get; set; } = 4096;

        public int MaxOutputTokens { get; set; } = 2048;

        public bool Chat { get; set; } = true;

        public bool Fim { get; set; }

        public bool Vision { get; set; }

        public bool Embeddings { get; set; }

        public bool Streaming { get; set; } = true;

        public bool PromptCaching { get; set; }

        public bool ReasoningEffort { get; set; }

        public ToolCallingSupport ToolCalling { get; set; }

        public decimal InputCostPerMillionTokens { get; set; }

        public decimal OutputCostPerMillionTokens { get; set; }

        public ModelCapabilityDescriptor ToDescriptor() => new()
        {
            ContextWindow = ContextWindow,
            MaxOutputTokens = MaxOutputTokens,
            Chat = Chat,
            Fim = Fim,
            Vision = Vision,
            Embeddings = Embeddings,
            Streaming = Streaming,
            PromptCaching = PromptCaching,
            ReasoningEffort = ReasoningEffort,
            ToolCalling = ToolCalling,
            InputCostPerMillionTokens = InputCostPerMillionTokens,
            OutputCostPerMillionTokens = OutputCostPerMillionTokens,
        };
    }
}

/// <summary>Converts <see cref="ProviderKind"/> to and from its <c>settings.json</c> name (for example <c>openai-compatible</c>).</summary>
public static class ProviderKinds
{
    private static readonly Dictionary<ProviderKind, string> Names = Enum.GetValues<ProviderKind>().ToDictionary(
        kind => kind,
        kind => typeof(ProviderKind).GetField(kind.ToString())!.GetCustomAttribute<JsonStringEnumMemberNameAttribute>()?.Name ?? kind.ToString());

    /// <summary>Gets the settings name of a kind.</summary>
    /// <param name="kind">The kind.</param>
    /// <returns>The name, for example <c>ollama</c>.</returns>
    public static string ToName(ProviderKind kind) => Names[kind];

    /// <summary>Parses a settings name (or the enum member name).</summary>
    /// <param name="name">The name.</param>
    /// <param name="kind">The kind, when the name is known.</param>
    /// <returns><see langword="true"/> when the name is known.</returns>
    public static bool TryParse(string name, out ProviderKind kind)
    {
        foreach (var (candidate, wireName) in Names)
        {
            if (string.Equals(wireName, name, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(candidate.ToString(), name, StringComparison.OrdinalIgnoreCase))
            {
                kind = candidate;
                return true;
            }
        }

        kind = default;
        return false;
    }
}

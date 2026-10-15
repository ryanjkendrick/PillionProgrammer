using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Pillion.Common.Hardware;
using Pillion.Common.Settings;
using Pillion.Contracts;

namespace Pillion.ModelGateway;

/// <summary>
/// What a local model costs in GPU memory and how much context it is recommended to keep resident per GPU tier
/// (design section 6, Local model residency). The numbers are estimates for the default quantisation (Q4_K_M): the weights
/// plus the runtime's fixed overhead, and the key/value cache per 1,024 tokens of context at the default f16 cache type.
/// </summary>
/// <param name="WeightsMiB">The memory a loaded instance takes before any context, in MiB.</param>
/// <param name="KvMiBPer1kTokens">The key/value cache per 1,024 tokens of <c>num_ctx</c>, in MiB.</param>
/// <param name="RecommendedContext">The largest <c>num_ctx</c> to keep resident per tier when the model is shared between autocomplete and chat.</param>
public sealed record ModelResidencyEstimate(int WeightsMiB, double KvMiBPer1kTokens, IReadOnlyDictionary<GpuTier, int> RecommendedContext)
{
    /// <summary>Estimates the memory one instance takes at a context size.</summary>
    /// <param name="contextTokens">The <c>num_ctx</c>.</param>
    /// <returns>The estimate in MiB.</returns>
    public int EstimateMiB(int contextTokens) => WeightsMiB + (int)Math.Ceiling(KvMiBPer1kTokens * contextTokens / 1024.0);

    /// <summary>Gets the largest context one instance can have within a memory budget.</summary>
    /// <param name="budgetMiB">The memory available to the instance.</param>
    /// <returns>The context size in tokens (0 when the weights alone do not fit; <see cref="int.MaxValue"/> when the cache costs nothing).</returns>
    public int MaxContextWithin(int budgetMiB)
    {
        var room = budgetMiB - WeightsMiB;
        if (room <= 0)
        {
            return 0;
        }

        return KvMiBPer1kTokens <= 0 ? int.MaxValue : (int)Math.Min(int.MaxValue, room / KvMiBPer1kTokens * 1024.0);
    }

    /// <summary>Gets the recommended resident context for a tier.</summary>
    /// <param name="tier">The tier.</param>
    /// <param name="fallback">The size to return when the catalog names none for the tier.</param>
    /// <returns>The size in tokens.</returns>
    public int RecommendedContextFor(GpuTier tier, int fallback) =>
        RecommendedContext.TryGetValue(tier, out var tokens) ? tokens : fallback;
}

/// <summary>
/// What the catalog knows about an embedding model (design section 7, Embedding models). The figures are data: where they
/// were measured or taken from is in the catalog entry's note.
/// </summary>
/// <param name="Dimensions">The vector size; 0 when it depends on the variant and is read from the first response.</param>
/// <param name="MaxInputTokens">The most tokens the model reads from one text.</param>
/// <param name="TokenizerFamily">The tokenizer family the chunk cap is expressed in (<c>qwen3-bpe</c>, <c>bert-wordpiece</c>, <c>openai-cl100k</c>, ...).</param>
/// <param name="License">The licence of the weights, or the terms of the hosted service.</param>
/// <param name="SupportsDimensions">Whether the model can be asked for shorter vectors.</param>
/// <param name="DocumentPrefix">Text the model expects before a passage (empty when none).</param>
/// <param name="QueryPrefix">Text the model expects before a search query (empty when none).</param>
/// <param name="Note">Where the figures come from.</param>
public sealed record EmbeddingModelEntry(
    int Dimensions,
    int MaxInputTokens,
    string TokenizerFamily,
    string License,
    bool SupportsDimensions,
    string DocumentPrefix,
    string QueryPrefix,
    string? Note);

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
    private readonly Dictionary<ProviderKind, IReadOnlyList<string>> _embeddingDefaults;
    private readonly IReadOnlyList<ProviderKind> _embeddingProviderOrder;
    private readonly List<Family> _families;

    private ModelCatalog(Dictionary<ProviderKind, Dictionary<Profile, string>> profiles, Dictionary<ProviderKind, IReadOnlyList<string>> embeddingDefaults, IReadOnlyList<ProviderKind> embeddingProviderOrder, List<Family> families)
    {
        _profiles = profiles;
        _embeddingDefaults = embeddingDefaults;
        _embeddingProviderOrder = embeddingProviderOrder;
        _families = families;
    }

    private sealed record Family(Regex Pattern, string Match, ModelCapabilityDescriptor Descriptor, ModelResidencyEstimate? Residency, EmbeddingModelEntry? Embedding);

    /// <summary>Gets the catalog shipped in this assembly.</summary>
    public static ModelCatalog Default { get; } = LoadEmbedded();

    /// <summary>Gets the family patterns in match order (for diagnostics and tests).</summary>
    public IReadOnlyList<string> FamilyPatterns => _families.Select(f => f.Match).ToList();

    /// <summary>
    /// Gets the embedding models the Embedding task class prefers for a provider kind, in order. Which of them is used depends on
    /// what the provider has installed or allows; <c>routing.profiles</c> and an explicit model id override them.
    /// </summary>
    /// <param name="kind">The provider kind.</param>
    /// <returns>The model ids; empty when the catalog names none.</returns>
    public IReadOnlyList<string> GetDefaultEmbeddingModels(ProviderKind kind) =>
        _embeddingDefaults.TryGetValue(kind, out var models) ? models : [];

    /// <summary>Gets the provider kinds the Embedding task class tries first, in order, before the other providers in settings order.</summary>
    public IReadOnlyList<ProviderKind> EmbeddingProviderOrder => _embeddingProviderOrder;

    /// <summary>
    /// Gets where a provider kind stands in <see cref="EmbeddingProviderOrder"/>: its position, or <see cref="int.MaxValue"/> for a
    /// kind the list does not name (those keep their settings order). A model named by <c>routing.profiles</c> or an explicit
    /// model id still comes first.
    /// </summary>
    /// <param name="kind">The provider kind.</param>
    /// <returns>The priority; lower is tried first.</returns>
    public int GetEmbeddingProviderPriority(ProviderKind kind)
    {
        for (var i = 0; i < _embeddingProviderOrder.Count; i++)
        {
            if (_embeddingProviderOrder[i] == kind)
            {
                return i;
            }
        }

        return int.MaxValue;
    }

    /// <summary>Gets what the catalog knows about an embedding model: dimensions, input limit, tokenizer family, licence and prefixes.</summary>
    /// <param name="modelId">The provider's model id; an Ollama namespace such as <c>library/</c> or a registry host is ignored.</param>
    /// <returns>The entry, or <see langword="null"/> when the matching family has none (it is not an embedding model, or the catalog does not describe it).</returns>
    public EmbeddingModelEntry? GetEmbeddingInfo(string modelId)
    {
        ArgumentNullException.ThrowIfNull(modelId);
        var name = StripNamespace(modelId);
        return _families.FirstOrDefault(f => f.Pattern.IsMatch(name))?.Embedding;
    }

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

        var embeddingDefaults = new Dictionary<ProviderKind, IReadOnlyList<string>>();
        foreach (var (kindName, models) in document.EmbeddingDefaults ?? [])
        {
            if (!ProviderKinds.TryParse(kindName, out var kind))
            {
                throw new FormatException($"The model catalog names an unknown provider kind '{kindName}' in embeddingDefaults.");
            }

            embeddingDefaults[kind] = [.. (models ?? []).Where(m => !string.IsNullOrWhiteSpace(m))];
        }

        var embeddingProviderOrder = new List<ProviderKind>();
        foreach (var kindName in document.EmbeddingProviderOrder ?? [])
        {
            if (!ProviderKinds.TryParse(kindName, out var kind))
            {
                throw new FormatException($"The model catalog names an unknown provider kind '{kindName}' in embeddingProviderOrder.");
            }

            if (!embeddingProviderOrder.Contains(kind))
            {
                embeddingProviderOrder.Add(kind);
            }
        }

        var families = new List<Family>();
        foreach (var family in document.Families ?? [])
        {
            if (string.IsNullOrWhiteSpace(family.Match))
            {
                throw new FormatException("Every model family in the catalog needs a 'match' pattern.");
            }

            families.Add(new Family(ToRegex(family.Match), family.Match, family.ToDescriptor(), family.Residency?.ToEstimate(family.Match), family.Embedding?.ToEntry(family.Match)));
        }

        if (families.Count == 0 || families[^1].Match != "*")
        {
            throw new FormatException("The last model family in the catalog must be the '*' default.");
        }

        return new ModelCatalog(profiles, embeddingDefaults, embeddingProviderOrder, families);
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

        var name = StripNamespace(modelId);
        foreach (var family in _families)
        {
            if (family.Pattern.IsMatch(name))
            {
                return isLocal
                    ? family.Descriptor with { IsLocal = true, InputCostPerMillionTokens = 0, OutputCostPerMillionTokens = 0 }
                    : family.Descriptor with { IsLocal = false };
            }
        }

        return ModelCapabilityDescriptor.Unknown with { IsLocal = isLocal };
    }

    private static string StripNamespace(string modelId)
    {
        var slash = modelId.LastIndexOf('/');
        return slash >= 0 ? modelId[(slash + 1)..] : modelId;
    }

    /// <summary>Gets the memory estimate of the first family whose pattern matches the model id.</summary>
    /// <param name="modelId">The provider's model id.</param>
    /// <returns>The estimate, or <see langword="null"/> when the matching family has none (the estimate is only known for the local coder families).</returns>
    public ModelResidencyEstimate? GetResidency(string modelId)
    {
        ArgumentNullException.ThrowIfNull(modelId);

        var name = StripNamespace(modelId);
        return _families.FirstOrDefault(f => f.Pattern.IsMatch(name))?.Residency;
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

        public Dictionary<string, List<string>?>? EmbeddingDefaults { get; set; }

        public List<string>? EmbeddingProviderOrder { get; set; }

        public List<FamilyEntry>? Families { get; set; }
    }

    private sealed class EmbeddingEntry
    {
        public int Dimensions { get; set; }

        public int MaxInputTokens { get; set; }

        public string? TokenizerFamily { get; set; }

        public string? License { get; set; }

        public bool SupportsDimensions { get; set; }

        public string? DocumentPrefix { get; set; }

        public string? QueryPrefix { get; set; }

        public string? Note { get; set; }

        public EmbeddingModelEntry ToEntry(string family)
        {
            if (Dimensions < 0 || MaxInputTokens <= 0)
            {
                throw new FormatException($"The embedding entry of model family '{family}' needs a maxInputTokens above 0 and a dimensions of 0 or more.");
            }

            return new EmbeddingModelEntry(
                Dimensions,
                MaxInputTokens,
                string.IsNullOrWhiteSpace(TokenizerFamily) ? EmbeddingModelInfo.UnknownTokenizer : TokenizerFamily,
                License ?? string.Empty,
                SupportsDimensions,
                DocumentPrefix ?? string.Empty,
                QueryPrefix ?? string.Empty,
                Note);
        }
    }

    private sealed class ResidencyEntry
    {
        public int VramMiB { get; set; }

        public double KvMiBPer1kTokens { get; set; }

        public Dictionary<string, int>? RecommendedContext { get; set; }

        public ModelResidencyEstimate ToEstimate(string family)
        {
            if (VramMiB <= 0 || KvMiBPer1kTokens < 0)
            {
                throw new FormatException($"The residency of model family '{family}' needs a positive vramMiB and a kvMiBPer1kTokens of 0 or more.");
            }

            var byTier = new Dictionary<GpuTier, int>();
            foreach (var (name, tokens) in RecommendedContext ?? [])
            {
                if (!TryParseTier(name, out var tier) || tokens <= 0)
                {
                    throw new FormatException($"The residency of model family '{family}' has an invalid recommendedContext entry '{name}': {tokens}.");
                }

                byTier[tier] = tokens;
            }

            return new ModelResidencyEstimate(VramMiB, KvMiBPer1kTokens, byTier);
        }

        private static bool TryParseTier(string name, out GpuTier tier)
        {
            tier = name.ToLowerInvariant() switch
            {
                "cpu" => GpuTier.CpuOnly,
                "8gb" => GpuTier.Gpu8GB,
                "12gb" => GpuTier.Gpu12GB,
                "16gb" => GpuTier.Gpu16GB,
                "24gb" => GpuTier.Gpu24GBPlus,
                _ => (GpuTier)(-1),
            };
            return (int)tier >= 0;
        }
    }

    private sealed class FamilyEntry
    {
        public string Match { get; set; } = string.Empty;

        public int ContextWindow { get; set; } = 4096;

        public int MaxOutputTokens { get; set; } = 2048;

        public bool Chat { get; set; } = true;

        public bool Fim { get; set; }

        public bool FimHeader { get; set; } = true;

        public bool Vision { get; set; }

        public bool Embeddings { get; set; }

        public bool Streaming { get; set; } = true;

        public bool PromptCaching { get; set; }

        public bool ReasoningEffort { get; set; }

        public ToolCallingSupport ToolCalling { get; set; }

        public ResidencyEntry? Residency { get; set; }

        public EmbeddingEntry? Embedding { get; set; }

        public decimal InputCostPerMillionTokens { get; set; }

        public decimal OutputCostPerMillionTokens { get; set; }

        public ModelCapabilityDescriptor ToDescriptor() => new()
        {
            ContextWindow = ContextWindow,
            MaxOutputTokens = MaxOutputTokens,
            Chat = Chat,
            Fim = Fim,
            FimHeader = FimHeader,
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

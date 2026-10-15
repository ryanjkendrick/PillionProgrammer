using System.Text.Json;

namespace Pillion.ModelGateway.Providers.Bundled;

/// <summary>How a model turns its per-token vectors into one vector per text.</summary>
public enum EmbeddingPooling
{
    /// <summary>The vector of the first token (<c>[CLS]</c>), as BGE specifies.</summary>
    Cls,

    /// <summary>The mean of the token vectors under the attention mask, as E5 and Jina specify.</summary>
    Mean,
}

/// <summary>What a file of a model is for.</summary>
public enum ModelFileRole
{
    /// <summary>The int8 ONNX graph, run on CPU (and on DirectML when there is no FP16 graph).</summary>
    Model,

    /// <summary>The FP16 ONNX graph, run with DirectML.</summary>
    ModelFp16,

    /// <summary>A WordPiece <c>vocab.txt</c>.</summary>
    Vocab,

    /// <summary>A SentencePiece model.</summary>
    SentencePiece,

    /// <summary>A Hugging Face <c>tokenizer.json</c>.</summary>
    Tokenizer,

    /// <summary>A <c>tokenizer_config.json</c>.</summary>
    TokenizerConfig,
}

/// <summary>One file of a model in <c>models.manifest.json</c>.</summary>
/// <param name="Role">What the file is for.</param>
/// <param name="Path">The file's path inside the model's folder (<c>models\&lt;id&gt;\</c>).</param>
/// <param name="Url">The https URL it is downloaded from; the only place it may come from.</param>
/// <param name="Sha256">The SHA-256 of the file, 64 lower-case hex digits.</param>
/// <param name="Size">The size in bytes.</param>
/// <param name="Optional">Whether the model runs without it (the FP16 graph); optional files are not bundled.</param>
public sealed record ModelFile(ModelFileRole Role, string Path, Uri Url, string Sha256, long Size, bool Optional);

/// <summary>One model in <c>models.manifest.json</c>.</summary>
/// <param name="Id">The model id, also the provider's model id and the folder name.</param>
/// <param name="DisplayName">The name shown to the user.</param>
/// <param name="Bundled">Whether the build copies the model into the output (the VSIX carries it).</param>
/// <param name="Available">Whether the model may be offered at all; <see langword="false"/> keeps it listed for the record only.</param>
/// <param name="License">The licence of the weights (SPDX id).</param>
/// <param name="LicenseUrl">Where the licence is stated.</param>
/// <param name="Source">Where the export comes from and what was checked.</param>
/// <param name="Dimensions">The vector size.</param>
/// <param name="MaxTokens">The most tokens the model reads from one text, special tokens included (the model card's limit).</param>
/// <param name="Pooling">How token vectors become one vector.</param>
/// <param name="Normalize">Whether the model card normalises the pooled vector to unit length.</param>
/// <param name="Tokenizer">The tokenizer family: <c>bert-wordpiece</c>, <c>xlmr-sentencepiece</c> or <c>roberta-bpe</c>.</param>
/// <param name="Files">The model's files.</param>
public sealed record ManifestModel(
    string Id,
    string DisplayName,
    bool Bundled,
    bool Available,
    string License,
    string LicenseUrl,
    string Source,
    int Dimensions,
    int MaxTokens,
    EmbeddingPooling Pooling,
    bool Normalize,
    string Tokenizer,
    IReadOnlyList<ModelFile> Files)
{
    /// <summary>Gets the first file with a role, or <see langword="null"/>.</summary>
    /// <param name="role">The role.</param>
    /// <returns>The file.</returns>
    public ModelFile? Find(ModelFileRole role) => Files.FirstOrDefault(f => f.Role == role);

    /// <summary>Gets the files the model cannot run without.</summary>
    public IEnumerable<ModelFile> RequiredFiles => Files.Where(f => !f.Optional);
}

/// <summary>
/// The model manifest (design sections 7, 10 and 11): every ONNX model the host may load and, for each of its files, the URL,
/// SHA-256 and size. The copy compiled into this assembly is the trusted one: files are only ever downloaded from its URLs, and
/// a file whose hash or size differs from it is refused.
/// </summary>
public sealed class ModelManifest
{
    /// <summary>The tokenizer family of WordPiece models (BGE).</summary>
    public const string BertWordPiece = "bert-wordpiece";

    /// <summary>The tokenizer family of XLM-RoBERTa SentencePiece models (multilingual E5).</summary>
    public const string XlmRobertaSentencePiece = "xlmr-sentencepiece";

    /// <summary>The tokenizer family of byte-level BPE models in the RoBERTa format (Jina v2 code).</summary>
    public const string RobertaBpe = "roberta-bpe";

    private const string ResourceName = "Pillion.ModelGateway.models.manifest.json";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    private static readonly Lazy<ModelManifest> Embedded = new(LoadEmbedded);

    private ModelManifest(IReadOnlyList<ManifestModel> models) => Models = models;

    /// <summary>Gets the manifest compiled into this assembly.</summary>
    public static ModelManifest Default => Embedded.Value;

    /// <summary>Gets the models, in manifest order.</summary>
    public IReadOnlyList<ManifestModel> Models { get; }

    /// <summary>Finds a model by id (case is ignored).</summary>
    /// <param name="modelId">The model id.</param>
    /// <returns>The model, or <see langword="null"/>.</returns>
    public ManifestModel? Find(string modelId)
    {
        ArgumentNullException.ThrowIfNull(modelId);
        return Models.FirstOrDefault(m => string.Equals(m.Id, modelId, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Parses and validates a manifest.</summary>
    /// <param name="json">The manifest JSON.</param>
    /// <returns>The manifest.</returns>
    /// <exception cref="FormatException">The document is not a valid manifest: a URL is not https, a hash is not 64 hex digits, a path leaves the model's folder, an id repeats, or a model lacks the files its tokenizer needs.</exception>
    public static ModelManifest Parse(string json)
    {
        ArgumentNullException.ThrowIfNull(json);

        Document document;
        try
        {
            document = JsonSerializer.Deserialize<Document>(json, JsonOptions) ?? throw new FormatException("The model manifest is empty.");
        }
        catch (JsonException ex)
        {
            throw new FormatException($"The model manifest is not valid JSON: {ex.Message}", ex);
        }

        if (document.Version != 1)
        {
            throw new FormatException($"The model manifest has version {document.Version}; this version of Pillion reads version 1.");
        }

        var models = new List<ManifestModel>();
        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in document.Models ?? [])
        {
            var model = entry.ToModel();
            if (!ids.Add(model.Id))
            {
                throw new FormatException($"The model manifest lists '{model.Id}' more than once.");
            }

            models.Add(model);
        }

        return new ModelManifest(models);
    }

    private static ModelManifest LoadEmbedded()
    {
        using var stream = typeof(ModelManifest).Assembly.GetManifestResourceStream(ResourceName)
            ?? throw new InvalidOperationException($"The embedded resource {ResourceName} is missing.");
        using var reader = new StreamReader(stream);
        return Parse(reader.ReadToEnd());
    }

    private static ModelFileRole ParseRole(string? role, string model) => role?.Trim().ToLowerInvariant() switch
    {
        "model" => ModelFileRole.Model,
        "model-fp16" => ModelFileRole.ModelFp16,
        "vocab" => ModelFileRole.Vocab,
        "sentencepiece" => ModelFileRole.SentencePiece,
        "tokenizer" => ModelFileRole.Tokenizer,
        "tokenizer-config" => ModelFileRole.TokenizerConfig,
        _ => throw new FormatException($"Model '{model}' in the model manifest has a file with the unknown role '{role}'."),
    };

    private sealed class Document
    {
        public int Version { get; set; }

        public List<ModelEntry>? Models { get; set; }
    }

    private sealed class ModelEntry
    {
        public string? Id { get; set; }

        public string? DisplayName { get; set; }

        public bool Bundled { get; set; }

        public bool Available { get; set; } = true;

        public string? License { get; set; }

        public string? LicenseUrl { get; set; }

        public string? Source { get; set; }

        public int Dimensions { get; set; }

        public int MaxTokens { get; set; }

        public string? Pooling { get; set; }

        public bool Normalize { get; set; } = true;

        public string? Tokenizer { get; set; }

        public List<FileEntry>? Files { get; set; }

        public ManifestModel ToModel()
        {
            if (string.IsNullOrWhiteSpace(Id) || Id.Any(c => !(char.IsAsciiLetterOrDigit(c) || c is '-' or '.' or '_')) || Id.Contains("..", StringComparison.Ordinal))
            {
                throw new FormatException($"A model in the model manifest has the invalid id '{Id}': use letters, digits, '-', '.' and '_'.");
            }

            if (Dimensions <= 0 || MaxTokens <= 2)
            {
                throw new FormatException($"Model '{Id}' in the model manifest needs dimensions above 0 and maxTokens above 2.");
            }

            var pooling = Pooling?.Trim().ToLowerInvariant() switch
            {
                "cls" => EmbeddingPooling.Cls,
                "mean" => EmbeddingPooling.Mean,
                _ => throw new FormatException($"Model '{Id}' in the model manifest has the unknown pooling '{Pooling}' (cls or mean)."),
            };

            var tokenizer = Tokenizer?.Trim().ToLowerInvariant();
            if (tokenizer is not (BertWordPiece or XlmRobertaSentencePiece or RobertaBpe))
            {
                throw new FormatException($"Model '{Id}' in the model manifest has the unknown tokenizer '{Tokenizer}'.");
            }

            var files = (Files ?? []).Select(f => f.ToFile(Id)).ToList();
            if (files.Select(f => f.Path).Distinct(StringComparer.OrdinalIgnoreCase).Count() != files.Count)
            {
                throw new FormatException($"Model '{Id}' in the model manifest lists a file path more than once.");
            }

            var model = new ManifestModel(
                Id,
                string.IsNullOrWhiteSpace(DisplayName) ? Id : DisplayName,
                Bundled,
                Available,
                License ?? string.Empty,
                LicenseUrl ?? string.Empty,
                Source ?? string.Empty,
                Dimensions,
                MaxTokens,
                pooling,
                Normalize,
                tokenizer,
                files);

            var tokenizerRole = tokenizer switch
            {
                BertWordPiece => ModelFileRole.Vocab,
                XlmRobertaSentencePiece => ModelFileRole.SentencePiece,
                _ => ModelFileRole.Tokenizer,
            };
            if (model.Find(ModelFileRole.Model) is not { Optional: false } || model.Find(tokenizerRole) is not { Optional: false })
            {
                throw new FormatException($"Model '{Id}' in the model manifest needs a required 'model' file and a required tokenizer file for {tokenizer}.");
            }

            return model;
        }
    }

    private sealed class FileEntry
    {
        public string? Role { get; set; }

        public string? Path { get; set; }

        public string? Url { get; set; }

        public string? Sha256 { get; set; }

        public long Size { get; set; }

        public bool Optional { get; set; }

        public ModelFile ToFile(string model)
        {
            var role = ParseRole(Role, model);
            var path = Path?.Trim() ?? string.Empty;
            if (path.Length == 0 || System.IO.Path.IsPathRooted(path) || path.Split('/', '\\').Any(part => part is "" or "." or ".."))
            {
                throw new FormatException($"Model '{model}' in the model manifest has a file with the invalid path '{Path}': it must stay inside the model's folder.");
            }

            if (!Uri.TryCreate(Url, UriKind.Absolute, out var url) || url.Scheme != Uri.UriSchemeHttps || !string.IsNullOrEmpty(url.UserInfo))
            {
                throw new FormatException($"Model '{model}' in the model manifest has a file ({path}) whose URL is not an https URL: '{Url}'.");
            }

            var sha = Sha256?.Trim() ?? string.Empty;
            if (sha.Length != 64 || !sha.All(char.IsAsciiHexDigit))
            {
                throw new FormatException($"Model '{model}' in the model manifest has a file ({path}) without a 64-digit SHA-256.");
            }

            if (Size <= 0)
            {
                throw new FormatException($"Model '{model}' in the model manifest has a file ({path}) without a size.");
            }

            return new ModelFile(role, path, url, sha.ToLowerInvariant(), Size, Optional);
        }
    }
}

using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Pillion.Eval.Golden;

/// <summary>One retrieval golden-set query: a line of <c>queries.jsonl</c>.</summary>
internal sealed record GoldenQuery
{
    /// <summary>Gets the id, unique within the set.</summary>
    public required string Id { get; init; }

    /// <summary>Gets the natural-language query.</summary>
    public required string Query { get; init; }

    /// <summary>Gets the corpus-relative paths (forward slashes) of the files that answer the query; a result in any of them is a hit.</summary>
    public IReadOnlyList<string> ExpectedFiles { get; init; } = [];

    /// <summary>Gets the symbols (<c>Type</c> or <c>Type.Member</c>) that answer the query; a result whose breadcrumb contains any of them is a symbol hit.</summary>
    public IReadOnlyList<string> ExpectedSymbols { get; init; } = [];

    /// <summary>Gets the optional kind (<c>where</c>, <c>which</c>, <c>symbol</c>, <c>concept</c>), for the breakdown in the report.</summary>
    public string? Kind { get; init; }

    /// <summary>Gets free-text notes (why these are the answers).</summary>
    public string? Notes { get; init; }
}

/// <summary>A folder of the corpus: copied from <see cref="Path"/> (relative to the set folder) to <see cref="As"/> inside the corpus root.</summary>
/// <param name="Path">The source folder, relative to the set folder or absolute.</param>
/// <param name="As">The folder inside the corpus root (forward slashes; empty for the root itself).</param>
internal sealed record CorpusSource(string Path, string As);

/// <summary>A retrieval golden set: <c>set.json</c> (the corpus) and <c>queries.jsonl</c> (the queries) in one folder.</summary>
/// <param name="Name">The set name.</param>
/// <param name="Directory">The set folder (full path).</param>
/// <param name="Description">What the set covers.</param>
/// <param name="Corpus">The folders the corpus is built from.</param>
/// <param name="Exclude">Folder names left out of the corpus at any depth, besides the defaults.</param>
/// <param name="Queries">The queries.</param>
internal sealed record GoldenSet(string Name, string Directory, string Description, IReadOnlyList<CorpusSource> Corpus, IReadOnlyList<string> Exclude, IReadOnlyList<GoldenQuery> Queries)
{
    /// <summary>The set definition file.</summary>
    public const string SetFileName = "set.json";

    /// <summary>The queries file.</summary>
    public const string QueriesFileName = "queries.jsonl";

    /// <summary>The folder names never copied into a corpus.</summary>
    public static readonly IReadOnlyList<string> DefaultExclude = ["bin", "obj", ".vs", ".git", "node_modules", "TestResults"];

    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    /// <summary>Loads a set from its folder.</summary>
    /// <param name="directory">The set folder.</param>
    /// <returns>The set.</returns>
    /// <exception cref="GoldenSetException">A file is missing or malformed.</exception>
    public static GoldenSet Load(string directory)
    {
        var full = System.IO.Path.GetFullPath(directory);
        var setPath = System.IO.Path.Combine(full, SetFileName);
        var queriesPath = System.IO.Path.Combine(full, QueriesFileName);
        if (!File.Exists(setPath))
        {
            throw new GoldenSetException($"{setPath} does not exist.");
        }

        if (!File.Exists(queriesPath))
        {
            throw new GoldenSetException($"{queriesPath} does not exist.");
        }

        SetFile definition;
        try
        {
            definition = JsonSerializer.Deserialize<SetFile>(File.ReadAllText(setPath), Json) ?? throw new GoldenSetException($"{setPath} is empty.");
        }
        catch (JsonException ex)
        {
            throw new GoldenSetException($"{setPath}: {ex.Message}", ex);
        }

        if (definition.Corpus is not { Count: > 0 })
        {
            throw new GoldenSetException($"{setPath}: 'corpus' must list at least one folder.");
        }

        var corpus = new List<CorpusSource>();
        foreach (var entry in definition.Corpus)
        {
            if (string.IsNullOrWhiteSpace(entry.Path))
            {
                throw new GoldenSetException($"{setPath}: every corpus entry needs a 'path'.");
            }

            var target = NormalizeRelative(entry.As ?? string.Empty);
            if (target is null)
            {
                throw new GoldenSetException($"{setPath}: corpus 'as' must be a relative folder without '..' (got '{entry.As}').");
            }

            corpus.Add(new CorpusSource(entry.Path, target));
        }

        var queries = ParseQueries(File.ReadAllText(queriesPath), queriesPath);
        return new GoldenSet(
            string.IsNullOrWhiteSpace(definition.Name) ? System.IO.Path.GetFileName(full) : definition.Name,
            full,
            definition.Description ?? string.Empty,
            corpus,
            definition.Exclude ?? [],
            queries);
    }

    /// <summary>Parses the text of a <c>queries.jsonl</c> file: one JSON object per line; blank lines are skipped.</summary>
    /// <param name="text">The file text.</param>
    /// <param name="source">The file name, for messages.</param>
    /// <returns>The queries, in file order.</returns>
    /// <exception cref="GoldenSetException">A line is malformed, a required field is missing, a path is not relative, or an id repeats.</exception>
    public static IReadOnlyList<GoldenQuery> ParseQueries(string text, string source = QueriesFileName)
    {
        ArgumentNullException.ThrowIfNull(text);
        var queries = new List<GoldenQuery>();
        var ids = new HashSet<string>(StringComparer.Ordinal);
        var lineNumber = 0;
        using var reader = new StringReader(text);
        while (reader.ReadLine() is { } line)
        {
            lineNumber++;
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            string Where() => string.Create(CultureInfo.InvariantCulture, $"{source}:{lineNumber}");
            QueryLine parsed;
            try
            {
                parsed = JsonSerializer.Deserialize<QueryLine>(line, Json) ?? throw new GoldenSetException($"{Where()}: the line is null.");
            }
            catch (JsonException ex)
            {
                throw new GoldenSetException($"{Where()}: {ex.Message}", ex);
            }

            if (string.IsNullOrWhiteSpace(parsed.Id))
            {
                throw new GoldenSetException($"{Where()}: 'id' is required.");
            }

            if (!ids.Add(parsed.Id))
            {
                throw new GoldenSetException($"{Where()}: the id '{parsed.Id}' is used twice.");
            }

            if (string.IsNullOrWhiteSpace(parsed.Query))
            {
                throw new GoldenSetException($"{Where()}: 'query' is required ({parsed.Id}).");
            }

            if (parsed.ExpectedFiles is not { Count: > 0 })
            {
                throw new GoldenSetException($"{Where()}: 'expectedFiles' must list at least one file ({parsed.Id}).");
            }

            var files = new List<string>();
            foreach (var file in parsed.ExpectedFiles)
            {
                var normalized = NormalizeRelative(file);
                if (string.IsNullOrEmpty(normalized))
                {
                    throw new GoldenSetException($"{Where()}: expected file '{file}' must be a corpus-relative path without '..' ({parsed.Id}).");
                }

                files.Add(normalized);
            }

            var symbols = new List<string>();
            foreach (var symbol in parsed.ExpectedSymbols ?? [])
            {
                if (BreadcrumbSymbols.SplitSymbol(symbol).Count == 0)
                {
                    throw new GoldenSetException($"{Where()}: expected symbol '{symbol}' is empty ({parsed.Id}).");
                }

                symbols.Add(symbol.Trim());
            }

            queries.Add(new GoldenQuery
            {
                Id = parsed.Id.Trim(),
                Query = parsed.Query.Trim(),
                ExpectedFiles = files,
                ExpectedSymbols = symbols,
                Kind = string.IsNullOrWhiteSpace(parsed.Kind) ? null : parsed.Kind.Trim(),
                Notes = parsed.Notes,
            });
        }

        if (queries.Count == 0)
        {
            throw new GoldenSetException($"{source}: no queries.");
        }

        return queries;
    }

    /// <summary>Normalizes a corpus-relative path: forward slashes, no leading or trailing slash, no <c>.</c> or <c>..</c> segments.</summary>
    /// <param name="path">The path.</param>
    /// <returns>The normalized path (empty for the root), or <see langword="null"/> when it is absolute or climbs out.</returns>
    public static string? NormalizeRelative(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        var trimmed = path.Trim().Replace('\\', '/');
        if (trimmed.StartsWith('/') || System.IO.Path.IsPathRooted(trimmed) || trimmed.Contains(':', StringComparison.Ordinal))
        {
            return null;
        }

        var segments = trimmed.Split('/', StringSplitOptions.RemoveEmptyEntries).Where(s => s != ".").ToList();
        return segments.Contains("..") ? null : string.Join('/', segments);
    }

    private sealed class SetFile
    {
        public string? Name { get; set; }

        public string? Description { get; set; }

        public List<CorpusEntry>? Corpus { get; set; }

        public List<string>? Exclude { get; set; }

        [JsonPropertyName("$comment")]
        public string? Comment { get; set; }
    }

    private sealed class CorpusEntry
    {
        public string? Path { get; set; }

        public string? As { get; set; }
    }

    private sealed class QueryLine
    {
        public string? Id { get; set; }

        public string? Query { get; set; }

        public List<string>? ExpectedFiles { get; set; }

        public List<string>? ExpectedSymbols { get; set; }

        public string? Kind { get; set; }

        public string? Notes { get; set; }
    }
}

/// <summary>A golden set that cannot be read.</summary>
internal sealed class GoldenSetException : Exception
{
    /// <summary>Initializes a new instance of the <see cref="GoldenSetException"/> class.</summary>
    public GoldenSetException()
    {
    }

    /// <summary>Initializes a new instance of the <see cref="GoldenSetException"/> class.</summary>
    /// <param name="message">The message.</param>
    public GoldenSetException(string message)
        : base(message)
    {
    }

    /// <summary>Initializes a new instance of the <see cref="GoldenSetException"/> class.</summary>
    /// <param name="message">The message.</param>
    /// <param name="innerException">The cause.</param>
    public GoldenSetException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

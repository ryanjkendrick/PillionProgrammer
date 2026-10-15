using Microsoft.Data.Sqlite;
using Pillion.ContextEngine.Chunking;
using Pillion.ContextEngine.Retrieval;
using Pillion.ContextEngine.Symbols;
using Pillion.Indexing.Storage;
using Pillion.LocalStore;
using Pillion.LocalStore.Index;

// The index databases are encrypted with DPAPI-protected keys, like Pillion Host's (Windows only).
[assembly: System.Runtime.Versioning.SupportedOSPlatform("windows")]

namespace Pillion.ContextEngine.Tests.Retrieval;

/// <summary>A unique scratch directory that is deleted when disposed.</summary>
internal sealed class RetrievalTempDirectory : IDisposable
{
    public RetrievalTempDirectory()
    {
        Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "pillion-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path);
    }

    public string Path { get; }

    public string File(string name) => System.IO.Path.Combine(Path, name);

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try
        {
            Directory.Delete(Path, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Best effort cleanup of a temp folder.
        }
    }
}

/// <summary>
/// A deterministic stand-in for an embedding model: a bag of identifier parts (split like the lexical index, lower-cased,
/// with a crude suffix stemmer and the query analyser's stop words left out) hashed into signed buckets, unit-normalised
/// and quantised to int8 like the gateway does. Texts that share words point the same way, which is all a test needs.
/// </summary>
internal sealed class HashingEmbedder(int dimensions = 384) : IQueryEmbedder
{
    private static readonly HashSet<string> StopWords =
        ["a", "an", "and", "are", "as", "at", "be", "by", "do", "does", "for", "from", "how", "in", "is", "it", "of", "on", "or", "the", "to", "what", "where", "which", "who", "with"];

    private int _calls;

    public int Dimensions => dimensions;

    public int Calls => Volatile.Read(ref _calls);

    public Task<QueryEmbedding> EmbedQueryAsync(string text, string? modelId, int? dims, CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref _calls);
        return Task.FromResult(new QueryEmbedding(Embed(text), modelId ?? "test/hashing"));
    }

    public sbyte[] Embed(string text)
    {
        var values = new double[dimensions];
        foreach (var term in Terms(text))
        {
            var hash = Fnv1a(term);
            values[(int)(hash % (uint)dimensions)] += (hash & 0x8000_0000) == 0 ? 1 : -1;
        }

        var max = values.Max(Math.Abs);
        var vector = new sbyte[dimensions];
        if (max == 0)
        {
            vector[0] = 1;
            return vector;
        }

        for (var i = 0; i < dimensions; i++)
        {
            vector[i] = (sbyte)Math.Clamp(Math.Round(values[i] / max * 127), -127, 127);
        }

        return vector;
    }

    private static IEnumerable<string> Terms(string text)
    {
        var token = new System.Text.StringBuilder();
        foreach (var c in text + " ")
        {
            if (char.IsLetterOrDigit(c) || c == '_')
            {
                token.Append(c);
                continue;
            }

            if (token.Length > 0)
            {
                foreach (var part in IdentifierSplitter.Split(token.ToString()))
                {
                    var stem = Stem(part);
                    if (stem.Length > 1 && !StopWords.Contains(stem))
                    {
                        yield return stem;
                    }
                }

                token.Clear();
            }
        }
    }

    private static string Stem(string word)
    {
        foreach (var suffix in new[] { "ing", "ed", "es", "s" })
        {
            if (word.Length > suffix.Length + 2 && word.EndsWith(suffix, StringComparison.Ordinal))
            {
                return word[..^suffix.Length];
            }
        }

        return word;
    }

    private static uint Fnv1a(string text)
    {
        var hash = 2166136261u;
        foreach (var c in text)
        {
            hash = (hash ^ c) * 16777619u;
        }

        return hash;
    }
}

/// <summary>Builds a real encrypted index from the chunker fixtures: files, chunks, FTS5 rows, vectors and symbols.</summary>
internal static class FixtureIndex
{
    public const string IndexId = "00112233445566778899aabbccddeeff";
    public const string ModelId = "test/hashing";

    public static readonly string[] Fixtures =
    [
        "csharp/Greeter.cs", "java/OrderService.java", "python/inventory.py", "typescript/shapes.ts", "go/server.go",
        "rust/geometry.rs", "javascript/app.js", "c/list.c", "cpp/matrix.hpp", "sql/schema.sql", "vb/Greeter.vb",
        "razor/Counter.razor", "css/site.css", "html/index.html", "text/notes.txt",
    ];

    public static async Task<(IndexStorage Storage, SymbolGraph Graph)> BuildAsync(RetrievalTempDirectory temp, HashingEmbedder embedder)
    {
        var storage = await IndexStorage.OpenAsync(new EncryptedDatabaseOptions { DatabasePath = temp.File("index.db") }, IndexSchema.Schema);
        var files = new FilesRepository(storage.Database);
        var chunker = new FileChunker();
        var extractor = new FileSymbolExtractor();
        var builder = new SymbolGraphBuilder();
        foreach (var fixture in Fixtures)
        {
            var path = "src/" + Path.GetFileName(fixture);
            var text = Fixture.Read(fixture);
            var language = LanguageCatalog.Default.Find(path)?.Id ?? "other";
            var stored = await files.UpsertAsync([new FileRecord(0, path, language, "src", 0, text.Length, 0, 0)]);
            var fileId = stored[0].Id;

            var chunks = chunker.Chunk(path, text, ChunkOptions.Default);
            await storage.Chunks.ReplaceFileChunksAsync(fileId, [.. chunks.Select(c => new NewChunk(c.StartLine, c.EndLine, KindName(c.Kind), c.Breadcrumb, c.Text, c.TokenCount)
            {
                Embedding = embedder.Embed(c.EmbeddingText),
            })]);

            var extraction = extractor.Extract(path, text);
            builder.AddFile(extraction);
            await storage.Symbols.ReplaceFileSymbolsAsync(fileId, [.. extraction.Definitions.Select(d => new NewSymbol(d.Name, SymbolKinds.Name(d.Kind), d.Container, d.StartLine, d.EndLine, d.Signature) { Id = d.Id })]);
        }

        await storage.Database.WriteAsync((connection, transaction, token) =>
        {
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = "INSERT INTO meta (key, value) VALUES ($key, $value) ON CONFLICT (key) DO UPDATE SET value = excluded.value";
            command.Parameters.AddWithValue("$key", StorageRetrievalIndex.EmbeddingModelMetaKey);
            command.Parameters.AddWithValue("$value", ModelId);
            command.ExecuteNonQuery();
            return Task.CompletedTask;
        });

        return (storage, builder.Build());
    }

    /// <summary>The chunk kind as stored: the chunker's kind in kebab case (<c>member</c>, <c>type-header</c>).</summary>
    public static string KindName(ChunkKind kind) => kind switch
    {
        ChunkKind.TypeHeader => "type-header",
        _ => kind.ToString().ToLowerInvariant(),
    };
}

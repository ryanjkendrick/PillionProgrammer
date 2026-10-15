using Pillion.Common;
using Pillion.ContextEngine;
using Pillion.ContextEngine.Chunking;
using Pillion.ContextEngine.Symbols;
using Pillion.Indexing.Discovery;
using Pillion.Indexing.Ownership;
using Pillion.Indexing.Storage;
using Pillion.LocalStore.Index;

namespace Pillion.Host.Tests.TestSupport;

/// <summary>
/// A small repository for the mention tests (work item 2.13): the Context Engine's chunker fixtures (copied next to the
/// tests as <c>ContextEngineFixtures</c>) plus a README, a guide, a program that uses the C# greeter and a secret file, and a
/// real encrypted index of it built the way the indexing pipeline does: the chunks, the symbols and the resolved reference
/// edges. The secret file is left out of the index, as discovery would.
/// </summary>
internal static class MentionTestIndex
{
    /// <summary>The files written besides the fixtures.</summary>
    public static readonly IReadOnlyDictionary<string, string> Extra = new Dictionary<string, string>
    {
        ["README.md"] = "# Shop sample\n\nA small repository for the mention tests.\n\n## Building\n\nRun the build.\n",
        ["docs/guide.md"] = Guide(),
        ["src/App/Program.cs"] = """
            using Fixtures.Sample;

            namespace Shop.App;

            /// <summary>Starts the shop.</summary>
            public static class Program
            {
                public static void Main()
                {
                    var greeter = new Greeter<string>("Hello");
                    System.Console.WriteLine(greeter.Greet("Ann"));
                }
            }
            """,
    };

    /// <summary>The secret file, on disk but not indexed.</summary>
    public const string SecretFile = ".env";

    /// <summary>Writes the repository under <paramref name="root"/> and builds its index under <paramref name="paths"/>.</summary>
    /// <param name="paths">The data root.</param>
    /// <param name="root">The repository root.</param>
    /// <returns>The index id and the indexed relative paths.</returns>
    public static async Task<(string IndexId, IReadOnlyList<string> Indexed)> BuildAsync(PillionPaths paths, string root)
    {
        var sources = new SortedDictionary<string, string>(StringComparer.Ordinal);
        var fixtures = Path.Combine(AppContext.BaseDirectory, "ContextEngineFixtures");
        foreach (var file in Directory.EnumerateFiles(fixtures, "*", SearchOption.AllDirectories))
        {
            sources[Path.GetRelativePath(fixtures, file).Replace('\\', '/')] = File.ReadAllText(file);
        }

        foreach (var (path, text) in Extra)
        {
            sources[path] = text;
        }

        foreach (var (path, text) in sources)
        {
            Write(root, path, text);
        }

        Write(root, SecretFile, "API_KEY=sk-live-0123456789abcdef0123456789abcdef\n");

        var factory = new IndexStoreFactory(paths, new TestIndex.AllOn());
        await using var store = await factory.OpenAsync(new IndexRequest(root, "test/none", ContextEngineVersions.ChunkerVersion, "pillion-mention-tests"));
        Assert.Equal(IndexRole.Writer, store.Role);
        await using var storage = IndexStorage.Create(store.Database, ownsDatabase: false);
        var chunker = new FileChunker();
        var extractor = new FileSymbolExtractor();
        var builder = new SymbolGraphBuilder();
        var fileIds = new Dictionary<string, long>(StringComparer.Ordinal);
        foreach (var (path, text) in sources)
        {
            var stored = await store.Files.UpsertAsync([new FileRecord(0, path, Languages.Detect(path), path.Split('/')[0], 0, text.Length, 0, 0)]);
            fileIds[path] = stored[0].Id;
            var chunks = chunker.Chunk(path, text, ChunkOptions.Default);
            await storage.Chunks.ReplaceFileChunksAsync(stored[0].Id, [.. chunks.Select(c => new NewChunk(c.StartLine, c.EndLine, c.Kind.ToString().ToLowerInvariant(), c.Breadcrumb, c.Text, c.TokenCount))]);
            var extraction = extractor.Extract(path, text);
            builder.AddFile(extraction);
            await storage.Symbols.ReplaceFileSymbolsAsync(stored[0].Id, [.. extraction.Definitions.Select(d => new NewSymbol(d.Name, SymbolKinds.Name(d.Kind), d.Container, d.StartLine, d.EndLine, d.Signature) { Id = d.Id })]);
        }

        var graph = builder.Build();
        foreach (var group in graph.Edges.GroupBy(e => e.FilePath, StringComparer.Ordinal))
        {
            var fileId = fileIds[group.Key];
            await storage.Symbols.ReplaceFileReferencesAsync(fileId, [.. group.Select(e => new LocalStore.Index.SymbolReference(e.FromSymbolId, e.ToSymbolId, fileId, e.Line, SymbolKinds.Name(e.Kind), e.IsAmbiguous, e.CandidateCount))]);
        }

        return (store.Identity.Id, [.. sources.Keys]);
    }

    private static void Write(string root, string relative, string text)
    {
        var full = Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, text);
    }

    private static string Guide()
    {
        var builder = new System.Text.StringBuilder("# Developer guide\n\n");
        for (var section = 1; section <= 12; section++)
        {
            builder.Append("## Section ").Append(section).Append("\n\n");
            for (var line = 0; line < 25; line++)
            {
                builder.Append("Paragraph ").Append(section).Append('.').Append(line)
                    .Append(" explains how the shop handles orders, stock and greetings in some detail.\n");
            }

            builder.Append('\n');
        }

        return builder.ToString();
    }
}

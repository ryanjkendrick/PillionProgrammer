using Pillion.Common;
using Pillion.Common.Settings;
using Pillion.ContextEngine;
using Pillion.ContextEngine.Chunking;
using Pillion.ContextEngine.Symbols;
using Pillion.Indexing.Ownership;
using Pillion.Indexing.Storage;
using Pillion.LocalStore.Index;

namespace Pillion.Host.Tests.TestSupport;

/// <summary>
/// Builds a real index folder for a small repository the way the indexing pipeline will (work item 2.9): the index
/// factory elects this test as the writer, the chunkers and the symbol extractor fill the storage, and disposing the
/// store releases the lock, so the host then opens the index read-only.
/// </summary>
internal static class TestIndex
{
    public static readonly IReadOnlyDictionary<string, string> Sources = new Dictionary<string, string>
    {
        ["src/Orders/OrderService.cs"] = """
            namespace Shop.Orders;

            /// <summary>Places customer orders and computes their totals.</summary>
            public sealed class OrderService
            {
                private readonly List<Order> _orders = new();

                /// <summary>Adds the order to the open orders.</summary>
                public void PlaceOrder(Order order)
                {
                    _orders.Add(order);
                }

                /// <summary>Sums the order lines, with tax.</summary>
                public decimal ComputeTotal(Order order)
                {
                    return order.Lines.Sum(line => line.Price * line.Quantity) * 1.2m;
                }
            }
            """,
        ["src/Inventory/StockRoom.cs"] = """
            namespace Shop.Inventory;

            /// <summary>Keeps the stock levels of the warehouse.</summary>
            public sealed class StockRoom
            {
                private readonly Dictionary<string, int> _levels = new();

                /// <summary>Puts more units of a product on the shelves when a delivery arrives.</summary>
                public void Restock(string product, int amount)
                {
                    _levels[product] = _levels.GetValueOrDefault(product) + amount;
                }

                /// <summary>Takes units of a product off the shelves for a shipment.</summary>
                public bool Pick(string product, int amount)
                {
                    if (_levels.GetValueOrDefault(product) < amount)
                    {
                        return false;
                    }

                    _levels[product] -= amount;
                    return true;
                }
            }
            """,
        ["scripts/report.py"] = """"
            def monthly_report(sales):
                """Prints the monthly sales report."""
                for month, total in sales.items():
                    print(month, total)
            """",
    };

    /// <summary>Writes the sources under <paramref name="root"/> and builds their index.</summary>
    /// <param name="paths">The data root the index folder goes under.</param>
    /// <param name="root">The repository root.</param>
    /// <param name="modelId">The embedding model id recorded in the index identity and meta.</param>
    /// <param name="embed">Embeds chunk texts (int8 vectors, in order), or <see langword="null"/> for an index without vectors.</param>
    /// <returns>The index id.</returns>
    public static async Task<string> BuildAsync(PillionPaths paths, string root, string modelId = "test/none", Func<IReadOnlyList<string>, Task<IReadOnlyList<sbyte[]>>>? embed = null)
    {
        foreach (var (path, text) in Sources)
        {
            var full = Path.Combine(root, path.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            await File.WriteAllTextAsync(full, text);
        }

        var factory = new IndexStoreFactory(paths, new AllOn());
        await using var store = await factory.OpenAsync(new IndexRequest(root, modelId, ContextEngineVersions.ChunkerVersion, "pillion-test-index"));
        Assert.Equal(IndexRole.Writer, store.Role);
        await using var storage = IndexStorage.Create(store.Database, ownsDatabase: false);
        var chunker = new FileChunker();
        var extractor = new FileSymbolExtractor();
        foreach (var (path, text) in Sources)
        {
            var language = LanguageCatalog.Default.Find(path)?.Id ?? "other";
            var partition = path.Split('/')[0];
            var stored = await store.Files.UpsertAsync([new FileRecord(0, path, language, partition, 0, text.Length, 0, 0)]);
            var chunks = chunker.Chunk(path, text, ChunkOptions.Default);
            var vectors = embed is null ? null : await embed([.. chunks.Select(c => c.EmbeddingText)]);
            await storage.Chunks.ReplaceFileChunksAsync(stored[0].Id, [.. chunks.Select((c, i) => new NewChunk(c.StartLine, c.EndLine, c.Kind.ToString().ToLowerInvariant(), c.Breadcrumb, c.Text, c.TokenCount)
            {
                Embedding = vectors is null ? ReadOnlyMemory<sbyte>.Empty : vectors[i],
            })]);
            var extraction = extractor.Extract(path, text);
            await storage.Symbols.ReplaceFileSymbolsAsync(stored[0].Id, [.. extraction.Definitions.Select(d => new NewSymbol(d.Name, SymbolKinds.Name(d.Kind), d.Container, d.StartLine, d.EndLine, d.Signature) { Id = d.Id })]);
        }

        return store.Identity.Id;
    }

    /// <summary>The 1-based line of the first line of a source that contains <paramref name="text"/>.</summary>
    public static int LineOf(string path, string text) =>
        Sources[path].Split('\n').Select((line, i) => (line, i)).First(x => x.line.Contains(text, StringComparison.Ordinal)).i + 1;

    /// <summary>Feature flags with everything on.</summary>
    public sealed class AllOn : IFeatureFlags
    {
        public event EventHandler? Changed
        {
            add { }
            remove { }
        }

        public bool IsEnabled(string feature) => true;
    }
}

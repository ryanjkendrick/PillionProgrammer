using Pillion.ContextEngine.Retrieval;
using Pillion.ContextEngine.Symbols;
using Pillion.LocalStore.Index;
using SymbolReference = Pillion.LocalStore.Index.SymbolReference;

namespace Pillion.ContextEngine.Tests.Retrieval;

/// <summary>An in-memory index for the retriever's unit tests: files, chunks, symbols and programmable search legs.</summary>
internal sealed class FakeIndex : IRetrievalIndex, IRetrievalFileNameLookup
{
    private readonly FakeChunks _chunks;
    private readonly FakeVectors _vectors;
    private readonly FakeSymbols _symbols;

    public FakeIndex(string indexId = "0123456789abcdef0123456789abcdef", string rootPath = @"C:\repo", int? dimensions = 4)
    {
        IndexId = indexId;
        RootPath = rootPath;
        VectorDimensions = dimensions;
        _chunks = new FakeChunks(this);
        _vectors = new FakeVectors(this);
        _symbols = new FakeSymbols(this);
    }

    public string IndexId { get; }

    public string RootPath { get; }

    public string? EmbeddingModelId { get; set; } = "fake/model";

    public int? VectorDimensions { get; set; }

    public IChunkRepository Chunks => _chunks;

    public IVectorIndex Vectors => _vectors;

    public ISymbolRepository Symbols => _symbols;

    public SymbolGraph? Graph { get; set; }

    public List<RetrievalFile> Files { get; } = [];

    public List<ChunkRecord> ChunkList { get; } = [];

    public List<SymbolRecord> SymbolList { get; } = [];

    /// <summary>Gets or sets the lexical leg: query text and filter in, hits out. Defaults to no hits.</summary>
    public Func<string, ChunkFilter?, CancellationToken, Task<IReadOnlyList<LexicalHit>>> Lexical { get; set; } = (_, _, _) => Task.FromResult<IReadOnlyList<LexicalHit>>([]);

    /// <summary>Gets or sets the vector leg. Defaults to no hits.</summary>
    public Func<ReadOnlyMemory<sbyte>, ChunkFilter?, CancellationToken, Task<IReadOnlyList<VectorHit>>> Vector { get; set; } = (_, _, _) => Task.FromResult<IReadOnlyList<VectorHit>>([]);

    public int LexicalCalls { get; set; }

    public int VectorCalls { get; set; }

    public int SymbolCalls { get; set; }

    public List<string> LexicalQueries { get; } = [];

    public List<ChunkFilter?> Filters { get; } = [];

    public long AddFile(string path, string language = "csharp", string partition = "src")
    {
        var id = Files.Count + 1;
        Files.Add(new RetrievalFile(id, path, language, partition));
        return id;
    }

    /// <summary>Adds a chunk of <paramref name="lines"/> lines starting at <paramref name="start"/>; its text is one numbered line per line.</summary>
    public long AddChunk(long fileId, int start, int lines = 5, string? breadcrumb = null, int tokens = 50, string kind = "member", string? text = null)
    {
        var id = 100 + ChunkList.Count + 1;
        var file = Files.Single(f => f.Id == fileId);
        text ??= string.Join('\n', Enumerable.Range(start, lines).Select(l => $"line {l} of {file.Path}"));
        ChunkList.Add(new ChunkRecord(id, fileId, start, start + lines - 1, kind, breadcrumb ?? $"{file.Path} > T > M{id}", text, tokens));
        return id;
    }

    /// <summary>Adds <paramref name="count"/> consecutive five-line chunks to a file and returns their ids.</summary>
    public List<long> AddChunks(long fileId, int count, int tokens = 50)
    {
        var start = ChunkList.Where(c => c.FileId == fileId).Select(c => c.EndLine).DefaultIfEmpty(0).Max() + 1;
        return [.. Enumerable.Range(0, count).Select(i => AddChunk(fileId, start + (i * 5), 5, tokens: tokens))];
    }

    public void LexicalReturns(params long[] chunkIds) =>
        Lexical = (_, _, _) => Task.FromResult<IReadOnlyList<LexicalHit>>([.. chunkIds.Select((id, i) => new LexicalHit(id, 100 - i))]);

    public void VectorReturns(params long[] chunkIds) =>
        Vector = (_, _, _) => Task.FromResult<IReadOnlyList<VectorHit>>([.. chunkIds.Select((id, i) => new VectorHit(id, 0.1 + (i * 0.01)))]);

    public Task<IReadOnlyDictionary<long, RetrievalFile>> GetFilesAsync(IReadOnlyCollection<long> fileIds, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyDictionary<long, RetrievalFile>>(Files.Where(f => fileIds.Contains(f.Id)).ToDictionary(f => f.Id));

    public Task<IReadOnlyDictionary<string, RetrievalFile>> FindFilesAsync(IReadOnlyCollection<string> paths, CancellationToken cancellationToken)
    {
        var result = new Dictionary<string, RetrievalFile>(StringComparer.OrdinalIgnoreCase);
        foreach (var path in paths)
        {
            if (Files.FirstOrDefault(f => string.Equals(f.Path, path, StringComparison.OrdinalIgnoreCase)) is { } file)
            {
                result[path] = file;
            }
        }

        return Task.FromResult<IReadOnlyDictionary<string, RetrievalFile>>(result);
    }

    public Task<IReadOnlyDictionary<string, IReadOnlyList<long>>> FindFilesByNameAsync(IReadOnlyCollection<string> names, CancellationToken cancellationToken)
    {
        var result = new Dictionary<string, IReadOnlyList<long>>(StringComparer.OrdinalIgnoreCase);
        foreach (var name in names)
        {
            var ids = Files.Where(f => f.Path.EndsWith("/" + name, StringComparison.OrdinalIgnoreCase) || string.Equals(f.Path, name, StringComparison.OrdinalIgnoreCase)).Select(f => f.Id).ToList();
            if (ids.Count > 0)
            {
                result[name] = ids;
            }
        }

        return Task.FromResult<IReadOnlyDictionary<string, IReadOnlyList<long>>>(result);
    }

    private sealed class FakeChunks(FakeIndex owner) : IChunkRepository
    {
        public Task<IReadOnlyList<LexicalHit>> SearchLexicalAsync(string query, int k, ChunkFilter? filter = null, CancellationToken cancellationToken = default)
        {
            lock (owner)
            {
                owner.LexicalCalls++;
                owner.LexicalQueries.Add(query);
                owner.Filters.Add(filter);
            }

            return owner.Lexical(query, filter, cancellationToken);
        }

        public Task<IReadOnlyList<ChunkRecord>> GetChunksAsync(IReadOnlyList<long> chunkIds, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<ChunkRecord>>([.. chunkIds.Select(id => owner.ChunkList.FirstOrDefault(c => c.Id == id)).Where(c => c is not null).Select(c => c!)]);

        public Task<IReadOnlyList<ChunkRecord>> GetChunksByFileAsync(long fileId, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<ChunkRecord>>([.. owner.ChunkList.Where(c => c.FileId == fileId).OrderBy(c => c.StartLine)]);

        public Task<ChunkRecord?> GetChunkAsync(long chunkId, CancellationToken cancellationToken = default) =>
            Task.FromResult(owner.ChunkList.FirstOrDefault(c => c.Id == chunkId));

        public Task<IReadOnlyList<long>> ReplaceFileChunksAsync(long fileId, IReadOnlyList<NewChunk> chunks, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<IReadOnlyList<IReadOnlyList<long>>> ReplaceFilesChunksAsync(IReadOnlyList<FileChunks> files, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<int> DeleteFileChunksAsync(long fileId, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<int> UpsertEmbeddingsAsync(IReadOnlyList<ChunkEmbedding> embeddings, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<long> CountChunksAsync(CancellationToken cancellationToken = default) => Task.FromResult((long)owner.ChunkList.Count);

        public Task<IReadOnlyList<FileChunkSummary>> ListFilesAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<IndexSize> GetSizeAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    private sealed class FakeVectors(FakeIndex owner) : IVectorIndex
    {
        public string Backend => "fake";

        public Task<IReadOnlyList<VectorHit>> SearchAsync(ReadOnlyMemory<sbyte> query, int k, ChunkFilter? filter = null, CancellationToken cancellationToken = default)
        {
            lock (owner)
            {
                owner.VectorCalls++;
                owner.Filters.Add(filter);
            }

            return owner.Vector(query, filter, cancellationToken);
        }

        public Task UpsertAsync(long chunkId, ReadOnlyMemory<sbyte> vector, VectorMetadata metadata, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<int> DeleteByFileAsync(long fileId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    private sealed class FakeSymbols(FakeIndex owner) : ISymbolRepository
    {
        public Task<IReadOnlyList<SymbolRecord>> FindSymbolsByNameAsync(string name, int limit = 50, CancellationToken cancellationToken = default)
        {
            lock (owner)
            {
                owner.SymbolCalls++;
            }

            return Task.FromResult<IReadOnlyList<SymbolRecord>>([.. owner.SymbolList.Where(s => string.Equals(s.Name, name, StringComparison.OrdinalIgnoreCase)).Take(limit)]);
        }

        public Task<IReadOnlyList<long>> ReplaceFileSymbolsAsync(long fileId, IReadOnlyList<NewSymbol> symbols, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task ReplaceFileReferencesAsync(long fileId, IReadOnlyList<SymbolReference> references, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<SymbolRecord?> GetSymbolAsync(long symbolId, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<IReadOnlyList<SymbolRecord>> GetSymbolsByFileAsync(long fileId, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<IReadOnlyList<SymbolReference>> GetReferencesInFileAsync(long fileId, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<IReadOnlyList<SymbolReference>> GetReferencesToAsync(long symbolId, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<IReadOnlyList<SymbolReference>> GetAllReferencesAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<long> CountSymbolsAsync(CancellationToken cancellationToken = default) => Task.FromResult((long)owner.SymbolList.Count);

        public Task<string?> GetSymbolsVersionAsync(CancellationToken cancellationToken = default) => Task.FromResult<string?>(null);

        public Task SetSymbolsVersionAsync(string version, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
}

/// <summary>An embedder that returns a fixed vector, counts its calls and can be slowed down or made to fail.</summary>
internal sealed class FakeEmbedder(int dimensions = 4) : IQueryEmbedder
{
    private int _calls;

    public int Calls => Volatile.Read(ref _calls);

    public TimeSpan Delay { get; set; }

    public Exception? Failure { get; set; }

    public List<(string Text, string? Model, int? Dimensions)> Requests { get; } = [];

    public async Task<QueryEmbedding> EmbedQueryAsync(string text, string? modelId, int? dims, CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref _calls);
        lock (Requests)
        {
            Requests.Add((text, modelId, dims));
        }

        if (Delay > TimeSpan.Zero)
        {
            await Task.Delay(Delay, cancellationToken);
        }

        if (Failure is not null)
        {
            throw Failure;
        }

        return new QueryEmbedding([.. Enumerable.Repeat((sbyte)1, dimensions)], modelId ?? "fake");
    }
}

/// <summary>A clock that only moves when the test says so (the query vector cache reads the time only).</summary>
internal sealed class SteppingClock : TimeProvider
{
    private DateTimeOffset _now = new(2026, 10, 15, 9, 0, 0, TimeSpan.Zero);

    public override DateTimeOffset GetUtcNow() => _now;

    public void Advance(TimeSpan by) => _now += by;
}

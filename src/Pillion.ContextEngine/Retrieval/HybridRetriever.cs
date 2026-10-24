using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Pillion.ContextEngine.Chunking;
using Pillion.ContextEngine.Symbols;
using Pillion.LocalStore.Index;

namespace Pillion.ContextEngine.Retrieval;

/// <summary>
/// Hybrid retrieval (design section 7, steps 1 to 4): understands the query, gathers lexical (BM25), vector and exact
/// symbol-name candidates concurrently within a time budget, fuses them with Reciprocal Rank Fusion, applies the focus
/// boost and the reranker, then expands each hit to its full member with a neighbour on each side, removes overlap and
/// caps the hits per file. Works over any number of open indexes; thread-safe.
/// </summary>
public sealed partial class HybridRetriever
{
    /// <summary>How many fused candidates the reranker sees (design section 7: the top 30).</summary>
    public const int RerankDepth = 30;

    private const int MaxDefinitionsPerName = 5;

    private readonly IQueryEmbedder? _embedder;
    private readonly IReranker _reranker;
    private readonly RetrievalOptions _options;
    private readonly TimeProvider _time;
    private readonly ILogger _logger;

    /// <summary>Initializes a new instance of the <see cref="HybridRetriever"/> class.</summary>
    /// <param name="embedder">Embeds the question for the vector leg, or <see langword="null"/> when no embedding model is available (the vector leg is then skipped and the response marked degraded).</param>
    /// <param name="options">Tuning, or <see langword="null"/> for <see cref="RetrievalOptions.Default"/>.</param>
    /// <param name="reranker">Reorders the top fused candidates, or <see langword="null"/> for <see cref="NoOpReranker"/>.</param>
    /// <param name="time">The clock for budgets, timings and the query vector cache.</param>
    /// <param name="logger">The logger. Query text is never logged.</param>
    public HybridRetriever(IQueryEmbedder? embedder, RetrievalOptions? options = null, IReranker? reranker = null, TimeProvider? time = null, ILogger? logger = null)
    {
        _embedder = embedder;
        _options = options ?? RetrievalOptions.Default;
        _reranker = reranker ?? NoOpReranker.Instance;
        _time = time ?? TimeProvider.System;
        _logger = logger ?? NullLogger.Instance;
        QueryVectors = new QueryVectorCache(_options.QueryVectorCacheTime, _options.QueryVectorCacheSize, _time);
    }

    /// <summary>Gets the cache of recent query vectors.</summary>
    public QueryVectorCache QueryVectors { get; }

    /// <summary>Gets the options.</summary>
    public RetrievalOptions Options => _options;

    /// <summary>Searches the given indexes.</summary>
    /// <param name="query">The query.</param>
    /// <param name="indexes">The open indexes to search; empty gives an empty response.</param>
    /// <param name="cancellationToken">Cancels the search.</param>
    /// <returns>The hits and timings.</returns>
    public async Task<RetrievalResponse> SearchAsync(RetrievalQuery query, IReadOnlyList<IRetrievalIndex> indexes, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        ArgumentNullException.ThrowIfNull(indexes);
        var started = _time.GetTimestamp();
        if (indexes.Count == 0 || string.IsNullOrWhiteSpace(query.Text))
        {
            return RetrievalResponse.Empty with { TotalMs = Elapsed(started) };
        }

        var analysis = QueryAnalyzer.Analyze(query.Text);
        var filter = new ChunkFilter
        {
            Partitions = query.Partitions.Count == 0 ? null : query.Partitions,
            Languages = query.Languages.Count == 0 ? null : query.Languages,
        };
        var budget = query.TimeBudget is { } b && b > TimeSpan.Zero ? b : _options.TimeBudget;
        var candidateBudget = TimeSpan.FromTicks((long)(budget.Ticks * Math.Clamp(_options.CandidateBudgetShare, 0.1, 1.0)));

        using var legs = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var lexical = query.Mode is RetrievalSearchMode.Hybrid or RetrievalSearchMode.Lexical or RetrievalSearchMode.LexicalAndSymbol
            ? StartLeg("lexical", ct => LexicalAsync(analysis, indexes, filter, ct), legs.Token)
            : null;
        var vector = query.Mode is RetrievalSearchMode.Hybrid or RetrievalSearchMode.Vector
            ? StartLeg("vector", ct => VectorAsync(query.Text, indexes, filter, ct), legs.Token)
            : null;
        var symbol = query.Mode is RetrievalSearchMode.Hybrid or RetrievalSearchMode.Symbol or RetrievalSearchMode.LexicalAndSymbol
            ? StartLeg("symbol", ct => SymbolAsync(analysis, indexes, filter, ct), legs.Token)
            : null;

        // The focus files (and files the query names) are looked up alongside; they are cheap and not part of the budget.
        var focus = Task.Run(() => ResolveFocusAsync(query, analysis, indexes, cancellationToken), cancellationToken);

        var running = new[] { lexical, vector, symbol }.Where(t => t is not null).Select(t => (Task)t!.Task).ToArray();
        try
        {
            await Task.WhenAll(running).WaitAsync(candidateBudget, _time, cancellationToken).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            // Over budget: use what is ready, below.
        }
        catch (Exception) when (!cancellationToken.IsCancellationRequested)
        {
            // A leg failed: the others may still have answered, below.
        }

        var reasons = new List<string>();
        var lexicalList = Collect(lexical, reasons);
        var vectorList = Collect(vector, reasons);
        var symbolList = Collect(symbol, reasons);
        await legs.CancelAsync().ConfigureAwait(false);

        IReadOnlyList<IReadOnlyDictionary<long, int>> focusRanks;
        try
        {
            focusRanks = await focus.ConfigureAwait(false);
        }
        catch (Exception) when (!cancellationToken.IsCancellationRequested)
        {
            focusRanks = [.. indexes.Select(_ => (IReadOnlyDictionary<long, int>)new Dictionary<long, int>())];
        }

        var fusionStarted = _time.GetTimestamp();
        var hits = await FuseAndExpandAsync(query, indexes, lexicalList, vectorList, symbolList, focusRanks, cancellationToken).ConfigureAwait(false);
        var fusionMs = Elapsed(fusionStarted);

        var response = new RetrievalResponse
        {
            Hits = hits,
            LexicalMs = lexical?.Milliseconds ?? 0,
            VectorMs = vector?.Milliseconds ?? 0,
            SymbolMs = symbol?.Milliseconds ?? 0,
            FusionMs = fusionMs,
            TotalMs = Elapsed(started),
            Degraded = reasons.Count > 0,
            DegradedReasons = reasons,
        };

        if (response.Degraded)
        {
            LogDegraded(_logger, reasons, response.TotalMs);
        }

        LogSearched(_logger, query.Mode, indexes.Count, hits.Count, response.LexicalMs, response.VectorMs, response.SymbolMs, response.FusionMs, response.TotalMs);
        return response;
    }

    private static List<Key> Collect(Leg? leg, List<string> reasons)
    {
        if (leg is null)
        {
            return [];
        }

        var task = leg.Task;
        if (task.IsCompletedSuccessfully)
        {
            var (keys, note) = task.Result;
            if (note is not null)
            {
                reasons.Add(leg.Name + ": " + note);
            }

            return keys;
        }

        leg.Stop();
        if (task.IsFaulted)
        {
            reasons.Add(leg.Name + " failed: " + (task.Exception?.GetBaseException().Message ?? "unknown error"));
        }
        else
        {
            reasons.Add(leg.Name + " over budget");
        }

        // Observe a late failure of an abandoned leg so it is never reported as unobserved.
        _ = task.ContinueWith(t => _ = t.Exception, CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        return [];
    }

    /// <summary>
    /// Runs one search per index, concurrently when there are several. With one index (the usual case) the search runs on
    /// the leg's own thread: another hop through the thread pool only adds queueing, and on a busy machine (a CI runner, a
    /// parallel test run) queueing alone can push a lexical search that has its answer past the candidate budget.
    /// </summary>
    private static async Task<T[]> PerIndexAsync<TItem, T>(IReadOnlyList<TItem> items, Func<TItem, int, Task<T>> search, CancellationToken cancellationToken)
    {
        if (items.Count == 1)
        {
            return [await search(items[0], 0).ConfigureAwait(false)];
        }

        return await Task.WhenAll(items.Select((item, i) => Task.Run(() => search(item, i), cancellationToken))).ConfigureAwait(false);
    }

    private Leg StartLeg(string name, Func<CancellationToken, Task<(List<Key> Keys, string? Note)>> run, CancellationToken cancellationToken)
    {
        var leg = new Leg(name, _time);
        leg.Task = Task.Run(
            async () =>
            {
                try
                {
                    return await run(cancellationToken).ConfigureAwait(false);
                }
                finally
                {
                    leg.Stop();
                }
            },
            cancellationToken);
        return leg;
    }

    private async Task<(List<Key> Keys, string? Note)> LexicalAsync(QueryAnalysis analysis, IReadOnlyList<IRetrievalIndex> indexes, ChunkFilter filter, CancellationToken cancellationToken)
    {
        if (analysis.LexicalQuery.Length == 0)
        {
            return ([], null);
        }

        var k = _options.LexicalCandidates;
        var perIndex = await PerIndexAsync(
            indexes,
            async (index, i) => (await index.Chunks.SearchLexicalAsync(analysis.LexicalQuery, k, LexicalSearchOptions.Question, filter, cancellationToken).ConfigureAwait(false))
                .Select(h => (Key: new Key(i, h.ChunkId), h.Score)),
            cancellationToken).ConfigureAwait(false);

        // BM25 scores of different indexes are close enough to merge directly (same tokenizer and weights).
        return ([.. perIndex.SelectMany(h => h).OrderByDescending(h => h.Score).ThenBy(h => h.Key.Index).Take(k).Select(h => h.Key)], null);
    }

    private async Task<(List<Key> Keys, string? Note)> VectorAsync(string text, IReadOnlyList<IRetrievalIndex> indexes, ChunkFilter filter, CancellationToken cancellationToken)
    {
        var embedded = indexes.Select((index, i) => (Index: index, Position: i)).Where(x => x.Index.VectorDimensions is > 0).ToList();
        if (embedded.Count == 0)
        {
            return ([], null);
        }

        if (_embedder is null)
        {
            return ([], "no embedding model is available");
        }

        var notes = new List<string>();
        var k = _options.VectorCandidates;
        var hits = new List<(Key Key, double Distance)>();
        foreach (var group in embedded.GroupBy(x => (Model: x.Index.EmbeddingModelId ?? string.Empty, Dimensions: x.Index.VectorDimensions!.Value)))
        {
            var vector = await EmbedAsync(text, group.Key.Model, group.Key.Dimensions, cancellationToken).ConfigureAwait(false);
            if (vector.Vector.Length != group.Key.Dimensions)
            {
                notes.Add($"the query vector has {vector.Vector.Length} dimensions, the index {group.Key.Dimensions}");
                continue;
            }

            var results = await PerIndexAsync(
                [.. group],
                async (x, _) => (await x.Index.Vectors.SearchAsync(vector.Vector, k, filter, cancellationToken).ConfigureAwait(false))
                    .Select(h => (Key: new Key(x.Position, h.ChunkId), h.Distance)),
                cancellationToken).ConfigureAwait(false);
            hits.AddRange(results.SelectMany(r => r));
        }

        var keys = hits.OrderBy(h => h.Distance).ThenBy(h => h.Key.Index).Take(k).Select(h => h.Key).ToList();
        return (keys, notes.Count == 0 ? null : string.Join("; ", notes));
    }

    private async Task<QueryEmbedding> EmbedAsync(string text, string model, int dimensions, CancellationToken cancellationToken)
    {
        if (QueryVectors.TryGet(text, model, dimensions, out var cached))
        {
            return cached;
        }

        var vector = await _embedder!.EmbedQueryAsync(text, model.Length == 0 ? null : model, dimensions, cancellationToken).ConfigureAwait(false);
        QueryVectors.Set(text, model, dimensions, vector);
        return vector;
    }

    private async Task<(List<Key> Keys, string? Note)> SymbolAsync(QueryAnalysis analysis, IReadOnlyList<IRetrievalIndex> indexes, ChunkFilter filter, CancellationToken cancellationToken)
    {
        if (analysis.SymbolCandidates.Count == 0)
        {
            return ([], null);
        }

        var perIndex = await PerIndexAsync(
            indexes,
            (index, i) => SymbolHitsAsync(index, i, analysis.SymbolCandidates, filter, cancellationToken),
            cancellationToken).ConfigureAwait(false);

        // Interleave the indexes by the order of the names in the query, then by kind.
        var keys = perIndex.SelectMany(h => h)
            .OrderBy(h => h.Order)
            .ThenBy(h => h.Key.Index)
            .Select(h => h.Key)
            .Distinct()
            .Take(_options.SymbolCandidates)
            .ToList();
        return (keys, null);
    }

    private static async Task<List<(Key Key, int Order)>> SymbolHitsAsync(IRetrievalIndex index, int position, IReadOnlyList<string> candidates, ChunkFilter filter, CancellationToken cancellationToken)
    {
        // Definitions: (name order, kind rank) -> where.
        var definitions = new List<(int Order, long? FileId, string? Path, int Line)>();
        for (var c = 0; c < candidates.Count; c++)
        {
            var (qualifier, name) = SplitQualified(candidates[c]);
            if (index.Graph is { } graph)
            {
                definitions.AddRange(graph.FindDefinitions(candidates[c])
                    .Where(d => string.Equals(d.Name, name, StringComparison.Ordinal) && d.Kind != SymbolKind.Constructor)
                    .Select(d => (Rank: KindRank(SymbolKinds.Name(d.Kind)), d))
                    .OrderBy(x => x.Rank)
                    .Take(MaxDefinitionsPerName)
                    .Select(x => (Order(c, x.Rank), (long?)null, (string?)x.d.FilePath, x.d.StartLine)));
            }
            else
            {
                var records = await index.Symbols.FindSymbolsByNameAsync(name, 50, cancellationToken).ConfigureAwait(false);
                definitions.AddRange(records
                    .Where(r => string.Equals(r.Name, name, StringComparison.Ordinal) && r.Kind != "constructor")
                    .Where(r => qualifier is null || ContainerEndsWith(r.Container, qualifier))
                    .Select(r => (Rank: KindRank(r.Kind), r))
                    .OrderBy(x => x.Rank)
                    .Take(MaxDefinitionsPerName)
                    .Select(x => (Order(c, x.Rank), (long?)x.r.FileId, (string?)null, x.r.StartLine)));
            }
        }

        if (definitions.Count == 0)
        {
            return [];
        }

        var paths = definitions.Where(d => d.FileId is null).Select(d => d.Path!).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var byPath = paths.Count == 0
            ? new Dictionary<string, RetrievalFile>()
            : await index.FindFilesAsync(paths, cancellationToken).ConfigureAwait(false);
        var resolved = definitions
            .Select(d => (d.Order, FileId: d.FileId ?? (byPath.TryGetValue(d.Path!, out var f) ? f.Id : 0), d.Line))
            .Where(d => d.FileId > 0)
            .ToList();
        var files = await index.GetFilesAsync([.. resolved.Select(d => d.FileId).Distinct()], cancellationToken).ConfigureAwait(false);

        var chunksByFile = new Dictionary<long, IReadOnlyList<ChunkRecord>>();
        var hits = new List<(Key, int)>();
        foreach (var (order, fileId, line) in resolved.OrderBy(d => d.Order))
        {
            if (!files.TryGetValue(fileId, out var file) || !Matches(filter, file))
            {
                continue;
            }

            if (!chunksByFile.TryGetValue(fileId, out var chunks))
            {
                chunks = await index.Chunks.GetChunksByFileAsync(fileId, cancellationToken).ConfigureAwait(false);
                chunksByFile[fileId] = chunks;
            }

            var chunk = chunks.Where(ch => ch.StartLine <= line && ch.EndLine >= line).MinBy(ch => ch.EndLine - ch.StartLine);
            if (chunk is not null)
            {
                hits.Add((new Key(position, chunk.Id), order));
            }
        }

        return hits;

        static int Order(int candidate, int kindRank) => (candidate * 4) + kindRank;
    }

    private static async Task<IReadOnlyList<IReadOnlyDictionary<long, int>>> ResolveFocusAsync(RetrievalQuery query, QueryAnalysis analysis, IReadOnlyList<IRetrievalIndex> indexes, CancellationToken cancellationToken)
    {
        var result = new List<IReadOnlyDictionary<long, int>>(indexes.Count);
        foreach (var index in indexes)
        {
            // Focus order: files the query names first, then the open and recently edited files, most recent first.
            var ranks = new Dictionary<long, int>();
            var next = 1;
            if (analysis.FileNames.Count > 0)
            {
                var named = await FindByNameAsync(index, analysis.FileNames, cancellationToken).ConfigureAwait(false);
                foreach (var name in analysis.FileNames)
                {
                    if (named.TryGetValue(name, out var ids))
                    {
                        foreach (var id in ids)
                        {
                            ranks.TryAdd(id, next);
                        }

                        next++;
                    }
                }
            }

            var relative = query.FocusFiles
                .Select(path => RelativeTo(index.RootPath, path))
                .ToList();
            var wanted = relative.Where(p => p is not null).Select(p => p!).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            if (wanted.Count > 0)
            {
                var found = await index.FindFilesAsync(wanted, cancellationToken).ConfigureAwait(false);
                foreach (var path in relative)
                {
                    if (path is not null && found.TryGetValue(path, out var file) && ranks.TryAdd(file.Id, next))
                    {
                        next++;
                    }
                }
            }

            result.Add(ranks);
        }

        return result;
    }

    private static async Task<Dictionary<string, List<long>>> FindByNameAsync(IRetrievalIndex index, IReadOnlyList<string> names, CancellationToken cancellationToken)
    {
        // A named file is matched by its whole relative path or by its last segments (Greeter.cs, Api/Program.cs).
        var map = new Dictionary<string, List<long>>(StringComparer.OrdinalIgnoreCase);
        var exact = await index.FindFilesAsync(names, cancellationToken).ConfigureAwait(false);
        foreach (var name in names)
        {
            if (exact.TryGetValue(name, out var file))
            {
                map[name] = [file.Id];
            }
        }

        var missing = names.Where(n => !map.ContainsKey(n)).ToList();
        if (missing.Count > 0 && index is IRetrievalFileNameLookup lookup)
        {
            foreach (var (name, ids) in await lookup.FindFilesByNameAsync(missing, cancellationToken).ConfigureAwait(false))
            {
                map[name] = [.. ids];
            }
        }

        return map;
    }

    private async Task<IReadOnlyList<RetrievalHit>> FuseAndExpandAsync(
        RetrievalQuery query,
        IReadOnlyList<IRetrievalIndex> indexes,
        List<Key> lexical,
        List<Key> vector,
        List<Key> symbol,
        IReadOnlyList<IReadOnlyDictionary<long, int>> focus,
        CancellationToken cancellationToken)
    {
        var k = _options.RrfK;
        var lexicalRanks = ReciprocalRankFusion.Ranks(lexical);
        var vectorRanks = ReciprocalRankFusion.Ranks(vector);
        var symbolRanks = ReciprocalRankFusion.Ranks(symbol);
        var fused = ReciprocalRankFusion.Fuse([lexical, vector, symbol], k);
        if (fused.Count == 0)
        {
            return [];
        }

        // The candidates' chunks and files, per index.
        var chunks = new Dictionary<Key, ChunkRecord>();
        var files = new Dictionary<(int Index, long FileId), RetrievalFile>();
        foreach (var group in fused.Keys.GroupBy(key => key.Index))
        {
            var index = indexes[group.Key];
            var records = await index.Chunks.GetChunksAsync([.. group.Select(key => key.ChunkId)], cancellationToken).ConfigureAwait(false);
            foreach (var record in records)
            {
                chunks[new Key(group.Key, record.Id)] = record;
            }

            var found = await index.GetFilesAsync([.. records.Select(r => r.FileId).Distinct()], cancellationToken).ConfigureAwait(false);
            foreach (var (id, file) in found)
            {
                files[(group.Key, id)] = file;
            }
        }

        var scored = fused
            .Where(pair => chunks.ContainsKey(pair.Key) && files.ContainsKey((pair.Key.Index, chunks[pair.Key].FileId)))
            .Select(pair =>
            {
                var chunk = chunks[pair.Key];
                var focusRank = focus[pair.Key.Index].GetValueOrDefault(chunk.FileId);
                return new Candidate(pair.Key, chunk, pair.Value, pair.Value + ReciprocalRankFusion.Contribution(focusRank, k, _options.FocusWeight), focusRank);
            })
            .OrderByDescending(c => c.Score)
            .ThenByDescending(c => c.Fused)
            .ThenBy(c => Best(lexicalRanks, c.Key))
            .ThenBy(c => Best(vectorRanks, c.Key))
            .ThenBy(c => c.Key.Index)
            .ThenBy(c => c.Key.ChunkId)
            .ToList();

        scored = await RerankAsync(query.Text, scored, cancellationToken).ConfigureAwait(false);

        // Expansion, overlap removal and the per-file cap, best first, until the result list is full.
        var maxResults = query.MaxResults > 0 ? Math.Min(query.MaxResults, _options.MaxResultsLimit) : _options.DefaultMaxResults;
        var maxPerFile = query.MaxPerFile > 0 ? query.MaxPerFile : _options.DefaultMaxPerFile;
        var fileChunks = new Dictionary<(int, long), IReadOnlyList<ChunkRecord>>();
        var accepted = new Dictionary<(int, long), List<(int Start, int End)>>();
        var hits = new List<RetrievalHit>(maxResults);
        foreach (var candidate in scored)
        {
            if (hits.Count >= maxResults)
            {
                break;
            }

            var fileKey = (candidate.Key.Index, candidate.Chunk.FileId);
            var spans = accepted.TryGetValue(fileKey, out var list) ? list : accepted[fileKey] = [];
            if (spans.Count >= maxPerFile)
            {
                continue;
            }

            if (!fileChunks.TryGetValue(fileKey, out var all))
            {
                all = [.. (await indexes[candidate.Key.Index].Chunks.GetChunksByFileAsync(candidate.Chunk.FileId, cancellationToken).ConfigureAwait(false))
                    .OrderBy(c => c.StartLine).ThenBy(c => c.Id)];
                fileChunks[fileKey] = all;
            }

            var position = IndexOf(all, candidate.Chunk.Id);
            if (position < 0)
            {
                continue; // The file was re-indexed between the search and now.
            }

            var (first, last) = SpanExpander.Expand(all, position, _options.MaxExpandedTokens);
            if (SpanExpander.Place(all, first, last, position, spans) is not { } placed)
            {
                continue;
            }

            spans.Add((placed.StartLine, placed.EndLine));
            var index = indexes[candidate.Key.Index];
            var file = files[fileKey];
            hits.Add(new RetrievalHit
            {
                IndexId = index.IndexId,
                ChunkId = candidate.Key.ChunkId,
                FilePath = FullPath(index.RootPath, file.Path),
                RelativePath = file.Path,
                StartLine = placed.StartLine,
                EndLine = placed.EndLine,
                Breadcrumb = candidate.Chunk.Breadcrumb,
                Kind = candidate.Chunk.Kind,
                Partition = file.Partition,
                Language = file.Language,
                Snippet = SpanExpander.Snippet(candidate.Chunk.Text, _options.MaxSnippetChars),
                Text = query.IncludeText ? SpanExpander.BuildText(all, placed) : null,
                LexicalRank = Best(lexicalRanks, candidate.Key) is var l && l < int.MaxValue ? l : 0,
                VectorRank = Best(vectorRanks, candidate.Key) is var v && v < int.MaxValue ? v : 0,
                SymbolRank = Best(symbolRanks, candidate.Key) is var s && s < int.MaxValue ? s : 0,
                FocusRank = candidate.FocusRank,
                FusedScore = candidate.Fused,
                Score = candidate.Score,
            });
        }

        return hits;
    }

    private async Task<List<Candidate>> RerankAsync(string text, List<Candidate> scored, CancellationToken cancellationToken)
    {
        if (_reranker is NoOpReranker || scored.Count < 2)
        {
            return scored;
        }

        var depth = Math.Min(RerankDepth, scored.Count);
        var top = scored.Take(depth).ToList();
        var byKey = top.ToDictionary(c => KeyText(c.Key));
        var reranked = await _reranker.RerankAsync(
            text,
            [.. top.Select(c => new RerankCandidate(KeyText(c.Key), c.Chunk.Breadcrumb + "\n" + c.Chunk.Text, c.Score))],
            cancellationToken).ConfigureAwait(false);
        var reordered = reranked.Where(r => byKey.ContainsKey(r.Key)).Select(r => byKey[r.Key] with { Score = r.Score }).DistinctBy(c => c.Key).ToList();
        reordered.AddRange(top.Where(c => !reordered.Any(r => r.Key == c.Key)));
        reordered.AddRange(scored.Skip(depth));
        return reordered;

        static string KeyText(Key key) => key.Index + ":" + key.ChunkId;
    }

    private static int Best(Dictionary<Key, int> ranks, Key key) => ranks.TryGetValue(key, out var rank) ? rank : int.MaxValue;

    private static int IndexOf(IReadOnlyList<ChunkRecord> chunks, long id)
    {
        for (var i = 0; i < chunks.Count; i++)
        {
            if (chunks[i].Id == id)
            {
                return i;
            }
        }

        return -1;
    }

    private static bool Matches(ChunkFilter filter, RetrievalFile file) =>
        (filter.Partitions is not { Count: > 0 } partitions || partitions.Contains(file.Partition, StringComparer.Ordinal))
        && (filter.Languages is not { Count: > 0 } languages || languages.Contains(file.Language, StringComparer.Ordinal));

    /// <summary>Kinds a question most likely means first: types and callables, then members holding data, then containers.</summary>
    private static int KindRank(string kind) => kind switch
    {
        "namespace" or "module" => 2,
        "field" or "const" or "variable" or "enum-member" or "element-id" or "selector" => 1,
        _ => 0,
    };

    private static (string? Qualifier, string Name) SplitQualified(string identifier)
    {
        var colon = identifier.LastIndexOf("::", StringComparison.Ordinal);
        var dot = identifier.LastIndexOf('.');
        var (cut, width) = colon > dot ? (colon, 2) : (dot, 1);
        if (cut <= 0)
        {
            return (null, identifier);
        }

        var head = identifier[..cut];
        return (head[(Math.Max(head.LastIndexOf('.'), head.LastIndexOf(':')) + 1)..], identifier[(cut + width)..]);
    }

    private static bool ContainerEndsWith(string? container, string qualifier)
    {
        if (string.IsNullOrEmpty(container))
        {
            return false;
        }

        var last = container.Split([Breadcrumbs.Separator, ".", "::"], StringSplitOptions.RemoveEmptyEntries).LastOrDefault() ?? string.Empty;
        var generic = last.IndexOf('<', StringComparison.Ordinal);
        return string.Equals((generic > 0 ? last[..generic] : last).Trim(), qualifier, StringComparison.Ordinal);
    }

    /// <summary>The path of <paramref name="fullPath"/> relative to <paramref name="root"/> with <c>/</c> separators, or <see langword="null"/> when it is not under it.</summary>
    internal static string? RelativeTo(string root, string fullPath)
    {
        if (string.IsNullOrEmpty(root) || string.IsNullOrEmpty(fullPath))
        {
            return null;
        }

        var normalizedRoot = root.Replace('\\', '/').TrimEnd('/') + "/";
        var normalized = fullPath.Replace('\\', '/');
        return normalized.StartsWith(normalizedRoot, StringComparison.OrdinalIgnoreCase) && normalized.Length > normalizedRoot.Length
            ? normalized[normalizedRoot.Length..]
            : null;
    }

    private static string FullPath(string root, string relative) =>
        Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar));

    private double Elapsed(long started) => _time.GetElapsedTime(started).TotalMilliseconds;

    [LoggerMessage(EventId = 2911, Level = LogLevel.Information, Message = "Retrieval was degraded ({Reasons}); answered in {TotalMs:F0} ms.")]
    private static partial void LogDegraded(ILogger logger, IReadOnlyList<string> reasons, double totalMs);

    [LoggerMessage(EventId = 2910, Level = LogLevel.Debug, Message = "Retrieval ({Mode}) over {Indexes} index(es): {Hits} hits; lexical {LexicalMs:F1} ms, vector {VectorMs:F1} ms, symbol {SymbolMs:F1} ms, fusion {FusionMs:F1} ms, total {TotalMs:F1} ms.")]
    private static partial void LogSearched(ILogger logger, RetrievalSearchMode mode, int indexes, int hits, double lexicalMs, double vectorMs, double symbolMs, double fusionMs, double totalMs);

    /// <summary>A candidate chunk: its position in the indexes list and its row id.</summary>
    private readonly record struct Key(int Index, long ChunkId);

    private sealed record Candidate(Key Key, ChunkRecord Chunk, double Fused, double Score, int FocusRank);

    /// <summary>A running candidate leg and how long it ran.</summary>
    private sealed class Leg(string name, TimeProvider time)
    {
        private readonly long _started = time.GetTimestamp();
        private double _milliseconds = -1;

        public string Name => name;

        public Task<(List<Key> Keys, string? Note)> Task { get; set; } = null!;

        public double Milliseconds => Volatile.Read(ref _milliseconds) is var ms && ms >= 0 ? ms : time.GetElapsedTime(_started).TotalMilliseconds;

        /// <summary>Records the time at the first call: when the leg finished, or when the search stopped waiting for it.</summary>
        public void Stop()
        {
            var ms = time.GetElapsedTime(_started).TotalMilliseconds;
            Interlocked.CompareExchange(ref _milliseconds, ms, -1);
        }
    }
}

/// <summary>
/// An optional capability of an <see cref="IRetrievalIndex"/>: finding files by their name or trailing path segments, for
/// file names a query mentions (<c>Greeter.cs</c>, <c>Api/Program.cs</c>).
/// </summary>
public interface IRetrievalFileNameLookup
{
    /// <summary>Finds files whose relative path is a name or ends with <c>/</c> and the name, without regard to case.</summary>
    /// <param name="names">File names or trailing paths, with <c>/</c> separators.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The matching file ids per name (names without a match are left out).</returns>
    Task<IReadOnlyDictionary<string, IReadOnlyList<long>>> FindFilesByNameAsync(IReadOnlyCollection<string> names, CancellationToken cancellationToken);
}

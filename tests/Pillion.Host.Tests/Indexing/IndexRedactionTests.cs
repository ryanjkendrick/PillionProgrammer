using System.Text.RegularExpressions;
using Pillion.Common.Redaction;
using Pillion.ContextEngine.Retrieval;
using Pillion.Contracts;
using Pillion.Host.Context.Retrieval;
using Pillion.Host.Indexing;

namespace Pillion.Host.Tests.Indexing;

/// <summary>
/// The secret scanner at index time (work item 3.2, issue #103, design section 12): a secret in an ordinary source file never
/// reaches <c>index.db</c>, the FTS rows or the embedder; the stored chunk carries an index placeholder, the root counts
/// its redacted chunks, and a retrieved chunk is served with the placeholder and never restored.
/// </summary>
public sealed class IndexRedactionTests : IDisposable
{
    // Shaped like real credentials, fabricated here: an AWS access key id and a classic GitHub token.
    private const string AwsKey = "AKIAJ7Q2M4N6P3R5T2V7";
    private const string GitHubToken = "ghp_K3m9Xq2Lp8Rt4Wz7Yb1Nc5Vd6Fh0Jg2Sa4E9";

    private readonly IndexingWorkspace _workspace = new();

    public void Dispose() => _workspace.Dispose();

    [Fact]
    public async Task ASecretInASourceFile_IsRedactedBeforeTheChunkIsStored_AndTheRootCountsIt()
    {
        Fixtures.Copy("shop", _workspace.Repository);
        File.WriteAllText(_workspace.File("src/Secrets.cs"), SecretsFile(AwsKey, GitHubToken));
        var embedder = new FakeEmbedder();
        await using var harness = new IndexingHarness(_workspace.Data, embedder);
        await harness.StartAsync(_workspace.Repository);
        var status = await harness.WaitForIdleAsync();

        var root = Assert.Single(status.Roots);
        Assert.True(root.RedactedChunkCount >= 1, "the chunk holding the secrets is counted");
        Assert.Equal(root.RedactedChunkCount, harness.Service.LastRunStatistics!.ChunksRedacted);

        // The stored row: placeholders in place of the values, nothing of the originals.
        var storage = harness.Root.Storage;
        var file = (await harness.Root.Store.Files.GetAsync("src/Secrets.cs"))!;
        var chunks = await storage.Chunks.GetChunksByFileAsync(file.Id);
        var redacted = chunks.Where(c => c.Text.Contains(IndexRedactor.PlaceholderPrefix, StringComparison.Ordinal)).ToList();
        Assert.Equal(root.RedactedChunkCount, redacted.Count);
        Assert.Contains(redacted, c => c.Text.Contains("<redacted-index:aws-access-key>", StringComparison.Ordinal));
        Assert.Contains(redacted, c => c.Text.Contains("<redacted-index:github-token>", StringComparison.Ordinal));
        Assert.All(chunks, c => Assert.DoesNotContain(AwsKey, c.Text, StringComparison.Ordinal));
        Assert.All(chunks, c => Assert.DoesNotContain(GitHubToken, c.Text, StringComparison.Ordinal));
        Assert.Equal(root.RedactedChunkCount, await storage.Chunks.CountRedactedChunksAsync());

        // The lexical index was built from the redacted text, and the embedder saw only the redacted text.
        Assert.Empty(await storage.Chunks.SearchLexicalAsync(AwsKey, 5));
        Assert.NotEmpty(await storage.Chunks.SearchLexicalAsync("Secrets AwsKey", 5));
        Assert.DoesNotContain(embedder.Texts, t => t.Contains(AwsKey, StringComparison.Ordinal) || t.Contains(GitHubToken, StringComparison.Ordinal));
        Assert.Contains(embedder.Texts, t => t.Contains("<redacted-index:aws-access-key>", StringComparison.Ordinal));

        // Retrieval serves the placeholder as is; the model boundary's scanner does not count it again, and no session restores it.
        var catalog = new LiveIndexCatalog(harness.Service, harness.Flags);
        var retrieval = new RetrievalService(catalog, harness.Flags, new FakeQueryEmbedder(), new RetrievalOptions { TimeBudget = TimeSpan.FromSeconds(30) });
        var response = await retrieval.SearchDetailedAsync(new SearchRequest { Query = "Secrets AwsKey GitHubToken", MaxResults = 5, IncludeText = true }, CancellationToken.None);
        var hit = Assert.Single(response.Results, r => r.FilePath.EndsWith("Secrets.cs", StringComparison.Ordinal));
        Assert.Contains("<redacted-index:aws-access-key>", hit.Text, StringComparison.Ordinal);
        using var session = new RedactionSession();
        Assert.Same(hit.Text, session.Redact(hit.Text!));
        Assert.Equal(0, session.Redactions.Count);
        Assert.Equal(hit.Text, session.Restore(hit.Text!));
        Assert.Empty(session.FindUnresolved(hit.Text!));
        Assert.True(IndexRedactor.ContainsPlaceholder(hit.Text));

        // The whole database, not only the chunk rows: the originals are in none of them (the FTS table has its own copy).
        var mentions = await storage.Database.ReadAsync(
            (connection, _) =>
            {
                using var command = connection.CreateCommand();
                command.CommandText = "SELECT count(*) FROM chunks_fts WHERE chunks_fts MATCH $needle";
                command.Parameters.AddWithValue("$needle", "\"" + AwsKey + "\"");
                return Task.FromResult((long)command.ExecuteScalar()!);
            },
            CancellationToken.None);
        Assert.Equal(0L, mentions);
    }

    [Fact]
    public async Task TheRedactedCount_FollowsEdits_AndAnUnchangedRedactedChunkKeepsItsVector()
    {
        Fixtures.Copy("shop", _workspace.Repository);
        Fixtures.Age(_workspace.Repository);
        File.WriteAllText(_workspace.File("src/Secrets.cs"), SecretsFile(AwsKey, GitHubToken));
        var embedder = new FakeEmbedder();
        await using var harness = new IndexingHarness(_workspace.Data, embedder);
        await harness.StartAsync(_workspace.Repository);
        var status = await harness.WaitForIdleAsync();
        var first = status.Roots[0].RedactedChunkCount;
        Assert.True(first >= 1, "the chunk holding the secrets is counted");
        var embedded = embedder.ChunksEmbedded;

        // A second file with a secret: the count grows; the unchanged redacted chunk is not embedded again (its stored text
        // and its new text are both the redacted text, so the vector is reused).
        File.WriteAllText(_workspace.File("src/MoreSecrets.cs"), SecretsFile(AwsKey, "no-token-here").Replace("class Secrets", "class MoreSecrets", StringComparison.Ordinal));
        File.WriteAllText(_workspace.File("src/Secrets.cs"), SecretsFile(AwsKey, GitHubToken).Replace("namespace Shop;\n", "namespace Shop;\n\n// touched: the members' chunks keep their text\n", StringComparison.Ordinal));
        var sent = harness.Sender.Sent.Count;
        harness.Service.RequestRun();
        await harness.WaitForAsync(s => s.Phase == IndexPhase.Idle && harness.Sender.Sent.Count > sent && s.Roots[0].RedactedChunkCount > first, what: "the run that indexes the second secret-bearing file");
        var second = harness.Service.GetStatus().Roots[0].RedactedChunkCount;
        Assert.Equal(second, harness.Service.LastRunStatistics!.ChunksRedacted); // both files were (re)indexed this run
        Assert.True(harness.Service.LastRunStatistics.ChunksReused >= 1, "the unchanged redacted chunk's vector should be reused");
        Assert.True(embedder.ChunksEmbedded > embedded, "the new file's chunks are embedded");

        // The secrets are edited out: the count follows.
        File.WriteAllText(_workspace.File("src/Secrets.cs"), SecretsFile("not-a-key", "not-a-token"));
        File.Delete(_workspace.File("src/MoreSecrets.cs"));
        sent = harness.Sender.Sent.Count;
        harness.Service.RequestRun();
        var after = await harness.WaitForAsync(s => s.Phase == IndexPhase.Idle && harness.Sender.Sent.Count > sent && s.Roots[0].RedactedChunkCount == 0, what: "the run that removes the secrets");
        Assert.Equal(0, after.Roots[0].RedactedChunkCount);
        Assert.Equal(0L, await harness.Root.Storage.Chunks.CountRedactedChunksAsync());
    }

    [Fact]
    public async Task APrivateKeyLongerThanAChunk_IsRedactedInEveryChunkItSpans_IncludingTheOnesWithoutTheHeader()
    {
        // PR 121 review: the chunker splits a long PEM block before the per-chunk scan, so the later chunks hold key material
        // without the BEGIN header the pattern needs. The scan over the whole file finds the span; every chunk it overlaps is
        // redacted by offset.
        Fixtures.Copy("shop", _workspace.Repository);
        var body = PemBody(lines: 120);
        File.WriteAllText(_workspace.File("src/Certificates.cs"), CertificatesFile(body));
        var embedder = new FakeEmbedder();
        await using var harness = new IndexingHarness(_workspace.Data, embedder);
        await harness.StartAsync(_workspace.Repository);
        var status = await harness.WaitForIdleAsync();

        var record = (await harness.Root.Store.Files.GetAsync("src/Certificates.cs"))!;
        var chunks = await harness.Root.Storage.Chunks.GetChunksByFileAsync(record.Id);
        var keyChunks = chunks.Where(c => c.StartLine <= 7 + body.Count && c.EndLine >= 7).ToList();
        Assert.True(keyChunks.Count >= 2, $"the key should span several chunks; it spans {keyChunks.Count} of {chunks.Count}");
        Assert.All(chunks, c => Assert.DoesNotContain(body, line => c.Text.Contains(line, StringComparison.Ordinal)));
        Assert.All(keyChunks, c => Assert.Contains("<redacted-index:private-key-block>", c.Text, StringComparison.Ordinal));
        Assert.True(status.Roots[0].RedactedChunkCount >= keyChunks.Count, $"{status.Roots[0].RedactedChunkCount} redacted chunks");
        Assert.DoesNotContain(embedder.Texts, t => body.Any(line => t.Contains(line, StringComparison.Ordinal)));
        Assert.Contains(embedder.Texts, t => t.Contains("<redacted-index:private-key-block>", StringComparison.Ordinal));
        Assert.Empty(await harness.Root.Storage.Chunks.SearchLexicalAsync(body[body.Count / 2], 5));

        // No signature of the file holds any of the key either (a field's signature is its modifiers, type and name).
        var symbols = await harness.Root.Storage.Symbols.GetSymbolsByFileAsync(record.Id);
        Assert.Contains(symbols, s => s.Name == "Key");
        Assert.All(symbols, s => Assert.All(body, line => Assert.DoesNotContain(line[..16], s.Signature ?? string.Empty, StringComparison.Ordinal)));
    }

    [Fact]
    public async Task WhenTheWholeFileScanTimesOut_TheFilesChunksAndSignaturesAreWithheld_NotScannedChunkByChunk()
    {
        // PR 121 review (second round): a whole-file scan that timed out fell back to the per-chunk scans, which miss the body
        // chunks of a split PEM block, so the leak the first round fixed came back on that path. A file whose scan failed is
        // unverified: every chunk and every signature is stored as the scan-failed placeholder, as the per-chunk path does for
        // one chunk. The production patterns do not time out on this input, so the whole-file scan is injected; the per-chunk
        // scans are the real ones.
        Fixtures.Copy("shop", _workspace.Repository);
        var body = PemBody(lines: 120);
        File.WriteAllText(_workspace.File("src/Certificates.cs"), CertificatesFile(body));
        var embedder = new FakeEmbedder();
        var options = new IndexingOptions
        {
            MinimumRefreshInterval = TimeSpan.FromMilliseconds(200),
            PowerPollInterval = TimeSpan.FromMilliseconds(100),
            MemoryPollInterval = TimeSpan.FromMilliseconds(50),
            ReaderPollInterval = TimeSpan.FromMilliseconds(200),
            EmbeddingRetryDelay = TimeSpan.FromMilliseconds(10),
            SecretScan = text => text.Contains("class Certificates", StringComparison.Ordinal)
                ? throw new RegexMatchTimeoutException("injected: the whole-file scan timed out")
                : SecretScanner.Scan(text),
        };
        await using var harness = new IndexingHarness(_workspace.Data, embedder, options);
        await harness.StartAsync(_workspace.Repository);
        var status = await harness.WaitForIdleAsync();

        // Every chunk of the file is the placeholder, counted as redacted; nothing of the file reached the embedder or the FTS rows.
        var record = (await harness.Root.Store.Files.GetAsync("src/Certificates.cs"))!;
        var chunks = await harness.Root.Storage.Chunks.GetChunksByFileAsync(record.Id);
        Assert.True(chunks.Count >= 2, $"{chunks.Count} chunks");
        Assert.All(chunks, c => Assert.Equal(IndexRedactor.ScanFailedPlaceholder, c.Text));
        Assert.True(status.Roots[0].RedactedChunkCount >= chunks.Count, $"{status.Roots[0].RedactedChunkCount} redacted chunks for {chunks.Count} withheld");
        Assert.Equal(status.Roots[0].RedactedChunkCount, await harness.Root.Storage.Chunks.CountRedactedChunksAsync());
        Assert.DoesNotContain(embedder.Texts, t => body.Any(line => t.Contains(line, StringComparison.Ordinal)));
        Assert.DoesNotContain(embedder.Texts, t => t.Contains("BEGIN RSA PRIVATE KEY", StringComparison.Ordinal) || t.Contains("class Certificates", StringComparison.Ordinal));
        Assert.Contains(embedder.Texts, t => t.Contains(IndexRedactor.ScanFailedPlaceholder, StringComparison.Ordinal));
        Assert.Empty(await harness.Root.Storage.Chunks.SearchLexicalAsync(body[body.Count / 2], 5));

        // Its signatures are withheld too; the names, kinds and lines are kept.
        var symbols = await harness.Root.Storage.Symbols.GetSymbolsByFileAsync(record.Id);
        Assert.Contains(symbols, s => s.Name == "Key");
        Assert.Contains(symbols, s => s.Name == "Count");
        Assert.All(symbols, s => Assert.Equal(IndexRedactor.ScanFailedPlaceholder, s.Signature));
        Assert.All(harness.Root.Graph!.FindDefinitions("Certificates.Key"), d => Assert.Equal(IndexRedactor.ScanFailedPlaceholder, d.Signature));

        // The other files were scanned as usual and stored with their text.
        var other = (await harness.Root.Store.Files.GetAsync("src/Payments/PaymentGateway.cs"))!;
        var otherChunks = await harness.Root.Storage.Chunks.GetChunksByFileAsync(other.Id);
        Assert.NotEmpty(otherChunks);
        Assert.All(otherChunks, c => Assert.DoesNotContain(IndexRedactor.PlaceholderPrefix, c.Text, StringComparison.Ordinal));
    }

    [Fact]
    public async Task ASecretInADefaultParameterValue_IsRedactedInTheSymbolSignature_StoredAndInTheGraph_OnBothExtractionPaths()
    {
        // PR 121 review: the extractor saw the original text, so a default parameter value kept the key in symbols.signature.
        Fixtures.Copy("shop", _workspace.Repository);
        File.WriteAllText(_workspace.File("src/Client.cs"), "namespace Shop;\n\npublic static class Client\n{\n" + $"    public static int Connect(string key = \"{AwsKey}\", int retries = 3) => retries;\n" + "}\n");
        await using (var harness = new IndexingHarness(_workspace.Data))
        {
            await harness.StartAsync(_workspace.Repository);
            await harness.WaitForIdleAsync();

            var stored = Assert.Single(await harness.Root.Storage.Symbols.FindSymbolsByNameAsync("Connect"));
            Assert.Contains("<redacted-index:aws-access-key>", stored.Signature, StringComparison.Ordinal);
            Assert.DoesNotContain(AwsKey, stored.Signature, StringComparison.Ordinal);
            var inGraph = Assert.Single(harness.Root.Graph!.FindDefinitions("Connect"));
            Assert.Contains("<redacted-index:aws-access-key>", inGraph.Signature, StringComparison.Ordinal);
            Assert.DoesNotContain(AwsKey, inGraph.Signature, StringComparison.Ordinal);
        }

        // A new session rebuilds the graph from the unchanged files through the symbols-only path.
        await using (var again = new IndexingHarness(_workspace.Data))
        {
            await again.StartAsync(_workspace.Repository);
            await again.WaitForIdleAsync();
            await again.WaitForAsync(_ => again.Root.Graph is not null, what: "the symbol graph of the second session");

            var inGraph = Assert.Single(again.Root.Graph!.FindDefinitions("Connect"));
            Assert.Contains("<redacted-index:aws-access-key>", inGraph.Signature, StringComparison.Ordinal);
            Assert.DoesNotContain(AwsKey, inGraph.Signature, StringComparison.Ordinal);
            Assert.Equal(0, ((FakeEmbedder)again.Embedder).ChunksEmbedded);
        }
    }

    [Fact]
    public async Task APrivateKeyAsADefaultParameterValue_IsRedactedInTheSignatureBeforeItIsNormalizedAndCut_OnBothExtractionPaths()
    {
        // PR 121 review (second round): the extractor normalizes a signature (line breaks collapsed, cut to 200 characters)
        // before it was scanned, so a multi-line PEM block in a default parameter value lost the END marker its pattern needs
        // and its first body lines were stored in symbols.signature and published to the graph, while the chunks were redacted.
        Fixtures.Copy("shop", _workspace.Repository);
        var body = PemBody(lines: 8); // 8 x 64 characters: longer than a signature
        var file = "namespace Shop;\n\npublic static class Client\n{\n"
            + "    public static int Connect(string key = \"\"\"\n        -----BEGIN RSA PRIVATE KEY-----\n"
            + string.Concat(body.Select(l => "        " + l + "\n"))
            + "        -----END RSA PRIVATE KEY-----\n        \"\"\", int retries = 3) => retries;\n\n"
            + "    public static int Disconnect() => 0;\n}\n";
        File.WriteAllText(_workspace.File("src/Client.cs"), file);
        long storedId;
        await using (var harness = new IndexingHarness(_workspace.Data))
        {
            await harness.StartAsync(_workspace.Repository);
            await harness.WaitForIdleAsync();

            var stored = Assert.Single(await harness.Root.Storage.Symbols.FindSymbolsByNameAsync("Connect"));
            AssertRedactedPrivateKeySignature(stored.Signature, body);
            Assert.Contains("Connect(string key =", stored.Signature, StringComparison.Ordinal); // the rest of the signature stays
            Assert.Contains("int retries = 3", stored.Signature, StringComparison.Ordinal);
            Assert.Equal(5, stored.StartLine);
            Assert.Equal(5 + body.Count + 3, stored.EndLine); // the line numbers are the file's, not the redacted source's
            storedId = stored.Id;
            var inGraph = Assert.Single(harness.Root.Graph!.FindDefinitions("Connect"));
            AssertRedactedPrivateKeySignature(inGraph.Signature, body);
            Assert.Equal(storedId, inGraph.Id);
            var sibling = Assert.Single(await harness.Root.Storage.Symbols.FindSymbolsByNameAsync("Disconnect"));
            Assert.Contains("Disconnect()", sibling.Signature, StringComparison.Ordinal);
            Assert.DoesNotContain(IndexRedactor.PlaceholderPrefix, sibling.Signature, StringComparison.Ordinal);

            // The chunks were redacted as before.
            var record = (await harness.Root.Store.Files.GetAsync("src/Client.cs"))!;
            var chunks = await harness.Root.Storage.Chunks.GetChunksByFileAsync(record.Id);
            Assert.All(chunks, c => Assert.DoesNotContain(body, line => c.Text.Contains(line, StringComparison.Ordinal)));
        }

        // A new session rebuilds the graph from the unchanged files through the symbols-only path.
        await using (var again = new IndexingHarness(_workspace.Data))
        {
            await again.StartAsync(_workspace.Repository);
            await again.WaitForIdleAsync();
            await again.WaitForAsync(_ => again.Root.Graph is not null, what: "the symbol graph of the second session");

            var inGraph = Assert.Single(again.Root.Graph!.FindDefinitions("Connect"));
            AssertRedactedPrivateKeySignature(inGraph.Signature, body);
            Assert.Equal(storedId, inGraph.Id);
            Assert.Equal(0, ((FakeEmbedder)again.Embedder).ChunksEmbedded);
        }
    }

    /// <summary>The signature carries the private-key placeholder and no line of the key's body, not even the start of one (a signature is cut to 200 characters).</summary>
    private static void AssertRedactedPrivateKeySignature(string? signature, List<string> body)
    {
        Assert.NotNull(signature);
        Assert.Contains("<redacted-index:private-key-block>", signature, StringComparison.Ordinal);
        Assert.DoesNotContain("BEGIN RSA PRIVATE KEY", signature, StringComparison.Ordinal);
        Assert.All(body, line => Assert.DoesNotContain(line[..16], signature, StringComparison.Ordinal));
    }

    /// <summary>A C# file with a PEM private key in a raw string constant: the key starts on line 7 and the body on line 8.</summary>
    private static string CertificatesFile(List<string> body) =>
        "namespace Shop;\n\n/// <summary>A certificate a developer should not have committed.</summary>\npublic static class Certificates\n{\n"
        + "    public const string Key = \"\"\"\n        -----BEGIN RSA PRIVATE KEY-----\n"
        + string.Concat(body.Select(l => "        " + l + "\n"))
        + "        -----END RSA PRIVATE KEY-----\n        \"\"\";\n\n    public static int Count => 1;\n}\n";

    /// <summary>Deterministic base64-looking lines (64 characters each), shaped like a PEM body.</summary>
    private static List<string> PemBody(int lines)
    {
        const string alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789+/";
        var random = new Random(7);
        var result = new List<string>(lines);
        for (var i = 0; i < lines; i++)
        {
            var chars = new char[64];
            for (var j = 0; j < chars.Length; j++)
            {
                chars[j] = alphabet[random.Next(alphabet.Length)];
            }

            result.Add(new string(chars));
        }

        return result;
    }

    private static string SecretsFile(string awsKey, string token) =>
        "namespace Shop;\n\n" +
        "/// <summary>Credentials a developer should not have hard-coded.</summary>\n" +
        "public static class Secrets\n{\n" +
        $"    public const string AwsKey = \"{awsKey}\";\n" +
        $"    public const string GitHubToken = \"{token}\";\n" +
        "}\n";

    private sealed class FakeQueryEmbedder : IQueryEmbedder
    {
        public Task<QueryEmbedding> EmbedQueryAsync(string text, string? modelId, int? dimensions, CancellationToken cancellationToken) =>
            Task.FromResult(new QueryEmbedding(FakeEmbedder.Vector(text).Quantized, modelId ?? "fake/hashed-words"));
    }
}

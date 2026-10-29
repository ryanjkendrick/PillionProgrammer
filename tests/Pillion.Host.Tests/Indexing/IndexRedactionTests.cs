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

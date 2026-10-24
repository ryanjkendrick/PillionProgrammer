using System.Text.Json;
using Pillion.Eval.Models;
using Pillion.Eval.Retrieval;
using Xunit.Abstractions;

namespace Pillion.Eval.Tests;

/// <summary>
/// The harness end to end with the bundled model on the CPU: the fixtures golden set is indexed through the real pipeline and
/// scored. The recall floor is a sanity check that the harness and retrieval work together, not the Gate 2 target.
/// </summary>
public sealed class RetrievalSmokeTests(ITestOutputHelper output) : IDisposable
{
    private readonly string _out = Path.Combine(Path.GetTempPath(), "pillion-eval-tests", Guid.NewGuid().ToString("N")[..12]);

    public void Dispose()
    {
        if (Directory.Exists(_out))
        {
            Directory.Delete(_out, recursive: true);
        }
    }

    [Fact]
    [Trait("Category", "Model")]
    public async Task TheFixturesSet_WithTheBundledModel_ProducesAReportWithRecallAboveTheSanityFloor()
    {
        var log = new StringWriter();
        var options = new RetrievalRunOptions
        {
            Sets = [EvalPaths.Set("fixtures")],
            Models = [ModelSpec.Parse("bundled/bge-small-en-v1.5")],
            OutputRoot = _out,
            ModelsDirectory = Path.Combine(_out, "models"),
            Download = false,
        };

        var report = await RetrievalRunner.RunAsync(options, log, CancellationToken.None);
        output.WriteLine(log.ToString());

        var result = Assert.Single(report.Results);
        Assert.Equal(["hybrid", "lexical", "vector"], result.Modes.Select(m => m.Mode));
        Assert.All(result.Modes, m => Assert.Equal(40, m.Queries));
        Assert.All(result.Modes, m => Assert.True(m.FileRecallAt10 > 0.5, $"{m.Mode}: recall at 10 {m.FileRecallAt10}"));
        Assert.Equal(result.Index.Chunks, result.Index.EmbeddedChunks);
        Assert.Equal(24, result.Index.IndexedFiles);
        Assert.Equal("6c9c6101a956d62dfb5e7190c538226c0c5bb9cb27b651234b6df063ee7dbfe4", result.Identity!.Hash);
        Assert.NotNull(result.Int8Loss);
        Assert.Equal(result.Index.Chunks, result.Int8Loss!.Chunks);
        Assert.Equal(0, result.Int8Loss.ReEmbedded); // every chunk vector was captured while indexing

        Assert.True(File.Exists(report.SummaryPath));
        Assert.Contains("| fixtures | bundled/bge-small-en-v1.5 | hybrid | 40 |", await File.ReadAllTextAsync(report.SummaryPath), StringComparison.Ordinal);
        using var json = JsonDocument.Parse(await File.ReadAllTextAsync(result.JsonPath!));
        Assert.Equal(40, json.RootElement.GetProperty("queries").GetArrayLength());
        Assert.Equal("bundled/bge-small-en-v1.5", json.RootElement.GetProperty("model").GetString());
    }
}

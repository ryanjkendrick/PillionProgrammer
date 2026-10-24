using System.Diagnostics;
using System.Text;
using Pillion.Indexing.Discovery;
using Pillion.Indexing.Storage;
using Pillion.Indexing.Sweep;
using Xunit.Abstractions;

namespace Pillion.Indexing.Tests;

/// <summary>
/// Enumerates and hashes a generated 20,000-file tree and logs how long it took. The bounds are generous so that CI
/// machines pass; the logged numbers are the evidence.
/// </summary>
public sealed class ThroughputTests : IDisposable
{
    private const int Directories = 200;
    private const int FilesPerDirectory = 100;
    private static readonly TimeSpan Bound = TimeSpan.FromSeconds(120);

    private readonly ITestOutputHelper _output;
    private readonly TempDirectory _temp = new();

    public ThroughputTests(ITestOutputHelper output)
    {
        _output = output;
    }

    public void Dispose() => _temp.Dispose();

    [Fact]
    public async Task TwentyThousandFiles_AreEnumeratedHashedAndResweptWithinTheBound()
    {
        var root = _temp.File("big");
        var generation = Stopwatch.StartNew();
        var expectedIncluded = GenerateTree(root);
        _output.WriteLine($"generated {Directories * FilesPerDirectory} files ({expectedIncluded} indexable) in {generation.Elapsed.TotalSeconds:F1} s");

        var options = new ScanOptions();
        _output.WriteLine($"parallelism {options.MaxDegreeOfParallelism} of {Environment.ProcessorCount} logical processors");

        // 1. Enumeration with every exclusion rule, content checks included, no hashing.
        var scan = Stopwatch.StartNew();
        var included = 0;
        var excluded = 0;
        await foreach (var entry in new RepositoryScanner(options).ScanAsync(root))
        {
            if (entry.Decision.IsIncluded)
            {
                included++;
            }
            else
            {
                excluded++;
            }
        }

        scan.Stop();
        _output.WriteLine($"scan: {included} included, {excluded} excluded in {scan.Elapsed.TotalSeconds:F2} s ({(included + excluded) / scan.Elapsed.TotalSeconds:F0} entries/s)");
        Assert.Equal(expectedIncluded, included);
        Assert.True(scan.Elapsed < Bound, $"scan took {scan.Elapsed}");

        // 2. A first sweep: enumerate and hash everything.
        using var temp = new TempDirectory();
        await using var database = await IndexDatabases.OpenAsync(temp);
        var files = new FilesRepository(database, new TestTime(DateTimeOffset.UtcNow.AddHours(1)));
        var sweeper = new RepositorySweeper(options);
        var first = Stopwatch.StartNew();
        var firstSweep = await sweeper.SweepAsync(root, []);
        first.Stop();
        _output.WriteLine($"first sweep (enumerate + hash): {firstSweep.Added.Count} files in {first.Elapsed.TotalSeconds:F2} s ({firstSweep.Added.Count / first.Elapsed.TotalSeconds:F0} files/s, {firstSweep.FilesHashed} hashed)");
        Assert.Equal(expectedIncluded, firstSweep.Added.Count);
        Assert.Equal(expectedIncluded, firstSweep.FilesHashed);
        Assert.True(first.Elapsed < Bound, $"first sweep took {first.Elapsed}");

        var store = Stopwatch.StartNew();
        await files.CommitAsync(firstSweep);
        _output.WriteLine($"stored {await files.CountAsync()} rows in {store.Elapsed.TotalSeconds:F2} s");

        // 3. A second sweep of the untouched tree reads no file contents at all.
        var second = Stopwatch.StartNew();
        var secondSweep = await sweeper.SweepAsync(root, await files.GetAllAsync());
        second.Stop();
        _output.WriteLine($"second sweep (nothing changed): {secondSweep.Unchanged.Count} unchanged, {secondSweep.FilesHashed} hashed in {second.Elapsed.TotalSeconds:F2} s");
        Assert.Equal(expectedIncluded, secondSweep.Unchanged.Count);
        Assert.Equal(0, secondSweep.FilesHashed);
        Assert.False(secondSweep.HasChanges);
        Assert.True(second.Elapsed < Bound, $"second sweep took {second.Elapsed}");

        // 4. A branch switch rewriting about 540 files (12 folders of 45 C# files).
        var changed = 0;
        for (var d = 0; d < 12; d++)
        {
            for (var f = 0; f < 100; f++)
            {
                var path = Path.Combine(root, $"dir{d:D3}", $"file{f:D3}.cs");
                if (!File.Exists(path))
                {
                    continue;
                }

                File.AppendAllText(path, "// edited by the branch switch\n");
                changed++;
            }
        }

        var switchWatch = Stopwatch.StartNew();
        var switched = await sweeper.SweepAsync(root, await files.GetAllAsync());
        switchWatch.Stop();
        _output.WriteLine($"branch-switch sweep: {switched.Changed.Count} of {changed} edited files changed, {switched.Unchanged.Count} unchanged in {switchWatch.Elapsed.TotalSeconds:F2} s");
        Assert.Equal(changed, switched.Changed.Count);
        Assert.Equal(expectedIncluded - changed, switched.Unchanged.Count);
        Assert.True(switchWatch.Elapsed < Bound, $"branch-switch sweep took {switchWatch.Elapsed}");
    }

    /// <summary>Creates 200 folders of 100 files: mostly C# and TypeScript of about 2 KB, with some files each rule excludes.</summary>
    private static int GenerateTree(string root)
    {
        var included = 0;
        var gate = new object();
        Parallel.For(0, Directories, d =>
        {
            var directory = Path.Combine(root, $"dir{d:D3}");
            Directory.CreateDirectory(directory);
            var local = 0;
            for (var f = 0; f < FilesPerDirectory; f++)
            {
                string name;
                string content;
                var indexable = true;
                switch (f % 20)
                {
                    case 17:
                        name = $"file{f:D3}.g.cs";
                        content = "// generated\n";
                        indexable = false;
                        break;
                    case 18:
                        name = $"file{f:D3}.min.js";
                        content = "var a=1;";
                        indexable = false;
                        break;
                    case 19:
                        name = $"secrets{f:D3}.pem";
                        content = "-----BEGIN-----";
                        indexable = false;
                        break;
                    default:
                        name = f % 2 == 0 ? $"file{f:D3}.cs" : $"file{f:D3}.ts";
                        content = Body(d, f);
                        break;
                }

                File.WriteAllText(Path.Combine(directory, name), content);
                if (indexable)
                {
                    local++;
                }
            }

            lock (gate)
            {
                included += local;
            }
        });

        return included;
    }

    private static string Body(int directory, int file)
    {
        var text = new StringBuilder();
        text.Append("// file ").Append(directory).Append('/').Append(file).Append('\n');
        for (var i = 0; text.Length < 2000; i++)
        {
            text.Append("public int Member").Append(i).Append(" => ").Append((directory * 31) + (file * 17) + i).Append(";\n");
        }

        return text.ToString();
    }
}

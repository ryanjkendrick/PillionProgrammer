using System.Diagnostics;
using System.Text;
using Pillion.ContextEngine.Chunking;
using Xunit.Abstractions;

namespace Pillion.ContextEngine.Tests;

/// <summary>
/// Benchmark-style tests with bounds generous enough for a shared CI runner; the measured figures are logged. The
/// design budget is 100,000 lines (about 10,000 chunks) indexed in 3 minutes including embedding, so chunking has to
/// be a small fraction of that; a 1 MB file (the discovery cap) should chunk in well under a second.
/// </summary>
public sealed class ChunkerPerformanceTests(ITestOutputHelper output)
{
    [Fact]
    public void A_100000_line_csharp_file_chunks_within_the_bound()
    {
        var text = SyntheticCSharp(100_000);
        var chunker = new FileChunker();
        chunker.Chunk("warmup.cs", SyntheticCSharp(2_000), ChunkOptions.Default);

        var stopwatch = Stopwatch.StartNew();
        var chunks = chunker.Chunk("src/Generated.cs", text, ChunkOptions.Default);
        stopwatch.Stop();

        var lines = text.Count(c => c == '\n');
        output.WriteLine($"{lines:N0} lines, {text.Length / 1024.0 / 1024.0:F1} MB of C#: {chunks.Count:N0} chunks in {stopwatch.Elapsed.TotalMilliseconds:N0} ms");
        Assert.True(lines >= 100_000);
        Assert.True(chunks.Count > 5_000);
        Assert.All(chunks, c => Assert.NotEqual(ChunkKind.Window, c.Kind));
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(30), $"Took {stopwatch.Elapsed}.");
        ChunkInvariants.Assert("src/Generated.cs", text, chunks, ChunkOptions.Default);
    }

    public static TheoryData<string> OneMegabyteFiles() => ["a.cs", "a.ts", "a.py", "a.html", "a.sql", "a.txt", "minified.js", "minified.txt"];

    [Theory]
    [MemberData(nameof(OneMegabyteFiles))]
    public void A_one_megabyte_file_chunks_within_the_bound(string path)
    {
        var text = OneMegabyte(path);
        var chunker = new FileChunker();
        chunker.Chunk(path, text[..Math.Min(text.Length, 20_000)], ChunkOptions.Default);

        var allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
        var stopwatch = Stopwatch.StartNew();
        var chunks = chunker.Chunk(path, text, ChunkOptions.Default);
        stopwatch.Stop();
        var allocated = GC.GetAllocatedBytesForCurrentThread() - allocatedBefore;

        output.WriteLine($"{path}: {text.Length / 1024.0:N0} KB, {chunks.Count:N0} chunks ({chunks[0].Kind}...) in {stopwatch.Elapsed.TotalMilliseconds:N0} ms, {allocated / 1024.0 / 1024.0:N0} MB allocated");
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(30), $"Took {stopwatch.Elapsed}.");

        // Linear, not quadratic: a generous multiple of the input (Roslyn's tree for C# is the largest consumer).
        Assert.True(allocated < 400L * 1024 * 1024, $"Allocated {allocated:N0} bytes.");
        ChunkInvariants.Assert(path, text, chunks, ChunkOptions.Default);
    }

    private static string SyntheticCSharp(int lines)
    {
        var builder = new StringBuilder(lines * 32);
        builder.Append("using System;\nusing System.Collections.Generic;\n\nnamespace Generated.Code;\n\n");
        var written = 5;
        for (var type = 0; written < lines; type++)
        {
            builder.Append("/// <summary>Type ").Append(type).Append(".</summary>\npublic sealed class Service").Append(type).Append(" : IDisposable\n{\n");
            builder.Append("    private readonly List<int> _items = new();\n    private int _count;\n\n");
            written += 6;
            for (var method = 0; method < 4; method++)
            {
                builder.Append("    /// <summary>Method ").Append(method).Append(".</summary>\n    public int Compute").Append(method).Append("(int value)\n    {\n");
                builder.Append("        var total = value * ").Append(method + 1).Append(";\n");
                builder.Append("        foreach (var item in _items)\n        {\n            total += item;\n        }\n\n");
                builder.Append("        _count++;\n        return total;\n    }\n\n");
                written += 13;
            }

            builder.Append("    public void Dispose() => _items.Clear();\n}\n\n");
            written += 3;
        }

        return builder.ToString();
    }

    private static string OneMegabyte(string path)
    {
        const int Size = 1024 * 1024;
        var builder = new StringBuilder(Size + 4096);
        var i = 0;
        while (builder.Length < Size)
        {
            switch (path)
            {
                case "a.cs":
                    builder.Append("class C").Append(i).Append(" {\n    int M(int x)\n    {\n        return x + ").Append(i).Append(";\n    }\n}\n");
                    break;
                case "a.ts":
                    builder.Append("export function f").Append(i).Append("(x: number): number {\n  if (x > 0) {\n    return x * ").Append(i).Append(";\n  }\n  return 0;\n}\n");
                    break;
                case "a.py":
                    builder.Append("def f").Append(i).Append("(x):\n    if x > 0:\n        return x * ").Append(i).Append("\n    return 0\n\n");
                    break;
                case "a.html":
                    builder.Append("<section id=\"s").Append(i).Append("\">\n  <h2>Title ").Append(i).Append("</h2>\n  <p>Some text.</p>\n</section>\n");
                    break;
                case "a.sql":
                    builder.Append("INSERT INTO dbo.T").Append(i % 7).Append(" (Id, Name) VALUES (").Append(i).Append(", 'n");
                    builder.Append(i).Append("');\n");
                    break;
                case "minified.js":
                case "minified.txt":
                    builder.Append("function f").Append(i).Append("(a){return a+").Append(i).Append("};");
                    break;
                default:
                    builder.Append("Line ").Append(i).Append(" of plain text with a few words in it.\n");
                    break;
            }

            i++;
        }

        return builder.ToString();
    }
}

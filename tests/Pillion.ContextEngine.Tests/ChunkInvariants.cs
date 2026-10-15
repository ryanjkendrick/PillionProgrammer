using System.Text;
using Pillion.ContextEngine.Chunking;
using Pillion.ContextEngine.Chunking.Core;

namespace Pillion.ContextEngine.Tests;

/// <summary>The invariants every chunker output must satisfy, shared by the unit and property tests.</summary>
internal static class ChunkInvariants
{
    /// <summary>
    /// Checks: line ranges inside the file and in order; the cap respected and the token count exact; breadcrumbs well
    /// formed and starting with the path, the file name or an ellipsis; text equal to the lines (or a prefix when
    /// truncated); part numbering consistent; and coverage: every line exactly once for syntactic output, at least once
    /// with the configured overlap for windows. Returns a failure message, or null.
    /// </summary>
    public static string? Check(string path, string text, IReadOnlyList<Chunk> chunks, ChunkOptions options)
    {
        var document = new SourceDocument(path, text);
        if (document.IsBlank)
        {
            return chunks.Count == 0 ? null : "Blank text produced chunks.";
        }

        if (chunks.Count == 0)
        {
            return "Non-blank text produced no chunks.";
        }

        var fileName = path[(path.LastIndexOfAny(['/', '\\']) + 1)..];
        foreach (var chunk in chunks)
        {
            if (chunk.StartLine < 1 || chunk.EndLine < chunk.StartLine || chunk.EndLine > document.LineCount)
            {
                return $"Range {chunk.StartLine}-{chunk.EndLine} outside 1-{document.LineCount}.";
            }

            if (chunk.TokenCount > options.MaxTokens)
            {
                return $"Chunk {chunk.StartLine}-{chunk.EndLine} has {chunk.TokenCount} tokens over the cap {options.MaxTokens}.";
            }

            var counted = options.TokenCounter(chunk.EmbeddingText);
            if (counted != chunk.TokenCount)
            {
                return $"Chunk {chunk.StartLine}-{chunk.EndLine} reports {chunk.TokenCount} tokens but counts {counted}.";
            }

            if (!Breadcrumbs.TryParse(chunk.Breadcrumb, out var segments, out var part, out var count))
            {
                return $"Malformed breadcrumb '{chunk.Breadcrumb}'.";
            }

            var first = segments[0];
            var expectedFirst = Breadcrumbs.Sanitize(path, int.MaxValue);
            if (first != expectedFirst && first != Breadcrumbs.Sanitize(fileName, int.MaxValue)
                && !first.EndsWith('…') && first != "…")
            {
                return $"Breadcrumb '{chunk.Breadcrumb}' does not start with the path.";
            }

            if (part != chunk.Part || count != chunk.PartCount && !(chunk.PartCount == 1 && count == 1))
            {
                return $"Breadcrumb '{chunk.Breadcrumb}' disagrees with part {chunk.Part} of {chunk.PartCount}.";
            }

            var expected = document.Slice(chunk.StartLine, chunk.EndLine);
            if (chunk.IsTruncated ? !expected.StartsWith(chunk.Text, StringComparison.Ordinal) : expected != chunk.Text)
            {
                return $"Chunk {chunk.StartLine}-{chunk.EndLine} text does not match the file.";
            }
        }

        var windows = chunks.All(c => c.Kind == ChunkKind.Window);
        var covered = new int[document.LineCount + 2];
        var previousStart = 0;
        foreach (var chunk in chunks)
        {
            // Parts of a split window may start before the next window's overlap; syntactic chunks never go back.
            if (!windows && chunk.StartLine < previousStart)
            {
                return "Chunks are not in line order.";
            }

            previousStart = chunk.StartLine;
            for (var line = chunk.StartLine; line <= chunk.EndLine; line++)
            {
                covered[line]++;
            }
        }

        for (var line = 1; line <= document.LineCount; line++)
        {
            if (covered[line] == 0)
            {
                return $"Line {line} is not covered.";
            }

            if (!windows && covered[line] != 1)
            {
                return $"Line {line} is covered {covered[line]} times.";
            }

            if (windows && covered[line] > 2)
            {
                return $"Line {line} is covered by {covered[line]} windows.";
            }
        }

        return null;
    }

    public static void Assert(string path, string text, IReadOnlyList<Chunk> chunks, ChunkOptions options)
    {
        var failure = Check(path, text, chunks, options);
        Xunit.Assert.True(failure is null, failure + Environment.NewLine + Describe(chunks));
    }

    public static string Describe(IReadOnlyList<Chunk> chunks)
    {
        var builder = new StringBuilder();
        foreach (var chunk in chunks)
        {
            builder.Append(chunk.StartLine).Append('-').Append(chunk.EndLine).Append(' ').Append(chunk.Kind)
                .Append(" [").Append(chunk.TokenCount).Append("] ").AppendLine(chunk.Breadcrumb);
        }

        return builder.ToString();
    }
}

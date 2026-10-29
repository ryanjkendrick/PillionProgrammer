using System.Text;
using Pillion.CommandRunner.Terminal;

namespace Pillion.CommandRunner.Tests;

/// <summary>The model's view: tail truncation to the byte cap, the omission note, and redaction.</summary>
public sealed class ModelViewTests
{
    [Fact]
    public void ShortOutput_IsPassedWhole()
    {
        var view = ModelView.Build(Lines("one", "two"), 1024);
        Assert.Equal("one\ntwo", view.Text);
        Assert.False(view.Truncated);
        Assert.Equal(2, view.TotalLines);
        Assert.Equal(2, view.IncludedLines);
        Assert.False(view.Redacted);
    }

    [Fact]
    public void LongOutput_KeepsTheTail_WithinTheCap_AndSaysHowManyLinesWereOmitted()
    {
        var lines = Enumerable.Range(0, 1000).Select(i => $"line {i:0000} {new string('x', 40)}").ToArray();
        var view = ModelView.Build(Lines(lines), 2048);

        Assert.True(view.Truncated);
        Assert.True(Encoding.UTF8.GetByteCount(view.Text) <= 2048, $"{Encoding.UTF8.GetByteCount(view.Text)} bytes");
        Assert.StartsWith($"[... {view.OmittedLines} earlier lines omitted", view.Text, StringComparison.Ordinal);
        Assert.EndsWith(lines[^1], view.Text, StringComparison.Ordinal);
        Assert.Equal(1000, view.TotalLines);
        Assert.Equal(1000 - view.OmittedLines, view.IncludedLines);
        Assert.Contains(lines[1000 - view.IncludedLines], view.Text, StringComparison.Ordinal);
        Assert.DoesNotContain(lines[999 - view.IncludedLines], view.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void MultiByteText_IsCountedInBytes()
    {
        var lines = Enumerable.Range(0, 100).Select(_ => new string('✓', 50)).ToArray(); // 150 bytes a line
        var view = ModelView.Build(Lines(lines), 1024);
        Assert.True(view.Truncated);
        Assert.True(Encoding.UTF8.GetByteCount(view.Text) <= 1024);
        Assert.True(view.IncludedLines is >= 4 and <= 6, $"{view.IncludedLines} lines");
    }

    [Fact]
    public void Secrets_AreRedacted()
    {
        var view = ModelView.Build(Lines("export AWS_KEY=AKIAJ4K7QW6ZP2XV3MNB", "done"), 1024);
        Assert.True(view.Redacted);
        Assert.DoesNotContain("AKIAJ4K7QW6ZP2XV3MNB", view.Text, StringComparison.Ordinal);
        Assert.Contains("<redacted:", view.Text, StringComparison.Ordinal);
        Assert.EndsWith("done", view.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void Empty_IsEmpty()
    {
        var view = ModelView.Build([], 1024);
        Assert.Equal(string.Empty, view.Text);
        Assert.Equal(0, view.TotalLines);
    }

    [Fact]
    public void FromABuffer_TakesTheTailFromAHistoryIndex()
    {
        var terminal = new TerminalBuffer(20, 2, 100);
        terminal.Write("a\r\nb\r\n".AsSpan());
        var mark = terminal.BeginRun("> run");
        terminal.Write("c\r\nd".AsSpan());
        Assert.Equal("> run\nc\nd", ModelView.Build(terminal, mark, 1024).Text);
        Assert.Equal("a\nb\n> run\nc\nd", ModelView.Build(terminal, 0, 1024).Text);
    }

    private static TerminalLine[] Lines(params string[] text) => text.Select(t => new TerminalLine(t, [])).ToArray();
}

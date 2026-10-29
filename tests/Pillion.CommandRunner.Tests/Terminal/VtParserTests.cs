using Pillion.CommandRunner.Terminal;

namespace Pillion.CommandRunner.Tests.Terminal;

/// <summary>The VT parser: the sequences it applies, the ones it drops, and that nothing leaks into the text.</summary>
public sealed class VtParserTests
{
    private const string Esc = "\u001B";

    [Fact]
    public void PlainText_WithCrLf_RendersAsLines()
    {
        var screen = Parse("hello\r\nworld\r\n");
        Assert.Equal("hello\nworld", screen.RenderScreenText());
        Assert.Equal(2, screen.CursorRow);
        Assert.Equal(0, screen.CursorColumn);
    }

    [Fact]
    public void CursorMovement_Erase_AndOverwrite_WorkLikeAProgressLine()
    {
        // A progress bar redraws its line: CR, overwrite, erase to end of line.
        var screen = Parse($"Progress 10%\rProgress 100%{Esc}[K\r\nDone\r\n");
        Assert.Equal("Progress 100%\nDone", screen.RenderScreenText());

        var cup = Parse($"{Esc}[2;3Hx{Esc}[1;1Hy{Esc}[2A{Esc}[5Cz");
        Assert.Equal("y     z\n  x", cup.RenderScreenText());
    }

    [Fact]
    public void Sgr_SetsColoursAndStyles_IncludingExtendedForms_AndResets()
    {
        var screen = Parse($"{Esc}[1;31mred{Esc}[0m {Esc}[38;5;208morange{Esc}[39m {Esc}[48;2;1;2;3mrgb{Esc}[m {Esc}[38:2::9:8:7mcolon{Esc}[0m");
        var line = screen.GetRow(0);
        Assert.Equal("red orange rgb colon", line.Text);
        var red = line.Spans.Single(s => s.Start == 0);
        Assert.Equal(TerminalColor.Indexed(1), red.Attributes.Foreground);
        Assert.Equal(CellStyle.Bold, red.Attributes.Style);
        var orange = line.Spans.Single(s => s.Start == 4);
        Assert.Equal(TerminalColor.Indexed(208), orange.Attributes.Foreground);
        var rgb = line.Spans.Single(s => s.Start == 11);
        Assert.Equal(TerminalColor.Rgb(1, 2, 3), rgb.Attributes.Background);
        var colon = line.Spans.Single(s => s.Start == 15);
        Assert.Equal(TerminalColor.Rgb(9, 8, 7), colon.Attributes.Foreground);
        Assert.Equal(CellAttributes.Default, screen.CurrentAttributes);
    }

    [Fact]
    public void OscTitle_IsCaptured_AndOtherOscStringsAreDropped()
    {
        var screen = Parse($"{Esc}]0;My Title\u0007text{Esc}]9;4;3;0{Esc}\\more{Esc}]2;Second{Esc}\\");
        Assert.Equal("Second", screen.Title);
        Assert.Equal("textmore", screen.RenderScreenText());
    }

    [Fact]
    public void BracketedPaste_CursorVisibility_AndKeypadModes_DoNotCorruptTheBuffer()
    {
        var screen = Parse($"{Esc}[?2004ha{Esc}[?2004l{Esc}[?25lb{Esc}[?25h{Esc}={Esc}>c{Esc}[?1h{Esc}[?1004h{Esc}[?9001h");
        Assert.Equal("abc", screen.RenderScreenText());
        Assert.True(screen.CursorVisible);
    }

    [Fact]
    public void AlternateScreen_1049_RestoresTheMainScreenAndCursor()
    {
        var screen = Parse($"main{Esc}[?1049h{Esc}[Hfull screen app{Esc}[?1049lX");
        Assert.Equal("mainX", screen.RenderScreenText());
        Assert.False(screen.AlternateScreen);
    }

    [Fact]
    public void ScrollRegion_AndScrollSequences_Work()
    {
        var screen = Parse($"{Esc}[2;3r{Esc}[2;1Ha\r\nb\r\nc{Esc}[r", rows: 4);
        Assert.Equal("\nb\nc", screen.RenderScreenText());

        var scrolled = Parse($"a\r\nb\r\nc{Esc}[2S", rows: 3);
        Assert.Equal("c", scrolled.RenderScreenText());
        Assert.Equal(2, scrolled.ScrolledLineCount);
    }

    [Fact]
    public void UnknownSequences_AreDroppedWhole_AndNeverLeak()
    {
        var text = $"a{Esc}[?12345;678$pb{Esc}P1$qm{Esc}\\c{Esc}_apc string\u0007d{Esc}[>4;2me{Esc}(Bf{Esc}[38;2mg{Esc}[?1;2;3zh{Esc}#8i";
        var screen = Parse(text);
        Assert.Equal("abcdefghi", screen.RenderScreenText());
        Assert.DoesNotContain('\u001B', screen.RenderScreenText());
    }

    [Fact]
    public void ControlsInsideASequence_Execute_AndCanAborts()
    {
        var screen = Parse($"{Esc}[1\u0018xa\r\n{Esc}[2\nmb");
        // CAN aborted the first CSI (so x prints); the LF inside the second executed before its final byte m (SGR 2) ended it.
        Assert.Equal("xa\n\nb", screen.RenderScreenText());
    }

    [Fact]
    public void SequencesSplitAcrossFeeds_AreReassembled()
    {
        var buffer = new ScreenBuffer(20, 3, 0);
        var parser = new VtParser(buffer);
        parser.Feed($"a{Esc}[3");
        parser.Feed("1mb");
        parser.Feed($"{Esc}");
        parser.Feed("[0mc");
        Assert.Equal("abc", buffer.RenderScreenText());
        Assert.Equal(TerminalColor.Indexed(1), buffer.GetRow(0).Spans.Single(s => s.Start == 1).Attributes.Foreground);
    }

    [Fact]
    public void DeviceStatusReport_IsAnsweredThroughTheResponder()
    {
        var buffer = new ScreenBuffer(20, 3, 0);
        var replies = new List<string>();
        var parser = new VtParser(buffer, replies.Add);
        parser.Feed($"ab{Esc}[6n{Esc}[c");
        Assert.Equal([$"{Esc}[1;3R", $"{Esc}[?1;0c"], replies);
        Assert.Equal("ab", buffer.RenderScreenText());
    }

    [Fact]
    public void Utf8_IsDecodedAcrossChunkBoundaries()
    {
        var terminal = new TerminalBuffer(20, 2, 0);
        var bytes = System.Text.Encoding.UTF8.GetBytes("héllo ✓");
        terminal.Write(bytes.AsSpan(0, 2));
        terminal.Write(bytes.AsSpan(2));
        Assert.Equal("héllo ✓", terminal.RenderScreenText());
    }

    [Fact]
    public void SaveAndRestoreCursor_BothForms()
    {
        var screen = Parse($"abc{Esc}7\r\nxyz{Esc}8Z{Esc}[s\r\n{Esc}[uQ");
        Assert.Equal("abcZQ\nxyz", screen.RenderScreenText());
    }

    private static ScreenBuffer Parse(string text, int columns = 40, int rows = 10)
    {
        var buffer = new ScreenBuffer(columns, rows, 100);
        new VtParser(buffer).Feed(text);
        return buffer;
    }
}

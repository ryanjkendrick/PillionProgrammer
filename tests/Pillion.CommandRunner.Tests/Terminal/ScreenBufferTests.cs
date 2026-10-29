using Pillion.CommandRunner.Terminal;

namespace Pillion.CommandRunner.Tests.Terminal;

/// <summary>The screen buffer: printing, wrapping, scrolling into a bounded scrollback, the scroll region, the alternate screen, resizing and history indices.</summary>
public sealed class ScreenBufferTests
{
    [Fact]
    public void Print_WrapsAtTheRightMargin_AndTheLineBeforeIsIntact()
    {
        var screen = new ScreenBuffer(10, 3, 100);
        Feed(screen, "abcdefghijKLM");

        Assert.Equal("abcdefghij", screen.GetRow(0).Text);
        Assert.Equal("KLM", screen.GetRow(1).Text);
        Assert.Equal(1, screen.CursorRow);
        Assert.Equal(3, screen.CursorColumn);
    }

    [Fact]
    public void LineFeedAtTheBottom_ScrollsTheTopLineIntoTheScrollback_WithHistoryIndices()
    {
        var screen = new ScreenBuffer(20, 2, 100);
        Feed(screen, "one");
        screen.CarriageReturn();
        screen.LineFeed();
        Feed(screen, "two");
        screen.CarriageReturn();
        screen.LineFeed();
        Feed(screen, "three");

        Assert.Equal(1, screen.ScrollbackCount);
        Assert.Equal(1, screen.ScrolledLineCount);
        Assert.Equal("two", screen.GetRow(0).Text);
        Assert.Equal("three", screen.GetRow(1).Text);
        Assert.Equal(["one", "two", "three"], screen.GetLines(0, screen.EndLineIndex).Select(l => l.Text));
        Assert.Equal(["two", "three"], screen.GetLinesToEnd(1).Select(l => l.Text));
    }

    [Fact]
    public void Scrollback_IsBounded_AndTheOldestLinesLeave()
    {
        var screen = new ScreenBuffer(10, 2, 3);
        for (var i = 0; i < 10; i++)
        {
            Feed(screen, $"L{i}");
            screen.NextLine();
        }

        Assert.Equal(3, screen.ScrollbackCount);
        Assert.Equal(9, screen.ScrolledLineCount);
        Assert.Equal(6, screen.OldestLineIndex);
        Assert.Equal(["L6", "L7", "L8", "L9"], screen.GetLinesToEnd(0).Select(l => l.Text));
    }

    [Fact]
    public void ScrollbackLimitZero_KeepsNothingButCountsLines()
    {
        var screen = new ScreenBuffer(10, 2, 0);
        for (var i = 0; i < 5; i++)
        {
            Feed(screen, $"L{i}");
            screen.NextLine();
        }

        Assert.Equal(0, screen.ScrollbackCount);
        Assert.Equal(4, screen.ScrolledLineCount);
        Assert.Equal(["L4"], screen.GetLinesToEnd(0).Select(l => l.Text));
    }

    [Fact]
    public void ScrollRegion_ScrollsInsideItOnly_AndLinesLeavingItAreNotScrollback()
    {
        var screen = new ScreenBuffer(10, 4, 100);
        Feed(screen, "top");
        screen.SetCursorPosition(3, 0);
        Feed(screen, "bottom");
        screen.SetScrollRegion(1, 2);
        screen.SetCursorPosition(1, 0);
        Feed(screen, "a");
        screen.NextLine();
        Feed(screen, "b");
        screen.NextLine();
        Feed(screen, "c");

        Assert.Equal("top", screen.GetRow(0).Text);
        Assert.Equal("b", screen.GetRow(1).Text);
        Assert.Equal("c", screen.GetRow(2).Text);
        Assert.Equal("bottom", screen.GetRow(3).Text);
        Assert.Equal(0, screen.ScrollbackCount);
    }

    [Fact]
    public void EraseInLineAndDisplay_ClearWhatTheyShould()
    {
        var screen = new ScreenBuffer(10, 3, 100);
        Feed(screen, "0123456789");
        screen.SetCursorPosition(1, 0);
        Feed(screen, "abcdefghij");
        screen.SetCursorPosition(2, 0);
        Feed(screen, "ABCDEFGHIJ");

        screen.SetCursorPosition(1, 4);
        screen.EraseInLine(0);
        Assert.Equal("abcd", screen.GetRow(1).Text);
        screen.EraseInLine(1);
        Assert.Equal(string.Empty, screen.GetRow(1).Text);

        screen.SetCursorPosition(0, 5);
        screen.EraseInDisplay(0);
        Assert.Equal("01234", screen.GetRow(0).Text);
        Assert.Equal(string.Empty, screen.GetRow(2).Text);

        screen.EraseInDisplay(2);
        Assert.Equal(-1, screen.LastUsedRow);
    }

    [Fact]
    public void InsertAndDeleteCharactersAndLines_ShiftTheRest()
    {
        var screen = new ScreenBuffer(8, 3, 100);
        Feed(screen, "abcdef");
        screen.SetCursorPosition(0, 2);
        screen.InsertCharacters(2);
        Assert.Equal("ab  cdef", screen.GetRow(0).Text);
        screen.DeleteCharacters(3);
        Assert.Equal("abdef", screen.GetRow(0).Text);

        screen.SetCursorPosition(1, 0);
        Feed(screen, "second");
        screen.SetCursorPosition(0, 0);
        screen.InsertLines(1);
        Assert.Equal(string.Empty, screen.GetRow(0).Text);
        Assert.Equal("abdef", screen.GetRow(1).Text);
        Assert.Equal("second", screen.GetRow(2).Text);
        screen.DeleteLines(1);
        Assert.Equal("abdef", screen.GetRow(0).Text);
        Assert.Equal("second", screen.GetRow(1).Text);
        Assert.Equal(string.Empty, screen.GetRow(2).Text);
    }

    [Fact]
    public void AlternateScreen_StartsBlank_KeepsNoScrollback_AndRestoresTheMainScreen()
    {
        var screen = new ScreenBuffer(10, 2, 100);
        Feed(screen, "main");
        screen.SetAlternateScreen(true);
        Assert.True(screen.AlternateScreen);
        Assert.Equal(-1, screen.LastUsedRow);
        for (var i = 0; i < 5; i++)
        {
            Feed(screen, $"alt{i}");
            screen.NextLine();
        }

        Assert.Equal(0, screen.ScrollbackCount);
        screen.SetAlternateScreen(false);
        Assert.Equal("main", screen.GetRow(0).Text);
        Assert.Equal(0, screen.CursorRow);
        Assert.Equal(4, screen.CursorColumn);
    }

    [Fact]
    public void FlushToScrollback_MovesTheScreenIntoHistory_AndReturnsTheNextIndex()
    {
        var screen = new ScreenBuffer(10, 4, 100);
        Feed(screen, "a");
        screen.NextLine();
        Feed(screen, "b");

        var mark = screen.FlushToScrollback();

        Assert.Equal(2, mark);
        Assert.Equal(2, screen.ScrollbackCount);
        Assert.Equal(-1, screen.LastUsedRow);
        Assert.Equal(0, screen.CursorRow);
        Feed(screen, "c");
        Assert.Equal(["c"], screen.GetLinesToEnd(mark).Select(l => l.Text));
        Assert.Equal(["a", "b", "c"], screen.GetLinesToEnd(0).Select(l => l.Text));
    }

    [Fact]
    public void Resize_KeepsContent_PushesRowsAboveTheCursorToScrollback_AndClampsTheCursor()
    {
        var screen = new ScreenBuffer(10, 4, 100);
        for (var i = 0; i < 3; i++)
        {
            Feed(screen, $"row{i}");
            screen.NextLine();
        }

        Feed(screen, "row3");
        screen.Resize(6, 2);

        Assert.Equal(6, screen.Columns);
        Assert.Equal(2, screen.Rows);
        Assert.Equal(2, screen.ScrollbackCount);
        Assert.Equal("row2", screen.GetRow(0).Text);
        Assert.Equal("row3", screen.GetRow(1).Text);
        Assert.Equal(1, screen.CursorRow);
        Assert.Equal(4, screen.CursorColumn);

        screen.Resize(20, 5);
        Assert.Equal("row2", screen.GetRow(0).Text);
        Assert.Equal(["row0", "row1", "row2", "row3"], screen.GetLinesToEnd(0).Select(l => l.Text));
    }

    [Fact]
    public void Attributes_AreKeptAsSpans_AndDefaultLinesHaveNone()
    {
        var screen = new ScreenBuffer(10, 1, 0);
        Feed(screen, "ab");
        screen.CurrentAttributes = new CellAttributes(TerminalColor.Indexed(1), TerminalColor.Default, CellStyle.Bold);
        Feed(screen, "cd");
        screen.CurrentAttributes = CellAttributes.Default;
        Feed(screen, "e");

        var line = screen.GetRow(0);
        Assert.Equal("abcde", line.Text);
        Assert.Equal(3, line.Spans.Count);
        Assert.Equal(new TerminalSpan(0, 2, CellAttributes.Default), line.Spans[0]);
        Assert.Equal(new TerminalSpan(2, 2, new CellAttributes(TerminalColor.Indexed(1), TerminalColor.Default, CellStyle.Bold)), line.Spans[1]);
        Assert.Equal(new TerminalSpan(4, 1, CellAttributes.Default), line.Spans[2]);

        var plain = new ScreenBuffer(10, 1, 0);
        Feed(plain, "plain");
        Assert.Empty(plain.GetRow(0).Spans);
    }

    [Fact]
    public void Tab_Backspace_And_ReverseIndex_MoveTheCursor()
    {
        var screen = new ScreenBuffer(20, 3, 100);
        Feed(screen, "a");
        screen.Tab();
        Assert.Equal(8, screen.CursorColumn);
        Feed(screen, "b");
        screen.Backspace();
        Assert.Equal(8, screen.CursorColumn);
        Feed(screen, "c");
        Assert.Equal("a       c", screen.GetRow(0).Text);

        screen.ReverseIndex();
        Assert.Equal(0, screen.CursorRow);
        Assert.Equal(string.Empty, screen.GetRow(0).Text);
        Assert.Equal("a       c", screen.GetRow(1).Text);
    }

    private static void Feed(ScreenBuffer screen, string text)
    {
        foreach (var ch in text)
        {
            screen.Print(ch);
        }
    }
}

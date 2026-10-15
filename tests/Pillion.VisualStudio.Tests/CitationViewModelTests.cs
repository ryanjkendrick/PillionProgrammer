using System.Linq;
using Pillion.Contracts;
using Pillion.VisualStudio.Core;

namespace Pillion.VisualStudio.Tests;

/// <summary>
/// Citations under a reply (work item 2.12): the <c>file:line</c> and <c>file:start-end</c> link forms, the span handed to the
/// editor when one is opened, and implicit citations collapsed under a "Sources" line.
/// </summary>
public sealed class CitationViewModelTests
{
    private const string OrderService = @"C:\repo\src\Orders\OrderService.cs";

    [Fact]
    public void ALink_ShowsOneBasedLines_AsALineOrASpan_WithTheBreadcrumbOrPathAsTooltip()
    {
        var line = new CitationItem(new Citation { FilePath = OrderService, StartLine = 11, EndLine = 11, ReplyOffset = 4 });
        var span = new CitationItem(new Citation { FilePath = OrderService, StartLine = 8, EndLine = 11, ReplyOffset = 9, Breadcrumb = "src/Orders/OrderService.cs > OrderService > ComputeTotal" });
        var reversed = new CitationItem(new Citation { FilePath = OrderService, StartLine = 8, EndLine = 2 });

        Assert.Equal("OrderService.cs:12", line.Text);
        Assert.False(line.IsSpan);
        Assert.Equal(OrderService + ":12", line.ToolTip);
        Assert.Equal("OrderService.cs:9-12", span.Text);
        Assert.True(span.IsSpan);
        Assert.Equal((8, 11), (span.StartLine, span.EndLine));
        Assert.Equal("src/Orders/OrderService.cs > OrderService > ComputeTotal", span.ToolTip);
        Assert.Equal(ChatStrings.Format(ChatStrings.CitationNameFormat, "OrderService.cs:9-12"), span.AutomationName);
        Assert.Equal("OrderService.cs:9", reversed.Text); // an end before the start reads as the start line
        Assert.False(line.IsImplicit);
        Assert.True(reversed.IsImplicit); // ReplyOffset -1: the reply as a whole
    }

    [Fact]
    public void From_DropsRepeatsAndFileless_AndPrefersTheExplicitCitationOfASpan()
    {
        var items = CitationItem.From(
            [
                new Citation { FilePath = OrderService, StartLine = 8, EndLine = 11, ReplyOffset = -1 },
                new Citation { FilePath = OrderService, StartLine = 8, EndLine = 11, ReplyOffset = 20 },
                new Citation { FilePath = string.Empty, StartLine = 1 },
                new Citation { FilePath = @"C:\repo\src\StockRoom.cs", StartLine = 2, EndLine = 7, ReplyOffset = -1 },
            ],
            open: null);

        Assert.Equal(["OrderService.cs:9-12", "StockRoom.cs:3-8"], items.Select(i => i.Text));
        Assert.False(items[0].IsImplicit);
        Assert.True(items[1].IsImplicit);
    }

    [Fact]
    public void AReply_ShowsExplicitCitationsAsLinks_AndImplicitOnesCollapsedUnderSources()
    {
        var turn = ChatTurnViewModel.FromSavedReply(
            "model",
            null,
            "text",
            stopped: false,
            CitationItem.From(
                [
                    new Citation { FilePath = OrderService, StartLine = 8, EndLine = 11, ReplyOffset = 12 },
                    new Citation { FilePath = @"C:\repo\src\StockRoom.cs", StartLine = 2, EndLine = 7, ReplyOffset = -1 },
                    new Citation { FilePath = @"C:\repo\src\Runner.cs", StartLine = 3, EndLine = 9, ReplyOffset = -1 },
                ],
                open: null));

        Assert.True(turn.HasCitations);
        Assert.True(turn.HasExplicitCitations);
        Assert.Equal(["OrderService.cs:9-12"], turn.ExplicitCitations.Select(c => c.Text));
        Assert.True(turn.HasImplicitCitations);
        Assert.Equal(["StockRoom.cs:3-8", "Runner.cs:4-10"], turn.ImplicitCitations.Select(c => c.Text));
        Assert.Equal(ChatStrings.Format(ChatStrings.SourcesFormat, 2), turn.SourcesText);
        Assert.Equal("Sources (2)", turn.SourcesText);
        Assert.False(turn.AreSourcesExpanded);
        Assert.Equal(ChatStrings.Format(ChatStrings.SourcesCollapsedNameFormat, 2), turn.SourcesAutomationName);

        Assert.True(turn.ToggleSourcesCommand.CanExecute(null));
        turn.ToggleSourcesCommand.Execute(null);
        Assert.True(turn.AreSourcesExpanded);
        Assert.Equal(ChatStrings.Format(ChatStrings.SourcesExpandedNameFormat, 2), turn.SourcesAutomationName);
        turn.ToggleSourcesCommand.Execute(null);
        Assert.False(turn.AreSourcesExpanded);
    }

    [Fact]
    public void AReplyWithOnlyImplicitSources_HasNoLinkRow_AndOneWithNone_HasNoSourcesLine()
    {
        var implicitOnly = ChatTurnViewModel.FromSavedReply("m", null, "t", false, CitationItem.From([new Citation { FilePath = OrderService, StartLine = 1, EndLine = 3 }], null));
        var none = ChatTurnViewModel.FromSavedReply("m", null, "t", false, []);

        Assert.False(implicitOnly.HasExplicitCitations);
        Assert.True(implicitOnly.HasImplicitCitations);
        Assert.False(none.HasImplicitCitations);
        Assert.False(none.ToggleSourcesCommand.CanExecute(null));
    }
}

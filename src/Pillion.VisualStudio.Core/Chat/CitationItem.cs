using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Windows.Input;
using Pillion.Contracts;

namespace Pillion.VisualStudio.Core;

/// <summary>
/// One cited span under a reply, shown as a <c>file:line</c> or <c>file:start-end</c> link (work item 2.2 restores the
/// citations a reply was saved with; work item 2.12 adds spans and implicit sources). Opening it goes to the file with the
/// span selected.
/// </summary>
public sealed class CitationItem
{
    /// <summary>Initializes a new instance of the <see cref="CitationItem"/> class.</summary>
    /// <param name="citation">The citation from the host.</param>
    public CitationItem(Citation citation)
    {
        Citation = citation ?? throw new ArgumentNullException(nameof(citation));
    }

    /// <summary>Gets the citation as the host sent it.</summary>
    public Citation Citation { get; }

    /// <summary>Gets the full path of the file.</summary>
    public string FilePath => Citation.FilePath ?? string.Empty;

    /// <summary>Gets the zero-based first line of the span.</summary>
    public int StartLine => Math.Max(0, Citation.StartLine);

    /// <summary>Gets the zero-based last line of the span (inclusive; the first line when the host sent less).</summary>
    public int EndLine => Math.Max(StartLine, Citation.EndLine);

    /// <summary>Gets a value indicating whether the span covers more than one line.</summary>
    public bool IsSpan => EndLine > StartLine;

    /// <summary>
    /// Gets a value indicating whether the citation is implicit: it applies to the reply as a whole
    /// (<see cref="Citation.ReplyOffset"/> -1) because the reply named none of the code it was given, so the window lists it
    /// collapsed under "Sources" rather than as a link the reply referred to.
    /// </summary>
    public bool IsImplicit => Citation.ReplyOffset < 0;

    /// <summary>Gets the link text, for example <c>Program.cs:12-15</c> (lines are shown one-based).</summary>
    public string Text
    {
        get
        {
            var name = FilePath.Length == 0 ? "?" : Path.GetFileName(FilePath);
            return IsSpan
                ? string.Format(CultureInfo.InvariantCulture, "{0}:{1}-{2}", name, StartLine + 1, EndLine + 1)
                : string.Format(CultureInfo.InvariantCulture, "{0}:{1}", name, StartLine + 1);
        }
    }

    /// <summary>Gets the tooltip: the chunk's breadcrumb when it has one, else the full path with the line numbers.</summary>
    public string ToolTip => !string.IsNullOrWhiteSpace(Citation.Breadcrumb)
        ? Citation.Breadcrumb
        : IsSpan
            ? FilePath + ":" + (StartLine + 1).ToString(CultureInfo.InvariantCulture) + "-" + (EndLine + 1).ToString(CultureInfo.InvariantCulture)
            : FilePath + ":" + (StartLine + 1).ToString(CultureInfo.InvariantCulture);

    /// <summary>Gets the name a screen reader announces for the link.</summary>
    public string AutomationName => ChatStrings.Format(ChatStrings.CitationNameFormat, Text);

    /// <summary>Gets the command that opens the span in the editor (the window's open-citation command; its parameter is this item).</summary>
    public ICommand? OpenCommand { get; internal set; }

    /// <summary>Makes the items for a reply's citations, dropping duplicates and ones without a file.</summary>
    /// <param name="citations">The citations.</param>
    /// <param name="open">The command each item opens with.</param>
    /// <returns>The items, in order (an explicit citation wins over an implicit one of the same span).</returns>
    public static IReadOnlyList<CitationItem> From(IEnumerable<Citation>? citations, ICommand? open) =>
        [
            .. (citations ?? [])
                .Where(c => c is not null && !string.IsNullOrWhiteSpace(c.FilePath))
                .Select(c => new CitationItem(c) { OpenCommand = open })
                .GroupBy(i => i.Text + "|" + i.FilePath, StringComparer.OrdinalIgnoreCase)
                .Select(g => g.FirstOrDefault(i => !i.IsImplicit) ?? g.First()),
        ];
}

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Windows.Input;
using Pillion.Contracts;

namespace Pillion.VisualStudio.Core;

/// <summary>
/// One cited span under a reply, shown as a plain <c>file:line</c> link (work item 2.2 restores the citations a reply was saved
/// with; work item 2.12 makes them richer). Opening it goes to the line in the editor.
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

    /// <summary>Gets the link text, for example <c>Program.cs:12-15</c> (lines are shown one-based).</summary>
    public string Text
    {
        get
        {
            var name = FilePath.Length == 0 ? "?" : Path.GetFileName(FilePath);
            var start = StartLine + 1;
            var end = Math.Max(StartLine, Citation.EndLine) + 1;
            return end > start
                ? string.Format(CultureInfo.InvariantCulture, "{0}:{1}-{2}", name, start, end)
                : string.Format(CultureInfo.InvariantCulture, "{0}:{1}", name, start);
        }
    }

    /// <summary>Gets the tooltip: the full path with the line numbers and the chunk's breadcrumb when it has one.</summary>
    public string ToolTip =>
        string.IsNullOrWhiteSpace(Citation.Breadcrumb) ? FilePath + ":" + (StartLine + 1).ToString(CultureInfo.InvariantCulture) : Citation.Breadcrumb;

    /// <summary>Gets the name a screen reader announces for the link.</summary>
    public string AutomationName => ChatStrings.Format(ChatStrings.CitationNameFormat, Text);

    /// <summary>Gets the command that opens the span in the editor (the window's open-citation command; its parameter is this item).</summary>
    public ICommand? OpenCommand { get; internal set; }

    /// <summary>Makes the items for a reply's citations, dropping duplicates and ones without a file.</summary>
    /// <param name="citations">The citations.</param>
    /// <param name="open">The command each item opens with.</param>
    /// <returns>The items, in order.</returns>
    public static IReadOnlyList<CitationItem> From(IEnumerable<Citation>? citations, ICommand? open) =>
        [
            .. (citations ?? [])
                .Where(c => c is not null && !string.IsNullOrWhiteSpace(c.FilePath))
                .Select(c => new CitationItem(c) { OpenCommand = open })
                .GroupBy(i => i.Text + "|" + i.FilePath, StringComparer.OrdinalIgnoreCase)
                .Select(g => g.First()),
        ];
}

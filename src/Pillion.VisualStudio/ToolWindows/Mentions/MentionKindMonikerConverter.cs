using System;
using System.Globalization;
using System.Windows.Data;
using Microsoft.VisualStudio.Imaging;
using Microsoft.VisualStudio.Imaging.Interop;
using Pillion.VisualStudio.Core.Mentions;

namespace Pillion.VisualStudio.ToolWindows;

/// <summary>
/// The icon of a mention picker entry by its mention kind (work item 2.13): Visual Studio's own image monikers, so the icons
/// follow the theme and high contrast. The entry's text never depends on the icon, which is hidden from screen readers.
/// </summary>
internal sealed class MentionKindMonikerConverter : IValueConverter
{
    /// <summary>The icon of a kind.</summary>
    /// <param name="kind">The mention kind.</param>
    /// <returns>The moniker.</returns>
    public static ImageMoniker MonikerOf(string? kind) => kind switch
    {
        MentionSyntax.File => KnownMonikers.Document,
        MentionSyntax.Folder => KnownMonikers.FolderClosed,
        MentionSyntax.Symbol => KnownMonikers.Method,
        MentionSyntax.Docs => KnownMonikers.MarkdownFile,
        MentionSyntax.Selection => KnownMonikers.Select,
        MentionSyntax.Errors => KnownMonikers.StatusError,
        MentionSyntax.Clipboard => KnownMonikers.Paste,
        MentionSyntax.Solution => KnownMonikers.Solution,
        MentionSyntax.Git => KnownMonikers.Git,
        MentionSyntax.Output => KnownMonikers.Output,
        MentionSyntax.Build => KnownMonikers.BuildSolution,
        MentionSyntax.Tests => KnownMonikers.TestGroup,
        _ => KnownMonikers.Document,
    };

    /// <inheritdoc/>
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) => MonikerOf(value as string);

    /// <inheritdoc/>
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
}

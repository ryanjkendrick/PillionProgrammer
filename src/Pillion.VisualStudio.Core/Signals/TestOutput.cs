using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace Pillion.VisualStudio.Core.Signals;

/// <summary>
/// Picks the failing tests out of the Tests pane for <c>@tests</c> (work item 2.13). Test Explorer has no documented API for
/// results (design section 5), so the pane's text stands in: each failure block (a <c>Failed</c> line and what follows it:
/// the error message and the stack trace) and the run summary are kept; when the pane names no failures, its tail is used.
/// </summary>
public static class TestOutput
{
    private static readonly Regex FailureStart = new(@"^\s*(?:Failed|X)\s+\S", RegexOptions.CultureInvariant);
    private static readonly Regex Summary = new(@"(?:Test run finished|Total tests|Failed!|Passed!|========== )", RegexOptions.CultureInvariant);

    /// <summary>The failure blocks and summary lines of the Tests pane, or the whole text when it names no failures.</summary>
    /// <param name="text">The Tests pane's text (already tail-limited).</param>
    /// <returns>The text to pin.</returns>
    public static string FailuresOrTail(string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return text ?? string.Empty;
        }

        var lines = text.Replace("\r\n", "\n").Split('\n');
        var kept = new List<string>();
        var inFailure = false;
        var failures = 0;
        foreach (var line in lines)
        {
            if (FailureStart.IsMatch(line))
            {
                inFailure = true;
                failures++;
                kept.Add(line);
                continue;
            }

            if (inFailure && (line.Length == 0 || char.IsWhiteSpace(line[0])))
            {
                kept.Add(line);
                continue;
            }

            inFailure = false;
            if (Summary.IsMatch(line))
            {
                kept.Add(line);
            }
        }

        return failures == 0 ? text : string.Join("\n", kept.Where((l, i) => l.Length > 0 || (i > 0 && kept[i - 1].Length > 0))).TrimEnd();
    }
}

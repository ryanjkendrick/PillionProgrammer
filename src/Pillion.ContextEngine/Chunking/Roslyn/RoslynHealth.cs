using System.Globalization;
using Microsoft.CodeAnalysis;

namespace Pillion.ContextEngine.Chunking.Roslyn;

/// <summary>Decides when a Roslyn parse failed so badly that windows serve the file better than syntax units.</summary>
internal static class RoslynHealth
{
    /// <summary>
    /// True when the tree has more syntax errors than one per twenty lines (and at least twenty): the text is most
    /// likely not code in this language, or is damaged beyond what error recovery can make sense of.
    /// </summary>
    public static bool IsBroken(SyntaxNode root, int lineCount, out string? reason)
    {
        reason = null;
        if (!root.ContainsDiagnostics)
        {
            return false;
        }

        var limit = Math.Max(20, lineCount / 20);
        var errors = 0;
        foreach (var diagnostic in root.GetDiagnostics())
        {
            if (diagnostic.Severity == DiagnosticSeverity.Error && ++errors > limit)
            {
                reason = string.Create(CultureInfo.InvariantCulture,
                    $"More than {limit} syntax errors in {lineCount} lines.");
                return true;
            }
        }

        return false;
    }
}

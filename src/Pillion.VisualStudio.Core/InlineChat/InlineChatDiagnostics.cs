using System;
using System.Collections.Generic;
using System.Linq;
using Pillion.Contracts;
using Pillion.VisualStudio.Core.Signals;
using DiagnosticInfo = Pillion.VisualStudio.Core.Signals.DiagnosticInfo;

namespace Pillion.VisualStudio.Core.InlineChat;

/// <summary>Turns the IDE signals' diagnostics into the ones an inline edit request carries for <c>/fix</c>.</summary>
public static class InlineChatDiagnostics
{
    /// <summary>The most diagnostics attached to one request.</summary>
    public const int MaxDiagnostics = 50;

    /// <summary>
    /// Keeps the errors and warnings of one file that have a line, converts the 1-based lines to the zero-based lines of
    /// <see cref="InlineEditDiagnostic.Line"/>, and drops duplicates (the Error List and Roslyn can both report one).
    /// </summary>
    /// <param name="diagnostics">The diagnostics from <see cref="IIdeSignals.GetDiagnosticsAsync"/>.</param>
    /// <param name="filePath">The document's full path; diagnostics of other files are dropped.</param>
    /// <returns>The diagnostics, by line.</returns>
    public static IReadOnlyList<InlineEditDiagnostic> FromSignals(IEnumerable<DiagnosticInfo>? diagnostics, string? filePath) =>
        (diagnostics ?? [])
            .Where(d => d is not null
                && d.Line > 0
                && d.Severity is IdeSeverity.Error or IdeSeverity.Warning
                && string.Equals(d.File, filePath, StringComparison.OrdinalIgnoreCase))
            .Select(d => new InlineEditDiagnostic
            {
                Line = d.Line - 1,
                Severity = d.Severity == IdeSeverity.Error ? DiagnosticSeverity.Error : DiagnosticSeverity.Warning,
                Code = d.Code ?? string.Empty,
                Message = d.Message,
            })
            .GroupBy(d => (d.Line, d.Code, d.Message))
            .Select(g => g.First())
            .OrderBy(d => d.Line)
            .Take(MaxDiagnostics)
            .ToList();
}

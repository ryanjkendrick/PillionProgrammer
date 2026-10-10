using System;
using System.Collections.Generic;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Pillion.VisualStudio.Core.Signals;

namespace Pillion.VisualStudio.Core.Mentions;

/// <summary>
/// Formats diagnostics as the <c>@errors</c> payload Pillion Host reads (<c>MentionRequest.Payload</c>): one JSON object per
/// line with <c>severity</c> (<c>error</c>, <c>warning</c> or <c>info</c>), <c>id</c>, <c>message</c>, <c>file</c> and
/// <c>line</c> (1-based; left out when unknown).
/// </summary>
public static class ErrorsPayload
{
    /// <summary>The most diagnostics sent; the host keeps at most 200 anyway, errors first.</summary>
    public const int MaxDiagnostics = 500;

    /// <summary>Formats diagnostics.</summary>
    /// <param name="diagnostics">The diagnostics (errors first, as <see cref="IIdeSignals.GetDiagnosticsAsync"/> returns them).</param>
    /// <returns>The JSON lines joined with <c>\n</c>; empty when there are none.</returns>
    public static string Format(IEnumerable<DiagnosticInfo> diagnostics)
    {
        if (diagnostics is null)
        {
            throw new ArgumentNullException(nameof(diagnostics));
        }

        var builder = new StringBuilder();
        var count = 0;
        foreach (var diagnostic in diagnostics)
        {
            if (diagnostic is null)
            {
                continue;
            }

            if (count++ == MaxDiagnostics)
            {
                break;
            }

            var line = new JObject
            {
                ["severity"] = SeverityName(diagnostic.Severity),
                ["id"] = diagnostic.Code ?? string.Empty,
                ["message"] = diagnostic.Message,
                ["file"] = diagnostic.File ?? string.Empty,
            };
            if (diagnostic.Line > 0)
            {
                line["line"] = diagnostic.Line;
            }

            if (builder.Length > 0)
            {
                builder.Append('\n');
            }

            builder.Append(line.ToString(Formatting.None));
        }

        return builder.ToString();
    }

    /// <summary>The payload's name for a severity.</summary>
    /// <param name="severity">The severity.</param>
    /// <returns><c>error</c>, <c>warning</c> or <c>info</c>.</returns>
    public static string SeverityName(IdeSeverity severity) => severity switch
    {
        IdeSeverity.Error => "error",
        IdeSeverity.Warning => "warning",
        _ => "info",
    };
}

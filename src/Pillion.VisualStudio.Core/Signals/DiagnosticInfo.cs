namespace Pillion.VisualStudio.Core.Signals;

/// <summary>How serious a diagnostic is.</summary>
public enum IdeSeverity
{
    /// <summary>An error: the build fails.</summary>
    Error = 0,

    /// <summary>A warning.</summary>
    Warning = 1,

    /// <summary>A message, suggestion or informational diagnostic.</summary>
    Info = 2,
}

/// <summary>Where a diagnostic came from.</summary>
public enum DiagnosticSource
{
    /// <summary>The Error List (build output and live IntelliSense diagnostics, as the developer's filters show them).</summary>
    ErrorList = 0,

    /// <summary>The Roslyn workspace's compiler diagnostics for an open document.</summary>
    Roslyn = 1,
}

/// <summary>Which diagnostics <see cref="IIdeSignals.GetDiagnosticsAsync"/> returns.</summary>
public enum DiagnosticScope
{
    /// <summary>The document that is active in the editor.</summary>
    ActiveDocument = 0,

    /// <summary>Every diagnostic the Error List shows for the solution.</summary>
    Solution = 1,
}

/// <summary>One diagnostic: an error, warning or message about a place in a file.</summary>
public sealed class DiagnosticInfo
{
    /// <summary>Initializes a new instance of the <see cref="DiagnosticInfo"/> class.</summary>
    /// <param name="file">The full path of the file, or <see langword="null"/> when the diagnostic has none (for example a project-level error).</param>
    /// <param name="line">The 1-based line, or 0 when unknown.</param>
    /// <param name="column">The 1-based column, or 0 when unknown.</param>
    /// <param name="severity">The severity.</param>
    /// <param name="code">The diagnostic code (for example <c>CS1002</c>), or <see langword="null"/>.</param>
    /// <param name="message">The message text.</param>
    /// <param name="project">The project name, or <see langword="null"/>.</param>
    /// <param name="source">Where the diagnostic came from.</param>
    public DiagnosticInfo(string? file, int line, int column, IdeSeverity severity, string? code, string message, string? project, DiagnosticSource source)
    {
        File = file;
        Line = line;
        Column = column;
        Severity = severity;
        Code = code;
        Message = message ?? string.Empty;
        Project = project;
        Source = source;
    }

    /// <summary>Gets the full path of the file, or <see langword="null"/> when the diagnostic has none.</summary>
    public string? File { get; }

    /// <summary>Gets the 1-based line, or 0 when unknown.</summary>
    public int Line { get; }

    /// <summary>Gets the 1-based column, or 0 when unknown.</summary>
    public int Column { get; }

    /// <summary>Gets the severity.</summary>
    public IdeSeverity Severity { get; }

    /// <summary>Gets the diagnostic code (for example <c>CS1002</c>), or <see langword="null"/>.</summary>
    public string? Code { get; }

    /// <summary>Gets the message text.</summary>
    public string Message { get; }

    /// <summary>Gets the project name, or <see langword="null"/>.</summary>
    public string? Project { get; }

    /// <summary>Gets where the diagnostic came from.</summary>
    public DiagnosticSource Source { get; }

    /// <inheritdoc/>
    public override string ToString() =>
        $"{File ?? "(no file)"}({Line},{Column}): {Severity.ToString().ToLowerInvariant()} {Code}: {Message}";
}

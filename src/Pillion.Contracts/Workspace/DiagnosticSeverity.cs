namespace Pillion.Contracts;

/// <summary>The severity of a <see cref="DiagnosticInfo"/>.</summary>
public enum DiagnosticSeverity
{
    /// <summary>Not normally shown to the user.</summary>
    Hidden,

    /// <summary>Informational.</summary>
    Info,

    /// <summary>A warning.</summary>
    Warning,

    /// <summary>An error.</summary>
    Error,
}

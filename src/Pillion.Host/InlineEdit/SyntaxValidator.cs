using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.VisualBasic;

namespace Pillion.Host.InlineEdit;

/// <summary>A syntax error found in a document.</summary>
/// <param name="Line">The zero-based line in the parsed text.</param>
/// <param name="Code">The diagnostic id, for example <c>CS1002</c>.</param>
/// <param name="Message">The message.</param>
internal sealed record SyntaxError(int Line, string Code, string Message);

/// <summary>Finds syntax errors in a whole document without compiling it (design section 8, Validate).</summary>
internal interface ISyntaxValidator
{
    /// <summary>Determines whether a language can be validated.</summary>
    /// <param name="languageId">The language id.</param>
    /// <returns><see langword="true"/> for the languages the validator parses.</returns>
    bool Supports(string? languageId);

    /// <summary>Parses a document and returns its syntax errors.</summary>
    /// <param name="text">The document text.</param>
    /// <param name="languageId">A supported language id.</param>
    /// <param name="cancellationToken">A token that cancels the parse.</param>
    /// <returns>The errors, in document order.</returns>
    IReadOnlyList<SyntaxError> GetErrors(string text, string languageId, CancellationToken cancellationToken);
}

/// <summary>An <see cref="ISyntaxValidator"/> over Roslyn's C# and Visual Basic parsers (parse only; no compilation, no references).</summary>
internal sealed class RoslynSyntaxValidator : ISyntaxValidator
{
    private static readonly CSharpParseOptions CSharpOptions = new(Microsoft.CodeAnalysis.CSharp.LanguageVersion.Preview, DocumentationMode.Parse);
    private static readonly VisualBasicParseOptions VisualBasicOptions = new(Microsoft.CodeAnalysis.VisualBasic.LanguageVersion.Latest, DocumentationMode.Parse);

    /// <summary>Gets the shared instance.</summary>
    public static RoslynSyntaxValidator Instance { get; } = new();

    /// <inheritdoc/>
    public bool Supports(string? languageId) => Language(languageId) is not null;

    /// <inheritdoc/>
    public IReadOnlyList<SyntaxError> GetErrors(string text, string languageId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(text);
        var tree = Language(languageId) switch
        {
            LanguageNames.CSharp => CSharpSyntaxTree.ParseText(text, CSharpOptions, cancellationToken: cancellationToken),
            LanguageNames.VisualBasic => VisualBasicSyntaxTree.ParseText(text, VisualBasicOptions, cancellationToken: cancellationToken),
            _ => throw new NotSupportedException($"Syntax validation does not support '{languageId}'."),
        };

        return tree.GetDiagnostics(cancellationToken)
            .Where(d => d.Severity == Microsoft.CodeAnalysis.DiagnosticSeverity.Error)
            .Select(d => new SyntaxError(d.Location.GetLineSpan().StartLinePosition.Line, d.Id, d.GetMessage(System.Globalization.CultureInfo.InvariantCulture)))
            .ToList();
    }

    private static string? Language(string? languageId) => languageId?.Trim().ToLowerInvariant() switch
    {
        "csharp" or "c#" or "cs" => LanguageNames.CSharp,
        "vb" or "vbnet" or "vb.net" or "visualbasic" or "basic" => LanguageNames.VisualBasic,
        _ => null,
    };
}

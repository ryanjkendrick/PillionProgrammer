using Pillion.ContextEngine.Chunking;

namespace Pillion.ContextEngine.Symbols;

/// <summary>Finds the definitions and references in one file's syntax (design section 7, indexing step 4).</summary>
/// <remarks>
/// Implementations never throw because of the content of <c>text</c>: a parse that fails badly, a grammar that cannot
/// be loaded or an unexpected error yields an empty, incomplete <see cref="SymbolExtraction"/> and a call to
/// <see cref="SymbolExtractionOptions.OnFailure"/>. They are safe to call from several threads at once.
/// </remarks>
public interface ISymbolExtractor
{
    /// <summary>Extracts the symbols of one file.</summary>
    /// <param name="path">The file's path as it should be stored (usually relative to the repository root).</param>
    /// <param name="text">The file's text.</param>
    /// <param name="options">The failure callback; <see cref="SymbolExtractionOptions.Default"/> when null.</param>
    /// <returns>The definitions and references, in source order.</returns>
    SymbolExtraction Extract(string path, string text, SymbolExtractionOptions? options = null);
}

/// <summary>The symbols of one file.</summary>
/// <param name="FilePath">The file path, as passed to the extractor.</param>
/// <param name="Language">The language the file was read as (<see cref="LanguageIds.Text"/> when none applies).</param>
/// <param name="Definitions">The definitions, in source order.</param>
/// <param name="References">The references, in source order.</param>
/// <param name="IsComplete">False when extraction failed and the lists are empty or partial.</param>
public sealed record SymbolExtraction(
    string FilePath,
    string Language,
    IReadOnlyList<SymbolDefinition> Definitions,
    IReadOnlyList<SymbolReference> References,
    bool IsComplete = true)
{
    /// <summary>An extraction with nothing in it.</summary>
    /// <param name="path">The file path.</param>
    /// <param name="language">The language.</param>
    /// <param name="isComplete">False when this is the result of a failure.</param>
    /// <returns>The empty extraction.</returns>
    public static SymbolExtraction Empty(string path, string language, bool isComplete = true) =>
        new(path ?? string.Empty, language, [], [], isComplete);
}

/// <summary>Settings for symbol extraction.</summary>
public sealed record SymbolExtractionOptions
{
    /// <summary>The default options: no failure callback.</summary>
    public static SymbolExtractionOptions Default { get; } = new();

    /// <summary>
    /// Called when an extractor gives up on a file (a parse that fails badly, a grammar that cannot be loaded, an
    /// unexpected error), the same way <see cref="ChunkOptions.OnFallback"/> reports chunker fallbacks. Optional; for
    /// logging and the Index window. A callback that throws does not stop extraction.
    /// </summary>
    public Action<SymbolExtractionFailure>? OnFailure { get; init; }
}

/// <summary>Why symbol extraction gave up on one file.</summary>
/// <param name="Path">The file.</param>
/// <param name="Language">The language that was attempted.</param>
/// <param name="Reason">A short description (for logs; not shown verbatim to users).</param>
public sealed record SymbolExtractionFailure(string Path, string Language, string Reason);

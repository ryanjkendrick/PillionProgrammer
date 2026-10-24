using Pillion.ContextEngine.Chunking.Core;

namespace Pillion.ContextEngine.Chunking;

/// <summary>
/// The shared shape of the syntactic chunkers: find units in the parsed file, then let the assembler cover every line
/// exactly once and fit each unit into the cap. Any failure (a parse that fails badly, a grammar that will not load, an
/// unexpected exception) falls back to <see cref="WindowChunker"/> for the whole file and reports it through
/// <see cref="ChunkOptions.OnFallback"/>; malformed input never makes <see cref="Chunk"/> throw.
/// </summary>
public abstract class SyntaxChunker : IChunker
{
    /// <summary>The language recorded on the chunks.</summary>
    public abstract string Language { get; }

    /// <inheritdoc />
    public IReadOnlyList<Chunk> Chunk(string path, string text, ChunkOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();
        var document = new SourceDocument(path, text);
        if (document.IsBlank)
        {
            return [];
        }

        var budget = new TokenBudget(document, options);
        string? failure;
        try
        {
            using var scope = new ResourceScope();
            var units = CollectUnits(budget, scope, out failure);
            if (units is not null)
            {
                return ChunkAssembler.AssemblePartitioned(budget, units, Language);
            }
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            failure = $"{exception.GetType().Name}: {exception.Message}";
        }

        ReportFallback(options, new ChunkerFallback(path, Language, failure ?? "No units."));
        return WindowChunker.Windows(budget, Language);
    }

    /// <summary>Calls the fallback callback; a callback that throws does not stop chunking.</summary>
    internal static void ReportFallback(ChunkOptions options, ChunkerFallback fallback)
    {
        try
        {
            options.OnFallback?.Invoke(fallback);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            // The callback is for logging only.
        }
    }

    /// <summary>
    /// Finds the units of a file, or returns null with a reason to fall back to windows. Native resources the units
    /// still point into (syntax trees) go in <paramref name="scope"/>, which is disposed after assembly.
    /// </summary>
    private protected abstract List<ChunkUnit>? CollectUnits(TokenBudget budget, ResourceScope scope, out string? failure);
}

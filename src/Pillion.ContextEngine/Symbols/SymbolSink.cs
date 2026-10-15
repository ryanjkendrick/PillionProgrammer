using Pillion.ContextEngine.Chunking;
using Pillion.ContextEngine.Chunking.Core;

namespace Pillion.ContextEngine.Symbols;

/// <summary>
/// Collects one file's definitions and references for an extractor: numbers lines through the document, shares
/// repeated strings (names, containers) so a large file does not hold thousands of copies of the same text, and drops
/// empty names.
/// </summary>
internal sealed class SymbolSink(SourceDocument document, string language)
{
    private readonly Dictionary<string, string> _pool = new(StringComparer.Ordinal);
    private readonly Dictionary<(string Container, string Name, SymbolKind Kind), int> _ordinals = [];

    public SourceDocument Document { get; } = document;

    public string Language { get; } = language;

    public List<SymbolDefinition> Definitions { get; } = [];

    public List<SymbolReference> References { get; } = [];

    /// <summary>Adds a definition with lines given as offsets into the document.</summary>
    public SymbolDefinition? Define(string name, SymbolKind kind, string container, int startOffset, int endOffset, string signature)
    {
        var start = Document.LineOfOffset(startOffset);
        var end = Document.LastLineOfSpan(startOffset, endOffset);
        return DefineLines(name, kind, container, start, end, signature);
    }

    /// <summary>Adds a definition with lines given directly (clamped to the file).</summary>
    public SymbolDefinition? DefineLines(string name, SymbolKind kind, string container, int startLine, int endLine, string signature)
    {
        name = CleanName(name);
        if (name.Length == 0)
        {
            return null;
        }

        var lines = Math.Max(1, Document.LineCount);
        startLine = Math.Clamp(startLine, 1, lines);
        endLine = Math.Clamp(endLine, startLine, lines);
        // The overload ordinal: how many definitions with the same container, name and kind came before in this file.
        container = Share(container ?? string.Empty);
        name = Share(name);
        var key = (container, name, kind);
        var ordinal = _ordinals.GetValueOrDefault(key);
        _ordinals[key] = ordinal + 1;
        var definition = new SymbolDefinition(Document.Path, name, kind, container, startLine, endLine, signature, Language, ordinal: ordinal);
        Definitions.Add(definition);
        return definition;
    }

    /// <summary>Adds a reference at a document offset.</summary>
    public void Reference(string name, SymbolReferenceKind kind, int offset, string? qualifier, string fromContainer, bool isConstruction = false)
    {
        name = CleanName(name);
        if (name.Length == 0)
        {
            return;
        }

        qualifier = qualifier is null ? null : CleanName(qualifier);
        References.Add(new SymbolReference
        {
            FilePath = Document.Path,
            Line = Document.LineOfOffset(offset),
            Name = Share(name),
            Kind = kind,
            Qualifier = string.IsNullOrEmpty(qualifier) ? null : Share(qualifier),
            FromContainer = Share(fromContainer ?? string.Empty),
            IsConstruction = isConstruction,
            Language = Language,
        });
    }

    public SymbolExtraction ToExtraction() => new(Document.Path, Language, Definitions, References);

    /// <summary>Returns one shared instance per distinct string.</summary>
    public string Share(string value)
    {
        if (value.Length == 0)
        {
            return string.Empty;
        }

        if (_pool.TryGetValue(value, out var shared))
        {
            return shared;
        }

        _pool[value] = value;
        return value;
    }

    /// <summary>Joins breadcrumb segments the way containers are stored.</summary>
    public static string Join(IReadOnlyList<string> segments) => segments.Count switch
    {
        0 => string.Empty,
        1 => segments[0],
        _ => string.Join(Breadcrumbs.Separator, segments),
    };

    /// <summary>Calls the failure callback; a callback that throws does not stop extraction.</summary>
    public static void ReportFailure(SymbolExtractionOptions? options, SymbolExtractionFailure failure)
    {
        try
        {
            options?.OnFailure?.Invoke(failure);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            // The callback is for logging only.
        }
    }

    private static string CleanName(string name)
    {
        if (string.IsNullOrEmpty(name))
        {
            return string.Empty;
        }

        // Names are identifiers; anything with white space or longer than a breadcrumb segment is sanitised.
        foreach (var c in name)
        {
            if (char.IsWhiteSpace(c) || char.IsControl(c))
            {
                return Breadcrumbs.Sanitize(name);
            }
        }

        return name.Length > Breadcrumbs.MaxSegmentLength ? Breadcrumbs.Sanitize(name) : name;
    }
}

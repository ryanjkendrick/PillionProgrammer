using System.Globalization;
using System.Text;
using Pillion.ContextEngine.Chunking;
using Pillion.ContextEngine.Chunking.Core;
using Pillion.ContextEngine.Symbols;

namespace Pillion.ContextEngine.Tests.Symbols;

/// <summary>Extracts fixtures' symbols, checks the invariants every extraction must satisfy, and describes them.</summary>
internal static class SymbolFixture
{
    /// <summary>Extracts a fixture under the path <c>src/&lt;file name&gt;</c>, checks the invariants and fails on a failure report.</summary>
    public static SymbolExtraction Extract(string relative)
    {
        var path = "src/" + Path.GetFileName(relative);
        return Extract(path, Fixture.Read(relative));
    }

    /// <summary>Extracts text, checks the invariants and fails on a failure report.</summary>
    public static SymbolExtraction Extract(string path, string text)
    {
        var failures = new List<SymbolExtractionFailure>();
        var extraction = new FileSymbolExtractor().Extract(path, text, new SymbolExtractionOptions { OnFailure = failures.Add });
        Assert.True(failures.Count == 0, string.Join("; ", failures.Select(f => f.Reason)));
        Assert.True(extraction.IsComplete);
        var failure = Check(path, text, extraction);
        Assert.True(failure is null, failure + Environment.NewLine + Describe(extraction));
        return extraction;
    }

    /// <summary>
    /// The invariants: every definition and reference is in the file, its lines are inside the file, start is not after
    /// end, names are non-empty one-line text, definitions are approximate, ids match the formula, and containers and
    /// from-containers are well formed breadcrumb tails. Returns a failure message, or null.
    /// </summary>
    public static string? Check(string path, string text, SymbolExtraction extraction)
    {
        var lines = Math.Max(1, new SourceDocument(path, text).LineCount);
        foreach (var definition in extraction.Definitions)
        {
            if (definition.FilePath != path)
            {
                return $"Definition {definition.Name} has path {definition.FilePath}.";
            }

            if (definition.StartLine < 1 || definition.EndLine > lines || definition.StartLine > definition.EndLine)
            {
                return $"Definition {definition.Name} has lines {definition.StartLine}-{definition.EndLine} in a {lines}-line file.";
            }

            if (string.IsNullOrWhiteSpace(definition.Name) || definition.Name.AsSpan().IndexOfAny('\r', '\n') >= 0)
            {
                return $"Definition at line {definition.StartLine} has the name '{definition.Name}'.";
            }

            if (!definition.IsApproximate)
            {
                return $"Definition {definition.Name} is not marked approximate.";
            }

            if (definition.Id != SymbolIds.Compute(definition.FilePath, definition.Container, definition.Name, definition.Kind, definition.StartLine))
            {
                return $"Definition {definition.Name} has an id that does not match the formula.";
            }

            if (definition.Signature.AsSpan().IndexOfAny('\r', '\n') >= 0 || definition.Signature.Length > SignatureText.MaxLength)
            {
                return $"Definition {definition.Name} has a signature that is not one short line.";
            }

            if (definition.Container.Length > 0 && !Breadcrumbs.IsWellFormed("p" + Breadcrumbs.Separator + definition.Container))
            {
                return $"Definition {definition.Name} has the container '{definition.Container}'.";
            }
        }

        foreach (var reference in extraction.References)
        {
            if (reference.FilePath != path || reference.Line < 1 || reference.Line > lines)
            {
                return $"Reference {reference.Name} has line {reference.Line} in a {lines}-line file.";
            }

            if (string.IsNullOrWhiteSpace(reference.Name) || reference.Name.AsSpan().IndexOfAny('\r', '\n') >= 0)
            {
                return $"Reference at line {reference.Line} has the name '{reference.Name}'.";
            }

            if (reference.FromContainer.Length > 0 && !Breadcrumbs.IsWellFormed("p" + Breadcrumbs.Separator + reference.FromContainer))
            {
                return $"Reference {reference.Name} has the from-container '{reference.FromContainer}'.";
            }
        }

        return null;
    }

    /// <summary>A definition with a qualified name (<c>container &gt; name</c>) and kind; fails unless exactly one matches.</summary>
    public static SymbolDefinition Definition(SymbolExtraction extraction, string qualifiedName, SymbolKind kind)
    {
        var matches = extraction.Definitions.Where(d => d.QualifiedName == qualifiedName && d.Kind == kind).ToList();
        Assert.True(matches.Count == 1, $"Expected one {kind} '{qualifiedName}', found {matches.Count}:{Environment.NewLine}{Describe(extraction)}");
        return matches[0];
    }

    /// <summary>Asserts there is at least one reference with the name and kind (and qualifier, when given).</summary>
    public static SymbolReference Reference(SymbolExtraction extraction, string name, SymbolReferenceKind kind, string? qualifier = null)
    {
        var match = extraction.References.FirstOrDefault(r => r.Name == name && r.Kind == kind && (qualifier is null || r.Qualifier == qualifier));
        Assert.True(match is not null, $"Expected a {kind} reference to '{name}'{(qualifier is null ? "" : " qualified by " + qualifier)}:{Environment.NewLine}{Describe(extraction)}");
        return match!;
    }

    /// <summary>Asserts there is no reference with the name and kind.</summary>
    public static void NoReference(SymbolExtraction extraction, string name, SymbolReferenceKind kind) =>
        Assert.True(!extraction.References.Any(r => r.Name == name && r.Kind == kind),
            $"Expected no {kind} reference to '{name}':{Environment.NewLine}{Describe(extraction)}");

    public static string Describe(SymbolExtraction extraction)
    {
        var builder = new StringBuilder();
        builder.Append(CultureInfo.InvariantCulture, $"{extraction.FilePath} ({extraction.Language}): {extraction.Definitions.Count} definitions, {extraction.References.Count} references").AppendLine();
        foreach (var d in extraction.Definitions)
        {
            builder.Append(CultureInfo.InvariantCulture, $"  def {d.StartLine}-{d.EndLine} {SymbolKinds.Name(d.Kind)} [{d.Container}] {d.Name} :: {d.Signature}").AppendLine();
        }

        foreach (var r in extraction.References)
        {
            builder.Append(CultureInfo.InvariantCulture, $"  ref {r.Line} {SymbolKinds.Name(r.Kind)} {(r.Qualifier is null ? "" : r.Qualifier + ".")}{r.Name} from [{r.FromContainer}]").AppendLine();
        }

        return builder.ToString();
    }
}

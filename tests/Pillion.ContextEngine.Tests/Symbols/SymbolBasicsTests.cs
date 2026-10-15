using System.Globalization;
using Pillion.ContextEngine.Symbols;

namespace Pillion.ContextEngine.Tests.Symbols;

public sealed class SymbolBasicsTests
{
    [Fact]
    public void The_chunker_and_symbols_versions_are_positive_integer_strings()
    {
        foreach (var value in new[] { ContextEngineVersions.ChunkerVersion, ContextEngineVersions.SymbolsVersion })
        {
            Assert.True(int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var version));
            Assert.True(version >= 1);
        }
    }

    [Fact]
    public void Kind_names_are_stable_and_round_trip()
    {
        foreach (var kind in Enum.GetValues<SymbolKind>())
        {
            Assert.True(SymbolKinds.TryParse(SymbolKinds.Name(kind), out SymbolKind parsed));
            Assert.Equal(kind, parsed);
        }

        foreach (var kind in Enum.GetValues<SymbolReferenceKind>())
        {
            Assert.True(SymbolKinds.TryParse(SymbolKinds.Name(kind), out SymbolReferenceKind parsed));
            Assert.Equal(kind, parsed);
        }

        // Persisted values: never renumber.
        Assert.Equal((1, "namespace"), ((int)SymbolKind.Namespace, SymbolKinds.Name(SymbolKind.Namespace)));
        Assert.Equal((12, "method"), ((int)SymbolKind.Method, SymbolKinds.Name(SymbolKind.Method)));
        Assert.Equal((24, "element-id"), ((int)SymbolKind.ElementId, SymbolKinds.Name(SymbolKind.ElementId)));
        Assert.Equal((1, "call"), ((int)SymbolReferenceKind.Call, SymbolKinds.Name(SymbolReferenceKind.Call)));
        Assert.Equal((5, "use"), ((int)SymbolReferenceKind.Use, SymbolKinds.Name(SymbolReferenceKind.Use)));
        Assert.False(SymbolKinds.TryParse("nope", out SymbolKind _));
    }

    [Fact]
    public void Kind_compatibility_matches_the_reference_kind()
    {
        Assert.True(SymbolKinds.IsCompatible(SymbolReferenceKind.Call, SymbolKind.Method));
        Assert.True(SymbolKinds.IsCompatible(SymbolReferenceKind.Call, SymbolKind.Class));
        Assert.False(SymbolKinds.IsCompatible(SymbolReferenceKind.Call, SymbolKind.Field));
        Assert.True(SymbolKinds.IsCompatible(SymbolReferenceKind.TypeUse, SymbolKind.Interface));
        Assert.False(SymbolKinds.IsCompatible(SymbolReferenceKind.TypeUse, SymbolKind.Method));
        Assert.False(SymbolKinds.IsCompatible(SymbolReferenceKind.Inheritance, SymbolKind.Namespace));
        Assert.True(SymbolKinds.IsCompatible(SymbolReferenceKind.Import, SymbolKind.Module));
        Assert.True(SymbolKinds.IsCompatible(SymbolReferenceKind.Use, SymbolKind.Property));
    }

    [Fact]
    public void Ids_follow_the_formula_and_differ_by_every_part()
    {
        var id = SymbolIds.Compute("src/A.cs", "N > A", "M", SymbolKind.Method, 1);

        Assert.Equal(id, new SymbolDefinition("src/A.cs", "M", SymbolKind.Method, "N > A", 10, 12, ordinal: 1).Id);
        Assert.Equal(id, new SymbolDefinition("src/A.cs", "M", SymbolKind.Method, "N > A", 90, 99, ordinal: 1).Id); // lines are not part of it
        Assert.True(id > 0);
        Assert.NotEqual(id, SymbolIds.Compute("src/B.cs", "N > A", "M", SymbolKind.Method, 1));
        Assert.NotEqual(id, SymbolIds.Compute("src/A.cs", "N > B", "M", SymbolKind.Method, 1));
        Assert.NotEqual(id, SymbolIds.Compute("src/A.cs", "N > A", "M2", SymbolKind.Method, 1));
        Assert.NotEqual(id, SymbolIds.Compute("src/A.cs", "N > A", "M", SymbolKind.Property, 1));
        Assert.NotEqual(id, SymbolIds.Compute("src/A.cs", "N > A", "M", SymbolKind.Method, 0));
        Assert.True(SymbolIds.Compute(new string('x', 5000), "", "M", SymbolKind.Method, 1) > 0);
    }

    [Theory]
    [InlineData("public  string\n    Greet(string name)\n    {", "public string Greet(string name)")]
    [InlineData("def load(path):", "def load(path)")]
    [InlineData("const f = (x) =>", "const f = (x)")]
    [InlineData("@Override @SuppressWarnings(\"x\") public void run()", "public void run()")]
    [InlineData("bool operator ==", "bool operator ==")]
    [InlineData("   ", "")]
    public void Signatures_are_one_trimmed_line(string header, string expected) =>
        Assert.Equal(expected, SignatureText.Normalize(header));

    [Fact]
    public void Long_signatures_are_cut_and_displays_start_at_the_name()
    {
        var signature = SignatureText.Normalize(new string('a', 500));
        Assert.Equal(SignatureText.MaxLength, signature.Length);
        Assert.EndsWith("…", signature, StringComparison.Ordinal);

        Assert.Equal("Greet(string name)", SignatureText.FromName("public string Greet(string name)", "Greet"));
        Assert.Equal("Greeter<T> : IGreeter", SignatureText.FromName("class Greeter<T> : IGreeter", "Greeter"));
        Assert.Equal("Get", SignatureText.FromName("public int GetAll()", "Get"));
        Assert.Equal("x", SignatureText.FromName(string.Empty, "x"));
    }
}

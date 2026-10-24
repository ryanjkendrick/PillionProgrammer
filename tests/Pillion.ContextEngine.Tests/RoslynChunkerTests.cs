using System.Text;
using Pillion.ContextEngine.Chunking;
using Pillion.ContextEngine.Chunking.Roslyn;

namespace Pillion.ContextEngine.Tests;

public sealed class RoslynChunkerTests
{
    private const string Type = "src/Greeter.cs > Fixtures.Sample > Greeter<T>";

    [Fact]
    public void CSharp_member_carries_its_doc_comment_and_attributes()
    {
        var chunks = Fixture.Chunk("csharp/Greeter.cs");

        var greet = Fixture.Single(chunks, Type + " > Greet");
        Assert.Equal(ChunkKind.Member, greet.Kind);
        Assert.Equal(LanguageIds.CSharp, greet.Language);
        Assert.StartsWith("        /// <summary>Greets someone.</summary>", greet.Text, StringComparison.Ordinal);
        Assert.Contains("[Obsolete(\"Use GreetAll.\")]", greet.Text, StringComparison.Ordinal);
        Assert.Contains("return _prefix + \", \" + name;", greet.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("GreetAll(IEnumerable", greet.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void CSharp_type_header_is_the_declaration_without_members()
    {
        var chunks = Fixture.Chunk("csharp/Greeter.cs");

        var header = Fixture.Single(chunks, Type);
        Assert.Equal(ChunkKind.TypeHeader, header.Kind);
        Assert.Contains("/// <summary>Greets people.</summary>", header.Text, StringComparison.Ordinal);
        Assert.Contains("[Serializable]", header.Text, StringComparison.Ordinal);
        Assert.Contains("public sealed class Greeter<T> : IDisposable", header.Text, StringComparison.Ordinal);
        Assert.Contains("where T : class", header.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("_prefix", header.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void CSharp_usings_fields_nested_enums_and_records_get_their_own_chunks()
    {
        var chunks = Fixture.Chunk("csharp/Greeter.cs");

        var usings = chunks[0];
        Assert.Equal(ChunkKind.Document, usings.Kind);
        Assert.Equal("src/Greeter.cs", usings.Breadcrumb);
        Assert.Contains("using System.Collections.Generic;", usings.Text, StringComparison.Ordinal);

        Assert.Equal(ChunkKind.Member, Fixture.Single(chunks, Type + " > _prefix, _count").Kind);
        Assert.Equal(ChunkKind.TypeHeader, Fixture.Single(chunks, Type + " > Mood").Kind);
        Assert.Equal(ChunkKind.TypeHeader, Fixture.Single(chunks, "src/Greeter.cs > Fixtures.Sample > Point").Kind);
        Fixture.Single(chunks, Type + " > Greeter");
        Fixture.Single(chunks, Type + " > Count");
    }

    [Fact]
    public void VisualBasic_members_types_and_namespaces()
    {
        var chunks = Fixture.Chunk("vb/Greeter.vb");
        const string VbType = "src/Greeter.vb > Fixtures.Sample > Greeter";

        var header = Fixture.Single(chunks, VbType);
        Assert.Equal(ChunkKind.TypeHeader, header.Kind);
        Assert.Contains("<Serializable>", header.Text, StringComparison.Ordinal);
        Assert.Contains("Implements IDisposable", header.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("_prefix", header.Text, StringComparison.Ordinal);

        var greet = Fixture.Single(chunks, VbType + " > Greet");
        Assert.StartsWith("        ''' <summary>Greets someone.</summary>", greet.Text, StringComparison.Ordinal);
        Assert.EndsWith("End Function", greet.Text.TrimEnd(), StringComparison.Ordinal);
        Assert.Equal(LanguageIds.VisualBasic, greet.Language);

        Fixture.Single(chunks, VbType + " > New");
        Fixture.Single(chunks, VbType + " > Count");
        Fixture.Single(chunks, VbType + " > _prefix, _count");
        Assert.Equal(ChunkKind.TypeHeader, Fixture.Single(chunks, "src/Greeter.vb > Fixtures.Sample > Mood").Kind);
        Assert.Contains("Imports System", chunks[0].Text, StringComparison.Ordinal);
    }

    [Fact]
    public void Member_over_the_cap_is_split_at_statement_boundaries_with_part_markers()
    {
        var source = new StringBuilder("namespace N;\n\npublic class Big\n{\n    /// <summary>Long.</summary>\n    public void Run()\n    {\n");
        for (var i = 0; i < 120; i++)
        {
            source.Append("        if (value").Append(i).Append(" > 0)\n        {\n            Console.WriteLine(\"step ")
                .Append(i).Append("\");\n        }\n");
        }

        source.Append("    }\n}\n");
        var text = source.ToString();
        var options = ChunkOptions.Default with { MaxTokens = 128 };

        var chunks = new CSharpChunker().Chunk("src/Big.cs", text, options);

        ChunkInvariants.Assert("src/Big.cs", text, chunks, options);
        var parts = chunks.Where(c => c.Breadcrumb.StartsWith("src/Big.cs > N > Big > Run (part ", StringComparison.Ordinal)).ToList();
        Assert.True(parts.Count > 5, ChunkInvariants.Describe(chunks));
        Assert.All(parts, p => Assert.Equal(parts.Count, p.PartCount));
        Assert.Equal(Enumerable.Range(1, parts.Count), parts.Select(p => p.Part));
        Assert.Equal($"src/Big.cs > N > Big > Run (part 2 of {parts.Count})", parts[1].Breadcrumb);
        Assert.All(parts, p => Assert.False(p.IsTruncated));

        // Every part after the first starts at a statement: an if, never inside its block.
        foreach (var part in parts.Skip(1))
        {
            Assert.StartsWith("        if (value", part.Text, StringComparison.Ordinal);
        }

        Assert.Contains("/// <summary>Long.</summary>", parts[0].Text, StringComparison.Ordinal);
    }

    [Fact]
    public void Badly_broken_csharp_falls_back_to_windows_and_reports_it()
    {
        var text = string.Join("\n", Enumerable.Range(0, 200).Select(i => $"this is ) not ( code {i} ;; }} {{ class"));
        var fallbacks = new List<ChunkerFallback>();
        var options = ChunkOptions.Default with { OnFallback = fallbacks.Add };

        var chunks = new CSharpChunker().Chunk("src/Broken.cs", text, options);

        ChunkInvariants.Assert("src/Broken.cs", text, chunks, options);
        Assert.All(chunks, c => Assert.Equal(ChunkKind.Window, c.Kind));
        Assert.All(chunks, c => Assert.Equal(LanguageIds.CSharp, c.Language));
        var fallback = Assert.Single(fallbacks);
        Assert.Equal(LanguageIds.CSharp, fallback.Language);
    }

    [Fact]
    public void Slightly_broken_csharp_still_chunks_by_syntax()
    {
        var text = Fixture.Read("csharp/Greeter.cs").Replace("_count++;", "_count++", StringComparison.Ordinal);

        var chunks = new CSharpChunker().Chunk("src/Greeter.cs", text, ChunkOptions.Default);

        ChunkInvariants.Assert("src/Greeter.cs", text, chunks, ChunkOptions.Default);
        Fixture.Single(chunks, Type + " > Greet");
    }
}

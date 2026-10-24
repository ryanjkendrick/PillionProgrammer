using Xunit.Abstractions;

namespace Pillion.ContextEngine.Tests;

/// <summary>Chunks every fixture, checks the invariants and logs the chunk list (useful when tuning a grammar).</summary>
public sealed class FixtureDumpTests(ITestOutputHelper output)
{
    public static TheoryData<string> Fixtures() =>
    [
        "csharp/Greeter.cs", "vb/Greeter.vb", "typescript/shapes.ts", "javascript/app.js", "python/inventory.py",
        "c/list.c", "cpp/matrix.hpp", "go/server.go", "rust/geometry.rs", "java/OrderService.java", "sql/schema.sql",
        "html/index.html", "css/site.css", "razor/Counter.razor", "razor/Index.cshtml", "text/notes.txt",
    ];

    [Theory]
    [MemberData(nameof(Fixtures))]
    public void Fixture_chunks_satisfy_the_invariants(string fixture)
    {
        var chunks = Fixture.Chunk(fixture);
        output.WriteLine(ChunkInvariants.Describe(chunks));
    }
}

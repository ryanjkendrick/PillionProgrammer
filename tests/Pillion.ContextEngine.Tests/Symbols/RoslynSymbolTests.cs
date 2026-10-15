using Pillion.ContextEngine.Symbols;
using Pillion.ContextEngine.Symbols.Roslyn;

namespace Pillion.ContextEngine.Tests.Symbols;

public sealed class RoslynSymbolTests
{
    private const string Type = "Fixtures.Sample > Greeter<T>";

    [Fact]
    public void CSharp_definitions_have_kinds_containers_lines_and_signatures()
    {
        var symbols = SymbolFixture.Extract("csharp/Greeter.cs");

        var ns = SymbolFixture.Definition(symbols, "Fixtures.Sample", SymbolKind.Namespace);
        Assert.Equal((5, 53), (ns.StartLine, ns.EndLine));

        var greeter = SymbolFixture.Definition(symbols, "Fixtures.Sample > Greeter", SymbolKind.Class);
        Assert.Equal(9, greeter.StartLine); // the declaration, after the attribute line
        Assert.Equal(50, greeter.EndLine);
        Assert.Equal("public sealed class Greeter<T> : IDisposable where T : class", greeter.Signature);

        var greet = SymbolFixture.Definition(symbols, Type + " > Greet", SymbolKind.Method);
        Assert.Equal((27, 31), (greet.StartLine, greet.EndLine));
        Assert.Equal("public string Greet(string name)", greet.Signature);
        Assert.Equal(Type, greet.Container);
        Assert.True(greet.IsApproximate);
        Assert.Equal("csharp", greet.Language);

        SymbolFixture.Definition(symbols, Type + " > Greeter", SymbolKind.Constructor);
        Assert.Equal("public int Count", SymbolFixture.Definition(symbols, Type + " > Count", SymbolKind.Property).Signature);
        Assert.Equal("private readonly string _prefix", SymbolFixture.Definition(symbols, Type + " > _prefix", SymbolKind.Field).Signature);
        SymbolFixture.Definition(symbols, Type + " > _count", SymbolKind.Field);
        SymbolFixture.Definition(symbols, Type + " > GreetAll", SymbolKind.Method);
        SymbolFixture.Definition(symbols, Type + " > Mood", SymbolKind.Enum);
        SymbolFixture.Definition(symbols, Type + " > Mood > Happy", SymbolKind.EnumMember);
        Assert.Equal("public record Point(int X, int Y)", SymbolFixture.Definition(symbols, "Fixtures.Sample > Point", SymbolKind.Class).Signature);
        SymbolFixture.Definition(symbols, "Fixtures.Sample > Point > X", SymbolKind.Property);
        SymbolFixture.Definition(symbols, "Fixtures.Sample > Point > Y", SymbolKind.Property);
        Assert.Equal(15, symbols.Definitions.Count);
    }

    [Fact]
    public void CSharp_references_are_imports_inheritance_calls_type_uses_and_member_uses()
    {
        var symbols = SymbolFixture.Extract("csharp/Greeter.cs");

        Assert.Equal(string.Empty, SymbolFixture.Reference(symbols, "System.Collections.Generic", SymbolReferenceKind.Import).FromContainer);
        Assert.Equal(Type, SymbolFixture.Reference(symbols, "IDisposable", SymbolReferenceKind.Inheritance).FromContainer);
        SymbolFixture.Reference(symbols, "SerializableAttribute", SymbolReferenceKind.TypeUse);
        SymbolFixture.Reference(symbols, "ObsoleteAttribute", SymbolReferenceKind.TypeUse);
        SymbolFixture.Reference(symbols, "IEnumerable", SymbolReferenceKind.TypeUse);
        var call = SymbolFixture.Reference(symbols, "Greet", SymbolReferenceKind.Call);
        Assert.Equal(Type + " > GreetAll", call.FromContainer);
        Assert.Equal(37, call.Line);
        Assert.Contains(symbols.References, r => r is { Name: "_count", Kind: SymbolReferenceKind.Use, FromContainer: Type + " > Greet" });
        Assert.Contains(symbols.References, r => r is { Name: "_count", Kind: SymbolReferenceKind.Use, FromContainer: Type + " > Count" });

        // Locals, parameters and type parameters are not references.
        Assert.DoesNotContain(symbols.References, r => r.Name is "name" or "names" or "prefix" or "T");
    }

    [Fact]
    public void CSharp_member_access_creation_initializers_and_top_level_statements()
    {
        const string Source = """
            using static System.Math;
            var service = OrderService.Create();
            var order = new Repository<Order>(42) { Total = 5 };
            Console.WriteLine(order.Total + service.Count);
            Print(nameof(order));

            static void Print(string text) { }

            public static class OrderService
            {
                public static OrderService Create() => null!;
                public int Count { get; }
                private int Local()
                {
                    int Helper(int x) => x;
                    return Helper(Count);
                }
            }
            """;
        var symbols = SymbolFixture.Extract("src/Program.cs", Source);

        SymbolFixture.Reference(symbols, "System.Math", SymbolReferenceKind.Import);
        Assert.Equal(string.Empty, SymbolFixture.Reference(symbols, "Create", SymbolReferenceKind.Call, "OrderService").FromContainer);
        SymbolFixture.Reference(symbols, "Repository", SymbolReferenceKind.Call);
        SymbolFixture.Reference(symbols, "Order", SymbolReferenceKind.TypeUse);
        SymbolFixture.Reference(symbols, "Total", SymbolReferenceKind.Use, "Repository");
        SymbolFixture.Reference(symbols, "WriteLine", SymbolReferenceKind.Call, "Console");
        SymbolFixture.Reference(symbols, "Print", SymbolReferenceKind.Call);
        SymbolFixture.NoReference(symbols, "nameof", SymbolReferenceKind.Call);

        // Top-level locals and a local function's parameter are not uses; the local function is not a definition.
        Assert.DoesNotContain(symbols.References, r => r is { Kind: SymbolReferenceKind.Use, Qualifier: null } && r.Name is "order" or "service" or "x");
        Assert.DoesNotContain(symbols.Definitions, d => d.Name is "Helper" or "Print");
        Assert.Equal("OrderService > Local", SymbolFixture.Reference(symbols, "Helper", SymbolReferenceKind.Call).FromContainer);
        Assert.Equal("OrderService > Local", symbols.References.Last(r => r is { Name: "Count", Kind: SymbolReferenceKind.Use }).FromContainer);
        SymbolFixture.Reference(symbols, "Count", SymbolReferenceKind.Use, "service"); // a variable qualifier is kept
    }

    [Fact]
    public void CSharp_file_that_is_not_csharp_reports_a_failure_and_yields_nothing()
    {
        var text = string.Join("\n", Enumerable.Range(0, 200).Select(i => $"}}}}}} ))) ]]] <<< @@@ {i} ''' \"\"\" ```"));
        var failures = new List<SymbolExtractionFailure>();

        var symbols = new CSharpSymbolExtractor().Extract("junk.cs", text, new SymbolExtractionOptions { OnFailure = failures.Add });

        Assert.False(symbols.IsComplete);
        Assert.Empty(symbols.Definitions);
        Assert.Empty(symbols.References);
        Assert.Equal("junk.cs", Assert.Single(failures).Path);
    }

    [Fact]
    public void VisualBasic_definitions_and_references()
    {
        var symbols = SymbolFixture.Extract("vb/Greeter.vb");
        const string VbType = "Fixtures.Sample > Greeter";

        SymbolFixture.Definition(symbols, "Fixtures.Sample", SymbolKind.Namespace);
        Assert.Equal(8, SymbolFixture.Definition(symbols, VbType, SymbolKind.Class).StartLine);
        Assert.Equal("Public Function Greet(name As String) As String", SymbolFixture.Definition(symbols, VbType + " > Greet", SymbolKind.Method).Signature);
        SymbolFixture.Definition(symbols, VbType + " > New", SymbolKind.Constructor);
        SymbolFixture.Definition(symbols, VbType + " > Count", SymbolKind.Property);
        SymbolFixture.Definition(symbols, VbType + " > _prefix", SymbolKind.Field);
        SymbolFixture.Definition(symbols, VbType + " > Dispose", SymbolKind.Method);
        SymbolFixture.Definition(symbols, "Fixtures.Sample > Mood", SymbolKind.Enum);
        SymbolFixture.Definition(symbols, "Fixtures.Sample > Mood > Sad", SymbolKind.EnumMember);

        SymbolFixture.Reference(symbols, "System.Collections.Generic", SymbolReferenceKind.Import);
        Assert.Equal(VbType, SymbolFixture.Reference(symbols, "IDisposable", SymbolReferenceKind.Inheritance).FromContainer);
        SymbolFixture.Reference(symbols, "SerializableAttribute", SymbolReferenceKind.TypeUse);
        SymbolFixture.Reference(symbols, "Dispose", SymbolReferenceKind.Use, "IDisposable");
        Assert.Equal(VbType + " > Greet", symbols.References.Last(r => r.Name == "_prefix").FromContainer);
        Assert.DoesNotContain(symbols.References, r => r.Name is "name" or "prefix");
    }

    [Fact]
    public void VisualBasic_calls_creations_and_modules()
    {
        const string Source = """
            Module Program
                Sub Main()
                    Dim repository As New OrderRepository()
                    Dim order = repository.Find(1)
                    Console.WriteLine(order.Total)
                End Sub
            End Module
            """;
        var symbols = SymbolFixture.Extract("src/Program.vb", Source);

        SymbolFixture.Definition(symbols, "Program", SymbolKind.Module);
        SymbolFixture.Definition(symbols, "Program > Main", SymbolKind.Method);
        SymbolFixture.Reference(symbols, "OrderRepository", SymbolReferenceKind.Call);
        SymbolFixture.Reference(symbols, "Find", SymbolReferenceKind.Call);
        SymbolFixture.Reference(symbols, "WriteLine", SymbolReferenceKind.Call, "Console");
        SymbolFixture.Reference(symbols, "Total", SymbolReferenceKind.Use);
        Assert.DoesNotContain(symbols.References, r => r.Name is "repository" or "order" && r.Qualifier is null);
    }

    [Fact]
    public void Razor_components_have_their_code_members_and_markup_references()
    {
        var counter = SymbolFixture.Extract("razor/Counter.razor");
        var component = SymbolFixture.Definition(counter, "Counter", SymbolKind.Class);
        Assert.Equal((1, 24), (component.StartLine, component.EndLine));
        var increment = SymbolFixture.Definition(counter, "Counter > IncrementCount", SymbolKind.Method);
        Assert.Equal((14, 18), (increment.StartLine, increment.EndLine));
        SymbolFixture.Definition(counter, "Counter > currentCount", SymbolKind.Field);
        SymbolFixture.Definition(counter, "Counter > OnInitialized", SymbolKind.Method);
        SymbolFixture.Reference(counter, "System.Globalization", SymbolReferenceKind.Import);
        Assert.Equal(6, counter.References.First(r => r is { Name: "currentCount", FromContainer: "Counter" }).Line);
        Assert.Equal(8, SymbolFixture.Reference(counter, "IncrementCount", SymbolReferenceKind.Use).Line);
        Assert.Equal("Counter > IncrementCount", counter.References.First(r => r.Line == 16).FromContainer);

        var index = SymbolFixture.Extract("razor/Index.cshtml");
        SymbolFixture.Definition(index, "Index > Shout", SymbolKind.Method);
        SymbolFixture.Reference(index, "IndexModel", SymbolReferenceKind.TypeUse);
        Assert.Equal("Index > @{ }", SymbolFixture.Reference(index, "ViewData", SymbolReferenceKind.Use).FromContainer);
    }

    [Fact]
    public void Razor_component_tags_and_directives_are_references()
    {
        const string Source = """
            @inherits LayoutComponentBase
            @inject IOrderService Orders
            <OrderList Items="@items" />
            <Shared.Spinner />
            <div>@title</div>
            """;
        var symbols = SymbolFixture.Extract("src/Page.razor", Source);

        SymbolFixture.Reference(symbols, "LayoutComponentBase", SymbolReferenceKind.Inheritance);
        SymbolFixture.Reference(symbols, "IOrderService", SymbolReferenceKind.TypeUse);
        SymbolFixture.Reference(symbols, "OrderList", SymbolReferenceKind.TypeUse);
        SymbolFixture.Reference(symbols, "Spinner", SymbolReferenceKind.TypeUse, "Shared");
        SymbolFixture.Reference(symbols, "items", SymbolReferenceKind.Use);
        SymbolFixture.Reference(symbols, "title", SymbolReferenceKind.Use);
    }
}

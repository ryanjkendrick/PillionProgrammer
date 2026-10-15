using Pillion.ContextEngine.Symbols;

namespace Pillion.ContextEngine.Tests.Symbols;

public sealed class SymbolGraphTests
{
    private static SymbolGraph Graph(params (string Path, string Text)[] files) => Graph(SymbolGraphOptions.Default, files);

    private static SymbolGraph Graph(SymbolGraphOptions options, params (string Path, string Text)[] files)
    {
        var builder = new SymbolGraphBuilder(options);
        foreach (var (path, text) in files)
        {
            builder.AddFile(SymbolFixture.Extract(path, text));
        }

        return builder.Build();
    }

    private static SymbolDefinition Single(SymbolGraph graph, string name, string path) =>
        Assert.Single(graph.FindDefinitions(name), d => d.FilePath == path);

    [Fact]
    public void Same_name_in_two_files_prefers_the_same_partition_and_marks_the_rest_ambiguous()
    {
        var graph = Graph(
            ("a/Order.cs", "class Order { }"),
            ("b/Order.cs", "class Order { }"),
            ("a/Service.cs", "class Service { Order Make() => new Order(); }"),
            ("c/Report.cs", "class Report { Order Last() => null; }"));

        var inA = Single(graph, "Order", "a/Order.cs");
        var inB = Single(graph, "Order", "b/Order.cs");
        var fromService = graph.Edges.Where(e => e.FilePath == "a/Service.cs").ToList();
        Assert.Equal(2, fromService.Count); // the return type and the creation
        Assert.All(fromService, e => Assert.Equal((inA.Id, 1, false), (e.ToSymbolId, e.CandidateCount, e.IsAmbiguous)));

        var fromReport = graph.Edges.Where(e => e.FilePath == "c/Report.cs").ToList();
        Assert.Equal(new[] { inA.Id, inB.Id }.Order(), fromReport.Select(e => e.ToSymbolId).Order());
        Assert.All(fromReport, e => Assert.True(e.IsAmbiguous && e.CandidateCount == 2));
        Assert.Equal(1, graph.Statistics.Ambiguous);
    }

    [Fact]
    public void Same_file_candidates_win_over_other_files()
    {
        var graph = Graph(
            ("src/A.cs", "class A { void Save() { } void Run() { Save(); } }"),
            ("src/B.cs", "class B { void Save() { } }"));

        var call = Assert.Single(graph.Edges, e => e.Kind == SymbolReferenceKind.Call);
        Assert.Equal(Single(graph, "Save", "src/A.cs").Id, call.ToSymbolId);
        Assert.Equal(Single(graph, "Run", "src/A.cs").Id, call.FromSymbolId);
    }

    [Fact]
    public void A_qualifier_selects_the_definition_in_the_named_container()
    {
        var graph = Graph(
            ("src/Billing.cs", "static class Billing { public static void Charge() { } }"),
            ("src/Shipping.cs", "static class Shipping { public static void Charge() { } }"),
            ("src/Checkout.cs", "class Checkout { void Pay() { Billing.Charge(); Shipping.Charge(); Unknown.Charge(); } }"));

        var calls = graph.Edges.Where(e => e is { FilePath: "src/Checkout.cs", Kind: SymbolReferenceKind.Call }).ToList();
        var billing = Single(graph, "Charge", "src/Billing.cs").Id;
        var shipping = Single(graph, "Charge", "src/Shipping.cs").Id;

        // Billing.Charge and Shipping.Charge resolve exactly; Unknown.Charge falls back to both, marked ambiguous.
        Assert.Single(calls, e => e.ToSymbolId == billing && !e.IsAmbiguous);
        Assert.Single(calls, e => e.ToSymbolId == shipping && !e.IsAmbiguous);
        Assert.Equal(2, calls.Count(e => e.IsAmbiguous));
    }

    [Fact]
    public void A_qualifier_naming_a_module_file_selects_its_definitions()
    {
        var graph = Graph(
            ("pkg/billing.py", "def charge():\n    pass\n"),
            ("pkg/shipping.py", "def charge():\n    pass\n"),
            ("app/main.py", "import billing\nbilling.charge()\n"));

        var call = Assert.Single(graph.Edges, e => e.Kind == SymbolReferenceKind.Call);
        Assert.Equal(Single(graph, "charge", "pkg/billing.py").Id, call.ToSymbolId);
        var import = Assert.Single(graph.Edges, e => e.Kind == SymbolReferenceKind.Import);
        Assert.Equal(SymbolKind.Module, graph.GetDefinition(import.ToSymbolId)!.Kind);
    }

    [Fact]
    public void Too_many_candidates_give_no_edges_and_names_outside_the_repository_are_unresolved()
    {
        var files = Enumerable.Range(0, 6).Select(i => ($"p{i}/Job.cs", $"class Job{i} {{ public void Run() {{ }} }}")).ToList();
        files.Add(("main/Program.cs", "class Program { void Main() { Run(); System.Console.WriteLine(); } }"));

        var graph = Graph([.. files]);

        Assert.DoesNotContain(graph.Edges, e => e.FilePath == "main/Program.cs");
        Assert.Equal(1, graph.Statistics.TooAmbiguous);
        Assert.True(graph.Statistics.Unresolved >= 1);

        var lenient = Graph(new SymbolGraphOptions { MaxCandidates = 6 }, [.. files]);
        Assert.Equal(6, lenient.Edges.Count(e => e.FilePath == "main/Program.cs"));
    }

    [Fact]
    public void Kinds_must_fit_constructors_are_not_candidates_and_bare_uses_stay_in_their_file()
    {
        var graph = Graph(
            ("src/Order.cs", "class Order { public Order() { } public int Total; }"),
            ("src/Factory.cs", "class Factory { void Order() { } object Make() { var o = new Order(); return Total; } }"));

        var order = Assert.Single(graph.FindDefinitions("Order"), d => d.Kind == SymbolKind.Class);
        Assert.Contains(graph.FindDefinitions("Order"), d => d.Kind == SymbolKind.Constructor);
        var creation = Assert.Single(graph.Edges, e => e is { FilePath: "src/Factory.cs", Kind: SymbolReferenceKind.Call });
        Assert.Equal(order.Id, creation.ToSymbolId); // the class, not its constructor or the method named Order

        // "Total" without a qualifier is a bare use: only a definition in the same file could match.
        Assert.DoesNotContain(graph.Edges, e => e.FilePath == "src/Factory.cs" && e.Kind == SymbolReferenceKind.Use);
    }

    [Fact]
    public void File_level_references_have_no_source_symbol()
    {
        var graph = Graph(
            ("src/Lib.cs", "namespace Lib { class Tool { } }"),
            ("src/App.cs", "using Lib;\nnamespace App { class Program { Lib.Tool T; } }"));

        var import = Assert.Single(graph.Edges, e => e.Kind == SymbolReferenceKind.Import);
        Assert.Null(import.FromSymbolId);
        var typeUse = Assert.Single(graph.Edges, e => e.Kind == SymbolReferenceKind.TypeUse);
        Assert.Equal("T", graph.GetDefinition(typeUse.FromSymbolId!.Value)!.Name);
    }

    [Fact]
    public void Ids_are_stable_across_rebuilds_and_edits_to_other_files()
    {
        const string A = "class A { void M() { new B().N(); } }";
        const string B = "class B { public void N() { } }";
        var before = Graph(("src/A.cs", A), ("src/B.cs", B));
        var after = Graph(("src/A.cs", A), ("src/B.cs", "// a new first line\n" + B));

        var idsOfA = before.DefinitionsIn("src/A.cs").Select(d => d.Id).ToList();
        Assert.Equal(idsOfA, after.DefinitionsIn("src/A.cs").Select(d => d.Id));
        Assert.NotEqual(before.DefinitionsIn("src/B.cs").Select(d => d.Id), after.DefinitionsIn("src/B.cs").Select(d => d.Id));
        Assert.Equal(SymbolIds.Compute("src/A.cs", string.Empty, "A", SymbolKind.Class, 1), idsOfA[0]);
        Assert.All(before.Definitions, d => Assert.True(d.Id > 0));

        // The same name on two lines (overloads) gets two ids.
        var overloads = Graph(("src/O.cs", "class O {\n void F(int a) { }\n void F(string s) { }\n}"));
        Assert.Equal(2, overloads.FindDefinitions("F").Select(d => d.Id).Distinct().Count());
    }

    [Fact]
    public void Building_does_not_depend_on_the_order_files_are_added_and_files_can_be_replaced_or_removed()
    {
        var files = SyntheticRepository.Files(6, 150).Select(f => SymbolFixture.Extract(f.Path, f.Text)).ToList();
        var forward = new SymbolGraphBuilder();
        var backward = new SymbolGraphBuilder();
        files.ForEach(forward.AddFile);
        Enumerable.Reverse(files).ToList().ForEach(backward.AddFile);

        var one = forward.Build();
        var two = backward.Build();
        Assert.Equal(one.Definitions.Select(d => d.Id), two.Definitions.Select(d => d.Id));
        Assert.Equal(one.Edges, two.Edges);
        Assert.True(one.Edges.Count > 0);

        forward.AddFile(SymbolFixture.Extract(files[1].FilePath, "class Replaced { }"));
        Assert.True(forward.RemoveFile(files[2].FilePath));
        var changed = forward.Build();
        Assert.Equal("Replaced", Assert.Single(changed.DefinitionsIn(files[1].FilePath)).Name);
        Assert.Empty(changed.DefinitionsIn(files[2].FilePath));
        Assert.Equal(5, forward.FileCount);
    }

    [Fact]
    public void Query_helpers_find_definitions_references_callers_and_parents()
    {
        var graph = Graph(
            ("src/Greeter.cs", "namespace App { class Greeter { public string Greet(string n) => n; public void All() { Greet(\"a\"); Greet(\"b\"); } } }"),
            ("src/Program.cs", "namespace App { class Program { void Main() { new Greeter().Greet(\"x\"); } } }"));

        var greet = Assert.Single(graph.FindDefinitions("Greet"));
        Assert.Equal(greet, Assert.Single(graph.FindDefinitions("Greeter.Greet")));
        Assert.Equal(SymbolKind.Class, Assert.Single(graph.FindDefinitions("App.Greeter")).Kind);
        Assert.Empty(graph.FindDefinitions("Nope"));
        Assert.Empty(graph.FindDefinitions("Other.Greet"));

        var references = graph.FindReferences(greet.Id);
        Assert.Equal(3, references.Count);
        Assert.Equal(["src/Greeter.cs", "src/Greeter.cs", "src/Program.cs"], references.Select(r => r.FilePath));

        Assert.Equal(["All", "Main"], graph.CallersOf(greet.Id).Select(d => d.Name));
        var main = Assert.Single(graph.FindDefinitions("Main"));
        Assert.Contains(graph.ReferencesFrom(main.Id), e => e.ToSymbolId == greet.Id);
        Assert.Equal("Greeter", graph.ParentOf(greet.Id)!.Name);
        Assert.Equal("App", graph.ParentOf(graph.ParentOf(greet.Id)!.Id)!.Name);
        // Source order; members on the same line are ordered by name.
        Assert.Equal(["App", "Greeter", "All", "Greet"], graph.DefinitionsIn("src/Greeter.cs").Select(d => d.Name));
        Assert.Equal("Greeter", graph.ParentOf(Assert.Single(graph.FindDefinitions("All")).Id)!.Name);
        Assert.Empty(graph.CallersOf(main.Id));
        Assert.Null(graph.GetDefinition(12345));
    }

    [Fact]
    public void Loose_definitions_and_references_build_the_same_graph()
    {
        var extractions = new[]
        {
            SymbolFixture.Extract("src/A.cs", "class A { void M() { new B().N(); } }"),
            SymbolFixture.Extract("src/B.cs", "class B { public void N() { } }"),
        };
        var builder = new SymbolGraphBuilder();
        foreach (var extraction in extractions)
        {
            builder.AddFile(extraction);
        }

        var loose = SymbolGraphBuilder.Build(extractions.SelectMany(e => e.Definitions), extractions.SelectMany(e => e.References));

        Assert.Equal(builder.Build().Edges, loose.Edges);
    }
}

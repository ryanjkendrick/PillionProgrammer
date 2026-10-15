using Pillion.ContextEngine.Chunking.TreeSitter;
using Pillion.ContextEngine.Symbols;
using Pillion.ContextEngine.Symbols.TreeSitter;

namespace Pillion.ContextEngine.Tests.Symbols;

public sealed class TreeSitterSymbolTests
{
    [Fact]
    public void Every_grammar_description_loads_on_this_runtime()
    {
        foreach (var grammar in TreeSitterSymbolGrammars.All)
        {
            Assert.True(new TreeSitterSymbolExtractor(grammar).IsAvailable, grammar.Grammar.GrammarId);
        }
    }

    [Fact]
    public void TypeScript_fixture()
    {
        var symbols = SymbolFixture.Extract("typescript/shapes.ts");

        SymbolFixture.Definition(symbols, "shapes", SymbolKind.Module);
        SymbolFixture.Definition(symbols, "Shape", SymbolKind.Interface);
        SymbolFixture.Definition(symbols, "Shape > area", SymbolKind.Method);
        SymbolFixture.Definition(symbols, "Shape > name", SymbolKind.Property);
        var circle = SymbolFixture.Definition(symbols, "Circle", SymbolKind.Class);
        Assert.Equal((9, 18), (circle.StartLine, circle.EndLine));
        Assert.Equal("class Circle implements Shape", circle.Signature);
        SymbolFixture.Definition(symbols, "Circle > constructor", SymbolKind.Constructor);
        Assert.Equal("area(): number", SymbolFixture.Definition(symbols, "Circle > area", SymbolKind.Method).Signature);
        SymbolFixture.Definition(symbols, "Circle > name", SymbolKind.Field);
        SymbolFixture.Definition(symbols, "Colour", SymbolKind.Enum);
        SymbolFixture.Definition(symbols, "ShapeList", SymbolKind.TypeAlias);
        Assert.Equal("const describe = (shape: Shape): string", SymbolFixture.Definition(symbols, "describe", SymbolKind.Function).Signature);
        SymbolFixture.Definition(symbols, "Geometry", SymbolKind.Namespace);
        SymbolFixture.Definition(symbols, "Geometry > unit", SymbolKind.Function);
        SymbolFixture.Definition(symbols, "logger", SymbolKind.Variable);

        SymbolFixture.Reference(symbols, "Logger", SymbolReferenceKind.Import);
        SymbolFixture.Reference(symbols, "logger", SymbolReferenceKind.Import);
        Assert.Equal("Circle", SymbolFixture.Reference(symbols, "Shape", SymbolReferenceKind.Inheritance).FromContainer);
        SymbolFixture.Reference(symbols, "area", SymbolReferenceKind.Call, "shape");
        Assert.Equal("Geometry > unit", SymbolFixture.Reference(symbols, "Circle", SymbolReferenceKind.Call).FromContainer);
        SymbolFixture.Reference(symbols, "Circle", SymbolReferenceKind.TypeUse);
        SymbolFixture.Reference(symbols, "info", SymbolReferenceKind.Call, "logger");
        SymbolFixture.NoReference(symbols, "Circle", SymbolReferenceKind.Inheritance);
    }

    [Fact]
    public void TypeScript_inheritance_imports_generics_and_type_parameters()
    {
        const string Source = """
            import * as path from "node:path";
            import Default, { A as B } from "../lib/things.js";
            export class Square extends Base implements Shape, Named {}
            interface Fancy extends Plain {}
            function wrap<T>(value: T, map: Map<string, Order>): Box<T> { return new Box(value); }
            """;
        var symbols = SymbolFixture.Extract("src/square.ts", Source);

        SymbolFixture.Reference(symbols, "path", SymbolReferenceKind.Import);
        SymbolFixture.Reference(symbols, "things", SymbolReferenceKind.Import);
        SymbolFixture.Reference(symbols, "Default", SymbolReferenceKind.Import);
        SymbolFixture.Reference(symbols, "A", SymbolReferenceKind.Import);
        SymbolFixture.Reference(symbols, "Base", SymbolReferenceKind.Inheritance);
        SymbolFixture.Reference(symbols, "Shape", SymbolReferenceKind.Inheritance);
        SymbolFixture.Reference(symbols, "Named", SymbolReferenceKind.Inheritance);
        SymbolFixture.Reference(symbols, "Plain", SymbolReferenceKind.Inheritance);
        SymbolFixture.Reference(symbols, "Map", SymbolReferenceKind.TypeUse);
        SymbolFixture.Reference(symbols, "Order", SymbolReferenceKind.TypeUse);
        SymbolFixture.Reference(symbols, "Box", SymbolReferenceKind.Call);

        // <T> declares T; the two uses of T are type uses.
        Assert.Equal(2, symbols.References.Count(r => r is { Name: "T", Kind: SymbolReferenceKind.TypeUse }));
    }

    [Fact]
    public void JavaScript_fixture_and_require()
    {
        var symbols = SymbolFixture.Extract("javascript/app.js");

        SymbolFixture.Definition(symbols, "buildRouter", SymbolKind.Function);
        SymbolFixture.Definition(symbols, "Store", SymbolKind.Class);
        SymbolFixture.Definition(symbols, "Store > add", SymbolKind.Method);
        SymbolFixture.Definition(symbols, "Store > size", SymbolKind.Method);
        SymbolFixture.Definition(symbols, "Store > items", SymbolKind.Field);
        SymbolFixture.Definition(symbols, "handler", SymbolKind.Function);
        SymbolFixture.Definition(symbols, "express", SymbolKind.Variable);

        SymbolFixture.Reference(symbols, "express", SymbolReferenceKind.Import);
        Assert.Equal("Store > add", SymbolFixture.Reference(symbols, "push", SymbolReferenceKind.Call, "items").FromContainer);
        SymbolFixture.Reference(symbols, "stringify", SymbolReferenceKind.Call, "JSON");

        var extends = SymbolFixture.Extract("src/b.js", "const util = require('./util/index.js');\nclass B extends A { m() { return new C(); } }\n");
        SymbolFixture.Reference(extends, "index", SymbolReferenceKind.Import);
        SymbolFixture.Reference(extends, "A", SymbolReferenceKind.Inheritance);
        SymbolFixture.Reference(extends, "C", SymbolReferenceKind.Call);
    }

    [Fact]
    public void Python_fixture()
    {
        var symbols = SymbolFixture.Extract("python/inventory.py");

        SymbolFixture.Definition(symbols, "inventory", SymbolKind.Module);
        var item = SymbolFixture.Definition(symbols, "Item", SymbolKind.Class);
        Assert.Equal((7, 20), (item.StartLine, item.EndLine));
        SymbolFixture.Definition(symbols, "Item > name", SymbolKind.Field);
        SymbolFixture.Definition(symbols, "Item > quantity", SymbolKind.Field);
        Assert.Equal("def restock(self, amount)", SymbolFixture.Definition(symbols, "Item > restock", SymbolKind.Method).Signature);
        SymbolFixture.Definition(symbols, "Item > empty", SymbolKind.Method);
        Assert.Equal("def load(path)", SymbolFixture.Definition(symbols, "load", SymbolKind.Function).Signature);

        SymbolFixture.Reference(symbols, "os", SymbolReferenceKind.Import);
        SymbolFixture.Reference(symbols, "dataclass", SymbolReferenceKind.Import);
        SymbolFixture.Reference(symbols, "dataclass", SymbolReferenceKind.Call);
        Assert.Equal("load", SymbolFixture.Reference(symbols, "Item", SymbolReferenceKind.Call).FromContainer);
        SymbolFixture.Reference(symbols, "int", SymbolReferenceKind.TypeUse);
        Assert.Equal(string.Empty, SymbolFixture.Reference(symbols, "load", SymbolReferenceKind.Call).FromContainer);
        Assert.DoesNotContain(symbols.Definitions, d => d.Name is "handle" or "line" or "amount");
    }

    [Fact]
    public void Python_superclasses_imports_constructors_and_annotations()
    {
        const string Source = """
            from app.models import Order
            import billing.gateway as gw

            RETRIES = 3

            class Admin(User, mixins.Audit):
                def __init__(self, order: Order) -> None:
                    self.order = order

                def charge(self) -> Receipt:
                    total = 1
                    return gw.charge(self.order)
            """;
        var symbols = SymbolFixture.Extract("src/admin.py", Source);

        SymbolFixture.Definition(symbols, "RETRIES", SymbolKind.Variable);
        SymbolFixture.Definition(symbols, "Admin > __init__", SymbolKind.Constructor);
        SymbolFixture.Definition(symbols, "Admin > charge", SymbolKind.Method);
        SymbolFixture.Reference(symbols, "models", SymbolReferenceKind.Import, "app");
        SymbolFixture.Reference(symbols, "Order", SymbolReferenceKind.Import);
        SymbolFixture.Reference(symbols, "gateway", SymbolReferenceKind.Import, "billing");
        SymbolFixture.Reference(symbols, "User", SymbolReferenceKind.Inheritance);
        SymbolFixture.Reference(symbols, "Audit", SymbolReferenceKind.Inheritance, "mixins");
        SymbolFixture.Reference(symbols, "Order", SymbolReferenceKind.TypeUse);
        SymbolFixture.Reference(symbols, "Receipt", SymbolReferenceKind.TypeUse);
        SymbolFixture.Reference(symbols, "charge", SymbolReferenceKind.Call, "gw");
        Assert.DoesNotContain(symbols.Definitions, d => d.Name is "total" or "order");
    }

    [Fact]
    public void C_fixture_and_prototypes_macros_enums()
    {
        var symbols = SymbolFixture.Extract("c/list.c");

        SymbolFixture.Definition(symbols, "list.c", SymbolKind.Module);
        SymbolFixture.Definition(symbols, "node", SymbolKind.Struct);
        SymbolFixture.Definition(symbols, "node > next", SymbolKind.Field);
        SymbolFixture.Definition(symbols, "list_t", SymbolKind.TypeAlias);
        SymbolFixture.Definition(symbols, "count", SymbolKind.Variable);
        Assert.Equal("int list_push(list_t *list, int value)", SymbolFixture.Definition(symbols, "list_push", SymbolKind.Function).Signature);
        SymbolFixture.Definition(symbols, "list_free", SymbolKind.Function);
        Assert.DoesNotContain(symbols.Definitions, d => d.Name is "head" or "length" or "n"); // anonymous struct fields, locals

        SymbolFixture.Reference(symbols, "list.h", SymbolReferenceKind.Import);
        SymbolFixture.Reference(symbols, "stdlib.h", SymbolReferenceKind.Import);
        Assert.Equal("list_push", SymbolFixture.Reference(symbols, "malloc", SymbolReferenceKind.Call).FromContainer);
        SymbolFixture.Reference(symbols, "list_t", SymbolReferenceKind.TypeUse);
        SymbolFixture.Reference(symbols, "node", SymbolReferenceKind.TypeUse);

        var header = SymbolFixture.Extract("src/list.h", "int list_push(list_t *list, int value);\n#define MAX(a, b) ((a) > (b) ? (a) : (b))\nenum colour { RED, GREEN };\n");
        SymbolFixture.Definition(header, "list_push", SymbolKind.Function);
        SymbolFixture.Definition(header, "MAX", SymbolKind.Macro);
        SymbolFixture.Definition(header, "colour", SymbolKind.Enum);
        SymbolFixture.Definition(header, "colour > GREEN", SymbolKind.EnumMember);
    }

    [Fact]
    public void Cpp_fixture_and_base_classes()
    {
        var symbols = SymbolFixture.Extract("cpp/matrix.hpp");

        SymbolFixture.Definition(symbols, "linalg", SymbolKind.Namespace);
        SymbolFixture.Definition(symbols, "linalg > Matrix", SymbolKind.Class);
        SymbolFixture.Definition(symbols, "linalg > Matrix > Matrix", SymbolKind.Constructor);
        SymbolFixture.Definition(symbols, "linalg > Matrix > at", SymbolKind.Method);
        Assert.Equal(2, symbols.Definitions.Count(d => d.QualifiedName == "linalg > Matrix > rows" && d.Kind == SymbolKind.Method));
        SymbolFixture.Definition(symbols, "linalg > Matrix > data_", SymbolKind.Field);
        SymbolFixture.Reference(symbols, "vector", SymbolReferenceKind.Import);
        SymbolFixture.Reference(symbols, "vector", SymbolReferenceKind.TypeUse);
        Assert.DoesNotContain(symbols.References, r => r.Line == 9); // template <typename T> declares T

        var derived = SymbolFixture.Extract("src/widget.cpp", "class Button : public Widget, private Clickable {\n  void Draw() { auto w = new Label(); std::sort(a, b); }\n};\n");
        SymbolFixture.Reference(derived, "Widget", SymbolReferenceKind.Inheritance);
        SymbolFixture.Reference(derived, "Clickable", SymbolReferenceKind.Inheritance);
        SymbolFixture.Reference(derived, "Label", SymbolReferenceKind.Call);
        SymbolFixture.Reference(derived, "sort", SymbolReferenceKind.Call, "std");
        SymbolFixture.Definition(derived, "Button > Draw", SymbolKind.Method);
    }

    [Fact]
    public void Go_fixture_and_interfaces()
    {
        var symbols = SymbolFixture.Extract("go/server.go");

        SymbolFixture.Definition(symbols, "server", SymbolKind.Namespace);
        SymbolFixture.Definition(symbols, "server > Server", SymbolKind.Struct);
        SymbolFixture.Definition(symbols, "server > Server > addr", SymbolKind.Field);
        SymbolFixture.Definition(symbols, "server > defaultAddr", SymbolKind.Constant);
        Assert.Equal("func New(addr string) *Server", SymbolFixture.Definition(symbols, "server > New", SymbolKind.Function).Signature);
        SymbolFixture.Definition(symbols, "server > Server > Start", SymbolKind.Method);

        SymbolFixture.Reference(symbols, "http", SymbolReferenceKind.Import);
        SymbolFixture.Reference(symbols, "Server", SymbolReferenceKind.TypeUse);
        Assert.Equal("server > Server > Start", SymbolFixture.Reference(symbols, "ListenAndServe", SymbolReferenceKind.Call, "http").FromContainer);

        var iface = SymbolFixture.Extract("src/store.go", "package store\n\ntype Store interface {\n\tGet(id ID) (*Item, error)\n}\n\ntype ID = string\n");
        SymbolFixture.Definition(iface, "store > Store", SymbolKind.Interface);
        SymbolFixture.Definition(iface, "store > Store > Get", SymbolKind.Method);
        SymbolFixture.Definition(iface, "store > ID", SymbolKind.TypeAlias);
        SymbolFixture.Reference(iface, "Item", SymbolReferenceKind.TypeUse);
    }

    [Fact]
    public void Rust_fixture_traits_and_use_lists()
    {
        var symbols = SymbolFixture.Extract("rust/geometry.rs");

        SymbolFixture.Definition(symbols, "Point", SymbolKind.Struct);
        SymbolFixture.Definition(symbols, "Point > x", SymbolKind.Field);
        SymbolFixture.Definition(symbols, "Point > origin", SymbolKind.Method);
        Assert.Equal("pub fn distance(&self, other: &Point) -> f64", SymbolFixture.Definition(symbols, "Point > distance", SymbolKind.Method).Signature);
        SymbolFixture.Definition(symbols, "Point > fmt", SymbolKind.Method);
        SymbolFixture.Definition(symbols, "shapes", SymbolKind.Module);
        SymbolFixture.Definition(symbols, "shapes > unit", SymbolKind.Function);

        SymbolFixture.Reference(symbols, "fmt", SymbolReferenceKind.Import, "std");
        Assert.Equal("Point", SymbolFixture.Reference(symbols, "Display", SymbolReferenceKind.Inheritance, "fmt").FromContainer);
        SymbolFixture.Reference(symbols, "Formatter", SymbolReferenceKind.TypeUse, "fmt");
        SymbolFixture.Reference(symbols, "write", SymbolReferenceKind.Call);
        SymbolFixture.Reference(symbols, "powi", SymbolReferenceKind.Call);
        Assert.DoesNotContain(symbols.References, r => r.Name == "Self" || r.Qualifier == "Self");

        var traits = SymbolFixture.Extract("src/shape.rs", "use crate::geometry::{Point, Line};\ntrait Shape { fn area(&self) -> f64; }\nimpl Shape for Circle { fn area(&self) -> f64 { Point::new(); 1.0 } }\n");
        SymbolFixture.Reference(traits, "Point", SymbolReferenceKind.Import);
        SymbolFixture.Reference(traits, "Line", SymbolReferenceKind.Import);
        SymbolFixture.Definition(traits, "Shape", SymbolKind.Interface);
        SymbolFixture.Definition(traits, "Shape > area", SymbolKind.Method);
        SymbolFixture.Definition(traits, "Circle > area", SymbolKind.Method);
        SymbolFixture.Reference(traits, "Shape", SymbolReferenceKind.Inheritance);
        SymbolFixture.Reference(traits, "new", SymbolReferenceKind.Call, "Point");
    }

    [Fact]
    public void Java_fixture_inheritance_and_enums()
    {
        var symbols = SymbolFixture.Extract("java/OrderService.java");
        const string Type = "com.example.orders > OrderService";

        SymbolFixture.Definition(symbols, "com.example.orders", SymbolKind.Namespace);
        Assert.Equal("public class OrderService", SymbolFixture.Definition(symbols, Type, SymbolKind.Class).Signature);
        Assert.Equal("public void place(String order)", SymbolFixture.Definition(symbols, Type + " > place", SymbolKind.Method).Signature);
        SymbolFixture.Definition(symbols, Type + " > OrderService", SymbolKind.Constructor);
        SymbolFixture.Definition(symbols, Type + " > orders", SymbolKind.Field);
        SymbolFixture.Definition(symbols, Type + " > created", SymbolKind.Field);

        SymbolFixture.Reference(symbols, "List", SymbolReferenceKind.Import, "util");
        SymbolFixture.Reference(symbols, "Service", SymbolReferenceKind.TypeUse);
        SymbolFixture.Reference(symbols, "Transactional", SymbolReferenceKind.TypeUse);
        Assert.Equal(Type + " > orders", SymbolFixture.Reference(symbols, "ArrayList", SymbolReferenceKind.Call).FromContainer);
        SymbolFixture.NoReference(symbols, "ArrayList", SymbolReferenceKind.TypeUse);
        Assert.Equal(Type + " > place", SymbolFixture.Reference(symbols, "add", SymbolReferenceKind.Call, "orders").FromContainer);

        var derived = SymbolFixture.Extract("src/Admin.java", "class Admin<T> extends User implements Auditable, Named { }\ninterface Named extends Base { }\nenum Level { LOW, HIGH; int weight() { return 1; } }\n");
        SymbolFixture.Reference(derived, "User", SymbolReferenceKind.Inheritance);
        SymbolFixture.Reference(derived, "Auditable", SymbolReferenceKind.Inheritance);
        SymbolFixture.Reference(derived, "Base", SymbolReferenceKind.Inheritance);
        SymbolFixture.Definition(derived, "Level > HIGH", SymbolKind.EnumMember);
        SymbolFixture.Definition(derived, "Level > weight", SymbolKind.Method);
        SymbolFixture.NoReference(derived, "T", SymbolReferenceKind.TypeUse);
    }

    [Fact]
    public void A_grammar_that_does_not_exist_reports_a_failure()
    {
        var missing = TreeSitterSymbolGrammars.Python with { Grammar = TreeSitterGrammars.Python with { Language = "nope", GrammarId = "NoSuchGrammar" } };
        var failures = new List<SymbolExtractionFailure>();
        var extractor = new TreeSitterSymbolExtractor(missing);

        var symbols = extractor.Extract("a.nope", "def f(): pass\n", new SymbolExtractionOptions { OnFailure = failures.Add });

        Assert.False(extractor.IsAvailable);
        Assert.False(symbols.IsComplete);
        Assert.Empty(symbols.Definitions);
        Assert.Contains("NoSuchGrammar", Assert.Single(failures).Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void Text_that_is_not_the_language_reports_a_failure()
    {
        var text = string.Join("\n", Enumerable.Range(0, 120).Select(i => $"}}}}}}}}}} ))) ]]] <<< @@@ {i} ''' \"\"\" ```"));
        var failures = new List<SymbolExtractionFailure>();

        var symbols = new FileSymbolExtractor().Extract("junk.py", text, new SymbolExtractionOptions { OnFailure = failures.Add });

        Assert.False(symbols.IsComplete);
        Assert.Single(failures);
    }
}

using Pillion.ContextEngine.Chunking.TreeSitter;
using Xunit.Abstractions;

namespace Pillion.ContextEngine.Tests;

public sealed class SmokeTests(ITestOutputHelper output)
{
    [Theory]
    [InlineData("JavaScript", "export function f(a) { return a; }\nconst g = () => 1;")]
    [InlineData("TypeScript", "namespace N { export class A<T> { @dec m(): void {} x: number = 1; } }\nexport default interface I { a: string }\nenum E { A, B }\ntype T = string;\ndeclare module 'm' { }")]
    [InlineData("TSX", "const a = <div>{x}</div>;")]
    [InlineData("Python", "import os\n@dec\nclass A(B):\n    \"\"\"doc\"\"\"\n    x = 1\n    def f(self):\n        pass\nif __name__ == '__main__':\n    f()")]
    [InlineData("C", "#ifndef H\n#define H\n#include <a.h>\nstruct s { int a; };\ntypedef struct { int b; } T;\nint f(int);\nstatic int g(void) { return 0; }\nenum e { A };\n#endif")]
    [InlineData("C++", "namespace a::b { template<typename T> class C : public D { public: void m() {} int x; private: void n(); };\nstruct S { int a; };\n}\nvoid C::n() {}\nextern \"C\" { int q(); }")]
    [InlineData("Go", "package main\nimport \"fmt\"\ntype S struct { a int }\nfunc (s *S) M() {}\nfunc main() { fmt.Println() }\nconst x = 1")]
    [InlineData("Rust", "use a::b;\n#[derive(Debug)]\nstruct S { a: i32 }\nimpl T for S { fn m(&self) {} }\nmod m { fn f() {} }\ntrait T { fn m(&self); }\nenum E { A }")]
    [InlineData("Java", "package a.b;\nimport c.D;\n/** doc */\n@Ann public class A extends B { int x; public A() {} void m() { } enum E { X; void f() {} } interface I { void g(); } }\nrecord R(int a) {}")]
    [InlineData("HTML", "<!DOCTYPE html><html><head><title>t</title><style>a{}</style></head><body><div id=\"main\" class=\"c\"><p>x</p></div><script>f()</script></body></html>")]
    [InlineData("CSS", "@import 'a.css';\na, b > c { color: red; }\n@media (max-width: 600px) { .x { color: blue; } }\n@keyframes k { from { a: b } }")]
    public void Parses(string id, string code)
    {
        var tree = Smoke.Parse(id, code);
        output.WriteLine(tree);
        Assert.NotEmpty(tree);
    }
}

using Pillion.Contracts;

namespace Pillion.ModelGateway.Tests.Autocomplete;

/// <summary>One fixed autocomplete situation of the prompt-quality measurement.</summary>
/// <param name="Name">A short name for the output.</param>
/// <param name="LanguageId">The language id.</param>
/// <param name="FilePath">The document's full path (the extension sends the full path).</param>
/// <param name="Prefix">The text before the caret.</param>
/// <param name="Suffix">The text after the caret.</param>
/// <param name="MultiLine">Whether the extension would ask for a multi-line suggestion here.</param>
/// <param name="Declaration">The enclosing declaration the extension would send, or <see langword="null"/>.</param>
/// <param name="Closing">For a caret at the end of the document (empty suffix): what the user would type after the suggestion, appended only to check that the document parses.</param>
public sealed record CompletionQualityCase(string Name, string LanguageId, string FilePath, string Prefix, string Suffix, bool MultiLine, string? Declaration = null, string Closing = "")
{
    /// <summary>Builds the request the extension would send, with open files and recent edits elsewhere (full paths, as the extension sends them).</summary>
    /// <param name="id">The request id.</param>
    /// <returns>The request.</returns>
    public CompletionRequest ToRequest(string id)
    {
        var folder = Path.GetDirectoryName(FilePath)!;
        var extension = Path.GetExtension(FilePath);
        return new CompletionRequest
        {
            RequestId = id,
            Prefix = Prefix,
            Suffix = Suffix,
            LanguageId = LanguageId,
            FilePath = FilePath,
            CaretLine = Prefix.Count(c => c == '\n'),
            EnclosingDeclaration = Declaration,
            OpenDocuments = [FilePath, Path.Combine(folder, "Models" + extension), Path.Combine(folder, "Helpers" + extension), Path.Combine(folder, "Program" + extension)],
            RecentEdits =
            [
                Path.Combine(folder, "Models" + extension) + ":14: " + (LanguageId == "python" ? "    price: float = 0.0" : "public decimal Price { get; set; }"),
                Path.Combine(folder, "Helpers" + extension) + ":31: }",
                Path.Combine(folder, "Program" + extension) + ":8: " + (LanguageId == "python" ? "main()" : "Run(args);"),
            ],
            AllowMultiLine = MultiLine,
            MaxTokens = MultiLine ? 256 : 64,
            TaskClass = TaskClass.Autocomplete,
        };
    }
}

/// <summary>
/// The fixed set of 20 prefixes (8 C#, 6 TypeScript, 6 Python) the autocomplete prompt policy was measured on (Gate 1
/// open issues 1 and 2). Several start near the top of a file, where the header outweighs the code, and eight have the
/// caret at the end of the document (an empty suffix), as when a new file is typed with brace completion off: that is
/// the Gate 1 typing scenario, where the 1.5B model answered in prose and the 7B opened with a fence. Every case's
/// document parses once the hole is filled with a reasonable completion (and, at the end of a document, closed).
/// </summary>
public static class CompletionQualityCases
{
    private const string Cs = @"C:\Users\dev\source\repos\Shop\Orders\";
    private const string Ts = @"C:\Users\dev\source\repos\web\src\";
    private const string Py = @"C:\Users\dev\source\repos\ledger\ledger\";

    /// <summary>Gets the cases.</summary>
    public static IReadOnlyList<CompletionQualityCase> All { get; } =
    [
        new("cs-class-start", "csharp", Cs + "OrderService.cs",
            "using System;\nusing System.Collections.Generic;\n\nnamespace Shop.Orders;\n\npublic sealed class OrderService\n{\n    private readonly List<Order> _orders = new();\n\n    public void Add(Order order)\n    {\n        ",
            "\n    }\n}\n", MultiLine: false, "public void Add(Order order)"),
        new("cs-sum", "csharp", Cs + "Cart.cs",
            "namespace Shop.Orders;\n\npublic sealed class Cart\n{\n    private readonly List<Item> _items = new();\n\n    public decimal Total()\n    {\n        var total = ",
            "\n        return total;\n    }\n}\n", MultiLine: false, "public decimal Total()"),
        new("cs-property", "csharp", Cs + "Customer.cs",
            "namespace Shop.Orders;\n\npublic sealed class Customer\n{\n    public int Id { get; init; }\n\n    public string Name { get; ",
            "\n}\n", MultiLine: false, "public sealed class Customer"),
        new("cs-max-body", "csharp", Cs + "MathUtil.cs",
            "namespace Shop.Orders;\n\npublic static class MathUtil\n{\n    public static int Max(int[] values)\n    {\n",
            "    }\n}\n", MultiLine: true, "public static int Max(int[] values)"),
        new("cs-linq", "csharp", Cs + "People.cs",
            "using System.Linq;\n\nnamespace Shop.Orders;\n\npublic static class People\n{\n    public static IEnumerable<Person> Adults(IEnumerable<Person> people)\n    {\n        return people.Where(p => ",
            ");\n    }\n}\n\npublic sealed record Person(string Name, int Age);\n", MultiLine: false, "public static IEnumerable<Person> Adults(IEnumerable<Person> people)"),
        new("cs-guard", "csharp", Cs + "Checkout.cs",
            "namespace Shop.Orders;\n\npublic sealed class Checkout\n{\n    public void Place(Order order)\n    {\n        if (order is null)\n        {\n            throw new ",
            "", MultiLine: false, "public void Place(Order order)", Closing: "\n        }\n    }\n}\n"),
        new("cs-typefast", "csharp", Cs + "TypeFast.cs",
            "namespace Gate;\n\npublic static class TypeFast\n{\n    public static int Twice(int value)\n    {\n        return ",
            "", MultiLine: false, "public static int Twice(int value)", Closing: "\n    }\n}\n"),
        new("cs-foreach", "csharp", Cs + "Report.cs",
            "namespace Shop.Orders;\n\npublic static class Report\n{\n    public static void Print(IReadOnlyList<Order> orders)\n    {\n        foreach (var order in orders)\n        {\n",
            "        }\n    }\n}\n", MultiLine: true, "public static void Print(IReadOnlyList<Order> orders)"),

        new("ts-load-config", "typescript", Ts + "config.ts",
            "import { readFile } from 'node:fs/promises';\n\nexport interface Config {\n  port: number;\n}\n\nexport async function loadConfig(path: string): Promise<Config> {\n  const text = ",
            "\n  return JSON.parse(text) as Config;\n}\n", MultiLine: false, "export async function loadConfig(path: string): Promise<Config>"),
        new("ts-interface", "typescript", Ts + "user.ts",
            "export interface User {\n  id: number;\n  ",
            "", MultiLine: false, Closing: "\n}\n"),
        new("ts-arrow", "typescript", Ts + "math.ts",
            "export const sum = (values: number[]): number =>",
            "", MultiLine: false),
        new("ts-class-method", "typescript", Ts + "stack.ts",
            "export class Stack<T> {\n  private items: T[] = [];\n\n  public push(item: T): void {\n",
            "  }\n}\n", MultiLine: true, "public push(item: T): void"),
        new("ts-fetch", "typescript", Ts + "api.ts",
            "export async function getJson(url: string): Promise<unknown> {\n  const response = await fetch(url);\n  if (!response.ok) {\n    throw new ",
            "\n  }\n  return response.json();\n}\n", MultiLine: false, "export async function getJson(url: string): Promise<unknown>"),
        new("ts-map", "typescript", Ts + "names.ts",
            "interface User {\n  name: string;\n}\n\nexport function names(users: User[]): string[] {\n  return users.map((u) => ",
            ");\n}\n", MultiLine: false, "export function names(users: User[]): string[]"),

        new("py-load", "python", Py + "store.py",
            "import json\nfrom pathlib import Path\n\n\ndef load(path: Path) -> dict:\n",
            "", MultiLine: true, "def load(path: Path) -> dict:"),
        new("py-mean", "python", Py + "stats.py",
            "def mean(values):\n    if not values:\n        return 0\n    return ",
            "", MultiLine: false, "def mean(values):"),
        new("py-init", "python", Py + "account.py",
            "class Account:\n    def __init__(self, owner, balance=0):\n        self.owner = owner\n        ",
            "\n", MultiLine: false, "def __init__(self, owner, balance=0):"),
        new("py-evens", "python", Py + "numbers.py",
            "def evens(values):\n    return [",
            "", MultiLine: false, "def evens(values):"),
        new("py-with-open", "python", Py + "reader.py",
            "def read_lines(path):\n    with open(path) as f:\n        ",
            "\n", MultiLine: false, "def read_lines(path):"),
        new("py-total", "python", Py + "invoice.py",
            "from dataclasses import dataclass, field\n\n\n@dataclass\nclass Invoice:\n    lines: list = field(default_factory=list)\n\n    def total(self) -> float:\n",
            "", MultiLine: true, "def total(self) -> float:"),
    ];
}

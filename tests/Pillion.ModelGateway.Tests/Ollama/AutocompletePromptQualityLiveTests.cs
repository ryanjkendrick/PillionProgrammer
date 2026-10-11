using System.Diagnostics;
using System.Globalization;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.CodeAnalysis.CSharp;
using Pillion.ModelGateway.Tests.Autocomplete;
using Xunit.Abstractions;

namespace Pillion.ModelGateway.Tests.Ollama;

/// <summary>
/// The autocomplete prompt policy measured against the local models (Gate 1 open issues 1 and 2): the fixed 20-prefix
/// set (<see cref="CompletionQualityCases"/>) in four variants, posted to Ollama as the gateway posts them: the Gate 1
/// request (the header of sentences <c>// File:</c>, <c>// Open files:</c>, <c>// Recent edit: C:\full\path...</c>, an
/// empty suffix passed on as it was, which makes Ollama's template fall back to a chat turn, and the newline stop of a
/// single-line request sent to the provider), the terse header with the suffix still passed on empty, and the shipped
/// request with the terse header or with no header (an empty suffix sent as a line break, no newline stop at the
/// provider, the fence stop). For each it counts what the model returned (starts with prose,
/// opens with a Markdown fence, starts with code) and what the user would see after the gateway's content filter and
/// shaper and the extension's block rule (empty, and whether the document then parses: Roslyn for C#, Node's
/// TypeScript type stripping, Python's <c>ast</c>). The figures are written to the test output; run with
/// <c>dotnet test --filter "FullyQualifiedName~AutocompletePromptQualityLiveTests" --logger "console;verbosity=detailed"</c>.
/// The assertions hold the shipped policy (the catalog's <c>fimHeader</c>) to no prose and no fence in what is shown.
/// </summary>
[Trait("Category", "Live")]
public sealed class AutocompletePromptQualityLiveTests(ITestOutputHelper output)
{
    private static readonly TimeSpan Budget = TimeSpan.FromMinutes(10);

    /// <summary>The request variants measured.</summary>
    public enum HeaderVariant
    {
        /// <summary>The Gate 1 request: the header of sentences with full paths, an empty suffix passed on, the newline stop at the provider.</summary>
        Gate1Sentences,

        /// <summary>The terse header with an empty suffix still passed on as it is (isolates the suffix).</summary>
        TerseEmptySuffix,

        /// <summary>The terse comment header (<c>// file:</c>, <c>// see:</c>, <c>// in:</c>, <c>// edit:</c>).</summary>
        Terse,

        /// <summary>No header: the code alone.</summary>
        None,
    }

    [LiveOllamaModelFact(AutocompleteLiveTests.Small)]
    public Task Small_ProseAndFenceCounts() => MeasureAsync(AutocompleteLiveTests.Small);

    [LiveOllamaModelFact(AutocompleteLiveTests.Large)]
    public Task Large_ProseAndFenceCounts() => MeasureAsync(AutocompleteLiveTests.Large);

    private async Task MeasureAsync(string model)
    {
        using var http = new HttpClient { BaseAddress = new Uri(LiveOllama.Endpoint), Timeout = Budget };
        var counter = TokenCounter.Default.ForModel(model);
        var format = PromptTemplates.Default.FindFimFormat(model)!;
        var shipped = ModelCatalog.Default.GetCapabilities(model, isLocal: true).FimHeader ? HeaderVariant.Terse : HeaderVariant.None;
        await GenerateAsync(http, model, "int x = ", ";", 8, format.StopSequences); // Load the model first.

        output.WriteLine($"{model}: 20 prefixes (8 C#, 6 TypeScript, 6 Python); shipped header policy: {shipped} (catalog fimHeader).");
        output.WriteLine("variant           | raw: prose fence code | shown: empty prose fence parses/non-empty | median ms");
        var shippedShown = new List<(string Case, string Shown)>();
        foreach (var variant in Enum.GetValues<HeaderVariant>())
        {
            var rows = new List<Row>();
            foreach (var item in CompletionQualityCases.All)
            {
                var request = item.ToRequest(item.Name);
                var prompt = AutocompletePromptBuilder.Build(request, AutocompleteBudget.GpuPromptTokens, counter, explicitFormat: null, includeHeader: variant is HeaderVariant.Terse or HeaderVariant.TerseEmptySuffix);
                var promptPrefix = variant == HeaderVariant.Gate1Sentences ? Gate1Header(item, request) + prompt.Prefix : prompt.PromptPrefix;
                var stops = format.StopSequences.Concat(PromptTemplates.Default.DefaultFimStopSequences).ToList();
                List<string> providerStops = variant == HeaderVariant.Gate1Sentences
                    ? [.. item.MultiLine ? [] : new[] { "\n" }, .. stops.Where(s => s != "\n```")]
                    : stops;
                var suffix = variant is HeaderVariant.Gate1Sentences or HeaderVariant.TerseEmptySuffix ? prompt.Suffix : global::Pillion.ModelGateway.Providers.Ollama.OllamaFimClient.SuffixToSend(prompt.Suffix);
                var watch = Stopwatch.StartNew();
                var raw = await GenerateAsync(http, model, promptPrefix, suffix, item.MultiLine ? 256 : 64, providerStops);
                var elapsed = watch.Elapsed.TotalMilliseconds;
                var shown = Show(item, promptPrefix, raw, stops);
                rows.Add(new Row(item, raw, shown, elapsed, await ParsesAsync(item, shown)));
                if (variant == shipped)
                {
                    shippedShown.Add((item.Name, shown));
                }
            }

            var rawProse = rows.Count(r => IsProse(r.Raw));
            var rawFence = rows.Count(r => StartsWithFence(r.Raw));
            var rawCode = rows.Count - rawProse - rawFence;
            var empty = rows.Count(r => r.Shown.Trim().Length == 0);
            var shownProse = rows.Count(r => r.Shown.Trim().Length > 0 && IsProse(r.Shown));
            var shownFence = rows.Count(r => StartsWithFence(r.Shown) || r.Shown.Split('\n').Any(CompletionContentFilter.IsFenceLine));
            var parses = rows.Count(r => r.Shown.Trim().Length > 0 && r.Parses == true);
            var checkedCount = rows.Count(r => r.Shown.Trim().Length > 0 && r.Parses is not null);
            var median = rows.Select(r => r.Ms).Order().ElementAt(rows.Count / 2);
            output.WriteLine(string.Create(CultureInfo.InvariantCulture, $"{variant,-17} | {rawProse,10} {rawFence,5} {rawCode,4} | {empty,12} {shownProse,5} {shownFence,5} {parses,7}/{checkedCount,-9} | {median,6:F0}"));
            foreach (var row in rows)
            {
                output.WriteLine($"    {row.Case.Name,-16} raw: {Excerpt(row.Raw)}");
                output.WriteLine($"    {string.Empty,-16} shown: {Excerpt(row.Shown)}{(row.Parses is { } p ? p ? "  [parses]" : "  [does not parse]" : string.Empty)}");
            }

            if (variant == shipped)
            {
                Assert.Equal(0, shownProse);
                Assert.Equal(0, shownFence);
            }
        }
    }

    /// <summary>The header the gateway wrote at Gate 1 (before this change), reconstructed for comparison.</summary>
    private static string Gate1Header(CompletionQualityCase item, Contracts.CompletionRequest request)
    {
        var comment = AutocompletePromptBuilder.CommentSyntax(item.LanguageId, item.FilePath)!.Value;
        string Line(string text) => comment.Open + text + comment.Close + "\n";
        var name = Path.GetFileName(item.FilePath);
        var header = new StringBuilder(Line("File: " + name));
        var open = request.OpenDocuments!.Select(Path.GetFileName).Where(n => !string.Equals(n, name, StringComparison.OrdinalIgnoreCase)).Order(StringComparer.OrdinalIgnoreCase);
        header.Append(Line("Open files: " + string.Join(", ", open)));
        foreach (var edit in request.RecentEdits!.Where(e => !e.Contains(name, StringComparison.OrdinalIgnoreCase)))
        {
            header.Append(Line("Recent edit: " + edit));
        }

        return header.ToString();
    }

    /// <summary>What the user would see: the gateway's content filter and shaper, then the extension's block rule and suffix overlap.</summary>
    private static string Show(CompletionQualityCase item, string promptPrefix, string raw, IReadOnlyList<string> stops)
    {
        var filter = new CompletionContentFilter(item.Prefix, item.LanguageId, promptPrefix);
        var shaperStops = item.MultiLine ? stops : ["\n", .. stops];
        var shaper = new CompletionShaper(shaperStops, item.MultiLine ? AutocompleteBudget.MultiLineMaxLines : 1);
        var passed = filter.Push(raw);
        if (!filter.IsDropped)
        {
            shaper.Push(passed);
            var held = filter.Flush();
            if (held.Length > 0)
            {
                shaper.Push(held);
            }

            shaper.Flush();
        }

        var text = filter.IsDropped ? string.Empty : shaper.Text;
        if (item.MultiLine)
        {
            text = CutAtBlockEnd(item, text);
        }

        var after = item.Suffix.Split('\n')[0].Trim();
        var trimmed = text.TrimEnd();
        return after.Length > 0 && trimmed.EndsWith(after, StringComparison.Ordinal) ? trimmed[..^after.Length] : text;
    }

    /// <summary>The extension's multi-line block rule, simplified: braces close the caret's block, or Python dedents out of it.</summary>
    private static string CutAtBlockEnd(CompletionQualityCase item, string text)
    {
        var lines = text.Split('\n');
        var kept = new List<string>();
        if (item.LanguageId == "python")
        {
            var caretIndent = item.Prefix.Split('\n')[^2].TakeWhile(c => c == ' ').Count() + 4;
            foreach (var line in lines)
            {
                if (line.Trim().Length > 0 && line.TakeWhile(c => c == ' ').Count() < caretIndent)
                {
                    break;
                }

                kept.Add(line);
            }

            return string.Join("\n", kept).TrimEnd();
        }

        var depth = 0;
        foreach (var line in lines)
        {
            var cut = -1;
            for (var i = 0; i < line.Length && cut < 0; i++)
            {
                depth += line[i] == '{' ? 1 : line[i] == '}' ? -1 : 0;
                if (depth < 0)
                {
                    cut = i;
                }
            }

            if (cut >= 0)
            {
                if (line[..cut].Trim().Length > 0)
                {
                    kept.Add(line[..cut].TrimEnd());
                }

                break;
            }

            kept.Add(line);
        }

        return string.Join("\n", kept).TrimEnd();
    }

    private static bool IsProse(string text) => CompletionContentFilter.ClassifyStart(text, lineComplete: true) == CompletionContentVerdict.Prose;

    private static bool StartsWithFence(string text) => text.TrimStart().StartsWith("```", StringComparison.Ordinal) || text.TrimStart().StartsWith("~~~", StringComparison.Ordinal);

    private static string Excerpt(string text)
    {
        var flat = text.Replace("\r", string.Empty, StringComparison.Ordinal).Replace("\n", "\\n", StringComparison.Ordinal);
        return "[" + (flat.Length > 110 ? flat[..110] + "..." : flat) + "]";
    }

    /// <summary>Whether the document parses with the suggestion inserted; <see langword="null"/> when there is nothing to check or no parser.</summary>
    private static async Task<bool?> ParsesAsync(CompletionQualityCase item, string shown)
    {
        if (shown.Trim().Length == 0)
        {
            return null;
        }

        var document = item.Prefix + shown + item.Suffix + item.Closing;
        switch (item.LanguageId)
        {
            case "csharp":
                return !CSharpSyntaxTree.ParseText(document).GetDiagnostics().Any(d => d.Severity == Microsoft.CodeAnalysis.DiagnosticSeverity.Error);
            case "typescript":
                return await RunCheckerAsync("node", ["-e", "const m=require('node:module');let s='';process.stdin.on('data',d=>s+=d).on('end',()=>{try{m.stripTypeScriptTypes(s,{mode:'transform'});process.exit(0)}catch(e){process.exit(1)}})"], document);
            case "python":
                return await RunCheckerAsync("py", ["-3", "-c", "import ast,sys; ast.parse(sys.stdin.read())"], document);
            default:
                return null;
        }
    }

    private static async Task<bool?> RunCheckerAsync(string file, string[] arguments, string input)
    {
        try
        {
            var start = new ProcessStartInfo(file) { RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
            foreach (var argument in arguments)
            {
                start.ArgumentList.Add(argument);
            }

            using var process = Process.Start(start)!;
            await process.StandardInput.WriteAsync(input);
            process.StandardInput.Close();
            _ = process.StandardOutput.ReadToEndAsync();
            _ = process.StandardError.ReadToEndAsync();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            await process.WaitForExitAsync(timeout.Token);
            return process.ExitCode == 0;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or OperationCanceledException)
        {
            return null;
        }
    }

    private static async Task<string> GenerateAsync(HttpClient http, string model, string prefix, string suffix, int maxTokens, IReadOnlyList<string> stops)
    {
        var body = new JsonObject
        {
            ["model"] = model,
            ["prompt"] = prefix,
            ["suffix"] = suffix,
            ["raw"] = false,
            ["stream"] = false,
            ["keep_alive"] = -1,
            ["options"] = new JsonObject
            {
                ["num_ctx"] = 4096,
                ["num_predict"] = maxTokens,
                ["temperature"] = 0.2,
                ["seed"] = 42,
                ["stop"] = new JsonArray([.. stops.Select(s => (JsonNode?)s)]),
            },
        };
        using var response = await http.PostAsJsonAsync("api/generate", body);
        response.EnsureSuccessStatusCode();
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return json.RootElement.GetProperty("response").GetString() ?? string.Empty;
    }

    private sealed record Row(CompletionQualityCase Case, string Raw, string Shown, double Ms, bool? Parses);
}

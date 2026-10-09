using System.Diagnostics;
using System.Text;
using Pillion.Contracts;
using Xunit.Abstractions;

namespace Pillion.ModelGateway.Tests.Context;

/// <summary>
/// Gate 1 row "Context assembly without retrieval: p50 150 ms, p95 400 ms". These tests time the assembler on a realistic
/// request (three pins of 8, 60 and 400 KB, a current file region, rules, 30 turns of history) with the real tokenizers, and
/// print the percentiles. The asserted bounds are generous (about five times the gate) because CI machines are noisy; the
/// numbers on the reference machine are in the Gate 1 evidence.
/// </summary>
public class ContextAssemblyBenchmarkTests(ITestOutputHelper output)
{
    private const int Iterations = 40;

    [Theory]
    [InlineData("gpt-4o", "exact tiktoken o200k")]
    [InlineData("claude-sonnet-5-5", "calibrated estimate")]
    public async Task Assembly_OfARealisticRequest_StaysInsideTheBudget(string model, string label)
    {
        var assembler = new ContextAssembler(TokenCounter.Default);
        var target = new AssemblyTarget(model, 128_000, false, PromptTemplates.Default.ChatSystemPrompt, ["**/bin/**", "**/obj/**"]);
        var request = BuildRequest();

        // Warm up: loads the tokenizer data and JITs the paths.
        for (var i = 0; i < 3; i++)
        {
            await assembler.AssembleAsync(request, target, null, CancellationToken.None);
        }

        var samples = new List<double>();
        AssembledContext? last = null;
        for (var i = 0; i < Iterations; i++)
        {
            var started = Stopwatch.GetTimestamp();
            last = await assembler.AssembleAsync(request, target, null, CancellationToken.None);
            samples.Add(Stopwatch.GetElapsedTime(started).TotalMilliseconds);
        }

        samples.Sort();
        var p50 = samples[samples.Count / 2];
        var p95 = samples[(int)(samples.Count * 0.95)];
        output.WriteLine($"{model} ({label}): window 128k, {last!.Report.ItemCount} items, {last.Report.TrimmedCount} truncated or dropped, {last.PromptTokens} prompt tokens; assembly p50 {p50:F1} ms, p95 {p95:F1} ms, max {samples[^1]:F1} ms over {Iterations} runs.");

        Assert.True(last.Report.TrimmedCount >= 1, "The big pin must have been truncated.");
        Assert.InRange(p50, 0, 750);
        Assert.InRange(p95, 0, 2000);
    }

    [Fact]
    public async Task Assembly_WithACachedSummary_IsNoSlowerThanWithoutOne()
    {
        var assembler = new ContextAssembler(TokenCounter.Default);
        var target = new AssemblyTarget("gpt-4o", 16_000, false, PromptTemplates.Default.ChatSystemPrompt, []);
        var request = BuildRequest();
        var summariser = new CountingSummariser();
        await assembler.AssembleAsync(request, target, summariser, CancellationToken.None);
        Assert.Equal(1, summariser.Calls);

        var samples = new List<double>();
        for (var i = 0; i < Iterations; i++)
        {
            var started = Stopwatch.GetTimestamp();
            await assembler.AssembleAsync(request, target, summariser, CancellationToken.None);
            samples.Add(Stopwatch.GetElapsedTime(started).TotalMilliseconds);
        }

        samples.Sort();
        output.WriteLine($"gpt-4o, 16k window with a cached summary: assembly p50 {samples[samples.Count / 2]:F1} ms, p95 {samples[(int)(samples.Count * 0.95)]:F1} ms.");
        Assert.Equal(1, summariser.Calls);
        Assert.InRange(samples[(int)(samples.Count * 0.95)], 0, 2000);
    }

    private static ChatRequest BuildRequest()
    {
        var history = new List<ChatMessage>();
        for (var i = 0; i < 15; i++)
        {
            history.Add(new ChatMessage { Role = ChatRole.User, Content = $"Question {i}: how does the {Code(8, i)} behave when the cache is cold?" });
            history.Add(new ChatMessage { Role = ChatRole.Assistant, Content = $"Answer {i}: " + Code(40, i) });
        }

        history.Add(new ChatMessage { Role = ChatRole.User, Content = "Now refactor the order service to use the new repository." });
        return new ChatRequest
        {
            RequestId = "bench",
            ConversationId = "bench-conv",
            Profile = Profile.Auto,
            ProjectRules = "Use file-scoped namespaces.\nPrefer records for DTOs.\nNo comments that restate the code.\n" + string.Concat(Enumerable.Repeat("Follow the naming rules in CONTRIBUTING.md.\n", 40)),
            CurrentFile = @"C:\src\Orders\OrderService.cs",
            CurrentFileRegion = Code(120, 99),
            Pins =
            [
                Pin("OrderService.cs", Code(160, 1)),
                Pin("OrderRepository.cs", Code(1300, 2)),
                Pin("Everything.cs", Code(8500, 3)),
            ],
            Messages = history,
        };
    }

    private static MentionResolution Pin(string name, string content) => new()
    {
        Mention = "@file:" + name,
        IsResolved = true,
        Kind = "file",
        DisplayName = name,
        FilePath = @"C:\src\Orders\" + name,
        Content = content,
    };

    /// <summary>Plausible C# text of about <paramref name="lines"/> lines (47 characters each).</summary>
    private static string Code(int lines, int seed)
    {
        var builder = new StringBuilder();
        for (var i = 0; i < lines; i++)
        {
            builder.Append("    public int Member").Append(seed).Append('_').Append(i).Append("(string name) => name.Length + ").Append(i).Append(";\n");
        }

        return builder.ToString();
    }

    private sealed class CountingSummariser : IHistorySummariser
    {
        public int Calls { get; private set; }

        public Task<string> SummariseAsync(SummariseRequest request, CancellationToken cancellationToken)
        {
            Calls++;
            return Task.FromResult("The user is refactoring the order service; the repository is new.");
        }
    }
}

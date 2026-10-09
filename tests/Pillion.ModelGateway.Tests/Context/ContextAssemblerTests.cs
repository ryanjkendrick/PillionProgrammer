using System.Text;
using Pillion.Contracts;

namespace Pillion.ModelGateway.Tests.Context;

/// <summary>Context assembly (work item 1.10): budget maths, priority, caps, elision, summary trigger and cache, section order.</summary>
public class ContextAssemblerTests
{
    private const string SystemPrompt = "You are a test assistant."; // 25 characters, 7 tokens with the quarter counter.

    private static readonly QuarterCounter Counter = new();

    private static ContextAssembler Create(ContextAssemblerOptions? options = null, params IContextSource[] sources) =>
        new(Counter, options, sources);

    private static AssemblyTarget Target(int window = 20_000, bool local = true, params string[] excludes) =>
        new("test-model", window, local, SystemPrompt, excludes);

    private static ChatMessage User(string text) => new() { Role = ChatRole.User, Content = text };

    private static ChatMessage Assistant(string text) => new() { Role = ChatRole.Assistant, Content = text };

    /// <summary>A text of the given token count (four characters per token) in lines of 40 characters.</summary>
    private static string Text(int tokens, string label = "t")
    {
        var builder = new StringBuilder();
        var line = 0;
        while (builder.Length < tokens * 4)
        {
            var text = $"{label}{line++:D5} " + new string('x', 30) + "\n";
            builder.Append(text);
        }

        return builder.ToString(0, tokens * 4);
    }

    private static MentionResolution Pin(string name, string content, bool sensitive = false, string kind = "file", string? path = null) => new()
    {
        Mention = "@file:" + name,
        IsResolved = true,
        Kind = kind,
        DisplayName = name,
        FilePath = path ?? @"C:\src\" + name,
        Content = content,
        TokenCount = Counter.Count(content),
        IsSensitive = sensitive,
    };

    private static ChatRequest Request(string query = "Explain this.", IEnumerable<ChatMessage>? history = null, string conversation = "c1") => new()
    {
        RequestId = "r1",
        ConversationId = conversation,
        Messages = [.. history ?? [], User(query)],
    };

    [Fact]
    public void ShouldAssemble_IsFalseForAPlainRequest_AndTrueForEachKindOfContext()
    {
        Assert.False(ContextAssembler.ShouldAssemble(Request()));
        Assert.True(ContextAssembler.ShouldAssemble(Request().With(r => r.Pins = [Pin("a.cs", "x")])));
        Assert.True(ContextAssembler.ShouldAssemble(Request().With(r => r.CurrentFile = @"C:\a.cs")));
        Assert.True(ContextAssembler.ShouldAssemble(Request().With(r => r.CurrentFileRegion = "x")));
        Assert.True(ContextAssembler.ShouldAssemble(Request().With(r => r.ProjectRules = "Be brief.")));
        Assert.False(ContextAssembler.ShouldAssemble(Request().With(r => r.ProjectRules = "   ")));
    }

    [Theory]
    [InlineData(10_000, 4000, 2500)] // The reserve never exceeds a quarter of the window.
    [InlineData(100_000, 4000, 4000)]
    [InlineData(100_000, 1000, 1000)]
    public async Task Budget_IsWindowMinusReserveMinusSystemPrompt(int window, int configuredReserve, int expectedReserve)
    {
        var assembler = Create(new ContextAssemblerOptions { OutputReserveTokens = configuredReserve });
        var request = Request().With(r => r.ProjectRules = "Be brief.");

        var assembled = await assembler.AssembleAsync(request, Target(window), null, CancellationToken.None);

        Assert.Equal(expectedReserve, assembled.Report.OutputReserve);
        Assert.Equal(window - expectedReserve - Counter.Count(SystemPrompt), assembled.Report.BudgetTokens);
        Assert.Equal(window, assembled.Report.ContextWindow);
        Assert.Equal("test-model", assembled.Report.ModelId);
    }

    [Fact]
    public async Task Sections_AreOrderedMostToLeastStable_AndTheSystemMessageHoldsThemInThatOrder()
    {
        var request = Request("What does it do?", [User("hi"), Assistant("hello")]).With(r =>
        {
            r.ProjectRules = "Use file-scoped namespaces.";
            r.Pins = [Pin("Customer.cs", "class Customer { }")];
            r.CurrentFile = @"C:\src\Program.cs";
            r.CurrentFileRegion = "var c = new Customer();";
        });

        var assembled = await Create().AssembleAsync(request, Target(), null, CancellationToken.None);

        Assert.Equal(
            [PromptSectionKind.System, PromptSectionKind.Rules, PromptSectionKind.Pins, PromptSectionKind.CurrentFile, PromptSectionKind.History, PromptSectionKind.Query],
            assembled.Sections.Select(s => s.Kind));
        Assert.Equal(
            [ChatRole.System, ChatRole.User, ChatRole.Assistant, ChatRole.User],
            assembled.Messages.Select(m => m.Role));
        var system = assembled.Messages[0].Content;
        var order = new[] { SystemPrompt, "## Project rules", "## Pinned context", "class Customer { }", "## Current file: C:\\src\\Program.cs", "var c = new Customer();" }
            .Select(s => system.IndexOf(s, StringComparison.Ordinal)).ToArray();
        Assert.All(order, i => Assert.True(i >= 0));
        Assert.Equal(order.Order(), order);
        Assert.Equal("What does it do?", assembled.Messages[^1].Content);
        Assert.Equal("hi", assembled.Messages[1].Content);
        Assert.Equal(
            [ContextItemKind.SystemPrompt, ContextItemKind.Rules, ContextItemKind.Pin, ContextItemKind.CurrentFile, ContextItemKind.History, ContextItemKind.Query],
            assembled.Report.Items.Select(i => i.Kind));
        Assert.Equal(0, assembled.Report.TrimmedCount);
        Assert.Equal(6, assembled.Report.ItemCount);
    }

    [Fact]
    public async Task APinAboveAQuarterOfTheBudget_IsReducedToItsFirstAndLastParts()
    {
        var window = 20_000;
        var assembler = Create();
        var pinText = Text(6000, "p");
        var request = Request().With(r => r.Pins = [Pin("Big.cs", pinText)]);

        var assembled = await assembler.AssembleAsync(request, Target(window), null, CancellationToken.None);

        var budget = assembled.Report.BudgetTokens;
        var item = Assert.Single(assembled.Report.Items, i => i.Kind == ContextItemKind.Pin);
        Assert.True(item.Truncated);
        Assert.False(item.Dropped);
        Assert.InRange(item.Tokens, (int)(budget * 0.25) - 400, (int)(budget * 0.25));
        Assert.True(item.OriginalTokens > 6000);
        Assert.Contains("25%", item.Note.Replace("the pin cap", "25%"), StringComparison.Ordinal);
        var system = assembled.Messages[0].Content;
        Assert.Contains("p00000 ", system, StringComparison.Ordinal); // the first part
        Assert.Contains(pinText.Split('\n')[^2][..7], system, StringComparison.Ordinal); // the last part
        Assert.Matches(@"\[\.\.\. \d+ lines omitted \.\.\.\]", system);
        Assert.Equal(1, assembled.Report.TrimmedCount);
    }

    [Fact]
    public async Task APinUnderTheCap_IsKeptWhole()
    {
        var pinText = Text(1000, "p");
        var request = Request().With(r => r.Pins = [Pin("Small.cs", pinText)]);

        var assembled = await Create().AssembleAsync(request, Target(), null, CancellationToken.None);

        var item = Assert.Single(assembled.Report.Items, i => i.Kind == ContextItemKind.Pin);
        Assert.False(item.Truncated);
        Assert.Contains(pinText.TrimEnd(), assembled.Messages[0].Content, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheCurrentFileRegion_IsCappedAtFifteenPercent_KeepingTheMiddle()
    {
        var region = Text(5000, "f");
        var request = Request().With(r =>
        {
            r.CurrentFile = @"C:\src\Big.cs";
            r.CurrentFileRegion = region;
        });

        var assembled = await Create().AssembleAsync(request, Target(), null, CancellationToken.None);

        var budget = assembled.Report.BudgetTokens;
        var item = Assert.Single(assembled.Report.Items, i => i.Kind == ContextItemKind.CurrentFile);
        Assert.True(item.Truncated);
        Assert.InRange(item.Tokens, (int)(budget * 0.15) - 400, (int)(budget * 0.15));
        var system = assembled.Messages[0].Content;
        Assert.DoesNotContain("f00000 ", system, StringComparison.Ordinal);
        var middleLine = region.Split('\n')[region.Split('\n').Length / 2][..6];
        Assert.Contains(middleLine, system, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PinsAreKeptInPriorityOrder_AndOnesThatDoNotFitAreDroppedWithAReason()
    {
        var window = 6000; // Reserve 1500, budget about 4490.
        var request = Request().With(r => r.Pins =
        [
            Pin("One.cs", Text(900, "a")),
            Pin("Two.cs", Text(900, "b")),
            Pin("Three.cs", Text(900, "c")),
            Pin("Four.cs", Text(900, "d")),
            Pin("Five.cs", Text(900, "e")),
            Pin("Six.cs", Text(900, "g")),
        ]);

        var assembled = await Create().AssembleAsync(request, Target(window), null, CancellationToken.None);

        var pins = assembled.Report.Items.Where(i => i.Kind == ContextItemKind.Pin).ToList();
        Assert.Equal(["One.cs", "Two.cs", "Three.cs", "Four.cs", "Five.cs", "Six.cs"], pins.Select(p => p.Name));
        Assert.All(pins.Take(3), p => Assert.False(p.Dropped));
        Assert.Contains(pins, p => p.Dropped);
        var firstDropped = pins.FindIndex(p => p.Dropped);
        Assert.All(pins.Skip(firstDropped), p => Assert.True(p.Dropped || p.Truncated));
        Assert.All(pins.Where(p => p.Dropped), p => Assert.Equal(0, p.Tokens));
        Assert.All(pins.Where(p => p.Dropped), p => Assert.Contains("room", p.Note, StringComparison.Ordinal));
        Assert.True(assembled.Report.TotalTokens <= assembled.Report.BudgetTokens + Counter.Count(SystemPrompt));
        Assert.DoesNotContain("g00000", assembled.Messages[0].Content, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Rules_ComeBeforePins_SoPinsAreSqueezedFirstWhenRoomRunsOut()
    {
        var window = 4000; // Reserve 1000, budget about 2990.
        var request = Request().With(r =>
        {
            r.ProjectRules = Text(200, "r");
            r.Pins = [Pin("Huge.cs", Text(5000, "p"))];
        });

        var assembled = await Create().AssembleAsync(request, Target(window), null, CancellationToken.None);

        var rules = Assert.Single(assembled.Report.Items, i => i.Kind == ContextItemKind.Rules);
        Assert.False(rules.Truncated);
        Assert.False(rules.Dropped);
        var pin = Assert.Single(assembled.Report.Items, i => i.Kind == ContextItemKind.Pin);
        Assert.True(pin.Truncated);
        Assert.True(assembled.Report.TotalTokens <= assembled.Report.BudgetTokens + Counter.Count(SystemPrompt));
    }

    [Fact]
    public async Task ARulesFileAboveTenPercent_IsCut()
    {
        var request = Request().With(r => r.ProjectRules = Text(4000, "r"));

        var assembled = await Create().AssembleAsync(request, Target(), null, CancellationToken.None);

        var rules = Assert.Single(assembled.Report.Items, i => i.Kind == ContextItemKind.Rules);
        Assert.True(rules.Truncated);
        Assert.InRange(rules.Tokens, (int)(assembled.Report.BudgetTokens * 0.10) - 300, (int)(assembled.Report.BudgetTokens * 0.10));
    }

    [Fact]
    public async Task ASensitivePin_IsSentToALocalModelButNeverToACloudModel()
    {
        var request = Request().With(r => r.Pins = [Pin(".env", "KEY=value", sensitive: true), Pin("Program.cs", "class P { }")]);

        var local = await Create().AssembleAsync(request, Target(local: true), null, CancellationToken.None);
        var cloud = await Create().AssembleAsync(request, Target(local: false), null, CancellationToken.None);

        Assert.Contains("KEY=value", local.Messages[0].Content, StringComparison.Ordinal);
        Assert.DoesNotContain("KEY=value", cloud.Messages[0].Content, StringComparison.Ordinal);
        Assert.Contains("class P { }", cloud.Messages[0].Content, StringComparison.Ordinal);
        var dropped = Assert.Single(cloud.Report.Items, i => i.Dropped);
        Assert.Equal(".env", dropped.Name);
        Assert.Contains("cloud", dropped.Note, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnExcludedFilePin_WithoutApproval_IsDropped()
    {
        var request = Request().With(r => r.Pins = [Pin("Gen.cs", "generated", path: @"C:\src\obj\Gen.cs"), Pin("Ok.cs", "fine")]);

        var assembled = await Create().AssembleAsync(request, Target(excludes: "**/obj/**"), null, CancellationToken.None);

        var dropped = Assert.Single(assembled.Report.Items, i => i.Dropped);
        Assert.Equal("Gen.cs", dropped.Name);
        Assert.DoesNotContain("generated", assembled.Messages[0].Content, StringComparison.Ordinal);
        Assert.Contains("fine", assembled.Messages[0].Content, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheCurrentFile_IsLeftOut_WhenItIsASecretFile()
    {
        var request = Request().With(r =>
        {
            r.CurrentFile = @"C:\src\.env";
            r.CurrentFileRegion = "API_KEY=abc";
        });

        var assembled = await Create().AssembleAsync(request, Target(), null, CancellationToken.None);

        Assert.DoesNotContain("API_KEY", assembled.Messages[0].Content, StringComparison.Ordinal);
        var item = Assert.Single(assembled.Report.Items, i => i.Kind == ContextItemKind.CurrentFile);
        Assert.True(item.Dropped);
    }

    [Fact]
    public async Task AQueryThatDoesNotFitTheWindow_ThrowsContextOverflow()
    {
        var request = Request(Text(5000));

        var ex = await Assert.ThrowsAsync<PillionRpcException>(() =>
            Create().AssembleAsync(request.With(r => r.ProjectRules = "x"), Target(2000), null, CancellationToken.None));

        Assert.Equal(PillionErrorCode.ContextOverflow, ex.Error.Code);
    }

    [Fact]
    public async Task AClientSystemMessage_ReplacesTheDefaultSystemPrompt()
    {
        var request = new ChatRequest
        {
            RequestId = "r",
            Messages = [new ChatMessage { Role = ChatRole.System, Content = "Custom system." }, User("hi")],
            ProjectRules = "Rule.",
        };

        var assembled = await Create().AssembleAsync(request, Target(), null, CancellationToken.None);

        Assert.StartsWith("Custom system.", assembled.Messages[0].Content, StringComparison.Ordinal);
        Assert.DoesNotContain(SystemPrompt, assembled.Messages[0].Content, StringComparison.Ordinal);
        Assert.Equal(2, assembled.Messages.Count);
    }

    // ---- History and the rolling summary ----

    private static List<ChatMessage> LongHistory(int turns, int tokensPerMessage = 500)
    {
        var history = new List<ChatMessage>();
        for (var i = 0; i < turns; i++)
        {
            history.Add(User($"q{i:D3} " + Text(tokensPerMessage, "u")));
            history.Add(Assistant($"a{i:D3} " + Text(tokensPerMessage, "v")));
        }

        return history;
    }

    [Fact]
    public async Task HistoryThatFits_IsKeptVerbatim_WithoutASummary()
    {
        var summariser = new FakeSummariser();
        var request = Request("next", LongHistory(2, 100)).With(r => r.ProjectRules = "x");

        var assembled = await Create().AssembleAsync(request, Target(), summariser, CancellationToken.None);

        Assert.Equal(0, summariser.Calls);
        Assert.Equal(1 + 4 + 1, assembled.Messages.Count);
        Assert.DoesNotContain(assembled.Sections, s => s.Kind == PromptSectionKind.Summary);
        Assert.Single(assembled.Report.Items, i => i.Kind == ContextItemKind.History && !i.Dropped);
    }

    [Fact]
    public async Task HistoryOverBudget_IsCompressedIntoASummary_AndRecentTurnsStayVerbatim()
    {
        var summariser = new FakeSummariser { Reply = "The user is refactoring Customer." };
        var history = LongHistory(12); // 24 messages of ~500 tokens: 12,000 tokens against a budget of about 4,500.
        var request = Request("and now?", history).With(r => r.ProjectRules = "x");

        var assembled = await Create().AssembleAsync(request, Target(6000), summariser, CancellationToken.None);

        Assert.Equal(1, summariser.Calls);
        var call = summariser.Requests.Single();
        Assert.Null(call.PriorSummary);
        Assert.Equal("c1", call.ConversationId);
        Assert.True(call.KeepLocal);
        Assert.Equal(history[0].Content, call.Messages[0].Content);
        var summarised = call.Messages.Count;
        Assert.InRange(summarised, 2, history.Count - 1);
        Assert.Contains("The user is refactoring Customer.", assembled.Messages[0].Content, StringComparison.Ordinal);
        Assert.Contains("## Summary of the earlier conversation", assembled.Messages[0].Content, StringComparison.Ordinal);

        // Recent turns are the newest ones, in order, starting with a user turn, then the query.
        var recent = assembled.Messages.Skip(1).Take(assembled.Messages.Count - 2).ToList();
        Assert.Equal(history.Skip(summarised).Select(m => m.Content), recent.Select(m => m.Content));
        Assert.Equal(ChatRole.User, recent[0].Role);
        Assert.Equal("and now?", assembled.Messages[^1].Content);

        Assert.True(assembled.PromptTokens <= 6000 - assembled.Report.OutputReserve + 30, $"prompt {assembled.PromptTokens}");
        Assert.Contains(assembled.Report.Items, i => i.Kind == ContextItemKind.Summary && !i.Dropped);
        Assert.Equal(
            [PromptSectionKind.System, PromptSectionKind.Rules, PromptSectionKind.Summary, PromptSectionKind.History, PromptSectionKind.Query],
            assembled.Sections.Select(s => s.Kind));
    }

    [Fact]
    public async Task TheSummary_IsCachedPerConversation_AndReusedWhileTheHistoryIsUnchanged()
    {
        var summariser = new FakeSummariser { Reply = "SUMMARY" };
        var assembler = Create();
        var history = LongHistory(12);

        var first = await assembler.AssembleAsync(Request("q", history), Target(6000), summariser, CancellationToken.None);
        var again = await assembler.AssembleAsync(Request("q", history), Target(6000), summariser, CancellationToken.None);

        Assert.Equal(1, summariser.Calls);
        Assert.Equal(first.Messages.Select(m => m.Content), again.Messages.Select(m => m.Content));
        Assert.Equal(1, assembler.CachedSummaries);

        // One more turn pair still fits under the cached summary, so there is no new request either.
        var longer = history.Concat([User("one more"), Assistant("sure")]).ToList();
        var third = await assembler.AssembleAsync(Request("q2", longer), Target(6000), summariser, CancellationToken.None);
        Assert.Equal(1, summariser.Calls);
        Assert.Contains("SUMMARY", third.Messages[0].Content, StringComparison.Ordinal);
        Assert.Equal("sure", third.Messages[^2].Content);
    }

    [Fact]
    public async Task WhenTheTailOutgrowsTheCachedSummary_OnlyTheNewTurnsAreFoldedIn()
    {
        var summariser = new FakeSummariser { Reply = "SUMMARY-1" };
        var assembler = Create();
        var history = LongHistory(12);
        await assembler.AssembleAsync(Request("q", history), Target(6000), summariser, CancellationToken.None);
        var firstCovered = summariser.Requests[0].Messages.Count;

        summariser.Reply = "SUMMARY-2";
        var longer = history.Concat(LongHistory(6)).ToList();
        var assembled = await assembler.AssembleAsync(Request("q", longer), Target(6000), summariser, CancellationToken.None);

        Assert.Equal(2, summariser.Calls);
        var second = summariser.Requests[1];
        Assert.Equal("SUMMARY-1", second.PriorSummary);
        Assert.Equal(longer[firstCovered].Content, second.Messages[0].Content);
        Assert.Contains("SUMMARY-2", assembled.Messages[0].Content, StringComparison.Ordinal);
        Assert.DoesNotContain("SUMMARY-1", assembled.Messages[0].Content, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ChangingAnEarlierTurn_InvalidatesTheCachedSummary()
    {
        var summariser = new FakeSummariser { Reply = "SUMMARY" };
        var assembler = Create();
        var history = LongHistory(12);
        await assembler.AssembleAsync(Request("q", history), Target(6000), summariser, CancellationToken.None);

        var edited = history.ToList();
        edited[0] = User("an edited first message");
        await assembler.AssembleAsync(Request("q", edited), Target(6000), summariser, CancellationToken.None);

        Assert.Equal(2, summariser.Calls);
        Assert.Null(summariser.Requests[1].PriorSummary);
    }

    [Fact]
    public async Task EachConversation_HasItsOwnSummary_AndWithoutAnIdNothingIsCached()
    {
        var summariser = new FakeSummariser();
        var assembler = Create();
        var history = LongHistory(12);

        await assembler.AssembleAsync(Request("q", history, "A"), Target(6000), summariser, CancellationToken.None);
        await assembler.AssembleAsync(Request("q", history, "B"), Target(6000), summariser, CancellationToken.None);
        await assembler.AssembleAsync(Request("q", history, string.Empty), Target(6000), summariser, CancellationToken.None);
        await assembler.AssembleAsync(Request("q", history, string.Empty), Target(6000), summariser, CancellationToken.None);

        Assert.Equal(4, summariser.Calls);
        Assert.Equal(2, assembler.CachedSummaries);
    }

    [Fact]
    public async Task WithoutASummariser_OlderTurnsAreDroppedAndReported()
    {
        var request = Request("q", LongHistory(12));

        var assembled = await Create().AssembleAsync(request, Target(6000), null, CancellationToken.None);

        var dropped = Assert.Single(assembled.Report.Items, i => i.Dropped);
        Assert.Equal(ContextItemKind.History, dropped.Kind);
        Assert.Equal(0, dropped.Tokens);
        Assert.True(dropped.OriginalTokens > 0);
        Assert.Contains("Older history", dropped.Name, StringComparison.Ordinal);
        Assert.DoesNotContain(assembled.Sections, s => s.Kind == PromptSectionKind.Summary);
        Assert.Equal(ChatRole.User, assembled.Messages[1].Role);
    }

    [Fact]
    public async Task AFailingSummariser_FallsBackToDroppingOlderTurns_ButCancellationPropagates()
    {
        var failing = new FakeSummariser { Failure = new PillionRpcException(new PillionError { Code = PillionErrorCode.ProviderUnavailable, Message = "down" }) };
        var request = Request("q", LongHistory(12));

        var assembled = await Create().AssembleAsync(request, Target(6000), failing, CancellationToken.None);

        var dropped = Assert.Single(assembled.Report.Items, i => i.Dropped);
        Assert.Contains("down", dropped.Note, StringComparison.Ordinal);

        var cancelling = new FakeSummariser { Failure = new OperationCanceledException() };
        await Assert.ThrowsAsync<OperationCanceledException>(() => Create().AssembleAsync(request, Target(6000), cancelling, CancellationToken.None));
    }

    [Fact]
    public async Task AnOversizedSummary_IsCutToTheSummaryLimit()
    {
        var summariser = new FakeSummariser { Reply = Text(2000, "s") };
        var assembler = Create(new ContextAssemblerOptions { SummaryMaxTokens = 200 });

        var assembled = await assembler.AssembleAsync(Request("q", LongHistory(12)), Target(6000), summariser, CancellationToken.None);

        var summary = Assert.Single(assembled.Report.Items, i => i.Kind == ContextItemKind.Summary);
        Assert.True(summary.Truncated);
        Assert.InRange(summary.Tokens, 1, 200 + 20);
    }

    // ---- Stability and the phase 2 hooks ----

    [Fact]
    public async Task LeadingSections_AreByteIdentical_ForTwoRequestsWithTheSamePinsAndRules()
    {
        var assembler = Create();
        var pins = new[] { Pin("A.cs", Text(300, "a")), Pin("B.cs", Text(300, "b")) };
        var first = Request("first question", [User("hello"), Assistant("hi")]).With(r =>
        {
            r.ProjectRules = "Rules.";
            r.Pins = pins;
            r.CurrentFile = @"C:\src\One.cs";
            r.CurrentFileRegion = "one";
        });
        var second = Request("a different question", [User("hello"), Assistant("hi"), User("more"), Assistant("yes")]).With(r =>
        {
            r.ProjectRules = "Rules.";
            r.Pins = pins;
            r.CurrentFile = @"C:\src\Two.cs";
            r.CurrentFileRegion = "two two two";
        });

        var a = await assembler.AssembleAsync(first, Target(), null, CancellationToken.None);
        var b = await assembler.AssembleAsync(second, Target(), null, CancellationToken.None);

        foreach (var kind in new[] { PromptSectionKind.System, PromptSectionKind.Rules, PromptSectionKind.Pins })
        {
            var left = a.Sections.Single(s => s.Kind == kind);
            var right = b.Sections.Single(s => s.Kind == kind);
            Assert.Equal(left.Text, right.Text);
            Assert.Equal(left.Tokens, right.Tokens);
        }

        // The system message of both starts with the same bytes up to the current-file section.
        var cut = a.Messages[0].Content.IndexOf("## Current file", StringComparison.Ordinal);
        Assert.True(cut > 0);
        Assert.Equal(a.Messages[0].Content[..cut], b.Messages[0].Content[..cut]);
        Assert.NotEqual(a.Messages[0].Content, b.Messages[0].Content);
    }

    [Fact]
    public async Task PinningOneMoreItem_OnlyAppendsToThePinsSection()
    {
        var assembler = Create();
        var one = Request().With(r => r.Pins = [Pin("A.cs", "alpha")]);
        var two = Request().With(r => r.Pins = [Pin("A.cs", "alpha"), Pin("B.cs", "beta")]);

        var a = await assembler.AssembleAsync(one, Target(), null, CancellationToken.None);
        var b = await assembler.AssembleAsync(two, Target(), null, CancellationToken.None);

        var left = a.Sections.Single(s => s.Kind == PromptSectionKind.Pins).Text;
        var right = b.Sections.Single(s => s.Kind == PromptSectionKind.Pins).Text;
        Assert.StartsWith(left, right, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ContextSources_AreSlottedAndCapped_ForPhaseTwo()
    {
        var map = new FakeSource(ContextSlot.RepoMap, "Repository map", Text(3000, "m"));
        var chunks = new FakeSource(ContextSlot.RetrievedChunks, "Retrieved code", "chunk body");
        var assembler = Create(null, map, chunks);
        var request = Request().With(r =>
        {
            r.ProjectRules = "Rules.";
            r.Pins = [Pin("A.cs", "alpha")];
            r.CurrentFile = @"C:\src\A.cs";
            r.CurrentFileRegion = "region";
        });

        var assembled = await assembler.AssembleAsync(request, Target(), null, CancellationToken.None);

        Assert.Equal(
            [PromptSectionKind.System, PromptSectionKind.Rules, PromptSectionKind.RepoMap, PromptSectionKind.Pins, PromptSectionKind.CurrentFile, PromptSectionKind.RetrievedChunks, PromptSectionKind.Query],
            assembled.Sections.Select(s => s.Kind));
        var mapItem = Assert.Single(assembled.Report.Items, i => i.Kind == ContextItemKind.RepoMap);
        Assert.True(mapItem.Truncated);
        Assert.InRange(mapItem.Tokens, 1, (int)(assembled.Report.BudgetTokens * 0.10));
        Assert.Contains(assembled.Report.Items, i => i.Kind == ContextItemKind.RetrievedChunks && !i.Truncated);
        var system = assembled.Messages[0].Content;
        Assert.True(system.IndexOf("## Repository map", StringComparison.Ordinal) < system.IndexOf("## Pinned context", StringComparison.Ordinal));
        Assert.True(system.IndexOf("## Current file", StringComparison.Ordinal) < system.IndexOf("## Retrieved code", StringComparison.Ordinal));
    }

    [Fact]
    public async Task APinWithBackticks_IsFencedWithALongerFence()
    {
        var request = Request().With(r => r.Pins = [Pin("Doc.md", "Use ```code``` blocks.")]);

        var assembled = await Create().AssembleAsync(request, Target(), null, CancellationToken.None);

        Assert.Contains("````\nUse ```code``` blocks.\n````", assembled.Messages[0].Content, StringComparison.Ordinal);
    }

    private sealed class FakeSummariser : IHistorySummariser
    {
        public string Reply { get; set; } = "A summary.";

        public Exception? Failure { get; set; }

        public int Calls { get; private set; }

        public List<SummariseRequest> Requests { get; } = [];

        public Task<string> SummariseAsync(SummariseRequest request, CancellationToken cancellationToken)
        {
            Calls++;
            Requests.Add(request);
            return Failure is null ? Task.FromResult(Reply) : Task.FromException<string>(Failure);
        }
    }

    private sealed class FakeSource(ContextSlot slot, string name, string text) : IContextSource
    {
        public ContextSlot Slot => slot;

        public Task<ContextContribution?> GetAsync(ChatRequest request, AssemblyTarget target, CancellationToken cancellationToken) =>
            Task.FromResult<ContextContribution?>(new ContextContribution(name, text));
    }
}

internal static class ChatRequestTestExtensions
{
    /// <summary>Applies a change to a request and returns it (the DTO has no <c>with</c> support).</summary>
    public static ChatRequest With(this ChatRequest request, Action<ChatRequest> change)
    {
        change(request);
        return request;
    }
}

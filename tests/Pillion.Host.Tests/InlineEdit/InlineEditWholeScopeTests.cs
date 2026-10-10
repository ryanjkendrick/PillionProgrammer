using Microsoft.Extensions.Logging;
using Pillion.Common.Settings;
using Pillion.Contracts;
using Pillion.Host.InlineEdit;
using Pillion.ModelGateway.Editing;
using static Pillion.Host.Tests.InlineEdit.InlineEditTestSupport;

namespace Pillion.Host.Tests.InlineEdit;

/// <summary>Whole-scope mode: the model rewrites a small scope as plain code; blocks remain for large scopes and as the fallback.</summary>
public sealed class InlineEditWholeScopeTests
{
    private const string WholeSystem = "Reply with the complete rewritten code to edit and nothing else";
    private const string BlockSystem = "Reply with edit blocks in exactly this format";

    /// <summary>A scope of <paramref name="lines"/> lines inside a method, each a call, valid C# with <see cref="WideBefore"/> and <see cref="WideAfter"/>.</summary>
    private static string Wide(int lines) => string.Concat(Enumerable.Range(0, lines).Select(i => $"        Step{i}();\n"));

    private const string WideBefore = "class C\n{\n    void M()\n    {\n";
    private const string WideAfter = "    }\n}\n";

    private static InlineEditRequest WideRequest(int lines, InlineEditCommand command = InlineEditCommand.None)
    {
        var request = Request("rename the first step", command);
        request.ScopeText = Wide(lines);
        request.TextBefore = WideBefore;
        request.TextAfter = WideAfter;
        request.ScopeStartLine = 4;
        return request;
    }

    [Theory]
    [InlineData(1, 80, true)]
    [InlineData(79, 80, true)]
    [InlineData(80, 80, true)]
    [InlineData(81, 80, false)]
    [InlineData(200, 80, false)]
    [InlineData(1, 0, false)]
    [InlineData(10, 9, false)]
    [InlineData(10, 10, true)]
    public async Task TheMode_FollowsTheLineCount_AndTheLimit(int lines, int limit, bool expectWhole)
    {
        var scope = Wide(lines);
        var renamed = scope.Replace("Step0()", "First()", StringComparison.Ordinal);
        var reply = expectWhole ? renamed : "<<<<<<< SEARCH\n        Step0();\n=======\n        First();\n>>>>>>> REPLACE\n";
        var gateway = new ScriptedGateway().Reply(reply);

        var chunks = await CollectAsync(Engine(gateway, wholeScopeMaxLines: limit).RunAsync(WideRequest(lines), CancellationToken.None));

        Assert.Equal(renamed, Assemble(chunks));
        var chat = Assert.Single(gateway.Calls).Request;
        Assert.Contains(expectWhole ? WholeSystem : BlockSystem, chat.Messages[0].Content, StringComparison.Ordinal);
        Assert.Equal(expectWhole, chat.Messages[1].Content.EndsWith("no explanation.", StringComparison.Ordinal));
        Assert.Equal(0, chunks[^1].RejectedBlocks);
    }

    [Theory]
    [InlineData(InlineEditCommand.None, true)]
    [InlineData(InlineEditCommand.Doc, true)]
    [InlineData(InlineEditCommand.Fix, true)]
    [InlineData(InlineEditCommand.Test, false)]
    [InlineData(InlineEditCommand.Explain, false)]
    public async Task TheMode_FollowsTheCommand(InlineEditCommand command, bool expectWhole)
    {
        var reply = command switch
        {
            InlineEditCommand.Test => "```csharp\n[Fact] public void T() { }\n```\nIn OrdersTests.",
            InlineEditCommand.Explain => "It places an order.",
            InlineEditCommand.Doc => "    /// <summary>Places an order.</summary>\n" + Scope,
            _ => Extracted,
        };
        var gateway = new ScriptedGateway().Reply(reply);

        await CollectAsync(WholeEngine(gateway).RunAsync(Request(command == InlineEditCommand.None ? "extract the validation" : string.Empty, command), CancellationToken.None));

        var system = Assert.Single(gateway.Calls).Request.Messages[0].Content;
        Assert.Equal(expectWhole, system.Contains(WholeSystem, StringComparison.Ordinal));
        Assert.DoesNotContain(BlockSystem, system, StringComparison.Ordinal);
    }

    [Fact]
    public async Task APlainReply_IsTheProposal_WithTheFinalChunkAsBefore()
    {
        var gateway = new ScriptedGateway().Reply(Extracted);
        var request = Request();

        var chunks = await CollectAsync(WholeEngine(gateway).RunAsync(request, CancellationToken.None));

        Assert.Equal(Extracted, Assemble(chunks));
        var final = chunks[^1];
        Assert.True(final.IsFinal);
        Assert.Equal(string.Empty, final.Text);
        Assert.Null(final.Explanation);
        Assert.Null(final.Warning);
        Assert.Equal(0, final.RejectedBlocks);
        Assert.Equal("fake-coder", final.ModelId);
        Assert.Equal(100, final.PromptTokens);
        Assert.All(chunks, c => Assert.Equal(request.RequestId, c.RequestId));
        Assert.DoesNotContain(chunks, c => c.ResetText);
        Assert.Equal(TaskClass.InlineEdit, Assert.Single(gateway.Calls).TaskClass);
    }

    [Fact]
    public async Task AFencedReply_IsUnwrapped_AndItsProseIsTheExplanation()
    {
        var gateway = new ScriptedGateway().Reply("Here you go:\n```csharp\n" + Extracted + "```\nMoved the null check into Validate.");

        var chunks = await CollectAsync(WholeEngine(gateway).RunAsync(Request(), CancellationToken.None));

        Assert.Equal(Extracted, Assemble(chunks));
        Assert.Equal("Here you go:\nMoved the null check into Validate.", chunks[^1].Explanation);
    }

    [Fact]
    public async Task ANumberedReply_HasItsLineNumbersStripped()
    {
        var gateway = new ScriptedGateway().Reply(EditText.NumberLines(Extracted, 7) + "\n");

        var chunks = await CollectAsync(WholeEngine(gateway).RunAsync(Request(), CancellationToken.None));

        Assert.Equal(Extracted, Assemble(chunks));
    }

    [Fact]
    public async Task ANumberedAndFencedReply_IsUnwrappedAndStripped()
    {
        var gateway = new ScriptedGateway().Reply("```csharp\n" + EditText.NumberLines(Extracted, 7) + "\n```\n");

        var chunks = await CollectAsync(WholeEngine(gateway).RunAsync(Request(), CancellationToken.None));

        Assert.Equal(Extracted, Assemble(chunks));
    }

    [Theory]
    [InlineData("\n", "\n")]
    [InlineData("\r\n", "\n")]
    [InlineData("\n", "\r\n")]
    [InlineData("\r\n", "\r\n")]
    public async Task LineEndings_OfTheCode_DecideTheProposals_NotTheReplys(string codeNewline, string replyNewline)
    {
        var gateway = new ScriptedGateway().Reply(Extracted.Replace("\n", replyNewline, StringComparison.Ordinal));
        var request = Request();
        request.ScopeText = Scope.Replace("\n", codeNewline, StringComparison.Ordinal);
        request.TextBefore = Before.Replace("\n", codeNewline, StringComparison.Ordinal);
        request.TextAfter = After.Replace("\n", codeNewline, StringComparison.Ordinal);

        var chunks = await CollectAsync(WholeEngine(gateway).RunAsync(request, CancellationToken.None));

        Assert.Equal(Extracted.Replace("\n", codeNewline, StringComparison.Ordinal), Assemble(chunks));
    }

    [Fact]
    public async Task TheFirstLines_ArriveBeforeTheModelHasFinished()
    {
        // Hold the model's stream back after the first two lines of the rewrite.
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var firstTwoLines = string.Concat(Extracted.Split('\n').Take(2).Select(l => l + "\n"));
        var gateway = new ScriptedGateway().Reply(Extracted, gate, gateAfterPieces: ScriptedGateway.Pieces(firstTwoLines).Count);

        await using var stream = WholeEngine(gateway).RunAsync(Request(), CancellationToken.None).GetAsyncEnumerator();
        var early = new List<InlineEditChunk>();
        for (var i = 0; i < 2; i++)
        {
            Assert.True(await stream.MoveNextAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10)));
            early.Add(stream.Current);
        }

        Assert.False(gate.Task.IsCompleted);
        Assert.Equal(["    public void Place(Order order)", "\n    {"], early.Select(c => c.Text));
        Assert.DoesNotContain(early, c => c.IsFinal || c.ResetText);

        gate.SetResult();
        var all = new List<InlineEditChunk>(early);
        while (await stream.MoveNextAsync())
        {
            all.Add(stream.Current);
        }

        Assert.Equal(Extracted, Assemble(all));
        Assert.DoesNotContain(all, c => c.ResetText);
    }

    [Fact]
    public async Task APreviewOfFencesAndNumbers_IsShownWithoutThem()
    {
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var reply = "```csharp\n" + EditText.NumberLines(Extracted, 7) + "\n```\n";
        var head = "```csharp\n" + string.Concat(EditText.NumberLines(Extracted, 7).Split('\n').Take(2).Select(l => l + "\n"));
        var gateway = new ScriptedGateway().Reply(reply, gate, gateAfterPieces: ScriptedGateway.Pieces(head).Count);

        await using var stream = WholeEngine(gateway).RunAsync(Request(), CancellationToken.None).GetAsyncEnumerator();
        var early = new List<InlineEditChunk>();
        for (var i = 0; i < 2; i++)
        {
            Assert.True(await stream.MoveNextAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10)));
            early.Add(stream.Current);
        }

        Assert.Equal(["    public void Place(Order order)", "\n    {"], early.Select(c => c.Text));
        gate.SetResult();
        var all = new List<InlineEditChunk>(early);
        while (await stream.MoveNextAsync())
        {
            all.Add(stream.Current);
        }

        Assert.Equal(Extracted, Assemble(all));
        Assert.DoesNotContain(all, c => c.ResetText);
    }

    [Fact]
    public async Task NoChange_IsReportedWithAWarning()
    {
        var gateway = new ScriptedGateway().Reply(Scope);

        var chunks = await CollectAsync(WholeEngine(gateway).RunAsync(Request(), CancellationToken.None));

        Assert.Equal(Scope, Assemble(chunks));
        Assert.Equal("The model proposed no change.", chunks[^1].Warning);
    }

    [Fact]
    public async Task Doc_AddingCommentsOnly_IsAccepted()
    {
        var documented = "    /// <summary>\n    /// Places an order.\n    /// </summary>\n" + Scope;
        var gateway = new ScriptedGateway().Reply(documented);

        var chunks = await CollectAsync(WholeEngine(gateway).RunAsync(Request(string.Empty, InlineEditCommand.Doc), CancellationToken.None));

        Assert.Equal(documented, Assemble(chunks));
        Assert.Single(gateway.Calls);
    }

    [Fact]
    public async Task Doc_ThatChangesCode_IsSentBack_WithTheWholeScopeCorrection()
    {
        var changesCode = "    /// <summary>Places an order.</summary>\n" + Scope.Replace("public void Place", "public Order Place", StringComparison.Ordinal);
        var commentsOnly = "    /// <summary>Places an order.</summary>\n" + Scope;
        var gateway = new ScriptedGateway().Reply(changesCode).Reply(commentsOnly);

        var chunks = await CollectAsync(WholeEngine(gateway).RunAsync(Request(string.Empty, InlineEditCommand.Doc), CancellationToken.None));

        Assert.Equal(commentsOnly, Assemble(chunks));
        var calls = gateway.Calls.ToArray();
        Assert.Equal(2, calls.Length);
        var correction = calls[1].Request.Messages[^1].Content;
        Assert.Contains("Your rewrite changed code, not only documentation comments: 'public Order Place(Order order)'", correction, StringComparison.Ordinal);
        Assert.Equal(changesCode, calls[1].Request.Messages[^2].Content);
        Assert.Contains(WholeSystem, calls[1].Request.Messages[0].Content, StringComparison.Ordinal);
        Assert.Equal(0, chunks[^1].RejectedBlocks);
    }

    [Fact]
    public async Task AnEmptyReply_IsSentBackOnce()
    {
        var gateway = new ScriptedGateway().Reply(string.Empty).Reply(Extracted);

        var chunks = await CollectAsync(WholeEngine(gateway).RunAsync(Request(), CancellationToken.None));

        Assert.Equal(Extracted, Assemble(chunks));
        var calls = gateway.Calls.ToArray();
        Assert.Equal(2, calls.Length);
        Assert.Contains("Your reply has no code", calls[1].Request.Messages[^1].Content, StringComparison.Ordinal);
        Assert.Contains(WholeSystem, calls[1].Request.Messages[0].Content, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ABrokenRewrite_IsSentBackOnce_WithTheErrors_AndTheFixIsUsed()
    {
        var broken = Scope.Replace("_store.Add(order);", "_store.Add(order)", StringComparison.Ordinal);
        var fixedUp = Scope.Replace("_store.Add(order);", "_store.Add(order);\n        _log.Placed(order);", StringComparison.Ordinal);
        var gateway = new ScriptedGateway().Reply(broken).Reply(fixedUp);

        var chunks = await CollectAsync(WholeEngine(gateway).RunAsync(Request("log the order"), CancellationToken.None));

        Assert.Equal(fixedUp, Assemble(chunks));
        Assert.Null(chunks[^1].Warning);
        var correction = gateway.Calls.ToArray()[1].Request.Messages[^1].Content;
        Assert.Contains("Your rewrite introduced syntax errors", correction, StringComparison.Ordinal);
        Assert.Contains("- line 14: CS1002: ; expected", correction, StringComparison.Ordinal);
        Assert.Contains("14|         _store.Add(order)\n", correction, StringComparison.Ordinal);
        Assert.Contains("complete corrected code", correction, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ARewriteThatStillBreaksSyntax_IsReturnedWithAWarning_AfterOneRetry()
    {
        var broken = Scope.Replace("_store.Add(order);", "_store.Add(order)", StringComparison.Ordinal);
        var gateway = new ScriptedGateway().Reply(broken).Reply(broken);

        var chunks = await CollectAsync(WholeEngine(gateway).RunAsync(Request("log the order"), CancellationToken.None));

        Assert.Equal(2, gateway.Calls.Count);
        Assert.Equal(broken, Assemble(chunks));
        Assert.StartsWith("The proposal has 1 more syntax error(s) than the original code, for example line 14: CS1002", chunks[^1].Warning, StringComparison.Ordinal);
    }

    [Fact]
    public async Task VisualBasic_IsSyntaxChecked_InWholeScopeMode()
    {
        var request = Request("add zero");
        request.LanguageId = "vb";
        request.TextBefore = "Public Class Calc\n";
        request.ScopeText = "    Public Function Add(a As Integer, b As Integer) As Integer\n        Return a + b\n    End Function\n";
        request.TextAfter = "End Class\n";
        var gateway = new ScriptedGateway()
            .Reply(request.ScopeText.Replace("a + b", "(a + b", StringComparison.Ordinal))
            .Reply(request.ScopeText.Replace("a + b", "a + b + 0", StringComparison.Ordinal));

        var chunks = await CollectAsync(WholeEngine(gateway).RunAsync(request, CancellationToken.None));

        Assert.Equal(2, gateway.Calls.Count);
        Assert.Contains("Return a + b + 0", Assemble(chunks), StringComparison.Ordinal);
        Assert.Null(chunks[^1].Warning);
    }

    [Fact]
    public async Task OtherLanguages_AreNotSyntaxChecked_InWholeScopeMode()
    {
        var gateway = new ScriptedGateway().Reply("x = (\n");
        var request = Request("break it");
        request.LanguageId = "python";
        request.ScopeText = "x = 1\n";
        request.TextBefore = string.Empty;
        request.TextAfter = string.Empty;

        var chunks = await CollectAsync(WholeEngine(gateway).RunAsync(request, CancellationToken.None));

        Assert.Single(gateway.Calls);
        Assert.Equal("x = (\n", Assemble(chunks));
    }

    [Fact]
    public async Task AnInventedPlaceholder_IsSentBack_AndTheCleanRewriteIsUsed()
    {
        var invented = Extracted.Replace("nameof(order)", "\"<redacted:aws-access-key#9>\"", StringComparison.Ordinal);
        var gateway = new ScriptedGateway().Reply(invented).Reply(Extracted);

        var chunks = await CollectAsync(WholeEngine(gateway).RunAsync(Request(), CancellationToken.None));

        Assert.Equal(Extracted, Assemble(chunks));
        var correction = gateway.Calls.ToArray()[1].Request.Messages[^1].Content;
        Assert.Contains("Your rewrite contains <redacted:aws-access-key#9>", correction, StringComparison.Ordinal);
        Assert.DoesNotContain(chunks, c => c.Text.Contains("redacted", StringComparison.Ordinal));
    }

    [Fact]
    public async Task APlaceholderThatIsReallyInTheCode_IsNotAnError()
    {
        var scope = "    var key = \"<redacted:aws-access-key#1>\";\n";
        var request = Request("rename");
        request.ScopeText = scope;
        request.TextBefore = string.Empty;
        request.TextAfter = string.Empty;
        request.LanguageId = "python";
        var gateway = new ScriptedGateway().Reply(scope.Replace("var key", "var secret", StringComparison.Ordinal));

        var chunks = await CollectAsync(WholeEngine(gateway).RunAsync(request, CancellationToken.None));

        Assert.Single(gateway.Calls);
        Assert.Equal("    var secret = \"<redacted:aws-access-key#1>\";\n", Assemble(chunks));
    }

    [Fact]
    public async Task UnusableWholeScopeReplies_FallBackToBlocks_AfterOneRetry()
    {
        var gateway = new ScriptedGateway().Reply(string.Empty).Reply(string.Empty).Reply(ExtractReply);

        var chunks = await CollectAsync(WholeEngine(gateway).RunAsync(Request(), CancellationToken.None));

        Assert.Equal(Extracted, Assemble(chunks));
        var calls = gateway.Calls.ToArray();
        Assert.Equal(3, calls.Length);
        Assert.Contains(WholeSystem, calls[0].Request.Messages[0].Content, StringComparison.Ordinal);
        Assert.Contains(WholeSystem, calls[1].Request.Messages[0].Content, StringComparison.Ordinal);

        // The fallback starts the conversation again in the block format.
        var third = calls[2].Request.Messages;
        Assert.Equal(2, third.Count);
        Assert.Contains(BlockSystem, third[0].Content, StringComparison.Ordinal);
        Assert.DoesNotContain("Reply with only the complete", third[1].Content, StringComparison.Ordinal);
        Assert.Equal(0, chunks[^1].RejectedBlocks);
        Assert.Equal("Moved the null check into Validate.", chunks[^1].Explanation);
        Assert.Equal(300, chunks[^1].PromptTokens);
    }

    [Fact]
    public async Task ABlockFormatReply_ToAWholeScopePrompt_IsUnusable_ThenTheBlocksAreUsed()
    {
        var gateway = new ScriptedGateway().Reply(ExtractReply).Reply(ExtractReply).Reply(ExtractReply);

        var chunks = await CollectAsync(WholeEngine(gateway).RunAsync(Request(), CancellationToken.None));

        Assert.Equal(Extracted, Assemble(chunks));
        var calls = gateway.Calls.ToArray();
        Assert.Equal(3, calls.Length);
        Assert.Contains("Your reply has no code", calls[1].Request.Messages[^1].Content, StringComparison.Ordinal);
        Assert.Contains(WholeSystem, calls[1].Request.Messages[0].Content, StringComparison.Ordinal);
        Assert.Contains(BlockSystem, calls[2].Request.Messages[0].Content, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheFallback_HasOneRetryInBlocks_AndCountsRejectedBlocks()
    {
        var gateway = new ScriptedGateway().Reply(string.Empty).Reply(string.Empty).Reply(BadBlockReply).Reply(ExtractReply);

        var chunks = await CollectAsync(WholeEngine(gateway).RunAsync(Request(), CancellationToken.None));

        Assert.Equal(Extracted, Assemble(chunks));
        Assert.Equal(4, gateway.Calls.Count);
        Assert.Equal(1, chunks[^1].RejectedBlocks);
    }

    [Fact]
    public async Task TheFallback_FailsAfterItsOneRetry_WithTheTotalAttempts()
    {
        var gateway = new ScriptedGateway().Reply(string.Empty).Reply(string.Empty).Reply(BadBlockReply).Reply(BadBlockReply);

        var ex = await Assert.ThrowsAsync<PillionRpcException>(() => CollectAsync(WholeEngine(gateway).RunAsync(Request(), CancellationToken.None)));

        Assert.Equal(4, gateway.Calls.Count);
        Assert.Equal(PillionErrorCode.ProviderUnavailable, ex.Error.Code);
        Assert.StartsWith("The model's edit could not be used after 4 attempts: Block 1: its SEARCH lines do not match the code", ex.Error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnInventedPlaceholder_ThatKeepsComingBack_FailsWithPolicyDenied_AfterTheFallback()
    {
        var invented = Extracted.Replace("nameof(order)", "\"<redacted:aws-access-key#9>\"", StringComparison.Ordinal);
        var inventedBlocks = "<<<<<<< SEARCH\n        _store.Add(order);\n=======\n        _store.Add(\"<redacted:aws-access-key#9>\");\n>>>>>>> REPLACE\n";
        var gateway = new ScriptedGateway().Reply(invented).Reply(invented).Reply(inventedBlocks).Reply(inventedBlocks);

        var ex = await Assert.ThrowsAsync<PillionRpcException>(() => CollectAsync(WholeEngine(gateway).RunAsync(Request(), CancellationToken.None)));

        Assert.Equal(PillionErrorCode.PolicyDenied, ex.Error.Code);
        Assert.Equal(4, gateway.Calls.Count);
    }

    [Fact]
    public async Task APreviewOfAnUnusableReply_IsResetByTheFallbackResult()
    {
        // The first reply streams a line of code and then turns into a block; the preview must not survive.
        var gateway = new ScriptedGateway().Reply("    public void Wrong(Order order)\n<<<<<<< SEARCH\n    x\n").Reply(string.Empty).Reply(ExtractReply);

        var chunks = await CollectAsync(WholeEngine(gateway).RunAsync(Request(), CancellationToken.None));

        Assert.Equal(Extracted, Assemble(chunks));
        Assert.Equal(3, gateway.Calls.Count);
        Assert.Equal("    public void Wrong(Order order)", chunks[0].Text);
        Assert.Contains(chunks, c => c.ResetText && c.Text == Extracted);
    }

    [Fact]
    public async Task Refinement_UsesWholeScopeMode_OnThePreviousProposal()
    {
        var conversations = new InlineEditConversations();
        var first = new ScriptedGateway().Reply(Extracted);
        var request = Request();
        request.ConversationId = "conv-1";
        await CollectAsync(WholeEngine(first, conversations).RunAsync(request, CancellationToken.None));

        var renamed = Extracted.Replace("Validate", "EnsureValid", StringComparison.Ordinal);
        var refine = new ScriptedGateway().Reply(renamed);
        var followUp = Request("rename it to EnsureValid");
        followUp.ConversationId = "conv-1";
        followUp.PreviousProposal = Extracted;

        var chunks = await CollectAsync(WholeEngine(refine, conversations).RunAsync(followUp, CancellationToken.None));

        Assert.Equal(renamed, Assemble(chunks));
        var messages = Assert.Single(refine.Calls).Request.Messages;
        Assert.Contains(WholeSystem, messages[0].Content, StringComparison.Ordinal);
        Assert.Contains("your earlier proposal for lines 7 to 19", messages[1].Content, StringComparison.Ordinal);
        Assert.Contains("1. extract the validation into a method", messages[1].Content, StringComparison.Ordinal);
        Assert.EndsWith("Reply with only the complete revised code (your earlier proposal with the change made): plain code, no Markdown fence, no line numbers, no explanation.", messages[1].Content, StringComparison.Ordinal);
        Assert.Equal(0, chunks[^1].RejectedBlocks);
    }

    [Fact]
    public async Task Refinement_OfALongProposal_UsesBlocks()
    {
        // The scope is small but the proposal being refined has grown past the limit.
        var request = Request("rename it");
        request.PreviousProposal = Wide(12);
        var gateway = new ScriptedGateway().Reply("<<<<<<< SEARCH\n        Step0();\n=======\n        First();\n>>>>>>> REPLACE\n");

        await CollectAsync(Engine(gateway, wholeScopeMaxLines: 10).RunAsync(request, CancellationToken.None));

        Assert.Contains(BlockSystem, Assert.Single(gateway.Calls).Request.Messages[0].Content, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheSetting_IsReadForEveryRequest()
    {
        var limit = "1";
        var asked = new List<string>();
        var gateway = new ScriptedGateway().Reply(ExtractReply).Reply(Extracted);
        var engine = new InlineEditEngine(
            gateway,
            new InlineEditPromptBuilder(),
            RoslynSyntaxValidator.Instance,
            new InlineEditConversations(),
            options: new InlineEditOptions(key =>
            {
                asked.Add(key);
                return limit;
            }));

        await CollectAsync(engine.RunAsync(Request(), CancellationToken.None));
        limit = "100";
        await CollectAsync(engine.RunAsync(Request(), CancellationToken.None));

        var calls = gateway.Calls.ToArray();
        Assert.Contains(BlockSystem, calls[0].Request.Messages[0].Content, StringComparison.Ordinal);
        Assert.Contains(WholeSystem, calls[1].Request.Messages[0].Content, StringComparison.Ordinal);
        Assert.All(asked, key => Assert.Equal("inlineEdit.wholeScopeMaxLines", key));
    }

    [Fact]
    public async Task TheMode_IsLogged()
    {
        var log = new ListLogger();
        var gateway = new ScriptedGateway().Reply(Extracted);
        var engine = new InlineEditEngine(gateway, new InlineEditPromptBuilder(), RoslynSyntaxValidator.Instance, new InlineEditConversations(), log, new InlineEditOptions());

        await CollectAsync(engine.RunAsync(Request(), CancellationToken.None));

        Assert.Contains(log.Messages, m => m.Contains("whole-scope mode (9 line(s) to edit, whole-scope limit 80)", StringComparison.Ordinal));
        Assert.Contains(log.Messages, m => m.Contains("finished", StringComparison.Ordinal) && m.Contains("mode whole-scope", StringComparison.Ordinal));
    }

    [Fact]
    public async Task TheFallback_IsLogged()
    {
        var log = new ListLogger();
        var gateway = new ScriptedGateway().Reply(string.Empty).Reply(string.Empty).Reply(ExtractReply);
        var engine = new InlineEditEngine(gateway, new InlineEditPromptBuilder(), RoslynSyntaxValidator.Instance, new InlineEditConversations(), log, new InlineEditOptions());

        await CollectAsync(engine.RunAsync(Request(), CancellationToken.None));

        Assert.Contains(log.Messages, m => m.Contains("falling back to edit blocks", StringComparison.Ordinal));
        Assert.Contains(log.Messages, m => m.Contains("finished", StringComparison.Ordinal) && m.Contains("mode blocks (after whole-scope)", StringComparison.Ordinal));
    }

    [Fact]
    public void TheOptions_DefaultTo80Lines_AndReadTheSetting()
    {
        Assert.Equal(80, new InlineEditOptions().WholeScopeMaxLines);
        Assert.Equal(120, new InlineEditOptions(_ => "120").WholeScopeMaxLines);
        Assert.Equal(0, new InlineEditOptions(_ => "0").WholeScopeMaxLines);
        Assert.Equal(80, new InlineEditOptions(_ => null).WholeScopeMaxLines);
        Assert.Equal(40, new InlineEditOptions { WholeScopeMaxLines = 40 }.WholeScopeMaxLines);
    }

    [Theory]
    [InlineData("-5")]
    [InlineData("\"80\"")]
    [InlineData("1.5")]
    [InlineData("true")]
    [InlineData("null")]
    [InlineData("99999999999")]
    [InlineData("")]
    public void ASettingThatIsNotAWholeNumber_IsIgnored(string json)
    {
        Assert.Equal(80, new InlineEditOptions(_ => json).WholeScopeMaxLines);
    }

    [Fact]
    public void AnUnreadableSettingsFile_MeansTheDefault()
    {
        var options = new InlineEditOptions(_ => throw new SettingsValidationException("broken"));

        Assert.Equal(80, options.WholeScopeMaxLines);
    }

    [Fact]
    public void ANegativeLimit_IsRejected()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new InlineEditOptions { WholeScopeMaxLines = -1 });
    }

    private sealed class ListLogger : ILogger<InlineEditEngine>
    {
        public List<string> Messages { get; } = [];

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            Messages.Add(formatter(state, exception));
    }
}

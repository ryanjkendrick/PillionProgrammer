using Pillion.Contracts;
using Pillion.Host.InlineEdit;
using static Pillion.Host.Tests.InlineEdit.InlineEditTestSupport;

namespace Pillion.Host.Tests.InlineEdit;

public sealed class InlineEditEngineTests
{
    [Fact]
    public async Task AReplyAsQwenCoderWritesIt_WithLineNumbersAndAClosingDivider_IsUsed()
    {
        // Recorded from qwen2.5-coder:7b for /doc: fenced, numbered lines, closed with '=======', then prose.
        var reply =
            "```csharp\n<<<<<<< SEARCH\n" +
            " 7|     public void Place(Order order)\n 8|     {\n 9|         if (order is null)\n10|         {\n" +
            "11|             throw new ArgumentNullException(nameof(order));\n12|         }\n13| \n14|         _store.Add(order);\n15|     }\n" +
            "=======\n" +
            " 7|     /// <summary>\n 8|     /// Places an order.\n 9|     /// </summary>\n10|     /// <param name=\"order\">The order to place.</param>\n" +
            "11|     public void Place(Order order)\n12|     {\n13|         if (order is null)\n14|         {\n" +
            "15|             throw new ArgumentNullException(nameof(order));\n16|         }\n17| \n18|         _store.Add(order);\n19|     }\n" +
            "=======\n```\n\nAdded XML documentation comments to the `Place` method.";
        var gateway = new ScriptedGateway().Reply(reply);

        var chunks = await CollectAsync(Engine(gateway).RunAsync(Request(string.Empty, InlineEditCommand.Doc), CancellationToken.None));

        Assert.Equal(
            "    /// <summary>\n    /// Places an order.\n    /// </summary>\n    /// <param name=\"order\">The order to place.</param>\n" + Scope,
            Assemble(chunks));
        Assert.Equal("Added XML documentation comments to the `Place` method.", chunks[^1].Explanation);
    }

    [Fact]
    public async Task AGoodReply_StreamsTheRewrittenScope_NotTheBlocks()
    {
        var gateway = new ScriptedGateway().Reply(ExtractReply);
        var request = Request();

        var chunks = await CollectAsync(Engine(gateway).RunAsync(request, CancellationToken.None));

        Assert.Equal(Extracted, Assemble(chunks));
        Assert.DoesNotContain(chunks, c => c.Text.Contains("SEARCH", StringComparison.Ordinal));
        var final = chunks[^1];
        Assert.True(final.IsFinal);
        Assert.Equal(string.Empty, final.Text);
        Assert.Equal("Moved the null check into Validate.", final.Explanation);
        Assert.Equal("fake-coder", final.ModelId);
        Assert.Equal("fake", final.ProviderId);
        Assert.Equal(100, final.PromptTokens);
        Assert.Equal(10, final.CompletionTokens);
        Assert.Equal(0, final.RejectedBlocks);
        Assert.Null(final.Warning);
        Assert.Null(final.Error);
        Assert.All(chunks, c => Assert.Equal(request.RequestId, c.RequestId));

        var (chat, taskClass) = Assert.Single(gateway.Calls);
        Assert.Equal(TaskClass.InlineEdit, taskClass);
        Assert.Equal(request.RequestId, chat.RequestId);
        Assert.Equal(Profile.Auto, chat.Profile);
        Assert.Null(chat.ModelId);
    }

    [Fact]
    public async Task TheRequestsOverride_ReachesTheGateway()
    {
        var gateway = new ScriptedGateway().Reply(ExtractReply);
        var request = Request();
        request.ModelId = "ollama/qwen2.5-coder:7b";
        request.Profile = Profile.LocalStrong;

        await CollectAsync(Engine(gateway).RunAsync(request, CancellationToken.None));

        var (chat, _) = Assert.Single(gateway.Calls);
        Assert.Equal("ollama/qwen2.5-coder:7b", chat.ModelId);
        Assert.Equal(Profile.LocalStrong, chat.Profile);
    }

    [Fact]
    public async Task ABlockThatDoesNotMatch_IsBouncedBack_AndTheSecondAnswerIsUsed()
    {
        var gateway = new ScriptedGateway().Reply(BadBlockReply).Reply(ExtractReply);

        var chunks = await CollectAsync(Engine(gateway).RunAsync(Request(), CancellationToken.None));

        Assert.Equal(Extracted, Assemble(chunks));
        Assert.Equal(1, chunks[^1].RejectedBlocks);
        Assert.Equal(200, chunks[^1].PromptTokens);
        var calls = gateway.Calls.ToArray();
        Assert.Equal(2, calls.Length);
        var second = calls[1].Request.Messages;
        Assert.Equal(4, second.Count);
        Assert.Equal(ChatRole.Assistant, second[2].Role);
        Assert.Equal(BadBlockReply, second[2].Content);
        Assert.Equal(ChatRole.User, second[3].Role);
        Assert.Contains("Block 1: its SEARCH lines do not match the code", second[3].Content, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnAmbiguousBlock_IsBouncedBack_WithTheNumberOfMatches()
    {
        var ambiguous = "<<<<<<< SEARCH\n}\n=======\n} // done\n>>>>>>> REPLACE\n";
        var gateway = new ScriptedGateway().Reply(ambiguous).Reply(ExtractReply);

        await CollectAsync(Engine(gateway).RunAsync(Request(), CancellationToken.None));

        Assert.Contains("match 2 places", gateway.Calls.ToArray()[1].Request.Messages[^1].Content, StringComparison.Ordinal);
    }

    [Fact]
    public async Task MalformedBlocks_AndCodeWithoutBlocks_AreBouncedBack()
    {
        var gateway = new ScriptedGateway()
            .Reply("<<<<<<< SEARCH\n        _store.Add(order);\n>>>>>>> REPLACE\n")
            .Reply("```csharp\n" + Extracted + "```\n")
            .Reply(ExtractReply);

        var chunks = await CollectAsync(Engine(gateway).RunAsync(Request(), CancellationToken.None));

        Assert.Equal(Extracted, Assemble(chunks));
        var calls = gateway.Calls.ToArray();
        Assert.Contains("not in the required format", calls[1].Request.Messages[^1].Content, StringComparison.Ordinal);
        Assert.Contains("contains code but no edit blocks", calls[2].Request.Messages[^1].Content, StringComparison.Ordinal);
        Assert.Equal(1, chunks[^1].RejectedBlocks);
    }

    [Fact]
    public async Task ThreeUnusableReplies_FailTheRequest_WithAClearError()
    {
        var gateway = new ScriptedGateway().Reply(BadBlockReply).Reply(BadBlockReply).Reply(BadBlockReply);

        var ex = await Assert.ThrowsAsync<PillionRpcException>(() => CollectAsync(Engine(gateway).RunAsync(Request(), CancellationToken.None)));

        Assert.Equal(3, gateway.Calls.Count);
        Assert.Equal(PillionErrorCode.ProviderUnavailable, ex.Error.Code);
        Assert.StartsWith("The model's edit could not be used after 3 attempts: Block 1: its SEARCH lines do not match the code", ex.Error.Message, StringComparison.Ordinal);
        Assert.NotNull(ex.Error.RetryHint);
    }

    [Fact]
    public async Task AnEditThatBreaksCSharpSyntax_IsBouncedOnce_WithTheErrors()
    {
        var gateway = new ScriptedGateway().Reply(MissingSemicolonReply).Reply(
            "<<<<<<< SEARCH\n        _store.Add(order)\n=======\n        _store.Add(order);\n        _log.Placed(order);\n>>>>>>> REPLACE\n");

        var chunks = await CollectAsync(Engine(gateway).RunAsync(Request("log the order"), CancellationToken.None));

        Assert.Equal(Scope.Replace("        _store.Add(order);\n", "        _store.Add(order);\n        _log.Placed(order);\n", StringComparison.Ordinal), Assemble(chunks));
        Assert.Null(chunks[^1].Warning);
        var correction = gateway.Calls.ToArray()[1].Request.Messages[^1].Content;
        Assert.Contains("Your edit introduced syntax errors", correction, StringComparison.Ordinal);
        Assert.Contains("- line 14: CS1002: ; expected", correction, StringComparison.Ordinal);
        Assert.Contains("14|         _store.Add(order)\n", correction, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnEditThatStillBreaksSyntax_IsReturned_WithAWarning_AfterOneRetry()
    {
        var gateway = new ScriptedGateway().Reply(MissingSemicolonReply).Reply("No change is needed.");

        var chunks = await CollectAsync(Engine(gateway).RunAsync(Request("log the order"), CancellationToken.None));

        Assert.Equal(2, gateway.Calls.Count);
        Assert.Equal(Scope.Replace("_store.Add(order);", "_store.Add(order)", StringComparison.Ordinal), Assemble(chunks));
        Assert.StartsWith("The proposal has 1 more syntax error(s) than the original code, for example line 14: CS1002", chunks[^1].Warning, StringComparison.Ordinal);
    }

    [Fact]
    public async Task OtherLanguages_AreNotSyntaxChecked()
    {
        var gateway = new ScriptedGateway().Reply("<<<<<<< SEARCH\nx = 1\n=======\nx = (\n>>>>>>> REPLACE\n");
        var request = Request("break it");
        request.LanguageId = "python";
        request.ScopeText = "x = 1\n";
        request.TextBefore = string.Empty;
        request.TextAfter = string.Empty;

        var chunks = await CollectAsync(Engine(gateway).RunAsync(request, CancellationToken.None));

        Assert.Single(gateway.Calls);
        Assert.Equal("x = (\n", Assemble(chunks));
    }

    [Fact]
    public async Task VisualBasic_IsSyntaxChecked()
    {
        var gateway = new ScriptedGateway()
            .Reply("<<<<<<< SEARCH\n        Return a + b\n=======\n        Return (a + b\n>>>>>>> REPLACE\n")
            .Reply("<<<<<<< SEARCH\n        Return (a + b\n=======\n        Return a + b + 0\n>>>>>>> REPLACE\n");
        var request = Request("add zero");
        request.LanguageId = "vb";
        request.TextBefore = "Public Class Calc\n";
        request.ScopeText = "    Public Function Add(a As Integer, b As Integer) As Integer\n        Return a + b\n    End Function\n";
        request.TextAfter = "End Class\n";

        var chunks = await CollectAsync(Engine(gateway).RunAsync(request, CancellationToken.None));

        Assert.Equal(2, gateway.Calls.Count);
        Assert.Contains("Return a + b + 0", Assemble(chunks), StringComparison.Ordinal);
        Assert.Null(chunks[^1].Warning);
    }

    [Fact]
    public async Task CrlfCode_GetsACrlfProposal()
    {
        var gateway = new ScriptedGateway().Reply(ExtractReply.Replace("\n", "\r\n", StringComparison.Ordinal));
        var request = Request();
        request.ScopeText = Scope.Replace("\n", "\r\n", StringComparison.Ordinal);
        request.TextBefore = Before.Replace("\n", "\r\n", StringComparison.Ordinal);
        request.TextAfter = After.Replace("\n", "\r\n", StringComparison.Ordinal);

        var chunks = await CollectAsync(Engine(gateway).RunAsync(request, CancellationToken.None));

        Assert.Equal(Extracted.Replace("\n", "\r\n", StringComparison.Ordinal), Assemble(chunks));
    }

    [Fact]
    public async Task TheFirstLines_ArriveBeforeTheModelHasFinished()
    {
        // Hold the model's stream back after the first two replacement lines.
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var gateway = new ScriptedGateway().Reply(ExtractReply, gate, gateAfterPieces: IndexAfter(ExtractReply, "=======\n", 2));

        await using var stream = Engine(gateway).RunAsync(Request(), CancellationToken.None).GetAsyncEnumerator();
        var early = new List<InlineEditChunk>();
        for (var i = 0; i < 3; i++)
        {
            Assert.True(await stream.MoveNextAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10)));
            early.Add(stream.Current);
        }

        // The code before the edit as soon as SEARCH matched, then each replacement line as it completes.
        Assert.False(gate.Task.IsCompleted);
        Assert.Equal(
            ["    public void Place(Order order)\n    {", "\n        Validate(order);", "\n        _store.Add(order);"],
            early.Select(c => c.Text));
        Assert.DoesNotContain(early, c => c.IsFinal);

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
    public async Task APreviewThatALaterBlockInvalidates_IsReset()
    {
        // The second block edits a line above the first, so the early text is wrong and the final text resets it.
        var reply =
            "<<<<<<< SEARCH\n        _store.Add(order);\n=======\n        _store.Add(order);\n        _log.Placed(order);\n>>>>>>> REPLACE\n" +
            "<<<<<<< SEARCH\n    public void Place(Order order)\n=======\n    public void Place(Order order, ILog log)\n>>>>>>> REPLACE\n";
        var gateway = new ScriptedGateway().Reply(reply);

        var chunks = await CollectAsync(Engine(gateway).RunAsync(Request("log it"), CancellationToken.None));

        var expected = Scope
            .Replace("Place(Order order)", "Place(Order order, ILog log)", StringComparison.Ordinal)
            .Replace("        _store.Add(order);\n", "        _store.Add(order);\n        _log.Placed(order);\n", StringComparison.Ordinal);
        Assert.Equal(expected, Assemble(chunks));
        Assert.True(chunks.Count(c => c.Text.Length > 0) >= 2);
        Assert.Contains(chunks, c => c.ResetText && c.Text == expected);
    }

    [Fact]
    public async Task AFailureAfterPreviewText_StillFailsTheCall()
    {
        // Each reply previews its first (good) block, then has a block that never matches.
        var reply =
            "<<<<<<< SEARCH\n        _store.Add(order);\n=======\n        _store.Add(order);\n        _log.Placed(order);\n>>>>>>> REPLACE\n" +
            "<<<<<<< SEARCH\n        nothing();\n=======\n        else();\n>>>>>>> REPLACE\n";
        var gateway = new ScriptedGateway().Reply(reply).Reply(reply).Reply(reply);
        var received = new List<InlineEditChunk>();

        var ex = await Assert.ThrowsAsync<PillionRpcException>(async () =>
        {
            await foreach (var chunk in Engine(gateway).RunAsync(Request("log it"), CancellationToken.None))
            {
                received.Add(chunk);
            }
        });

        Assert.NotEmpty(received);
        Assert.DoesNotContain(received, c => c.IsFinal);
        Assert.Equal(PillionErrorCode.ProviderUnavailable, ex.Error.Code);
        Assert.Contains("Block 2: its SEARCH lines do not match", ex.Error.Message, StringComparison.Ordinal);
        Assert.Equal(3, gateway.Calls.Count);
    }

    [Fact]
    public async Task AGatewayFailure_BeforeAnyText_FailsTheCall()
    {
        var gateway = new ScriptedGateway().Fail(new PillionError { Code = PillionErrorCode.ModelNotInstalled, Message = "No model." });

        var ex = await Assert.ThrowsAsync<PillionRpcException>(() => CollectAsync(Engine(gateway).RunAsync(Request(), CancellationToken.None)));

        Assert.Equal(PillionErrorCode.ModelNotInstalled, ex.Error.Code);
    }

    [Fact]
    public async Task AStreamThatBreaksOff_FailsTheCall()
    {
        var gateway = new ScriptedGateway().FailAfterText("<<<<<<< SEARCH\n", new PillionError { Code = PillionErrorCode.ProviderUnavailable, Message = "Connection lost." });

        var ex = await Assert.ThrowsAsync<PillionRpcException>(() => CollectAsync(Engine(gateway).RunAsync(Request(), CancellationToken.None)));

        Assert.Equal("Connection lost.", ex.Error.Message);
    }

    [Fact]
    public async Task Refinement_EditsThePreviousProposal_AndShowsTheEarlierInstructions()
    {
        var conversations = new InlineEditConversations();
        var first = new ScriptedGateway().Reply(ExtractReply);
        var request = Request();
        request.ConversationId = "conv-1";
        await CollectAsync(Engine(first, conversations).RunAsync(request, CancellationToken.None));

        var refine = new ScriptedGateway().Reply(
            "<<<<<<< SEARCH\n    private static void Validate(Order order)\n=======\n    private static void EnsureValid(Order order)\n>>>>>>> REPLACE\n" +
            "<<<<<<< SEARCH\n        Validate(order);\n=======\n        EnsureValid(order);\n>>>>>>> REPLACE\n");
        var followUp = Request("rename it to EnsureValid");
        followUp.ConversationId = "conv-1";
        followUp.PreviousProposal = Extracted;

        var chunks = await CollectAsync(Engine(refine, conversations).RunAsync(followUp, CancellationToken.None));

        Assert.Equal(Extracted.Replace("Validate(", "EnsureValid(", StringComparison.Ordinal), Assemble(chunks));
        var user = Assert.Single(refine.Calls).Request.Messages[1].Content;
        Assert.Contains("your earlier proposal for lines 7 to 19", user, StringComparison.Ordinal);
        Assert.Contains("1. extract the validation into a method", user, StringComparison.Ordinal);
        Assert.Contains("Instruction: rename it to EnsureValid", user, StringComparison.Ordinal);
        Assert.Equal(["extract the validation into a method", "rename it to EnsureValid"], conversations.Get("conv-1"));
    }

    [Fact]
    public async Task Explain_ReturnsProseOnly()
    {
        var gateway = new ScriptedGateway().Reply("It stores an order after checking it is not null.\n");

        var chunks = await CollectAsync(Engine(gateway).RunAsync(Request(string.Empty, InlineEditCommand.Explain), CancellationToken.None));

        var final = Assert.Single(chunks);
        Assert.True(final.IsFinal);
        Assert.Equal(string.Empty, final.Text);
        Assert.Equal("It stores an order after checking it is not null.", final.Explanation);
        Assert.Contains("Explain the code to edit", Assert.Single(gateway.Calls).Request.Messages[0].Content, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Test_ReturnsTheTestCode_AndWhereItBelongs()
    {
        var test = "[Fact]\npublic void Place_WithNull_Throws()\n{\n    Assert.Throws<ArgumentNullException>(() => new Orders().Place(null!));\n}";
        var gateway = new ScriptedGateway().Reply("```csharp\n" + test + "\n```\nAdd it to OrdersTests in Demo.Tests.");

        var chunks = await CollectAsync(Engine(gateway).RunAsync(Request(string.Empty, InlineEditCommand.Test), CancellationToken.None));

        Assert.Equal(test, Assemble(chunks));
        Assert.Equal("Add it to OrdersTests in Demo.Tests.", chunks[^1].Explanation);
        Assert.True(chunks.Count(c => c.Text.Length > 0) > 1, "The test code should stream as it arrives.");
    }

    [Fact]
    public async Task Test_WithoutACodeBlock_IsBouncedBack()
    {
        var gateway = new ScriptedGateway().Reply("Write a test that passes null.").Reply("```\n[Fact] public void T() { }\n```\nIn OrdersTests.");

        var chunks = await CollectAsync(Engine(gateway).RunAsync(Request(string.Empty, InlineEditCommand.Test), CancellationToken.None));

        Assert.Equal("[Fact] public void T() { }", Assemble(chunks));
        Assert.Contains("has no Markdown code block", gateway.Calls.ToArray()[1].Request.Messages[^1].Content, StringComparison.Ordinal);
    }

    [Fact]
    public async Task NoBlocksAndNoCode_MeansNoChange_WithAWarning()
    {
        var gateway = new ScriptedGateway().Reply("The method already validates its argument.");

        var chunks = await CollectAsync(Engine(gateway).RunAsync(Request(string.Empty, InlineEditCommand.Fix), CancellationToken.None));

        Assert.Equal(Scope, Assemble(chunks));
        Assert.Equal("The model proposed no change.", chunks[^1].Warning);
        Assert.Equal("The method already validates its argument.", chunks[^1].Explanation);
    }

    [Fact]
    public async Task Fix_SendsTheDiagnostics()
    {
        var gateway = new ScriptedGateway().Reply(ExtractReply);
        var request = Request(string.Empty, InlineEditCommand.Fix);
        request.Diagnostics = [new InlineEditDiagnostic { Line = 13, Severity = DiagnosticSeverity.Error, Code = "CS0103", Message = "The name '_store' does not exist in the current context" }];

        await CollectAsync(Engine(gateway).RunAsync(request, CancellationToken.None));

        Assert.Contains("- line 14: error CS0103: The name '_store' does not exist", Assert.Single(gateway.Calls).Request.Messages[1].Content, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Doc_ThatChangesCode_IsSentBack()
    {
        // Seen from qwen2.5-coder:7b: /doc also turned void into Order and added a return.
        var changesCode =
            "<<<<<<< SEARCH\n    public void Place(Order order)\n=======\n    /// <summary>Places an order.</summary>\n    public Order Place(Order order)\n>>>>>>> REPLACE\n";
        var commentsOnly =
            "<<<<<<< SEARCH\n    public void Place(Order order)\n=======\n    /// <summary>Places an order.</summary>\n    public void Place(Order order)\n>>>>>>> REPLACE\n";
        var gateway = new ScriptedGateway().Reply(changesCode).Reply(commentsOnly);

        var chunks = await CollectAsync(Engine(gateway).RunAsync(Request(string.Empty, InlineEditCommand.Doc), CancellationToken.None));

        Assert.Equal("    /// <summary>Places an order.</summary>\n" + Scope, Assemble(chunks));
        var correction = gateway.Calls.ToArray()[1].Request.Messages[^1].Content;
        Assert.Contains("changed code, not only documentation comments: 'public Order Place(Order order)'", correction, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ARequestWithoutInstructionOrCommand_IsRejected()
    {
        await Assert.ThrowsAsync<ArgumentException>(() => CollectAsync(Engine(new ScriptedGateway()).RunAsync(Request(" "), CancellationToken.None)));
    }

    [Fact]
    public async Task Cancellation_StopsTheRequest()
    {
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var gateway = new ScriptedGateway().Reply(ExtractReply, gate, gateAfterPieces: 2);
        using var cts = new CancellationTokenSource();

        var run = CollectAsync(Engine(gateway).RunAsync(Request(), cts.Token));
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run);
    }

    private static int IndexAfter(string reply, string marker, int lines)
    {
        // The piece just after the marker line, plus two pieces per further line.
        var pieces = ScriptedGateway.Pieces(reply);
        var text = string.Empty;
        for (var i = 0; i < pieces.Count; i++)
        {
            text += pieces[i];
            if (text.EndsWith(marker, StringComparison.Ordinal))
            {
                return i + 1 + (2 * lines);
            }
        }

        throw new InvalidOperationException("The marker is not in the reply.");
    }
}

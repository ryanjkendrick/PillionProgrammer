using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.Threading;
using Pillion.Contracts;
using Pillion.VisualStudio.Core.Autocomplete;
using Pillion.VisualStudio.Core.InlineChat;

namespace Pillion.VisualStudio.Tests;

// The tests await their own TaskCompletionSource tasks and the session's tasks; no main thread is involved.
#pragma warning disable VSTHRD003

/// <summary>The inline chat session against a scripted inline edit service (work items 1.6 and 1.7).</summary>
public sealed class InlineChatSessionTests : IDisposable
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    private const string Code =
        "class C\n" +
        "{\n" +
        "    int M(int x)\n" +
        "    {\n" +
        "        return x;\n" +
        "    }\n" +
        "}\n";

    // Created on a thread-pool thread so the context has no SynchronizationContext (see ChatViewModelTests).
    private readonly JoinableTaskContext _context = Task.Run(() => new JoinableTaskContext()).Result;
    private readonly ScriptedInlineEditService _edits = new();
    private readonly ScriptedModelService _models = new();
    private readonly InMemorySettingsService _settings = TestModels.Settings();
    private readonly RecordingInlineChatEditor _editor = new();
    private readonly InlineChatHistory _history = new();
    private readonly FakeHostConnection _connection;
    private readonly InlineChatDocument _document;
    private readonly List<string> _proposals = [];

    public InlineChatSessionTests()
    {
        _connection = new FakeHostConnection().With<IModelService>(_models).With<ISettingsService>(_settings).With<IInlineEditService>(_edits);
        var text = new StringDocumentText(Code);
        var caret = Code.IndexOf("return", StringComparison.Ordinal);
        var memberStart = Code.IndexOf("    int M", StringComparison.Ordinal);
        var memberEnd = Code.IndexOf("    }\n}", StringComparison.Ordinal) + 5;
        _document = new InlineChatDocument(@"C:\src\C.cs", "csharp", text, InlineScopeSelector.Select(text, caret, caret, caret, memberStart, memberEnd));
    }

    private string ScopeText => _document.ScopeText;

    public void Dispose() => _context.Dispose();

    [Fact]
    public async Task Send_StreamsAPreviewThenEnterAppliesItAsOneEdit()
    {
        using var session = await StartAsync();
        var script = _edits.Next();
        session.PromptText = "rename x to value";

        var send = session.SendAsync();
        var request = await script.Started.Task.WithTimeout(Timeout);
        Assert.Equal(InlineChatState.Streaming, session.State);
        Assert.True(session.ShowsDiff);
        Assert.Equal("Thinking...", session.StatusText);
        Assert.Equal(string.Empty, session.PromptText);

        script.Send("    int M(int value)\n");
        await Wait.UntilAsync(() => session.ProposalText.Length > 0, "the first line is shown");
        Assert.StartsWith("Thinking... (", session.StatusText, StringComparison.Ordinal);
        Assert.Contains("via Ollama, local", session.StatusText, StringComparison.Ordinal);
        Assert.False(session.CanAccept); // Only a validated final proposal can be applied.

        script.Send("    {\n        return value;\n    }");
        script.Final();
        await send.WithTimeout(Timeout);

        Assert.Equal(InlineChatState.Preview, session.State);
        Assert.Equal("    int M(int value)\n    {\n        return value;\n    }", session.ProposalText);
        Assert.True(session.CanAccept);
        Assert.NotNull(session.FirstLineTime);
        Assert.NotNull(session.TotalTime);
        Assert.Contains("Enter applies", session.StatusText, StringComparison.Ordinal);

        Assert.Equal("rename x to value", request.Instruction);
        Assert.Equal(InlineEditCommand.None, request.Command);
        Assert.Equal(ScopeText, request.ScopeText);
        Assert.Equal(Profile.Auto, request.Profile);
        Assert.Null(request.ModelId); // Auto is picked by default: the host routes by the InlineEdit task class.
        Assert.Equal(session.ConversationId, request.ConversationId);
        Assert.Null(request.PreviousProposal);
        Assert.Empty(request.Diagnostics);

        session.HandleEnter();

        var (expected, proposal) = Assert.Single(_editor.Replaced);
        Assert.Equal(ScopeText, expected);
        Assert.Equal("    int M(int value)\n    {\n        return value;\n    }", proposal);
        Assert.Equal(1, _editor.Closed);
        Assert.Equal(InlineChatState.Closed, session.State);
        Assert.Equal(["rename x to value"], _history.Prompts);
    }

    [Fact]
    public async Task Escape_DiscardsWithoutTouchingTheDocument()
    {
        using var session = await StartAsync();
        await CompleteAsync(session, "add logging", "    // logged\n" + ScopeText);

        session.HandleEscape();

        Assert.Empty(_editor.Replaced);
        Assert.Empty(_editor.Inserted);
        Assert.Equal(1, _editor.Closed);
        Assert.Equal(InlineChatState.Closed, session.State);
    }

    [Fact]
    public async Task Escape_WhileStreaming_CancelsOnTheHostAndCloses()
    {
        using var session = await StartAsync();
        var script = _edits.Next();
        session.PromptText = "slowly";
        var send = session.SendAsync();
        var request = await script.Started.Task.WithTimeout(Timeout);
        script.Send("partial\n");

        session.HandleEscape();
        await send.WithTimeout(Timeout);

        await Wait.UntilAsync(() => _edits.CancelledIds.Contains(request.RequestId), "the host was asked to cancel");
        Assert.True(await script.Cancelled.Task.WithTimeout(Timeout));
        Assert.Empty(_editor.Replaced);
        Assert.Equal(1, _editor.Closed);
        Assert.Equal(InlineChatState.Closed, session.State);
    }

    [Fact]
    public async Task ResetText_ReplacesTheEarlyPreview()
    {
        using var session = await StartAsync();
        var script = _edits.Next();
        session.PromptText = "fix it";
        var send = session.SendAsync();
        await script.Started.Task.WithTimeout(Timeout);

        script.Send("// an early line that changes\n");
        await Wait.UntilAsync(() => session.ProposalText.StartsWith("// an early", StringComparison.Ordinal), "the early preview is shown");
        script.Send("    int M(int x) => x;", reset: true);
        script.Final();
        await send.WithTimeout(Timeout);

        Assert.Equal("    int M(int x) => x;", session.ProposalText);
        Assert.Contains(_proposals, p => p.StartsWith("// an early", StringComparison.Ordinal));
        Assert.Equal("    int M(int x) => x;", _proposals.Last());
    }

    [Fact]
    public async Task FollowUpPrompt_RefinesTheProposalInTheSameConversation()
    {
        using var session = await StartAsync();
        var first = await CompleteAsync(session, "make it static", "    static int M(int x)\n    {\n        return x;\n    }");

        var second = await CompleteAsync(session, "and add a doc comment", "    /// <summary>M.</summary>\n    static int M(int x)\n    {\n        return x;\n    }");

        Assert.Null(first.PreviousProposal);
        Assert.Equal("    static int M(int x)\n    {\n        return x;\n    }", second.PreviousProposal);
        Assert.Equal(first.ConversationId, second.ConversationId);
        Assert.Equal(ScopeText, second.ScopeText); // The scope stays the buffer's text.
        Assert.NotEqual(first.RequestId, second.RequestId);
        Assert.StartsWith("    /// <summary>", session.ProposalText, StringComparison.Ordinal);
        Assert.Equal(["and add a doc comment", "make it static"], session.RecentPrompts);
    }

    [Fact]
    public async Task Fix_AttachesTheDiagnosticsOnTheScopeLines()
    {
        var reads = 0;
        IReadOnlyList<InlineEditDiagnostic> diagnostics =
        [
            new() { Line = 4, Code = "CS0029", Message = "Cannot convert", Severity = DiagnosticSeverity.Error },
            new() { Line = 0, Code = "CS8618", Message = "Outside the scope", Severity = DiagnosticSeverity.Warning },
        ];
        using var session = await StartAsync(new InlineChatSessionOptions
        {
            Diagnostics = _ =>
            {
                Interlocked.Increment(ref reads);
                return Task.FromResult(diagnostics);
            },
        });

        var plain = await CompleteAsync(session, "tidy", ScopeText);
        var fix = await CompleteAsync(session, "/fix", ScopeText);

        Assert.Empty(plain.Diagnostics);
        Assert.Equal(1, reads); // Read only for /fix.
        Assert.Equal(InlineEditCommand.Fix, fix.Command);
        Assert.Equal(string.Empty, fix.Instruction);
        Assert.Equal("CS0029", Assert.Single(fix.Diagnostics).Code);
    }

    [Fact]
    public async Task Fix_GoesWithoutDiagnosticsWhenReadingThemIsTooSlow()
    {
        using var session = await StartAsync(new InlineChatSessionOptions
        {
            DiagnosticsTimeout = TimeSpan.FromMilliseconds(50),
            Diagnostics = async token =>
            {
                await Task.Delay(TimeSpan.FromSeconds(30), token).ConfigureAwait(false);
                return [];
            },
        });

        var request = await CompleteAsync(session, "/fix", ScopeText);

        Assert.Empty(request.Diagnostics);
        Assert.Equal(InlineChatState.Preview, session.State);
    }

    [Fact]
    public async Task Explain_ShowsTheExplanationInsteadOfADiff()
    {
        using var session = await StartAsync();
        var script = _edits.Next();
        session.PromptText = "/explain";
        var send = session.SendAsync();
        var request = await script.Started.Task.WithTimeout(Timeout);
        script.Final(explanation: "Returns its argument.");
        await send.WithTimeout(Timeout);

        Assert.Equal(InlineEditCommand.Explain, request.Command);
        Assert.Equal(InlineChatState.Explanation, session.State);
        Assert.True(session.ShowsExplanation);
        Assert.False(session.ShowsDiff);
        Assert.False(session.CanAccept);
        Assert.Equal("Returns its argument.", session.Explanation);

        session.HandleEnter(); // Closes the explanation; nothing to apply.
        Assert.Empty(_editor.Replaced);
        Assert.Equal(1, _editor.Closed);
    }

    [Fact]
    public async Task Test_OffersInsertAtCursorAndCopyInsteadOfReplace()
    {
        using var session = await StartAsync();
        var script = _edits.Next();
        session.PromptText = "/test";
        var send = session.SendAsync();
        await script.Started.Task.WithTimeout(Timeout);
        script.Send("public class CTests { }");
        script.Final(explanation: "Put it in the test project.");
        await send.WithTimeout(Timeout);

        Assert.True(session.IsTestResult);
        Assert.False(session.CanAccept);
        Assert.True(session.CanInsert);
        Assert.Equal("Put it in the test project.", session.Explanation);

        session.HandleEnter(); // Enter never replaces the scope with test code.
        Assert.Empty(_editor.Replaced);

        session.Copy();
        Assert.Equal(["public class CTests { }"], _editor.Copied);
        Assert.Equal(0, _editor.Closed);

        session.InsertAtCaret();
        Assert.Equal(["public class CTests { }"], _editor.Inserted);
        Assert.Equal(1, _editor.Closed);
    }

    [Fact]
    public async Task WarningAndRejectedBlocks_AreShownWithTheProposal()
    {
        using var session = await StartAsync();
        var script = _edits.Next();
        session.PromptText = "do nothing";
        var send = session.SendAsync();
        await script.Started.Task.WithTimeout(Timeout);
        script.Send(ScopeText);
        script.Final(warning: "The model proposed no change.", rejectedBlocks: 2);
        await send.WithTimeout(Timeout);

        Assert.Equal("The model proposed no change.", session.Warning);
        Assert.True(session.HasNote);
        Assert.Contains("2 edit blocks", session.Note, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Failure_DiscardsThePreviewAndRetrySendsTheSameRequest()
    {
        using var session = await StartAsync();
        var script = _edits.Next();
        session.PromptText = "/doc";
        var send = session.SendAsync();
        var first = await script.Started.Task.WithTimeout(Timeout);
        script.Send("    /// partial\n");
        await Wait.UntilAsync(() => session.ProposalText.Length > 0, "the preview is shown");
        script.Fail(new PillionRpcException(new PillionError
        {
            Code = PillionErrorCode.ProviderUnavailable,
            Message = "The model's reply could not be used.",
            RetryHint = "Try again.",
        }));
        await send.WithTimeout(Timeout);

        Assert.Equal(InlineChatState.Failed, session.State);
        Assert.Equal(string.Empty, session.ProposalText);
        Assert.Equal("The model's reply could not be used. Try again.", session.ErrorText);
        Assert.True(session.CanRetry);
        Assert.False(session.CanAccept);

        var retry = _edits.Next();
        var again = session.RetryAsync();
        var second = await retry.Started.Task.WithTimeout(Timeout);
        retry.Send("    /// <summary>M.</summary>\n" + ScopeText);
        retry.Final();
        await again.WithTimeout(Timeout);

        Assert.Equal(InlineEditCommand.Doc, second.Command);
        Assert.Equal(first.ConversationId, second.ConversationId);
        Assert.NotEqual(first.RequestId, second.RequestId);
        Assert.Null(session.ErrorText);
        Assert.Equal(InlineChatState.Preview, session.State);
    }

    [Fact]
    public async Task PolicyDenied_IsShownAsTheError()
    {
        using var session = await StartAsync();
        var script = _edits.Next();
        session.PromptText = "go";
        var send = session.SendAsync();
        await script.Started.Task.WithTimeout(Timeout);
        script.Fail(new PillionRpcException(new PillionError { Code = PillionErrorCode.PolicyDenied, Message = "Inline chat is switched off by the inlineChat feature flag." }));
        await send.WithTimeout(Timeout);

        Assert.Equal("Inline chat is switched off by the inlineChat feature flag.", session.ErrorText);
        Assert.Equal(InlineChatState.Failed, session.State);
    }

    [Fact]
    public async Task FinalChunkWithAnError_IsAFailure()
    {
        using var session = await StartAsync();
        var script = _edits.Next();
        session.PromptText = "go";
        var send = session.SendAsync();
        await script.Started.Task.WithTimeout(Timeout);
        script.Send("text\n");
        script.Final(error: new PillionError { Code = PillionErrorCode.ProviderUnavailable, Message = "Dropped." });
        await send.WithTimeout(Timeout);

        Assert.Equal(InlineChatState.Failed, session.State);
        Assert.Equal("Dropped.", session.ErrorText);
    }

    [Fact]
    public async Task StreamWithoutAFinalChunk_IsAFailure()
    {
        using var session = await StartAsync();
        var script = _edits.Next();
        session.PromptText = "go";
        var send = session.SendAsync();
        await script.Started.Task.WithTimeout(Timeout);
        script.Send("text\n");
        script.Finish();
        await send.WithTimeout(Timeout);

        Assert.Equal(InlineChatState.Failed, session.State);
        Assert.Contains("before it was complete", session.ErrorText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task HostNotConnected_FailsWithRetry()
    {
        var connection = new FakeHostConnection(HostState.Restarting);
        using var session = new InlineChatSession(connection, _context.Factory, _document, _editor, new InlineChatSessionOptions { History = _history });
        session.Start();
        session.PromptText = "go";

        await session.SendAsync().WithTimeout(Timeout);

        Assert.Equal(InlineChatState.Failed, session.State);
        Assert.Contains("not connected", session.ErrorText, StringComparison.Ordinal);
        Assert.True(session.CanRetry);
    }

    [Fact]
    public async Task Stop_CancelsOnTheHostAndGoesBackToThePrompt()
    {
        using var session = await StartAsync();
        var script = _edits.Next();
        session.PromptText = "slowly";
        var send = session.SendAsync();
        var request = await script.Started.Task.WithTimeout(Timeout);
        script.Send("partial\n");
        await Wait.UntilAsync(() => session.ProposalText.Length > 0, "the preview is shown");

        await session.StopAsync().WithTimeout(Timeout);
        await send.WithTimeout(Timeout);

        Assert.Contains(request.RequestId, _edits.CancelledIds);
        Assert.True(await script.Cancelled.Task.WithTimeout(Timeout));
        Assert.Equal(InlineChatState.Prompt, session.State);
        Assert.Equal(string.Empty, session.ProposalText);
        Assert.Equal("Stopped.", session.StatusText);
        Assert.Equal(0, _editor.Closed);
    }

    [Fact]
    public async Task Stop_DuringARefinement_GoesBackToThePreviousProposal()
    {
        using var session = await StartAsync();
        await CompleteAsync(session, "make it static", "static\n");
        var script = _edits.Next();
        session.PromptText = "more";
        var send = session.SendAsync();
        await script.Started.Task.WithTimeout(Timeout);

        await session.StopAsync().WithTimeout(Timeout);
        await send.WithTimeout(Timeout);

        Assert.Equal(InlineChatState.Preview, session.State);
        Assert.Equal("static\n", session.ProposalText);
        Assert.True(session.CanAccept);
    }

    [Fact]
    public async Task ApplyRefused_KeepsThePreviewAndShowsWhy()
    {
        using var session = await StartAsync();
        await CompleteAsync(session, "go", "new\n");
        _editor.RefuseWith = "The file could not be checked out.";

        session.Accept();

        Assert.Equal("The file could not be checked out.", session.ErrorText);
        Assert.Equal(InlineChatState.Preview, session.State);
        Assert.Equal(0, _editor.Closed);
    }

    [Fact]
    public async Task SlashCompletion_OffersAndAcceptsShortcuts()
    {
        using var session = await StartAsync();

        session.PromptText = "/";
        Assert.Equal(4, session.SlashSuggestions.Count);
        Assert.Equal("/doc", session.SelectedSuggestion!.Name);
        Assert.True(session.MoveSuggestion(1));
        Assert.Equal("/test", session.SelectedSuggestion!.Name);

        session.HandleEnter(); // Takes the highlighted shortcut; sends nothing.
        Assert.Equal("/test ", session.PromptText);
        Assert.False(session.HasSuggestions);
        Assert.Empty(_edits.Requests);

        session.PromptText = "/e";
        Assert.Equal("/explain", Assert.Single(session.SlashSuggestions).Name);
        session.HandleEscape(); // Hides the list; the adornment stays open.
        Assert.False(session.HasSuggestions);
        Assert.Equal(0, _editor.Closed);
    }

    [Fact]
    public async Task ModelPicker_UsesAutoByDefaultAndRemembersTheChoice()
    {
        using (var session = await StartAsync())
        {
            await Wait.UntilAsync(() => session.ModelPicker.Models.Count == 3, "the models are listed");
            Assert.True(session.ModelPicker.SelectedModel!.IsAuto);
            session.ModelPicker.SelectedModel = session.ModelPicker.Models.Single(m => m.Id == TestModels.Local.Id);
        }

        using var next = await StartAsync();
        Assert.Equal(TestModels.Local.Id, next.ModelPicker.SelectedModel!.Id); // From memory, before the host answers.
        var request = await CompleteAsync(next, "go", "x\n");
        Assert.Equal(TestModels.Local.Id, request.ModelId);
        Assert.Equal(Profile.Auto, request.Profile);
    }

    [Fact]
    public async Task ModelPicker_GreysOutCloudModelsWhenTheInlineEditCloudSwitchIsOff()
    {
        _settings.Values["cloudSwitches.inlineEdit"] = "false";
        using var session = await StartAsync();

        await Wait.UntilAsync(() => session.ModelPicker.Models.Count == 3, "the models are listed");
        Assert.False(session.ModelPicker.Models.Single(m => m.Id == TestModels.Cloud.Id).IsAvailable);
        Assert.True(session.ModelPicker.Models.Single(m => m.Id == TestModels.Local.Id).IsAvailable);
    }

    [Fact]
    public async Task FeatureFlagOff_CannotSend()
    {
        var availability = new InlineChatAvailability(_connection);
        availability.Apply(false);
        using var session = await StartAsync(new InlineChatSessionOptions { Availability = availability });
        session.PromptText = "go";

        await session.SendAsync().WithTimeout(Timeout);

        Assert.False(session.IsEnabled);
        Assert.Equal("Inline chat is disabled by settings or policy.", session.DisabledMessage);
        Assert.Empty(_edits.Requests);
        Assert.False(session.SendCommand.CanExecute(null));
    }

    [Fact]
    public async Task Availability_ReadsTheFlagAndFollowsSettingsChanges()
    {
        _settings.Values["effective.features"] = "{\"inlineChat\":false}";
        using var availability = new InlineChatAvailability(_connection);
        var changed = 0;
        availability.Changed += (_, _) => Interlocked.Increment(ref changed);
        availability.Start();
        await Wait.UntilAsync(() => !availability.IsEnabled, "the flag is read");

        _settings.Values["effective.features"] = "{\"inlineChat\":true}";
        await _connection.RaiseSettingsChangedAsync("features");
        await Wait.UntilAsync(() => availability.IsEnabled, "the change is read");
        Assert.Equal(2, Volatile.Read(ref changed));
    }

    [Fact]
    public async Task Elapsed_IsShownWhileStreaming()
    {
        using var session = await StartAsync();
        var script = _edits.Next();
        session.PromptText = "go";
        var send = session.SendAsync();
        await script.Started.Task.WithTimeout(Timeout);
        await Task.Delay(120);
        session.Tick();
        Assert.EndsWith(" s", session.ElapsedText, StringComparison.Ordinal);
        Assert.NotEqual(InlineChatSession.FormatSeconds(TimeSpan.Zero), session.ElapsedText);
        script.Send("x");
        script.Final();
        await send.WithTimeout(Timeout);
    }

    private async Task<InlineChatSession> StartAsync(InlineChatSessionOptions? options = null)
    {
        options ??= new InlineChatSessionOptions();
        options.History ??= _history;
        options.FlushInterval = TimeSpan.Zero;
        var session = new InlineChatSession(_connection, _context.Factory, _document, _editor, options);
        session.ProposalChanged += (_, _) =>
        {
            lock (_proposals)
            {
                _proposals.Add(session.ProposalText);
            }
        };
        session.Start();
        await Task.Yield();
        return session;
    }

    /// <summary>Sends a prompt and completes it with one text chunk and a final chunk; returns the request.</summary>
    private async Task<InlineEditRequest> CompleteAsync(InlineChatSession session, string prompt, string proposal)
    {
        var script = _edits.Next();
        session.PromptText = prompt;
        var send = session.SendAsync();
        var request = await script.Started.Task.WithTimeout(Timeout);
        script.Send(proposal);
        script.Final();
        await send.WithTimeout(Timeout);
        Assert.Equal(InlineChatState.Preview, session.State);
        return request;
    }
}

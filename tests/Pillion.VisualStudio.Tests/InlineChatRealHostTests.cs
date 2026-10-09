using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.Threading;
using Pillion.VisualStudio.Core;
using Pillion.VisualStudio.Core.Autocomplete;
using Pillion.VisualStudio.Core.InlineChat;

namespace Pillion.VisualStudio.Tests;

/// <summary>
/// The inline chat session over the real pipe: tests/Pillion.FakeHost streams canned replacements line by line, resets
/// an early preview, explains, fails and honours CancelEditAsync.
/// </summary>
[Collection(RealProcessTestGroup.Name)]
public sealed class InlineChatRealHostTests : IDisposable
{
    private const string ChunkDelayVariable = "PILLION_FAKEHOST_CHUNK_DELAY_MS";
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

    private const string Code =
        "public static class Calculator\r\n" +
        "{\r\n" +
        "    public static int Add(int a, int b)\r\n" +
        "    {\r\n" +
        "        return a + b;\r\n" +
        "    }\r\n" +
        "}\r\n";

    private readonly JoinableTaskContext _context = Task.Run(() => new JoinableTaskContext()).Result;
    private readonly string _logDirectory = Path.Combine(Path.GetTempPath(), "pillion-ic-tests-" + Guid.NewGuid().ToString("N"));
    private readonly RecordingInlineChatEditor _editor = new();
    private readonly PillionLog _log;
    private readonly InlineChatDocument _document;

    public InlineChatRealHostTests()
    {
        _log = new PillionLog(_logDirectory);
        Environment.SetEnvironmentVariable(ChunkDelayVariable, "30");
        var text = new StringDocumentText(Code);
        var start = Code.IndexOf("    public static int Add", StringComparison.Ordinal);
        var end = Code.IndexOf("    }\r\n}", StringComparison.Ordinal) + 5;
        _document = new InlineChatDocument(@"C:\src\Calculator.cs", "csharp", text, InlineScopeSelector.Select(text, start, end, end));
    }

    private static string FakeHostPath => typeof(InlineChatRealHostTests).Assembly
        .GetCustomAttributes<AssemblyMetadataAttribute>()
        .Single(a => a.Key == "FakeHostPath").Value!;

    public void Dispose()
    {
        Environment.SetEnvironmentVariable(ChunkDelayVariable, null);
        _context.Dispose();
        try
        {
            Directory.Delete(_logDirectory, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    [Fact]
    public async Task Edit_StreamsTheReplacementAndKeepsTheScopesLineEndings()
    {
        using var supervisor = await StartAsync();
        using var session = Create(supervisor);
        session.PromptText = "extract the validation";

        await session.SendAsync().WithTimeout(Timeout);

        Assert.Equal(InlineChatState.Preview, session.State);
        Assert.Equal("    // Edited by Pillion's fake host: extract the validation\r\n" + _document.ScopeText, session.ProposalText);
        Assert.Matches("(?i)fake[- ]local[- ]coder via ollama", session.ModelHeader);
        Assert.NotNull(session.FirstLineTime);
        Assert.True(session.FirstLineTime <= session.TotalTime);
        await Wait.UntilAsync(() => LogText.Contains("profile Auto"), "the host saw Profile.Auto");

        session.Accept();
        Assert.Single(_editor.Replaced);
    }

    [Fact]
    public async Task Edit_ResetTextReplacesTheEarlyPreview()
    {
        using var supervisor = await StartAsync();
        using var session = Create(supervisor);
        var seen = new System.Collections.Generic.List<string>();
        session.ProposalChanged += (_, _) =>
        {
            lock (seen)
            {
                seen.Add(session.ProposalText);
            }
        };
        session.PromptText = "reset please";

        await session.SendAsync().WithTimeout(Timeout);

        Assert.DoesNotContain("wrong early preview", session.ProposalText, StringComparison.Ordinal);
        Assert.StartsWith("    // Edited by Pillion's fake host: reset please\r\n", session.ProposalText, StringComparison.Ordinal);
        lock (seen)
        {
            Assert.Contains(seen, s => s.Contains("wrong early preview"));
        }
    }

    [Fact]
    public async Task Refinement_SendsThePreviousProposalAndTheSameConversation()
    {
        using var supervisor = await StartAsync();
        using var session = Create(supervisor);
        session.PromptText = "first";
        await session.SendAsync().WithTimeout(Timeout);

        session.PromptText = "/doc";
        await session.SendAsync().WithTimeout(Timeout);

        Assert.StartsWith(
            "    /// <summary>Fake documentation.</summary>\r\n    // Edited by Pillion's fake host: first\r\n",
            session.ProposalText,
            StringComparison.Ordinal);
        Assert.Equal(2, CountOccurrences(LogText, "conversation " + session.ConversationId));
        Assert.Contains("refining", LogText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Explain_ReturnsProseOnly()
    {
        using var supervisor = await StartAsync();
        using var session = Create(supervisor);
        session.PromptText = "/explain";

        await session.SendAsync().WithTimeout(Timeout);

        Assert.Equal(InlineChatState.Explanation, session.State);
        Assert.Equal(string.Empty, session.ProposalText);
        Assert.Contains("4 line(s)", session.Explanation, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Error_FailsWithTheHostsMessageAndRetryHint()
    {
        using var supervisor = await StartAsync();
        using var session = Create(supervisor);
        session.PromptText = "please error";

        await session.SendAsync().WithTimeout(Timeout);

        Assert.Equal(InlineChatState.Failed, session.State);
        Assert.StartsWith("The fake provider for Fake Local Coder is unreachable.", session.ErrorText, StringComparison.Ordinal);
        Assert.True(session.CanRetry);
    }

    [Fact]
    public async Task LateError_DiscardsThePreview()
    {
        using var supervisor = await StartAsync();
        using var session = Create(supervisor);
        session.PromptText = "late error";

        await session.SendAsync().WithTimeout(Timeout);

        Assert.Equal(InlineChatState.Failed, session.State);
        Assert.Equal(string.Empty, session.ProposalText);
    }

    [Fact]
    public async Task Stop_CancelsTheRequestOnTheHost()
    {
        using var supervisor = await StartAsync();
        using var session = Create(supervisor);
        session.PromptText = "slow";

        var send = session.SendAsync();
        await Wait.UntilAsync(() => LogText.Contains("fakehost: inline edit "), "the request reached the host");
        await session.StopAsync().WithTimeout(Timeout);
        await send.WithTimeout(Timeout);

        Assert.Equal(InlineChatState.Prompt, session.State);
        await Wait.UntilAsync(() => LogText.Contains("fakehost: cancel inline edit"), "the host was asked to cancel");
        await Wait.UntilAsync(() => LogText.Contains(" cancelled"), "the host stopped the stream");
        Assert.DoesNotContain(" complete after", LogText, StringComparison.Ordinal);
    }

    private static int CountOccurrences(string text, string value)
    {
        var count = 0;
        for (var i = text.IndexOf(value, StringComparison.Ordinal); i >= 0; i = text.IndexOf(value, i + value.Length, StringComparison.Ordinal))
        {
            count++;
        }

        return count;
    }

    private InlineChatSession Create(HostSupervisor supervisor)
    {
        var session = new InlineChatSession(supervisor, _context.Factory, _document, _editor, new InlineChatSessionOptions { Log = _log, History = new InlineChatHistory() });
        session.Start();
        return session;
    }

    private string LogText
    {
        get
        {
            var path = _log.CurrentFilePath;
            for (var attempt = 0; ; attempt++)
            {
                try
                {
                    if (path is null || !File.Exists(path))
                    {
                        return string.Empty;
                    }

                    using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                    using var reader = new StreamReader(stream);
                    return reader.ReadToEnd();
                }
                catch (IOException) when (attempt < 20)
                {
                    Thread.Sleep(10);
                }
            }
        }
    }

    private async Task<HostSupervisor> StartAsync()
    {
        Assert.True(File.Exists(FakeHostPath), "Build tests/Pillion.FakeHost first: " + FakeHostPath);
        var launcher = new HostProcessLauncher(
            new HostLauncherOptions(FakeHostPath, typeof(HostSupervisor).Assembly.Location, HostLocalTargets.CreateNotImplemented())
            {
                LogDirectory = _logDirectory,
            },
            _log);
        var supervisor = new HostSupervisor(launcher, _context.Factory, _log, SystemClock.Instance, new RestartPolicy());
        supervisor.Start();
        await supervisor.WhenReadyAsync(CancellationToken.None).WithTimeout(Timeout);
        return supervisor;
    }
}

using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.Threading;
using Pillion.VisualStudio.Core;
using Pillion.VisualStudio.Core.Autocomplete;

namespace Pillion.VisualStudio.Tests;

/// <summary>
/// The autocomplete pipeline over the real pipe: tests/Pillion.FakeHost streams a canned completion in chunks, and the
/// pipeline turns it into a suggestion (or stops it when the attempt is cancelled).
/// </summary>
[Collection(RealProcessTestGroup.Name)]
public sealed class AutocompleteRealHostTests : IDisposable
{
    private const string ChunkDelayVariable = "PILLION_FAKEHOST_CHUNK_DELAY_MS";
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

    private const string Code =
        "public static class Calculator\n" +
        "{\n" +
        "    public static int Min(int[] numbers)\n" +
        "}\n";

    private readonly JoinableTaskContext _context = new();
    private readonly string _logDirectory = Path.Combine(Path.GetTempPath(), "pillion-ac-tests-" + Guid.NewGuid().ToString("N"));
    private readonly PillionLog _log;

    public AutocompleteRealHostTests()
    {
        _log = new PillionLog(_logDirectory);
        Environment.SetEnvironmentVariable(ChunkDelayVariable, null);
    }

    private static string FakeHostPath => typeof(AutocompleteRealHostTests).Assembly
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
    public async Task StreamedCompletion_BecomesAMultiLineSuggestion()
    {
        Environment.SetEnvironmentVariable(ChunkDelayVariable, "60");
        using var supervisor = await StartAsync();
        var engine = new AutocompleteEngine(supervisor, availability: null, SystemClock.Instance, _log);
        var session = engine.CreateSession();

        var attempt = session.Start(InputAfterSignature(), CancellationToken.None);
        var result = await attempt.Completed.WithTimeout(Timeout);

        Assert.NotNull(result);
        Assert.True(result!.IsMultiLine);
        Assert.Equal(CompletionStopReason.BlockEnd, result.StopReason);
        Assert.Equal("\n    {\n        return numbers.Min();\n    }", result.Text);
        Assert.Equal("fake-local-coder", result.ModelId);
        Assert.Equal(1, engine.Latency.Snapshot().Count);

        // The block end stopped the stream early: the host was told to stop and never sent the rest.
        await Wait.UntilAsync(
            () => LogText.Contains("fakehost: complete " + attempt.RequestId + " cancelled") || LogText.Contains("fakehost: cancel " + attempt.RequestId),
            "the host was told to stop");
        await Task.Delay(TimeSpan.FromSeconds(2));
        Assert.DoesNotContain("fakehost: complete " + attempt.RequestId + " finished", LogText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CancellingTheAttempt_StopsTheStreamOnTheHost()
    {
        Environment.SetEnvironmentVariable(ChunkDelayVariable, "400");
        using var supervisor = await StartAsync();
        var engine = new AutocompleteEngine(supervisor, availability: null, SystemClock.Instance, _log);
        var session = engine.CreateSession();

        var attempt = session.Start(InputAfterSignature(), CancellationToken.None);
        await Wait.UntilAsync(() => LogText.Contains("fakehost: complete " + attempt.RequestId), "the request reached the host");
        session.CancelPending(); // A keystroke.

        Assert.Null(await attempt.Completed.WithTimeout(Timeout));
        await Wait.UntilAsync(() => LogText.Contains("fakehost: complete " + attempt.RequestId + " cancelled"), "the host stopped the stream");
        Assert.Equal(0, engine.Latency.Snapshot().Count);
    }

    private static CompletionInput InputAfterSignature() => new()
    {
        DocumentKey = "Calculator.cs",
        FilePath = @"C:\src\Calculator.cs",
        LanguageId = "csharp",
        Text = new StringDocumentText(Code),
        Caret = Code.IndexOf(')') + 1,
    };

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

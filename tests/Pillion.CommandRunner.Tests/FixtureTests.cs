using Pillion.CommandRunner.Terminal;

namespace Pillion.CommandRunner.Tests;

/// <summary>
/// Recorded pseudo-console output replayed through the parser: <c>dotnet build</c> with the Terminal Logger on and off,
/// a PowerShell progress bar and <c>git log --color</c>, as raw bytes in <c>Fixtures\*.vt</c>. The recorder below rewrote
/// them from a real pseudo-console; set <c>PILLION_RECORD_TERMINAL_FIXTURES</c> to the Fixtures source folder to record again.
/// </summary>
public sealed class FixtureTests
{
    private static readonly string FixturesDirectory = Path.Combine(AppContext.BaseDirectory, "Fixtures");

    [Theory]
    [InlineData("dotnet-build-tl-on.vt", "Build succeeded")]
    [InlineData("dotnet-build-tl-off.vt", "Build succeeded")]
    [InlineData("dotnet-build-error-tl-on.vt", "error CS")]
    [InlineData("powershell-progress.vt", "progress done")]
    [InlineData("git-log-color.vt", "commit ")]
    public void Fixture_RendersToReadableText_WithoutEscapes(string name, string expected)
    {
        var bytes = File.ReadAllBytes(Path.Combine(FixturesDirectory, name));
        var terminal = new TerminalBuffer(120, 40, 10_000);

        // Feed in odd-sized chunks so sequences and UTF-8 characters are split as a pipe would split them.
        for (var offset = 0; offset < bytes.Length; offset += 777)
        {
            terminal.Write(bytes.AsSpan(offset, Math.Min(777, bytes.Length - offset)));
        }

        var view = ModelView.Build(terminal, 0, 64 * 1024);
        Assert.Contains(expected, view.Text, StringComparison.Ordinal);
        Assert.DoesNotContain('\u001B', view.Text);
        Assert.DoesNotContain('\u0007', view.Text);
        Assert.DoesNotContain('\r', view.Text);
        Assert.All(view.Text.Split('\n'), line => Assert.True(line.Length <= 120, $"a line is {line.Length} cells wide: {line}"));
    }

    [Fact]
    public void TerminalLoggerOn_LeavesOnlyTheFinalStateOfTheLiveRegion()
    {
        var terminal = new TerminalBuffer(120, 40, 10_000);
        terminal.Write(File.ReadAllBytes(Path.Combine(FixturesDirectory, "dotnet-build-tl-on.vt")));
        var text = ModelView.Build(terminal, 0, 64 * 1024).Text;

        // The Terminal Logger redraws its progress lines in place; the final screen has the summary and no stray node lines.
        Assert.Contains("Build succeeded", text, StringComparison.Ordinal);
        Assert.Equal(1, text.Split("Build succeeded").Length - 1);
        Assert.Contains("Hello", text, StringComparison.Ordinal);
    }

    [Fact]
    public void ProgressBar_RendersAsItsLastState_NotEveryRedraw()
    {
        var terminal = new TerminalBuffer(120, 40, 10_000);
        terminal.Write(File.ReadAllBytes(Path.Combine(FixturesDirectory, "powershell-progress.vt")));
        var lines = terminal.GetLinesToEnd(0).Select(l => l.Text).Where(l => l.Length > 0).ToList();

        Assert.Contains("progress done", lines);
        // Fifty updates of one bar must not become fifty lines.
        Assert.True(lines.Count < 15, string.Join("\n", lines));
    }

    [Fact]
    public void GitLogColor_KeepsColoursAsAttributes()
    {
        var terminal = new TerminalBuffer(120, 40, 10_000);
        terminal.Write(File.ReadAllBytes(Path.Combine(FixturesDirectory, "git-log-color.vt")));
        var lines = terminal.GetLinesToEnd(0);

        var commit = lines.First(l => l.Text.StartsWith("commit ", StringComparison.Ordinal));
        Assert.NotEmpty(commit.Spans);
        Assert.Contains(commit.Spans, s => !s.Attributes.Foreground.IsDefault);
    }

    /// <summary>Rewrites the fixtures from a real pseudo-console on this machine. Skipped unless the environment variable names the destination folder.</summary>
    [RecorderFact]
    public async Task RecordFixtures()
    {
        var repository = new DirectoryInfo(AppContext.BaseDirectory);
        while (repository is not null && !File.Exists(Path.Combine(repository.FullName, "PillionProgrammer.sln")))
        {
            repository = repository.Parent;
        }

        Assert.NotNull(repository);
        var destination = Environment.GetEnvironmentVariable("PILLION_RECORD_TERMINAL_FIXTURES")!;
        Directory.CreateDirectory(destination);
        using var directory = new TempDirectory();
        File.WriteAllText(Path.Combine(directory.Path, "Hello.csproj"), "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><OutputType>Exe</OutputType><TargetFramework>net10.0</TargetFramework><ImplicitUsings>enable</ImplicitUsings></PropertyGroup></Project>");
        File.WriteAllText(Path.Combine(directory.Path, "Program.cs"), "Console.WriteLine(\"hello\");\n");
        File.WriteAllText(Path.Combine(directory.Path, "Directory.Build.props"), "<Project />");
        File.WriteAllText(Path.Combine(directory.Path, "Directory.Build.targets"), "<Project />");
        File.WriteAllText(Path.Combine(directory.Path, "Directory.Packages.props"), "<Project />");

        await using var session = await TestSessions.CreateAsync(directory, o => o.Timeout = TimeSpan.FromMinutes(5));
        var terminal = (TerminalBuffer)session.Terminal;

        async Task RecordAsync(string name, string command, CommandRunOptions? options = null)
        {
            using var stream = File.Create(Path.Combine(destination, name));
            terminal.RawSink = chunk => stream.Write(chunk.Span);
            try
            {
                await session.RunAsync(command, options);
            }
            finally
            {
                terminal.RawSink = null;
            }
        }

        var on = new CommandRunOptions();
        on.Environment["MSBUILDTERMINALLOGGER"] = "on";
        await RecordAsync("dotnet-build-tl-on.vt", "dotnet build Hello.csproj -tl:on -p:NuGetAudit=false", on);
        await RecordAsync("dotnet-build-tl-off.vt", "dotnet build Hello.csproj -p:NuGetAudit=false", new CommandRunOptions { Kind = CommandKind.Build });
        File.WriteAllText(Path.Combine(directory.Path, "Program.cs"), "Console.WriteLine(\"hello\")\nint x = ;\n");
        await RecordAsync("dotnet-build-error-tl-on.vt", "dotnet build Hello.csproj -tl:on -p:NuGetAudit=false", on);
        await RecordAsync("powershell-progress.vt", "1..50 | ForEach-Object { Write-Progress -Activity 'Working' -Status \"$_ of 50\" -PercentComplete ($_ * 2); Start-Sleep -Milliseconds 20 }; Write-Progress -Activity 'Working' -Completed; Write-Output 'progress done'");
        // A small repository of its own, in the temporary folder, so the recording does not depend on this checkout or on which git is first on PATH.
        const string findGit = @"$git = (Get-Command git.exe -All | Where-Object Source -like '*Program Files\Git*' | Select-Object -First 1).Source; if (-not $git) { $git = 'git' }; ";
        var gitSetup = await session.RunAsync(findGit + "& $git init -q; & $git config user.email t@example.com; & $git config user.name Test; 1..3 | ForEach-Object { Set-Content -Path \"file$_.txt\" -Value ('line' * $_); & $git add .; & $git commit -q -m \"commit $_\" }");
        Assert.True(gitSetup.Succeeded, gitSetup.ModelView.Text);
        await RecordAsync("git-log-color.vt", findGit + "& $git --no-pager -c color.ui=always log --color=always -n 3 --stat");
    }

    [AttributeUsage(AttributeTargets.Method)]
    private sealed class RecorderFactAttribute : FactAttribute
    {
        public RecorderFactAttribute()
        {
            if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("PILLION_RECORD_TERMINAL_FIXTURES")))
            {
                Skip = "Set PILLION_RECORD_TERMINAL_FIXTURES to the Fixtures source folder to rewrite the recorded fixtures.";
            }
        }
    }
}

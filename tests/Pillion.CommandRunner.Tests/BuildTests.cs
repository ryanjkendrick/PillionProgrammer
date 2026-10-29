namespace Pillion.CommandRunner.Tests;

/// <summary>
/// Real <c>dotnet build</c> runs on the pseudo-console with the Terminal Logger on (a developer-style run, the screen is
/// redrawn live) and off (an agent-run build, structured logs), both producing readable model views.
/// </summary>
public sealed class BuildTests : IDisposable
{
    private readonly TempDirectory _directory = new();

    public void Dispose() => _directory.Dispose();

    [Fact]
    public async Task DotnetBuild_WithTheTerminalLoggerOff_ProducesAReadableView_AndABinaryLog()
    {
        WriteProject(broken: false);
        await using var session = await TestSessions.CreateAsync(_directory, o => o.Timeout = TimeSpan.FromMinutes(5));

        var result = await session.RunAsync("dotnet build Hello.csproj -p:NuGetAudit=false", new CommandRunOptions { Kind = CommandKind.Build });

        Assert.True(result.Succeeded, result.ModelView.Text);
        Assert.Contains("Build succeeded", result.ModelView.Text, StringComparison.Ordinal);
        Assert.Contains("Hello", result.ModelView.Text, StringComparison.Ordinal);
        Assert.DoesNotContain('\u001B', result.ModelView.Text);
        Assert.NotNull(result.BinaryLogPath);
        Assert.True(File.Exists(result.BinaryLogPath), $"no binary log at {result.BinaryLogPath}");
    }

    [Fact]
    public async Task DotnetBuild_WithTheTerminalLoggerOn_ProducesAReadableView()
    {
        WriteProject(broken: false);
        await using var session = await TestSessions.CreateAsync(_directory, o => o.Timeout = TimeSpan.FromMinutes(5));
        var options = new CommandRunOptions();
        options.Environment["MSBUILDTERMINALLOGGER"] = "on";

        var result = await session.RunAsync("dotnet build Hello.csproj -tl:on -p:NuGetAudit=false", options);

        Assert.True(result.Succeeded, result.ModelView.Text);
        Assert.Contains("Build succeeded", result.ModelView.Text, StringComparison.Ordinal);
        Assert.Contains("Hello", result.ModelView.Text, StringComparison.Ordinal);
        Assert.DoesNotContain('\u001B', result.ModelView.Text);
        Assert.Null(result.BinaryLogPath);
    }

    [Fact]
    public async Task DotnetBuild_WithAnError_FailsWithTheDiagnosticInTheView()
    {
        WriteProject(broken: true);
        await using var session = await TestSessions.CreateAsync(_directory, o => o.Timeout = TimeSpan.FromMinutes(5));

        var result = await session.RunAsync("dotnet build Hello.csproj -p:NuGetAudit=false", new CommandRunOptions { Kind = CommandKind.Build });

        Assert.Equal(CommandOutcome.Completed, result.Outcome);
        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("error CS", result.ModelView.Text, StringComparison.Ordinal);
        Assert.Contains("Program.cs", result.ModelView.Text, StringComparison.Ordinal);
        Assert.True(File.Exists(result.BinaryLogPath), $"no binary log at {result.BinaryLogPath}");
    }

    private void WriteProject(bool broken)
    {
        File.WriteAllText(Path.Combine(_directory.Path, "Hello.csproj"), """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <OutputType>Exe</OutputType>
                <TargetFramework>net10.0</TargetFramework>
                <Nullable>enable</Nullable>
                <ImplicitUsings>enable</ImplicitUsings>
              </PropertyGroup>
            </Project>
            """);
        File.WriteAllText(Path.Combine(_directory.Path, "Program.cs"), broken
            ? "Console.WriteLine(\"hello\")\nint x = ;\n"
            : "Console.WriteLine(\"hello\");\n");
        // Keep the repository's Directory.Build.props and analyzers out of the temporary project.
        File.WriteAllText(Path.Combine(_directory.Path, "Directory.Build.props"), "<Project />");
        File.WriteAllText(Path.Combine(_directory.Path, "Directory.Build.targets"), "<Project />");
        File.WriteAllText(Path.Combine(_directory.Path, "Directory.Packages.props"), "<Project />");
    }
}

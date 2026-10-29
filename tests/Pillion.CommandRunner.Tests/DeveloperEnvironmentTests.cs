using Pillion.CommandRunner.Shells;

namespace Pillion.CommandRunner.Tests;

/// <summary>The Developer PowerShell environment: loaded from a Visual Studio installation, cached, and the fallback when there is none.</summary>
public sealed class DeveloperEnvironmentTests : IDisposable
{
    private readonly TempDirectory _directory = new();

    public void Dispose() => _directory.Dispose();

    [VisualStudioFact]
    public async Task Session_LoadsTheDeveloperEnvironment_AndCommandsSeeIt()
    {
        var installation = VisualStudioFactAttribute.Installation.Value!;
        var options = new CommandSessionOptions(_directory.Path)
        {
            VisualStudioInstallationPath = installation,
            LogDirectory = Path.Combine(_directory.Path, "logs"),
        };
        await using var session = await new CommandRunner().CreateSessionAsync(options);

        Assert.True(session.DeveloperEnvironmentLoaded, string.Join(" | ", session.Warnings));
        Assert.DoesNotContain(session.Warnings, w => w.Contains("VsDevCmd", StringComparison.Ordinal));
        Assert.True(session.Environment.ContainsKey("VSINSTALLDIR"), "VSINSTALLDIR");
        Assert.True(session.Environment.ContainsKey("VSCMD_VER"), "VSCMD_VER");
        Assert.StartsWith(Path.TrimEndingDirectorySeparator(installation), Path.TrimEndingDirectorySeparator(session.Environment["VSINSTALLDIR"]), StringComparison.OrdinalIgnoreCase);

        var result = await session.RunAsync("Write-Output \"VS=$env:VSINSTALLDIR\"; where.exe msbuild");
        Assert.Equal(0, result.ExitCode);
        Assert.Contains("VS=", result.ModelView.Text, StringComparison.Ordinal);
        Assert.Contains("MSBuild.exe", result.ModelView.Text, StringComparison.OrdinalIgnoreCase);
    }

    [VisualStudioFact]
    public async Task TheCache_LoadsAnInstallationOnce()
    {
        var installation = VisualStudioFactAttribute.Installation.Value!;
        var cache = new DeveloperEnvironmentCache();

        var first = cache.GetAsync(installation);
        var second = cache.GetAsync(installation + Path.DirectorySeparatorChar);
        var results = await Task.WhenAll(first, second);

        Assert.True(results[0].Loaded, results[0].Warning);
        Assert.Same(results[0].Environment, results[1].Environment);
    }

    [Fact]
    public async Task MissingInstallation_FallsBackToThePlainEnvironment_WithAWarning()
    {
        var options = new CommandSessionOptions(_directory.Path)
        {
            VisualStudioInstallationPath = Path.Combine(_directory.Path, "no-such-visual-studio"),
            LogDirectory = Path.Combine(_directory.Path, "logs"),
        };
        await using var session = await new CommandRunner().CreateSessionAsync(options);

        Assert.False(session.DeveloperEnvironmentLoaded);
        var warning = Assert.Single(session.Warnings);
        Assert.Contains("VsDevCmd.bat", warning, StringComparison.Ordinal);
        Assert.True(session.Environment.ContainsKey("PATH"));

        var result = await session.RunAsync("Write-Output \"vs=[$env:VSINSTALLDIR]\"");
        Assert.Equal(0, result.ExitCode);
        Assert.Contains(warning, result.Warnings);

        var none = await DeveloperEnvironment.CaptureAsync(null);
        Assert.False(none.Loaded);
        Assert.Contains("No Visual Studio installation path", none.Warning, StringComparison.Ordinal);
        Assert.Null(DeveloperEnvironment.FindScript(null));
        Assert.Null(DeveloperEnvironment.FindScript("Q:\\nowhere"));
    }

    [Fact]
    public void Parse_ReadsSetOutput()
    {
        var parsed = DeveloperEnvironment.Parse("A=1\r\nPath=C:\\x;C:\\y\r\n=C:=C:\\\r\nnoequals\r\nB=has=equals\r\n");

        Assert.Equal("1", parsed["a"]);
        Assert.Equal("C:\\x;C:\\y", parsed["PATH"]);
        Assert.Equal("has=equals", parsed["B"]);
        Assert.Equal(3, parsed.Count);
    }

    [Fact]
    public void TheLocator_FindsAnInstallationOrNothing_WithoutThrowing()
    {
        var found = VisualStudioLocator.FindNewestInstallation();
        if (found is not null)
        {
            Assert.NotNull(DeveloperEnvironment.FindScript(found));
        }
    }
}

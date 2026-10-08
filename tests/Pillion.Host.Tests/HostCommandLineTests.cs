using Pillion.Contracts;

namespace Pillion.Host.Tests;

public class HostCommandLineTests
{
    [Fact]
    public void Parse_RequiredArguments_ReturnsRun()
    {
        var result = HostCommandLine.Parse(["--pipe", "pillion-1234", "--parent-pid", "4321", "--token-handle", "1180"]);

        Assert.Equal(HostMode.Run, result.Mode);
        Assert.Equal("pillion-1234", result.PipeName);
        Assert.Equal(4321, result.ParentProcessId);
        Assert.Equal("1180", result.TokenHandle);
        Assert.Null(result.LogDirectory);
        Assert.Null(result.Error);
    }

    [Fact]
    public void Parse_LogDirectory_InAnyOrder_ReturnsRun()
    {
        var result = HostCommandLine.Parse(["--log-dir", @"C:\logs", "--token-handle", "7", "--parent-pid", "1", "--pipe", "p"]);

        Assert.Equal(HostMode.Run, result.Mode);
        Assert.Equal(@"C:\logs", result.LogDirectory);
        Assert.Equal(1, result.ParentProcessId);
    }

    [Theory]
    [InlineData("--version")]
    [InlineData("--version", "--pipe", "p")]
    [InlineData("--pipe", "p", "--version")]
    public void Parse_Version_ReturnsVersion(params string[] args)
    {
        var result = HostCommandLine.Parse(args);

        Assert.Equal(HostMode.Version, result.Mode);
    }

    [Theory]
    [InlineData]
    [InlineData("--pipe")]
    [InlineData("--pipe", "--version")]
    [InlineData("--pipe", " ")]
    [InlineData("--pipe", "a", "--pipe", "b")]
    [InlineData("--unknown")]
    [InlineData("pipe-name")]
    [InlineData("--pipe", "p")]
    [InlineData("--pipe", "p", "--parent-pid", "1")]
    [InlineData("--pipe", "p", "--token-handle", "1")]
    [InlineData("--pipe", "p", "--parent-pid", "0", "--token-handle", "1")]
    [InlineData("--pipe", "p", "--parent-pid", "-5", "--token-handle", "1")]
    [InlineData("--pipe", "p", "--parent-pid", "abc", "--token-handle", "1")]
    [InlineData("--pipe", "p", "--parent-pid", "1", "--token-handle", "0x10")]
    [InlineData("--pipe", "p", "--parent-pid", "1", "--token-handle", "1", "--log-dir")]
    [InlineData("--pipe", "p", "--parent-pid", "1", "--token-handle", "1", "--parent-pid", "2")]
    [InlineData("--pipe", "p", "--parent-pid", "1", "--token-handle", "1", "--token", "secret")]
    public void Parse_InvalidArguments_ReturnsInvalidWithError(params string[] args)
    {
        var result = HostCommandLine.Parse(args);

        Assert.Equal(HostMode.Invalid, result.Mode);
        Assert.False(string.IsNullOrWhiteSpace(result.Error));
    }

    [Fact]
    public void VersionText_ContainsHostAndContractVersions()
    {
        Assert.Contains(HostInfo.Version, HostInfo.VersionText, StringComparison.Ordinal);
        Assert.Contains($"contract {ContractVersion.Major}.{ContractVersion.Minor}", HostInfo.VersionText, StringComparison.Ordinal);
    }
}

using Pillion.Contracts;

namespace Pillion.Host.Tests;

public class HostCommandLineTests
{
    [Fact]
    public void Parse_Pipe_ReturnsRunWithPipeName()
    {
        var result = HostCommandLine.Parse(["--pipe", "pillion-1234"]);

        Assert.Equal(HostMode.Run, result.Mode);
        Assert.Equal("pillion-1234", result.PipeName);
        Assert.Null(result.Error);
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

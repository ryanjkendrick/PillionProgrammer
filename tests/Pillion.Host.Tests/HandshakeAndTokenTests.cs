using System.IO.Pipes;
using System.Text;
using Pillion.Contracts;
using Pillion.Host.Rpc;
using Pillion.Host.Tests.TestSupport;

namespace Pillion.Host.Tests;

public class HandshakeAndTokenTests
{
    private const int Parent = 4242;

    [Fact]
    public void Validate_AllChecksPass_IsAccepted()
    {
        var token = LaunchToken.CreateValue();

        var outcome = HandshakeValidator.Validate(Request(token), LaunchToken.Parse(token), Parent, Parent);

        Assert.True(outcome.Accepted);
        Assert.Null(outcome.Reason);
    }

    [Theory]
    [InlineData(null)]
    [InlineData(Parent + 1)]
    public void Validate_PipeClientIsNotTheParent_FailsPipeClientProcess(int? pipeClient)
    {
        var token = LaunchToken.CreateValue();

        var outcome = HandshakeValidator.Validate(Request(token), LaunchToken.Parse(token), Parent, pipeClient);

        Assert.Equal(HandshakeCheck.PipeClientProcess, outcome.Check);
    }

    [Fact]
    public void Validate_WrongToken_FailsProtocolTokenWithoutRevealingIt()
    {
        var token = LaunchToken.CreateValue();
        var presented = LaunchToken.CreateValue();

        var outcome = HandshakeValidator.Validate(Request(presented), LaunchToken.Parse(token), Parent, Parent);

        Assert.Equal(HandshakeCheck.ProtocolToken, outcome.Check);
        Assert.DoesNotContain(token, outcome.Reason, StringComparison.Ordinal);
        Assert.DoesNotContain(presented, outcome.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void Validate_RequestProcessIdDiffers_FailsClientProcessId()
    {
        var token = LaunchToken.CreateValue();
        var request = Request(token);
        request.ClientProcessId = Parent + 1;

        var outcome = HandshakeValidator.Validate(request, LaunchToken.Parse(token), Parent, Parent);

        Assert.Equal(HandshakeCheck.ClientProcessId, outcome.Check);
    }

    [Theory]
    [InlineData(ContractVersion.Major + 1, 0, false)]
    [InlineData(ContractVersion.Major - 1, 0, false)]
    [InlineData(ContractVersion.Major, ContractVersion.Minor + 3, true)]
    public void Validate_ContractVersion_MajorMustMatch(int major, int minor, bool accepted)
    {
        var token = LaunchToken.CreateValue();
        var request = Request(token);
        request.ContractMajor = major;
        request.ContractMinor = minor;

        var outcome = HandshakeValidator.Validate(request, LaunchToken.Parse(token), Parent, Parent);

        Assert.Equal(accepted, outcome.Accepted);
        if (!accepted)
        {
            Assert.Equal(HandshakeCheck.ContractVersion, outcome.Check);
        }
    }

    [Fact]
    public void Validate_NullRequest_FailsRequest()
    {
        var outcome = HandshakeValidator.Validate(null, LaunchToken.Parse(LaunchToken.CreateValue()), Parent, Parent);

        Assert.Equal(HandshakeCheck.Request, outcome.Check);
    }

    [Fact]
    public void LaunchToken_CreateValue_Is43Base64UrlCharacters()
    {
        var value = LaunchToken.CreateValue();

        Assert.Equal(LaunchToken.Length, value.Length);
        Assert.Matches("^[A-Za-z0-9_-]{43}$", value);
    }

    [Theory]
    [InlineData("")]
    [InlineData("short")]
    [InlineData("AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA=")]
    [InlineData("AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA+")]
    [InlineData("AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA")]
    public void LaunchToken_Parse_RejectsMalformedValuesWithoutEchoingThem(string value)
    {
        var ex = Assert.Throws<FormatException>(() => LaunchToken.Parse(value));

        if (value.Length > 5)
        {
            Assert.DoesNotContain(value, ex.Message, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void LaunchToken_ToString_IsRedacted()
    {
        var value = LaunchToken.CreateValue();

        Assert.DoesNotContain(value, LaunchToken.Parse(value).ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void LaunchToken_Matches_ComparesExactly()
    {
        var value = LaunchToken.CreateValue();
        var token = LaunchToken.Parse(value);

        Assert.True(token.Matches(value));
        Assert.False(token.Matches(value[..^1] + (value[^1] == 'A' ? 'B' : 'A')));
        Assert.False(token.Matches(value + "x"));
        Assert.False(token.Matches(string.Empty));
        Assert.False(token.Matches(null));
    }

    [Theory]
    [InlineData("\n")]
    [InlineData("\r\n")]
    [InlineData("")]
    public async Task AnonymousPipeSource_ReadsOneLine(string terminator)
    {
        var value = LaunchToken.CreateValue();
        using var server = new AnonymousPipeServerStream(PipeDirection.Out, HandleInheritability.None);
        var source = new AnonymousPipeLaunchTokenSource(server.GetClientHandleAsString());

        var read = source.GetTokenAsync(CancellationToken.None);
        if (terminator.Length > 0)
        {
            // Anything after the first line is ignored.
            server.Write(Encoding.UTF8.GetBytes(value + terminator + "trailing"));
            server.Flush();
        }
        else
        {
            // No newline: end of stream terminates the line.
            server.Write(Encoding.UTF8.GetBytes(value));
            server.Dispose();
        }

        var token = await read.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(token.Matches(value));
    }

    [Fact]
    public async Task AnonymousPipeSource_PipeClosedWithoutToken_Throws()
    {
        using var server = new AnonymousPipeServerStream(PipeDirection.Out, HandleInheritability.None);
        var source = new AnonymousPipeLaunchTokenSource(server.GetClientHandleAsString());

        var read = source.GetTokenAsync(CancellationToken.None);
        server.Dispose();

        await Assert.ThrowsAsync<FormatException>(() => read.WaitAsync(TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public void AddRpcTarget_RejectsDuplicatesClassesAndTheLifecycleContract()
    {
        var services = new Microsoft.Extensions.DependencyInjection.ServiceCollection();
        services.AddRpcTarget<IProbeService, ProbeService>();

        Assert.Throws<InvalidOperationException>(() => services.AddRpcTarget<IProbeService, ProbeService>());
        Assert.Throws<ArgumentException>(() => services.AddRpcTarget<ProbeService>(_ => new ProbeService()));
        Assert.Throws<ArgumentException>(() => services.AddRpcTarget<IHostLifecycleService>(_ => null!));
    }

    private static HandshakeRequest Request(string token) => TestRpcClient.CreateHandshake(token, clientProcessId: Parent);
}

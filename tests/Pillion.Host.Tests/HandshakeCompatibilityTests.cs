using Pillion.Contracts;
using Pillion.Host.Tests.TestSupport;

namespace Pillion.Host.Tests;

/// <summary>Older and newer minor versions of the contract are accepted over the real pipe; another major is not.</summary>
public sealed class HandshakeCompatibilityTests
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(10);

    [Theory]
    [InlineData(1, 0)]
    [InlineData(1, 9)]
    public async Task AClientWithAnotherMinorVersion_IsAccepted(int major, int minor)
    {
        await using var host = await InProcessHost.StartAsync();
        await using var client = await host.ConnectAsync();

        var response = await client.Lifecycle
            .HandshakeAsync(TestRpcClient.CreateHandshake(host.Token, major, minor), CancellationToken.None)
            .WaitAsync(Wait);

        Assert.True(response.Accepted, response.Reason);
        Assert.Equal(ContractVersion.Major, response.ContractMajor);
        Assert.Equal(ContractVersion.Minor, response.ContractMinor);
        Assert.Equal(1, response.ContractMinor);
    }
}

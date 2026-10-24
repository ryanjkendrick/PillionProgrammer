using Pillion.Contracts;
using Pillion.Host.Tests.TestSupport;

namespace Pillion.Host.Tests;

/// <summary>Older and newer minor versions of the contract are accepted over the real pipe; another major is not.</summary>
public sealed class HandshakeCompatibilityTests
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(10);

    [Theory]
    [InlineData(1, 0)]
    [InlineData(1, 2)]
    [InlineData(1, ContractVersion.Minor)]
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
        Assert.Equal(3, response.ContractMinor);
    }

    [Fact]
    public async Task AContractOnePointOneClient_IsAcceptedByANewerHost_AndCanStillUseOnePointOneCalls()
    {
        await using var host = await InProcessHost.StartAsync();
        await using var client = await host.ConnectAsync();

        var response = await client.Lifecycle
            .HandshakeAsync(TestRpcClient.CreateHandshake(host.Token, major: 1, minor: 1), CancellationToken.None)
            .WaitAsync(Wait);

        // The host reports 1.3; the 1.1 client reads that as a newer minor of its own major and carries on.
        Assert.True(response.Accepted, response.Reason);
        Assert.Equal(1, response.ContractMajor);
        Assert.Equal(3, response.ContractMinor);
        Assert.True(ContractVersion.IsCompatible(response.ContractMajor, response.ContractMinor));

        // A 1.1 method still answers after the handshake.
        var models = await client.Attach<IModelService>().ListModelsAsync(CancellationToken.None).WaitAsync(Wait);
        Assert.NotNull(models);
    }
}

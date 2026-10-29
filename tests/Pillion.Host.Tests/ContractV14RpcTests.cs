using Microsoft.Extensions.DependencyInjection;
using Pillion.Contracts;
using Pillion.Host.Rpc;
using Pillion.Host.Tests.TestSupport;
using StreamJsonRpc;

namespace Pillion.Host.Tests;

/// <summary>
/// Contract 1.4 in Pillion Host, over the real pipe (work item 3.5): a 1.3 client is accepted and its calls still answer; the
/// agent, terminal and journal services are served as stubs that say which work item implements them (3.6, 3.11, 3.8); the
/// handshake's Visual Studio installation reaches <see cref="IVsixConnection.Client"/> for the Command Runner (3.11).
/// </summary>
public sealed class ContractV14RpcTests
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(10);

    [Fact]
    public async Task AOnePointThreeClient_IsAcceptedByAOnePointFourHost_AndItsCallsStillAnswer()
    {
        await using var host = await InProcessHost.StartAsync();
        await using var client = await host.ConnectAsync();

        var response = await client.Lifecycle
            .HandshakeAsync(TestRpcClient.CreateHandshake(host.Token, major: 1, minor: 3), CancellationToken.None)
            .WaitAsync(Wait);

        Assert.True(response.Accepted, response.Reason);
        Assert.Equal(1, response.ContractMajor);
        Assert.Equal(4, response.ContractMinor);

        var models = await client.Attach<IModelService>().ListModelsAsync(CancellationToken.None).WaitAsync(Wait);
        Assert.NotNull(models);

        // A 1.3 client sent no Visual Studio installation; the host reads it as unknown, not as an error.
        var vsix = host.Services.GetRequiredService<IVsixConnection>();
        Assert.Equal((1, 3, string.Empty), (vsix.Client!.ContractMajor, vsix.Client.ContractMinor, vsix.Client.VisualStudioInstallationPath));
    }

    [Fact]
    public async Task TheHandshakesVisualStudioInstallation_IsKeptForTheCommandRunner()
    {
        await using var host = await InProcessHost.StartAsync();
        await using var client = await host.ConnectAsync();
        var request = TestRpcClient.CreateHandshake(host.Token);
        request.VisualStudioInstallationPath = @"C:\Program Files\Microsoft Visual Studio\18\Enterprise";
        request.VisualStudioVersion = "18.0.11025.1";

        var response = await client.Lifecycle.HandshakeAsync(request, CancellationToken.None).WaitAsync(Wait);

        Assert.True(response.Accepted, response.Reason);
        var vsix = host.Services.GetRequiredService<IVsixConnection>();
        Assert.Equal(request.VisualStudioInstallationPath, vsix.Client!.VisualStudioInstallationPath);
        Assert.Equal("18.0.11025.1", vsix.Client.VisualStudioVersion);
        Assert.Equal(Environment.ProcessId, vsix.Client.ProcessId);
    }

    [Theory]
    [InlineData("agent", "3.6")]
    [InlineData("terminal", "3.11")]
    [InlineData("journal", "3.8")]
    public async Task TheNewServices_AreServedAsStubsNamingTheirWorkItem(string service, string workItem)
    {
        await using var host = await InProcessHost.StartAsync();
        await using var client = await host.ConnectAndHandshakeAsync();

        Func<Task> call = service switch
        {
            "agent" => () => client.Attach<IAgentService>().GetSessionAsync("s", CancellationToken.None),
            "terminal" => () => client.Attach<ITerminalService>().ListTerminalsAsync(CancellationToken.None),
            _ => () => client.Attach<IJournalService>().ListInterruptedAppliesAsync(CancellationToken.None),
        };

        var ex = await Assert.ThrowsAsync<RemoteInvocationException>(() => call().WaitAsync(Wait));
        var error = Assert.IsType<PillionError>(ex.DeserializedErrorData);
        Assert.Equal(PillionErrorCode.NotImplemented, error.Code);
        Assert.Equal("work item " + workItem, error.Details);
    }

    [Fact]
    public async Task TheAgentServiceStub_AnswersEveryOnePointFourMethodWithNotImplemented()
    {
        await using var host = await InProcessHost.StartAsync();
        await using var client = await host.ConnectAndHandshakeAsync();
        var agent = client.Attach<IAgentService>();

        foreach (var call in new Func<Task>[]
        {
            () => agent.StartSessionAsync(new AgentSessionRequest { Goal = "g", ApprovalMode = AgentApprovalMode.EditsAuto }, CancellationToken.None),
            () => agent.AnswerApprovalAsync(new ApprovalDecision { ApprovalId = "a", IsApproved = true, RememberForSession = true }, CancellationToken.None),
            () => agent.ListSessionsAsync(new AgentSessionListRequest(), CancellationToken.None),
            () => agent.SendMessageAsync(new AgentMessageRequest { SessionId = "s", Message = "m" }, CancellationToken.None),
            () => agent.SetApprovalModeAsync("s", AgentApprovalMode.Ask, CancellationToken.None),
            () => agent.RevertSessionAsync("s", CancellationToken.None),
        })
        {
            var ex = await Assert.ThrowsAsync<RemoteInvocationException>(() => call().WaitAsync(Wait));
            Assert.Equal(PillionErrorCode.NotImplemented, Assert.IsType<PillionError>(ex.DeserializedErrorData).Code);
        }
    }
}

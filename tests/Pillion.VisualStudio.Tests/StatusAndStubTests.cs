using System.Threading;
using System.Threading.Tasks;
using Pillion.Contracts;
using Pillion.VisualStudio.Core;

namespace Pillion.VisualStudio.Tests;

public class StatusAndStubTests
{
    [Theory]
    [InlineData(HostState.Starting, 0, "Pillion: Starting")]
    [InlineData(HostState.Ready, 0, "Pillion: Ready")]
    [InlineData(HostState.Degraded, 0, "Pillion: Degraded")]
    [InlineData(HostState.Restarting, 2, "Pillion: Restarting (2/3)")]
    [InlineData(HostState.Restarting, 0, "Pillion: Restarting")]
    [InlineData(HostState.Failed, 0, "Pillion: Failed, see Tools > Pillion: Open Logs")]
    public void StatusText_MatchesTheStatusBarWording(HostState state, int attempt, string expected) =>
        Assert.Equal(expected, HostStatusText.Format(state, attempt, 3));

    [Fact]
    public async Task ApprovalStub_FailsWithPolicyDenied()
    {
        var targets = HostLocalTargets.CreateNotImplemented();

        var ex = await Assert.ThrowsAsync<PillionRpcException>(() => targets.Approval.RequestApprovalAsync(new ApprovalRequest(), CancellationToken.None));

        Assert.Equal(PillionErrorCode.PolicyDenied, ex.Error.Code);
        Assert.Equal("Not implemented in phase 0", ex.Error.Message);
    }

    [Fact]
    public async Task WorkspaceStub_FailsWithPolicyDenied()
    {
        var targets = HostLocalTargets.CreateNotImplemented();

        var ex = await Assert.ThrowsAsync<PillionRpcException>(() => targets.WorkspaceQuery.GetSymbolAsync(new SymbolRequest(), CancellationToken.None));

        Assert.Equal(PillionErrorCode.PolicyDenied, ex.Error.Code);
    }

    [Fact]
    public async Task EditorStub_RefusesChangesButListsNoDocuments()
    {
        var targets = HostLocalTargets.CreateNotImplemented();

        var ex = await Assert.ThrowsAsync<PillionRpcException>(() => targets.Editor.ApplyChangesetAsync(new Changeset(), CancellationToken.None));
        var documents = await targets.Editor.GetOpenDocumentsAsync(CancellationToken.None);

        Assert.Equal(PillionErrorCode.PolicyDenied, ex.Error.Code);
        Assert.Empty(documents);
    }
}

using System;
using System.Threading;
using System.Threading.Tasks;
using Pillion.Contracts;

namespace Pillion.VisualStudio.Core;

/// <summary>The services the VSIX exposes to the host on every connection (host-to-VSIX calls).</summary>
public sealed class HostLocalTargets
{
    /// <summary>Initializes a new instance of the <see cref="HostLocalTargets"/> class with a trust service that refuses every call (contract 1.4; the trust prompt is work item 3.14).</summary>
    /// <param name="editor">The editor service.</param>
    /// <param name="approval">The approval service.</param>
    /// <param name="workspaceQuery">The workspace query service.</param>
    public HostLocalTargets(IEditorService editor, IApprovalService approval, IWorkspaceQueryService workspaceQuery)
        : this(editor, approval, workspaceQuery, new NotImplementedTrustService())
    {
    }

    /// <summary>Initializes a new instance of the <see cref="HostLocalTargets"/> class.</summary>
    /// <param name="editor">The editor service.</param>
    /// <param name="approval">The approval service.</param>
    /// <param name="workspaceQuery">The workspace query service.</param>
    /// <param name="trust">The workspace trust service (contract 1.4).</param>
    public HostLocalTargets(IEditorService editor, IApprovalService approval, IWorkspaceQueryService workspaceQuery, ITrustService trust)
    {
        Editor = editor ?? throw new ArgumentNullException(nameof(editor));
        Approval = approval ?? throw new ArgumentNullException(nameof(approval));
        WorkspaceQuery = workspaceQuery ?? throw new ArgumentNullException(nameof(workspaceQuery));
        Trust = trust ?? throw new ArgumentNullException(nameof(trust));
    }

    /// <summary>Gets the editor service.</summary>
    public IEditorService Editor { get; }

    /// <summary>Gets the approval service.</summary>
    public IApprovalService Approval { get; }

    /// <summary>Gets the workspace query service.</summary>
    public IWorkspaceQueryService WorkspaceQuery { get; }

    /// <summary>Gets the workspace trust service (contract 1.4).</summary>
    public ITrustService Trust { get; }

    /// <summary>Creates targets that refuse every call (phase 0), with an editor that reports no open documents.</summary>
    /// <returns>The targets.</returns>
    public static HostLocalTargets CreateNotImplemented() =>
        new(new NotImplementedEditorService(), new NotImplementedApprovalService(), new NotImplementedWorkspaceQueryService(), new NotImplementedTrustService());
}

/// <summary>Phase 0 stubs: every call fails with <see cref="PillionErrorCode.PolicyDenied"/>.</summary>
public static class PhaseZero
{
    /// <summary>The message of the error the stubs throw.</summary>
    public const string NotImplementedMessage = "Not implemented in phase 0";

    /// <summary>Creates the exception the stubs fail with.</summary>
    /// <returns>A <see cref="PillionRpcException"/> with <see cref="PillionErrorCode.PolicyDenied"/>.</returns>
    public static PillionRpcException NotImplemented() =>
        new(new PillionError { Code = PillionErrorCode.PolicyDenied, Message = NotImplementedMessage });
}

/// <summary>
/// Contract 1.4 stubs in the VSIX (work item 3.5): a host-to-VSIX method that is on the contract before the work item that
/// implements it lands answers with <see cref="PillionErrorCode.NotImplemented"/> naming the feature and the work item, as the
/// host's stubs do.
/// </summary>
public static class NotYetImplemented
{
    /// <summary>Creates the exception a stub fails with.</summary>
    /// <param name="feature">What is missing, for the user, for example "Reading an open document".</param>
    /// <param name="workItem">The work item that implements it, for example "3.13".</param>
    /// <returns>A <see cref="PillionRpcException"/> with <see cref="PillionErrorCode.NotImplemented"/>.</returns>
    public static PillionRpcException For(string feature, string workItem) => new(new PillionError
    {
        Code = PillionErrorCode.NotImplemented,
        Message = feature + " is not available in this version of Pillion Programmer.",
        RetryHint = "Update Pillion Programmer.",
        Details = "work item " + workItem,
    });
}

/// <summary>An <see cref="IEditorService"/> that refuses changes and reports no open documents.</summary>
public class NotImplementedEditorService : IEditorService
{
    /// <inheritdoc/>
    public Task<ApplyResult> ApplyChangesetAsync(Changeset changeset, CancellationToken cancellationToken) =>
        Task.FromException<ApplyResult>(PhaseZero.NotImplemented());

    /// <inheritdoc/>
    public Task<PreviewDecision> ShowPreviewAsync(Changeset changeset, CancellationToken cancellationToken) =>
        Task.FromException<PreviewDecision>(PhaseZero.NotImplemented());

    /// <inheritdoc/>
    public virtual Task<OpenDocument[]> GetOpenDocumentsAsync(CancellationToken cancellationToken) =>
        Task.FromResult(Array.Empty<OpenDocument>());

    /// <inheritdoc/>
    public virtual Task<DocumentText> ReadDocumentAsync(string path, CancellationToken cancellationToken) =>
        Task.FromException<DocumentText>(NotYetImplemented.For("Reading an open document's text", "3.13"));
}

/// <summary>An <see cref="IApprovalService"/> that refuses every call.</summary>
public sealed class NotImplementedApprovalService : IApprovalService
{
    /// <inheritdoc/>
    public Task<ApprovalDecision> RequestApprovalAsync(ApprovalRequest request, CancellationToken cancellationToken) =>
        Task.FromException<ApprovalDecision>(PhaseZero.NotImplemented());
}

/// <summary>An <see cref="ITrustService"/> that refuses every call (contract 1.4; the trust prompt is work item 3.14), which the host reads as not trusted.</summary>
public sealed class NotImplementedTrustService : ITrustService
{
    /// <inheritdoc/>
    public Task<TrustDecision> RequestTrustAsync(TrustRequest request, CancellationToken cancellationToken) =>
        Task.FromException<TrustDecision>(NotYetImplemented.For("The workspace trust prompt", "3.14"));
}

/// <summary>An <see cref="IWorkspaceQueryService"/> that refuses every call.</summary>
public sealed class NotImplementedWorkspaceQueryService : IWorkspaceQueryService
{
    /// <inheritdoc/>
    public Task<FindReferencesResponse> FindReferencesAsync(FindReferencesRequest request, CancellationToken cancellationToken) =>
        Task.FromException<FindReferencesResponse>(PhaseZero.NotImplemented());

    /// <inheritdoc/>
    public Task<DiagnosticsResponse> GetDiagnosticsAsync(DiagnosticsRequest request, CancellationToken cancellationToken) =>
        Task.FromException<DiagnosticsResponse>(PhaseZero.NotImplemented());

    /// <inheritdoc/>
    public Task<SymbolResponse> GetSymbolAsync(SymbolRequest request, CancellationToken cancellationToken) =>
        Task.FromException<SymbolResponse>(PhaseZero.NotImplemented());

    /// <inheritdoc/>
    public Task<RenameSymbolResponse> RenameSymbolAsync(RenameSymbolRequest request, CancellationToken cancellationToken) =>
        Task.FromException<RenameSymbolResponse>(PhaseZero.NotImplemented());

    /// <inheritdoc/>
    public Task<RunTestsResponse> RunTestsAsync(RunTestsRequest request, CancellationToken cancellationToken) =>
        Task.FromException<RunTestsResponse>(PhaseZero.NotImplemented());

    // TODO(2.14): the live Roslyn workspace answers callers_of off the UI thread, within the budgets in the contract.

    /// <inheritdoc/>
    public Task<CallersOfResponse> CallersOfAsync(CallersOfRequest request, CancellationToken cancellationToken) =>
        Task.FromException<CallersOfResponse>(PhaseZero.NotImplemented());

    /// <inheritdoc/>
    public Task<RenamePlan> PlanRenameAsync(RenameSymbolRequest request, CancellationToken cancellationToken) =>
        Task.FromException<RenamePlan>(NotYetImplemented.For("Planning a symbol rename", "3.24"));
}

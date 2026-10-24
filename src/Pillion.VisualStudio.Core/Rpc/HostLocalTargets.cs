using System;
using System.Threading;
using System.Threading.Tasks;
using Pillion.Contracts;

namespace Pillion.VisualStudio.Core;

/// <summary>The services the VSIX exposes to the host on every connection (host-to-VSIX calls).</summary>
public sealed class HostLocalTargets
{
    /// <summary>Initializes a new instance of the <see cref="HostLocalTargets"/> class.</summary>
    /// <param name="editor">The editor service.</param>
    /// <param name="approval">The approval service.</param>
    /// <param name="workspaceQuery">The workspace query service.</param>
    public HostLocalTargets(IEditorService editor, IApprovalService approval, IWorkspaceQueryService workspaceQuery)
    {
        Editor = editor ?? throw new ArgumentNullException(nameof(editor));
        Approval = approval ?? throw new ArgumentNullException(nameof(approval));
        WorkspaceQuery = workspaceQuery ?? throw new ArgumentNullException(nameof(workspaceQuery));
    }

    /// <summary>Gets the editor service.</summary>
    public IEditorService Editor { get; }

    /// <summary>Gets the approval service.</summary>
    public IApprovalService Approval { get; }

    /// <summary>Gets the workspace query service.</summary>
    public IWorkspaceQueryService WorkspaceQuery { get; }

    /// <summary>Creates targets that refuse every call (phase 0), with an editor that reports no open documents.</summary>
    /// <returns>The targets.</returns>
    public static HostLocalTargets CreateNotImplemented() =>
        new(new NotImplementedEditorService(), new NotImplementedApprovalService(), new NotImplementedWorkspaceQueryService());
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
}

/// <summary>An <see cref="IApprovalService"/> that refuses every call.</summary>
public sealed class NotImplementedApprovalService : IApprovalService
{
    /// <inheritdoc/>
    public Task<ApprovalDecision> RequestApprovalAsync(ApprovalRequest request, CancellationToken cancellationToken) =>
        Task.FromException<ApprovalDecision>(PhaseZero.NotImplemented());
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
}

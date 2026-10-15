using Pillion.Common.Settings;
using Pillion.Contracts;
using Pillion.Host.Rpc;

namespace Pillion.Host.Conversations;

/// <summary>
/// The contract 1.3 <see cref="IConversationService"/> target. Every call needs the <see cref="FeatureNames.LocalStore"/>
/// feature (<see cref="PillionErrorCode.PolicyDenied"/> naming the flag while it is off); with the flag on, every call fails
/// with <see cref="PillionErrorCode.NotImplemented"/> until work item 2.2 implements the service over the Local Store's
/// <c>IConversationRepository</c>.
/// </summary>
internal sealed class ConversationRpcService : IConversationService
{
    /// <summary>The message of the error returned while the <see cref="FeatureNames.LocalStore"/> feature is off.</summary>
    internal const string LocalStoreDisabledMessage =
        "Saved conversations are turned off by settings or policy (feature localStore). "
        + "Set features.localStore to true in settings.json, or remove it from disabledFeatures in the repository policy.";

    private const string WorkItem = "2.2";

    private readonly IFeatureFlags? _features;

    /// <summary>Initializes a new instance of the <see cref="ConversationRpcService"/> class.</summary>
    /// <param name="features">The feature flags; <see langword="null"/> means every feature is on.</param>
    public ConversationRpcService(IFeatureFlags? features = null)
    {
        _features = features;
    }

    // TODO(2.2): implement every method over IConversationRepository (conversations.db); map LocalStoreException as is.

    /// <inheritdoc/>
    public Task<ConversationPage> ListConversationsAsync(ConversationListRequest request, CancellationToken cancellationToken) =>
        Task.FromException<ConversationPage>(NotYet("Listing saved conversations"));

    /// <inheritdoc/>
    public Task<ConversationDetail?> GetConversationAsync(string conversationId, CancellationToken cancellationToken) =>
        Task.FromException<ConversationDetail?>(NotYet("Opening a saved conversation"));

    /// <inheritdoc/>
    public Task<ConversationSummary> CreateConversationAsync(ConversationCreateRequest request, CancellationToken cancellationToken) =>
        Task.FromException<ConversationSummary>(NotYet("Saving conversations"));

    /// <inheritdoc/>
    public Task<bool> RenameConversationAsync(string conversationId, string title, CancellationToken cancellationToken) =>
        Task.FromException<bool>(NotYet("Renaming a saved conversation"));

    /// <inheritdoc/>
    public Task<bool> DeleteConversationAsync(string conversationId, CancellationToken cancellationToken) =>
        Task.FromException<bool>(NotYet("Deleting a saved conversation"));

    /// <inheritdoc/>
    public Task<DeleteAllConversationsResult> DeleteAllConversationsAsync(CancellationToken cancellationToken) =>
        Task.FromException<DeleteAllConversationsResult>(NotYet("Deleting all saved conversations"));

    /// <inheritdoc/>
    public Task<bool> SetPinsAsync(string conversationId, ConversationPin[] pins, CancellationToken cancellationToken) =>
        Task.FromException<bool>(NotYet("Saving a conversation's pins"));

    private PillionRpcException NotYet(string feature)
    {
        if (_features is not null && !_features.IsEnabled(FeatureNames.LocalStore))
        {
            return new PillionRpcException(new PillionError { Code = PillionErrorCode.PolicyDenied, Message = LocalStoreDisabledMessage });
        }

        return RpcErrors.NotImplemented(feature, WorkItem);
    }
}

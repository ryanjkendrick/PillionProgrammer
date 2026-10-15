namespace Pillion.LocalStore.Conversations;

/// <summary>How long one kind of data is kept.</summary>
/// <param name="MaxAge">The age after which it is pruned, or <see langword="null"/> to keep it until the user deletes it.</param>
public sealed record RetentionRule(TimeSpan? MaxAge)
{
    /// <summary>Gets the rule that keeps data until the user deletes it.</summary>
    public static RetentionRule UntilDeleted { get; } = new((TimeSpan?)null);
}

/// <summary>
/// The retention rules of <c>conversations.db</c>. The defaults are design section 10's: conversations and the Outbound
/// log are kept until the user deletes them (Delete all in Options).
/// </summary>
public sealed class ConversationRetentionPolicy
{
    /// <summary>Gets the rule for conversations, with their messages and pins.</summary>
    public RetentionRule Conversations { get; init; } = RetentionRule.UntilDeleted;

    /// <summary>Gets the rule for the Outbound log.</summary>
    public RetentionRule OutboundLog { get; init; } = RetentionRule.UntilDeleted;
}

/// <summary>What <see cref="ConversationRetention.PruneAsync"/> removed.</summary>
/// <param name="Conversations">Conversations removed.</param>
/// <param name="OutboundEntries">Outbound log entries removed.</param>
public sealed record PruneResult(int Conversations, int OutboundEntries);

/// <summary>
/// Applies the retention rules of <c>conversations.db</c> and offers Delete all. With the default policy pruning removes
/// nothing: conversations and the Outbound log stay until the user deletes them.
/// </summary>
public sealed class ConversationRetention
{
    private readonly IConversationStore _store;
    private readonly TimeProvider _time;

    /// <summary>Initializes a new instance of the <see cref="ConversationRetention"/> class.</summary>
    /// <param name="store">The conversation store.</param>
    /// <param name="policy">The rules, or <see langword="null"/> for the design defaults.</param>
    /// <param name="time">The clock, or <see langword="null"/> for the system clock.</param>
    public ConversationRetention(IConversationStore store, ConversationRetentionPolicy? policy = null, TimeProvider? time = null)
    {
        ArgumentNullException.ThrowIfNull(store);
        _store = store;
        Policy = policy ?? new ConversationRetentionPolicy();
        _time = time ?? TimeProvider.System;
    }

    /// <summary>Gets the rules in force.</summary>
    public ConversationRetentionPolicy Policy { get; }

    /// <summary>Removes the data the rules say has expired. Nothing is opened when no rule has an age limit.</summary>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>What was removed.</returns>
    public async Task<PruneResult> PruneAsync(CancellationToken cancellationToken = default)
    {
        if (Policy.Conversations.MaxAge is null && Policy.OutboundLog.MaxAge is null)
        {
            return new PruneResult(0, 0);
        }

        var repository = await _store.GetRepositoryAsync(cancellationToken).ConfigureAwait(false);
        var now = _time.GetUtcNow();
        var conversations = Policy.Conversations.MaxAge is { } conversationAge
            ? await repository.DeleteConversationsUpdatedBeforeAsync(now - conversationAge, cancellationToken).ConfigureAwait(false)
            : 0;
        var outbound = Policy.OutboundLog.MaxAge is { } outboundAge
            ? await repository.DeleteOutboundBeforeAsync(now - outboundAge, cancellationToken).ConfigureAwait(false)
            : 0;
        return new PruneResult(conversations, outbound);
    }

    /// <summary>Deletes every conversation and the whole Outbound log (Delete all in Options).</summary>
    /// <param name="cancellationToken">Cancels the call before the delete is committed.</param>
    /// <returns>What was deleted.</returns>
    public async Task<DeleteAllResult> DeleteAllAsync(CancellationToken cancellationToken = default)
    {
        var repository = await _store.GetRepositoryAsync(cancellationToken).ConfigureAwait(false);
        return await repository.DeleteAllAsync(cancellationToken).ConfigureAwait(false);
    }
}

namespace Pillion.Contracts;

/// <summary>Well-known error codes returned across the RPC boundary.</summary>
public enum PillionErrorCode
{
    /// <summary>The model provider could not be reached or is not configured.</summary>
    ProviderUnavailable,

    /// <summary>The requested local model is not installed.</summary>
    ModelNotInstalled,

    /// <summary>The request does not fit in the model's context window.</summary>
    ContextOverflow,

    /// <summary>The request was blocked by a policy (for example an organisation or privacy setting).</summary>
    PolicyDenied,

    /// <summary>The user denied an approval the operation required.</summary>
    ApprovalDenied,

    /// <summary>The host is restarting; the request may be retried once it is ready.</summary>
    HostRestarting,

    /// <summary>The operation was cancelled.</summary>
    Cancelled,

    /// <summary>
    /// The Local Store (an encrypted database such as <c>conversations.db</c> or an <c>index.db</c>) could not be opened or
    /// used: its key is missing or unreadable, it was written by a newer schema, a migration failed, or the encrypting SQLite
    /// library is missing. Added in contract 1.3; a 1.2 host reported these as <see cref="PolicyDenied"/>.
    /// </summary>
    StorageUnavailable,

    /// <summary>
    /// The method is on the contract but this build of the host does not implement it yet (a later work item does). The
    /// message names the feature. Added in contract 1.3.
    /// </summary>
    NotImplemented,
}

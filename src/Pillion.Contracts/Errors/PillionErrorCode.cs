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
}

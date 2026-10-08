using Pillion.Contracts;

namespace Pillion.Host.Rpc;

/// <summary>Factory methods for the <see cref="PillionRpcException"/>s the RPC layer throws.</summary>
internal static class RpcErrors
{
    /// <summary>The message of the error returned for calls made before an accepted handshake.</summary>
    public const string HandshakeRequiredMessage = "Handshake required";

    /// <summary>The message of the error returned when an operation is cancelled.</summary>
    public const string CancelledMessage = "The operation was cancelled.";

    /// <summary>Creates the error for a call made before the handshake was accepted.</summary>
    /// <returns>A <see cref="PillionErrorCode.PolicyDenied"/> exception.</returns>
    public static PillionRpcException HandshakeRequired() => new(new PillionError
    {
        Code = PillionErrorCode.PolicyDenied,
        Message = HandshakeRequiredMessage,
        RetryHint = "Call HandshakeAsync first.",
    });

    /// <summary>Creates the error for a host-to-VSIX call made while no VSIX is connected.</summary>
    /// <returns>A <see cref="PillionErrorCode.HostRestarting"/> exception.</returns>
    public static PillionRpcException NotConnected() => new(new PillionError
    {
        Code = PillionErrorCode.HostRestarting,
        Message = "Visual Studio is not connected to Pillion Host.",
        RetryHint = "Retry once Visual Studio has reconnected.",
    });

    /// <summary>Creates the structured error sent for a cancelled operation.</summary>
    /// <returns>A <see cref="PillionErrorCode.Cancelled"/> error.</returns>
    public static PillionError Cancelled() => new()
    {
        Code = PillionErrorCode.Cancelled,
        Message = CancelledMessage,
    };
}

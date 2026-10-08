using System;
using System.IO;
using Pillion.Contracts;
using StreamJsonRpc;

namespace Pillion.VisualStudio.Core;

/// <summary>
/// Turns an exception from a host call into text for the user: the <see cref="PillionError"/>'s message and retry hint
/// when the host sent one (as a <see cref="PillionRpcException"/> from <see cref="IHostConnection.GetProxy{T}"/>, or
/// as the typed error data of a <see cref="RemoteInvocationException"/>), and plain wording otherwise.
/// </summary>
public sealed class RpcFailure
{
    private RpcFailure(string message, string? retryHint, PillionErrorCode? code, bool hostLost)
    {
        Message = message;
        RetryHint = retryHint;
        Code = code;
        HostLost = hostLost;
    }

    /// <summary>Gets the user-facing message.</summary>
    public string Message { get; }

    /// <summary>Gets the retry hint, when the host supplied one.</summary>
    public string? RetryHint { get; }

    /// <summary>Gets the error code, when the host supplied one.</summary>
    public PillionErrorCode? Code { get; }

    /// <summary>Gets a value indicating whether the failure was the host going away (restart, crash or disconnect).</summary>
    public bool HostLost { get; }

    /// <summary>Describes an exception from a host call.</summary>
    /// <param name="exception">The exception.</param>
    /// <returns>The description.</returns>
    public static RpcFailure Describe(Exception exception)
    {
        if (exception is null)
        {
            throw new ArgumentNullException(nameof(exception));
        }

        var current = exception;
        while (current is AggregateException { InnerException: { } inner })
        {
            current = inner;
        }

        switch (current)
        {
            case PillionRpcException pillion:
                return FromError(pillion.Error);

            case RemoteMethodNotFoundException:
                return new RpcFailure(
                    "This version of Pillion Host does not offer that service yet.",
                    "Update Pillion Programmer, or check Tools > Pillion: Open Logs.",
                    code: null,
                    hostLost: false);

            case RemoteInvocationException remote when PillionClientJsonRpc.TryGetPillionError(remote, out var error):
                return FromError(error!);

            case RemoteInvocationException remote:
                return new RpcFailure(remote.Message, retryHint: null, code: null, hostLost: false);

            case ConnectionLostException or ObjectDisposedException or IOException:
                return new RpcFailure("Pillion Host disconnected.", "Retry once Pillion Host is ready.", PillionErrorCode.HostRestarting, hostLost: true);

            default:
                return new RpcFailure(current.Message, retryHint: null, code: null, hostLost: false);
        }
    }

    private static RpcFailure FromError(PillionError error) => new(
        string.IsNullOrWhiteSpace(error.Message) ? error.Code.ToString() : error.Message,
        string.IsNullOrWhiteSpace(error.RetryHint) ? null : error.RetryHint,
        error.Code,
        hostLost: error.Code == PillionErrorCode.HostRestarting);
}

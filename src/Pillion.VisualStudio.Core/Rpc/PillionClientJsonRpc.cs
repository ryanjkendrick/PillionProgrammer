using System;
using System.Reflection;
using Pillion.Contracts;
using StreamJsonRpc;
using StreamJsonRpc.Protocol;

namespace Pillion.VisualStudio.Core;

/// <summary>
/// The VSIX side's <see cref="JsonRpc"/>. Maps <see cref="PillionError"/> in both directions the way Pillion Host does:
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item>Errors from the host with code <see cref="JsonRpcErrorCode.InvocationError"/> (-32000) or
/// <see cref="JsonRpcErrorCode.RequestCanceled"/> (-32800) carry a <see cref="PillionError"/> as <c>data</c>, so
/// <see cref="RemoteInvocationException.DeserializedErrorData"/> is typed. Use <see cref="TryGetPillionError"/>.</item>
/// <item>A <see cref="PillionRpcException"/> thrown by one of the VSIX's own targets (the phase 0 stubs) goes back to
/// the host as code -32000 with the <see cref="PillionError"/> as <c>data</c>; cancellation as -32800 with
/// <see cref="PillionErrorCode.Cancelled"/>.</item>
/// </list>
/// </remarks>
public sealed class PillionClientJsonRpc : JsonRpc
{
    /// <summary>Initializes a new instance of the <see cref="PillionClientJsonRpc"/> class.</summary>
    /// <param name="handler">The message handler.</param>
    public PillionClientJsonRpc(IJsonRpcMessageHandler handler)
        : base(handler)
    {
    }

    /// <summary>Gets the <see cref="PillionError"/> a host call failed with, when the host supplied one.</summary>
    /// <param name="exception">The exception from a proxy call.</param>
    /// <param name="error">The error.</param>
    /// <returns><see langword="true"/> when <paramref name="exception"/> carries a <see cref="PillionError"/>.</returns>
    public static bool TryGetPillionError(Exception? exception, out PillionError? error)
    {
        error = (exception as RemoteInvocationException)?.DeserializedErrorData as PillionError;
        return error is not null;
    }

    /// <inheritdoc/>
    protected override Type? GetErrorDetailsDataType(JsonRpcError error) =>
        error?.Error?.Code is JsonRpcErrorCode.InvocationError or JsonRpcErrorCode.RequestCanceled
            ? typeof(PillionError)
            : base.GetErrorDetailsDataType(error!);

    /// <inheritdoc/>
    protected override JsonRpcError.ErrorDetail CreateErrorDetails(JsonRpcRequest request, Exception exception)
    {
        switch (Unwrap(exception))
        {
            case PillionRpcException pillion:
                return new JsonRpcError.ErrorDetail
                {
                    Code = JsonRpcErrorCode.InvocationError,
                    Message = pillion.Error.Message,
                    Data = pillion.Error,
                };

            case OperationCanceledException:
                return new JsonRpcError.ErrorDetail
                {
                    Code = JsonRpcErrorCode.RequestCanceled,
                    Message = "The operation was cancelled.",
                    Data = new PillionError { Code = PillionErrorCode.Cancelled, Message = "The operation was cancelled." },
                };

            default:
                return base.CreateErrorDetails(request, exception);
        }
    }

    private static Exception Unwrap(Exception exception)
    {
        var current = exception;
        while (current is TargetInvocationException or AggregateException && current.InnerException is not null)
        {
            current = current.InnerException;
        }

        return current;
    }
}

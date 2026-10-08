using System.Reflection;
using Pillion.Contracts;
using StreamJsonRpc;
using StreamJsonRpc.Protocol;

namespace Pillion.FakeHost;

/// <summary>
/// Maps <see cref="PillionError"/> in both directions as Pillion Host does: reads it from -32000 and -32800 errors, and
/// sends a <see cref="PillionRpcException"/> as -32000 with the error as <c>data</c> and a cancellation as -32800 with
/// <see cref="PillionErrorCode.Cancelled"/>.
/// </summary>
internal sealed class FakeHostJsonRpc(IJsonRpcMessageHandler handler) : JsonRpc(handler)
{
    protected override Type? GetErrorDetailsDataType(JsonRpcError error) =>
        error?.Error?.Code is JsonRpcErrorCode.InvocationError or JsonRpcErrorCode.RequestCanceled
            ? typeof(PillionError)
            : base.GetErrorDetailsDataType(error!);

    protected override JsonRpcError.ErrorDetail CreateErrorDetails(JsonRpcRequest request, Exception exception)
    {
        var current = exception;
        while (current is TargetInvocationException or AggregateException && current.InnerException is not null)
        {
            current = current.InnerException;
        }

        return current switch
        {
            PillionRpcException pillion => new JsonRpcError.ErrorDetail
            {
                Code = JsonRpcErrorCode.InvocationError,
                Message = pillion.Error.Message,
                Data = pillion.Error,
            },
            OperationCanceledException => new JsonRpcError.ErrorDetail
            {
                Code = JsonRpcErrorCode.RequestCanceled,
                Message = "The operation was cancelled.",
                Data = new PillionError { Code = PillionErrorCode.Cancelled, Message = "The operation was cancelled." },
            },
            _ => base.CreateErrorDetails(request, exception),
        };
    }
}

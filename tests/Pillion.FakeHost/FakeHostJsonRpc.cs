using Pillion.Contracts;
using StreamJsonRpc;
using StreamJsonRpc.Protocol;

namespace Pillion.FakeHost;

/// <summary>Reads <see cref="PillionError"/> from -32000 and -32800 errors, as Pillion Host does.</summary>
internal sealed class FakeHostJsonRpc(IJsonRpcMessageHandler handler) : JsonRpc(handler)
{
    protected override Type? GetErrorDetailsDataType(JsonRpcError error) =>
        error?.Error?.Code is JsonRpcErrorCode.InvocationError or JsonRpcErrorCode.RequestCanceled
            ? typeof(PillionError)
            : base.GetErrorDetailsDataType(error!);
}

using System.IO.Pipes;
using System.Threading.Channels;
using Pillion.Contracts;
using StreamJsonRpc;
using StreamJsonRpc.Protocol;

namespace Pillion.Host.Tests.TestSupport;

/// <summary>
/// A client built the way the VSIX is expected to build one: NamedPipeClientStream, LengthHeaderMessageHandler,
/// MessagePackFormatter with default options, proxies from <see cref="JsonRpc.Attach{T}()"/>, and a local
/// <c>hostStateChanged</c> method.
/// </summary>
internal sealed class TestRpcClient : IAsyncDisposable
{
    private readonly NamedPipeClientStream _pipe;

    private TestRpcClient(NamedPipeClientStream pipe, ClientJsonRpc rpc)
    {
        _pipe = pipe;
        Rpc = rpc;
    }

    public JsonRpc Rpc { get; }

    /// <summary>Gets every error response received, as it arrived on the wire.</summary>
    public ChannelReader<JsonRpcError> ReceivedErrors => ((ClientJsonRpc)Rpc).ReceivedErrors.Reader;

    public IHostLifecycleService Lifecycle { get; private set; } = null!;

    public Channel<HostState> StateNotifications { get; } = Channel.CreateUnbounded<HostState>();

    public static async Task<TestRpcClient> ConnectAsync(string pipeName, Action<JsonRpc>? configure = null, TimeSpan? timeout = null)
    {
        var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        try
        {
            await pipe.ConnectAsync(timeout ?? TimeSpan.FromSeconds(10), CancellationToken.None);
        }
        catch
        {
            await pipe.DisposeAsync();
            throw;
        }

        var formatter = new MessagePackFormatter();
        var rpc = new ClientJsonRpc(new LengthHeaderMessageHandler(pipe, pipe, formatter));
        var client = new TestRpcClient(pipe, rpc);
        rpc.AddLocalRpcMethod("hostStateChanged", new Action<HostState>(state => client.StateNotifications.Writer.TryWrite(state)));
        configure?.Invoke(rpc);
        client.Lifecycle = rpc.Attach<IHostLifecycleService>();
        rpc.StartListening();
        return client;
    }

    public static HandshakeRequest CreateHandshake(string token, int? major = null, int? minor = null, int? clientProcessId = null) => new()
    {
        ContractMajor = major ?? ContractVersion.Major,
        ContractMinor = minor ?? ContractVersion.Minor,
        ClientProcessId = clientProcessId ?? Environment.ProcessId,
        ProtocolToken = token,
    };

    public Task<HandshakeResponse> HandshakeAsync(string token) =>
        Lifecycle.HandshakeAsync(CreateHandshake(token), CancellationToken.None);

    public T Attach<T>()
        where T : class => Rpc.Attach<T>();

    public async ValueTask DisposeAsync()
    {
        Rpc.Dispose();
        await _pipe.DisposeAsync();
    }

    /// <summary>Deserialises error data as <see cref="PillionError"/>, as the VSIX should.</summary>
    private sealed class ClientJsonRpc(IJsonRpcMessageHandler handler) : JsonRpc(handler)
    {
        public Channel<JsonRpcError> ReceivedErrors { get; } = Channel.CreateUnbounded<JsonRpcError>();

        protected override Type? GetErrorDetailsDataType(JsonRpcError error) =>
            error?.Error?.Code is JsonRpcErrorCode.InvocationError or JsonRpcErrorCode.RequestCanceled
                ? typeof(PillionError)
                : base.GetErrorDetailsDataType(error!);

        protected override void OnResponseReceived(JsonRpcMessage response)
        {
            if (response is JsonRpcError error)
            {
                ReceivedErrors.Writer.TryWrite(error);
            }

            base.OnResponseReceived(response);
        }
    }
}

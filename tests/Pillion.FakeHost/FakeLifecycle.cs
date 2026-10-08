using Pillion.Contracts;
using StreamJsonRpc;

namespace Pillion.FakeHost;

/// <summary>A minimal <see cref="IHostLifecycleService"/> that follows the handshake rules.</summary>
internal sealed class FakeLifecycle(string expectedToken, string mode) : IHostLifecycleService
{
    private readonly TaskCompletionSource _shutdown = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private volatile HostState _state = HostState.Starting;

    public JsonRpc? Rpc { get; set; }

    public Task ShutdownRequested => _shutdown.Task;

    public Task<HandshakeResponse> HandshakeAsync(HandshakeRequest request, CancellationToken cancellationToken)
    {
        var response = new HandshakeResponse
        {
            ContractMajor = ContractVersion.Major,
            ContractMinor = ContractVersion.Minor,
            HostVersion = "0.0.0-fake",
            HostProcessId = Environment.ProcessId,
            Accepted = true,
        };

        if (!string.Equals(request.ProtocolToken, expectedToken, StringComparison.Ordinal))
        {
            response.Accepted = false;
            response.Reason = "token mismatch";
        }
        else if (!ContractVersion.IsCompatible(request.ContractMajor, request.ContractMinor))
        {
            response.Accepted = false;
            response.Reason = "contract mismatch";
        }
        else if (mode == "reject")
        {
            response.Accepted = false;
            response.Reason = "fake host rejects this client";
        }

        Console.Error.WriteLine("fakehost: handshake from pid " + request.ClientProcessId + ", accepted " + response.Accepted);
        if (response.Accepted)
        {
            _ = Task.Run(AfterHandshakeAsync, CancellationToken.None);
        }

        return Task.FromResult(response);
    }

    public Task<HostState> GetStateAsync(CancellationToken cancellationToken) => Task.FromResult(_state);

    public Task ShutdownAsync(CancellationToken cancellationToken)
    {
        Console.Error.WriteLine("fakehost: shutdown requested");
        _shutdown.TrySetResult();
        return Task.CompletedTask;
    }

    private async Task AfterHandshakeAsync()
    {
        var rpc = Rpc!;

        // Like Pillion Host, report connectivity once after the handshake, possibly before the client has the response.
        // PILLION_FAKEHOST_CONNECTIVITY picks the state (Online by default).
        var connectivity = Enum.TryParse<ConnectivityState>(Environment.GetEnvironmentVariable("PILLION_FAKEHOST_CONNECTIVITY"), ignoreCase: true, out var configured)
            ? configured
            : ConnectivityState.Online;
        await rpc.NotifyAsync("connectivityChanged", connectivity).ConfigureAwait(false);
        Console.Error.WriteLine("fakehost: connectivity " + connectivity);

        await Task.Delay(100).ConfigureAwait(false);
        _state = mode == "fail" ? HostState.Failed : HostState.Ready;
        await rpc.NotifyAsync("hostStateChanged", _state).ConfigureAwait(false);

        if (mode != "normal")
        {
            return; // "quiet" and "fail" do not call back into the VSIX.
        }

        try
        {
            var documents = await rpc.Attach<IEditorService>().GetOpenDocumentsAsync(CancellationToken.None).ConfigureAwait(false);
            Console.Error.WriteLine("fakehost: open documents " + documents.Length);
        }
        catch (RemoteInvocationException ex)
        {
            Console.Error.WriteLine("fakehost: open documents failed: " + ex.Message);
        }

        try
        {
            await rpc.Attach<IApprovalService>().RequestApprovalAsync(new ApprovalRequest(), CancellationToken.None).ConfigureAwait(false);
            Console.Error.WriteLine("fakehost: approval unexpectedly succeeded");
        }
        catch (RemoteInvocationException ex)
        {
            var error = ex.DeserializedErrorData as PillionError;
            Console.Error.WriteLine("fakehost: approval refused: " + ex.ErrorCode + " " + error?.Code + " " + ex.Message);
        }
    }
}

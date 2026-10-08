using Microsoft.Extensions.DependencyInjection;
using Pillion.Contracts;
using Pillion.Host.Lifecycle;
using Pillion.Host.Rpc;
using Pillion.Host.Tests.TestSupport;
using StreamJsonRpc;
using Xunit.Abstractions;

namespace Pillion.Host.Tests;

/// <summary>In-process tests: the composed host serves a random pipe and the test connects as the VSIX would.</summary>
public class RpcServerTests(ITestOutputHelper output)
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(5);

    private readonly ITestOutputHelper _output = output;

    [Fact]
    public async Task Handshake_WithValidRequest_IsAccepted()
    {
        await using var host = await InProcessHost.StartAsync();
        await using var client = await host.ConnectAsync();

        var response = await client.HandshakeAsync(host.Token);

        Assert.True(response.Accepted);
        Assert.Null(response.Reason);
        Assert.Equal(ContractVersion.Major, response.ContractMajor);
        Assert.Equal(ContractVersion.Minor, response.ContractMinor);
        Assert.Equal(Environment.ProcessId, response.HostProcessId);
        Assert.Equal(HostInfo.Version, response.HostVersion);
    }

    [Fact]
    public async Task Handshake_WithWrongToken_IsRejectedAndDisconnected()
    {
        await using var host = await InProcessHost.StartAsync();
        await using var client = await host.ConnectAsync();

        var response = await client.HandshakeAsync(LaunchToken.CreateValue());

        Assert.False(response.Accepted);
        Assert.Contains("token", response.Reason, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(host.Token, response.Reason, StringComparison.Ordinal);
        await client.Rpc.Completion.WaitAsync(Wait);
    }

    [Fact]
    public async Task Handshake_WithIncompatibleMajor_IsRejected()
    {
        await using var host = await InProcessHost.StartAsync();
        await using var client = await host.ConnectAsync();

        var response = await client.Lifecycle.HandshakeAsync(
            TestRpcClient.CreateHandshake(host.Token, major: ContractVersion.Major + 1),
            CancellationToken.None);

        Assert.False(response.Accepted);
        Assert.Contains("contract version", response.Reason, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(ContractVersion.Major, response.ContractMajor);
        await client.Rpc.Completion.WaitAsync(Wait);
    }

    [Fact]
    public async Task Handshake_WithDifferentMinor_IsAccepted()
    {
        await using var host = await InProcessHost.StartAsync();
        await using var client = await host.ConnectAsync();

        var response = await client.Lifecycle.HandshakeAsync(
            TestRpcClient.CreateHandshake(host.Token, minor: ContractVersion.Minor + 7),
            CancellationToken.None);

        Assert.True(response.Accepted, response.Reason);
    }

    [Fact]
    public async Task Handshake_FromProcessOtherThanParent_IsRejected()
    {
        await using var host = await InProcessHost.StartAsync(s => s.UseClientProcessId(Environment.ProcessId + 1));
        await using var client = await host.ConnectAsync();

        var response = await client.HandshakeAsync(host.Token);

        Assert.False(response.Accepted);
        Assert.Contains("not the process that launched the host", response.Reason, StringComparison.Ordinal);
        await client.Rpc.Completion.WaitAsync(Wait);
    }

    [Fact]
    public async Task Handshake_WithMismatchedClientProcessIdInRequest_IsRejected()
    {
        await using var host = await InProcessHost.StartAsync();
        await using var client = await host.ConnectAsync();

        var response = await client.Lifecycle.HandshakeAsync(
            TestRpcClient.CreateHandshake(host.Token, clientProcessId: Environment.ProcessId + 1),
            CancellationToken.None);

        Assert.False(response.Accepted);
        Assert.Contains("client process id", response.Reason, StringComparison.Ordinal);
        await client.Rpc.Completion.WaitAsync(Wait);
    }

    [Fact]
    public async Task RejectedHandshake_ResponseReachesTheClientBeforeTheConnectionCloses_UnderLoad()
    {
        // Regression: the host used to close the pipe when StreamJsonRpc raised ResponseSent, which happens before the
        // response is written, so a client on a busy machine saw ConnectionLostException instead of the rejection.
        // Several hosts rejecting at once is what exposed it (one host on an idle machine rarely did).
        async Task RejectRepeatedlyAsync()
        {
            await using var host = await InProcessHost.StartAsync();
            for (var i = 0; i < 25; i++)
            {
                await using var client = await host.ConnectAsync();
                var response = await client.Lifecycle.HandshakeAsync(
                    TestRpcClient.CreateHandshake(host.Token, major: ContractVersion.Major + 1),
                    CancellationToken.None);

                Assert.False(response.Accepted);
                Assert.Contains("contract version", response.Reason, StringComparison.OrdinalIgnoreCase);
                await client.Rpc.Completion.WaitAsync(Wait);
            }
        }

        await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => Task.Run(RejectRepeatedlyAsync)));
    }

    [Fact]
    public async Task Handshake_Twice_IsIdempotent()
    {
        await using var host = await InProcessHost.StartAsync();
        await using var client = await host.ConnectAsync();

        var first = await client.HandshakeAsync(host.Token);
        var second = await client.HandshakeAsync(host.Token);

        Assert.True(first.Accepted);
        Assert.True(second.Accepted);
        Assert.Equal(first.HostProcessId, second.HostProcessId);
        Assert.Equal(HostState.Ready, await client.Lifecycle.GetStateAsync(CancellationToken.None));
    }

    [Fact]
    public async Task CallBeforeHandshake_FailsWithPolicyDenied()
    {
        await using var host = await InProcessHost.StartAsync(s => s.AddRpcTarget<IProbeService, ProbeService>());
        await using var client = await host.ConnectAsync();

        var lifecycleCall = await Assert.ThrowsAsync<RemoteInvocationException>(
            () => client.Lifecycle.GetStateAsync(CancellationToken.None));
        var targetCall = await Assert.ThrowsAsync<RemoteInvocationException>(
            () => client.Attach<IProbeService>().EchoAsync(1, CancellationToken.None));

        foreach (var ex in new[] { lifecycleCall, targetCall })
        {
            var error = Assert.IsType<PillionError>(ex.DeserializedErrorData);
            Assert.Equal(PillionErrorCode.PolicyDenied, error.Code);
            Assert.Equal("Handshake required", error.Message);
            Assert.Equal("Handshake required", ex.Message);
        }

        // Unknown methods are not dispatched at all, so StreamJsonRpc answers method-not-found.
        await Assert.ThrowsAsync<RemoteMethodNotFoundException>(() => client.Rpc.InvokeAsync("NoSuchMethod"));

        // The connection stays usable: a handshake still succeeds.
        Assert.True((await client.HandshakeAsync(host.Token)).Accepted);
        Assert.Equal(5, await client.Attach<IProbeService>().EchoAsync(5, CancellationToken.None));
    }

    [Fact]
    public async Task GetState_AfterHandshake_ReturnsReady()
    {
        await using var host = await InProcessHost.StartAsync();
        await using var client = await host.ConnectAndHandshakeAsync();

        Assert.Equal(HostState.Ready, await client.Lifecycle.GetStateAsync(CancellationToken.None));
    }

    [Fact]
    public async Task StateChanges_AreSentAsHostStateChangedNotifications()
    {
        await using var host = await InProcessHost.StartAsync();
        await using var client = await host.ConnectAndHandshakeAsync();

        Assert.True(host.State.TryTransition(HostState.Degraded, "test"));
        Assert.True(host.State.TryTransition(HostState.Ready, "test"));

        var reader = client.StateNotifications.Reader;
        Assert.Equal(HostState.Degraded, await reader.ReadAsync().AsTask().WaitAsync(Wait));
        Assert.Equal(HostState.Ready, await reader.ReadAsync().AsTask().WaitAsync(Wait));
        Assert.Equal(HostState.Ready, await client.Lifecycle.GetStateAsync(CancellationToken.None));
    }

    [Fact]
    public async Task Streaming_ThroughTestTarget_DeliversEveryChunkInOrder()
    {
        await using var host = await InProcessHost.StartAsync(s => s.AddRpcTarget<IProbeService, ProbeService>());
        await using var client = await host.ConnectAndHandshakeAsync();
        var probe = client.Attach<IProbeService>();

        var chunks = new List<ChatChunk>();
        await foreach (var chunk in probe.StreamAsync(25, CancellationToken.None))
        {
            chunks.Add(chunk);
        }

        Assert.Equal(25, chunks.Count);
        Assert.Equal(Enumerable.Range(0, 25).Select(i => $"chunk {i}"), chunks.Select(c => c.Text));
        Assert.All(chunks.SkipLast(1), c => Assert.False(c.IsFinal));
        Assert.True(chunks[^1].IsFinal);
    }

    [Fact]
    public async Task Streaming_CancelledByClient_StopsTheStream()
    {
        await using var host = await InProcessHost.StartAsync(s => s.AddRpcTarget<IProbeService, ProbeService>());
        await using var client = await host.ConnectAndHandshakeAsync();
        var probe = client.Attach<IProbeService>();
        using var cts = new CancellationTokenSource();

        var received = 0;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        {
            await foreach (var chunk in probe.StreamAsync(1000, cts.Token))
            {
                if (++received == 3)
                {
                    await cts.CancelAsync();
                }
            }
        });

        Assert.InRange(received, 3, 999);
        Assert.Equal(1, await probe.EchoAsync(1, CancellationToken.None));
    }

    [Fact]
    public async Task PillionRpcException_IsMappedToErrorData()
    {
        await using var host = await InProcessHost.StartAsync(s => s.AddRpcTarget<IProbeService, ProbeService>());
        await using var client = await host.ConnectAndHandshakeAsync();

        var ex = await Assert.ThrowsAsync<RemoteInvocationException>(
            () => client.Attach<IProbeService>().ThrowPillionAsync(CancellationToken.None));

        Assert.Equal((int)StreamJsonRpc.Protocol.JsonRpcErrorCode.InvocationError, ex.ErrorCode);
        Assert.Equal(ProbeService.ProviderError.Message, ex.Message);
        var error = Assert.IsType<PillionError>(ex.DeserializedErrorData);
        Assert.Equal(PillionErrorCode.ProviderUnavailable, error.Code);
        Assert.Equal(ProbeService.ProviderError.Message, error.Message);
        Assert.Equal(ProbeService.ProviderError.RetryHint, error.RetryHint);
        Assert.Equal(ProbeService.ProviderError.Details, error.Details);
    }

    [Fact]
    public async Task UnexpectedException_IsGenericWithoutStackTraceOrDetail()
    {
        await using var host = await InProcessHost.StartAsync(s => s.AddRpcTarget<IProbeService, ProbeService>());
        await using var client = await host.ConnectAndHandshakeAsync();

        var ex = await Assert.ThrowsAsync<RemoteInvocationException>(
            () => client.Attach<IProbeService>().ThrowUnexpectedAsync(CancellationToken.None));

        Assert.Equal((int)StreamJsonRpc.Protocol.JsonRpcErrorCode.InvocationError, ex.ErrorCode);
        Assert.StartsWith("An unexpected error occurred in Pillion Host (reference ", ex.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(ProbeService.SecretInUnexpectedException, ex.Message, StringComparison.Ordinal);
        Assert.Null(ex.DeserializedErrorData);
        Assert.Null(ex.ErrorData);
    }

    [Fact]
    public async Task UnexpectedException_IsLoggedInFullOnTheHost()
    {
        await using var host = await InProcessHost.StartAsync(s => s.AddRpcTarget<IProbeService, ProbeService>());
        await using var client = await host.ConnectAndHandshakeAsync();
        var probe = client.Attach<IProbeService>();

        await Assert.ThrowsAsync<RemoteInvocationException>(() => probe.ThrowPillionAsync(CancellationToken.None));
        var ex = await Assert.ThrowsAsync<RemoteInvocationException>(() => probe.ThrowUnexpectedAsync(CancellationToken.None));
        var reference = ex.Message.Split("reference ")[1][..12];

        var log = ReadLog(host.LogDirectory);
        _output.WriteLine(log);
        var line = Assert.Single(log.Split('\n'), l => l.Contains(reference, StringComparison.Ordinal));
        Assert.Contains("\"level\":\"Error\"", line, StringComparison.Ordinal);
        Assert.Contains(ProbeService.SecretInUnexpectedException, line, StringComparison.Ordinal);
        Assert.Contains("ThrowUnexpectedAsync", line, StringComparison.Ordinal);
        Assert.DoesNotContain(host.Token, log, StringComparison.Ordinal);
    }

    [Fact]
    public async Task OperationCanceledException_IsMappedToCancelled()
    {
        await using var host = await InProcessHost.StartAsync(s => s.AddRpcTarget<IProbeService, ProbeService>());
        await using var client = await host.ConnectAndHandshakeAsync();

        // The host answers with code -32800 (RequestCanceled) and PillionError { Code = Cancelled } as data;
        // StreamJsonRpc clients surface that code as an OperationCanceledException.
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => client.Attach<IProbeService>().ThrowCancelledAsync(CancellationToken.None));

        var wire = await client.ReceivedErrors.ReadAsync().AsTask().WaitAsync(Wait);
        Assert.Equal(StreamJsonRpc.Protocol.JsonRpcErrorCode.RequestCanceled, wire.Error!.Code);
        var error = Assert.IsType<PillionError>(wire.Error.GetData<PillionError>());
        Assert.Equal(PillionErrorCode.Cancelled, error.Code);

        // The connection is unaffected.
        Assert.Equal(3, await client.Attach<IProbeService>().EchoAsync(3, CancellationToken.None));
    }

    [Fact]
    public async Task ClientCancellation_CancelsTheServerCall()
    {
        var probe = new ProbeService();
        await using var host = await InProcessHost.StartAsync(s => s.AddRpcTarget<IProbeService>(_ => probe));
        await using var client = await host.ConnectAndHandshakeAsync();
        using var cts = new CancellationTokenSource();

        var call = client.Attach<IProbeService>().WaitUntilCancelledAsync(cts.Token);
        await probe.WaitStarted.Task.WaitAsync(Wait);
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => call);
        Assert.True(await probe.WaitEnded.Task.WaitAsync(Wait));
    }

    [Fact]
    public async Task Shutdown_StopsTheServerWithExitCodeZero()
    {
        var host = await InProcessHost.StartAsync();
        await using (host)
        {
            await using var client = await host.ConnectAndHandshakeAsync();

            await client.Lifecycle.ShutdownAsync(CancellationToken.None);

            await host.Run.WaitAsync(Wait);
            await client.Rpc.Completion.WaitAsync(Wait);
            Assert.Equal(HostShutdown.Success, host.Shutdown.ExitCode);
            await Assert.ThrowsAsync<TimeoutException>(() => host.ConnectAsync(timeout: TimeSpan.FromMilliseconds(300)));
        }
    }

    [Fact]
    public async Task Shutdown_CancelsCallsStillRunningAfterTheDrainTimeout()
    {
        var probe = new ProbeService();
        await using var host = await InProcessHost.StartAsync(s => s.AddRpcTarget<IProbeService>(_ => probe));
        await using var client = await host.ConnectAndHandshakeAsync();

        var call = client.Attach<IProbeService>().WaitUntilCancelledAsync(CancellationToken.None);
        await probe.WaitStarted.Task.WaitAsync(Wait);
        var started = DateTime.UtcNow;
        await client.Lifecycle.ShutdownAsync(CancellationToken.None);

        Assert.True(await probe.WaitEnded.Task.WaitAsync(Wait), "The in-flight call was not cancelled.");
        await host.Run.WaitAsync(Wait);
        Assert.InRange(DateTime.UtcNow - started, TimeSpan.Zero, TimeSpan.FromSeconds(2.5));
        await Assert.ThrowsAnyAsync<Exception>(() => call);
    }

    [Fact]
    public async Task Disconnect_ThenReconnect_HandshakesAgain()
    {
        await using var host = await InProcessHost.StartAsync();

        var first = await host.ConnectAndHandshakeAsync();
        await first.DisposeAsync();

        await using var second = await host.ConnectAsync();
        var response = await second.HandshakeAsync(host.Token);

        Assert.True(response.Accepted, response.Reason);
        Assert.Equal(HostState.Ready, await second.Lifecycle.GetStateAsync(CancellationToken.None));
    }

    [Fact]
    public async Task RejectedConnection_DoesNotStopTheServer()
    {
        await using var host = await InProcessHost.StartAsync();

        await using (var intruder = await host.ConnectAsync())
        {
            Assert.False((await intruder.HandshakeAsync(LaunchToken.CreateValue())).Accepted);
            await intruder.Rpc.Completion.WaitAsync(Wait);
        }

        await using var client = await host.ConnectAndHandshakeAsync();
        Assert.Equal(HostState.Ready, await client.Lifecycle.GetStateAsync(CancellationToken.None));
    }

    [Fact]
    public async Task Connection_WithoutHandshake_IsClosedAfterTheTimeout()
    {
        await using var host = await InProcessHost.StartAsync(
            s => s.Configure<RpcServerOptions>(o => o.HandshakeTimeout = TimeSpan.FromMilliseconds(300)));
        await using var client = await host.ConnectAsync();

        await client.Rpc.Completion.WaitAsync(Wait);
    }

    [Fact]
    public async Task VsixConnection_GivesProxiesToTheConnectedClient()
    {
        await using var host = await InProcessHost.StartAsync();
        var vsix = host.Services.GetRequiredService<IVsixConnection>();

        var notConnected = Assert.Throws<PillionRpcException>(vsix.GetEditorService);
        Assert.Equal(PillionErrorCode.HostRestarting, notConnected.Error.Code);

        await using (var client = await host.ConnectAndHandshakeAsync(rpc => rpc.AddLocalRpcTarget<IEditorService>(new FakeEditorService(), null)))
        {
            Assert.True(vsix.IsConnected);
            var documents = await vsix.GetEditorService().GetOpenDocumentsAsync(CancellationToken.None);
            Assert.Equal(@"C:\src\Program.cs", Assert.Single(documents).Path);
        }

        var deadline = DateTime.UtcNow + Wait;
        while (vsix.IsConnected && DateTime.UtcNow < deadline)
        {
            await Task.Delay(10);
        }

        Assert.False(vsix.IsConnected);
        Assert.Equal(PillionErrorCode.HostRestarting, Assert.Throws<PillionRpcException>(vsix.GetApprovalService).Error.Code);
    }

    private static string ReadLog(string directory)
    {
        var file = Assert.Single(Directory.GetFiles(directory, "host-*.log"));
        using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}

using System.Diagnostics;
using System.IO.Pipes;
using System.Threading.Tasks;
using Pillion.VisualStudio.Core;

namespace Pillion.VisualStudio.Tests;

public class PipeVerificationTests
{
    [Fact]
    public async Task VerifyServer_AcceptsThePipeServedByTheExpectedProcess()
    {
        using var current = Process.GetCurrentProcess();
        var (server, client) = await ConnectAsync();
        using (server)
        using (client)
        {
            HostProcessLauncher.VerifyServer(client, current.Id);
        }
    }

    [Fact]
    public async Task VerifyServer_TreatsASquattedPipeAsARetryableCrash()
    {
        using var current = Process.GetCurrentProcess();
        var (server, client) = await ConnectAsync();
        using (server)
        using (client)
        {
            // The pipe is served by this (test) process, not by the "host" we pretend to have launched.
            var ex = Assert.Throws<HostLaunchException>(() => HostProcessLauncher.VerifyServer(client, current.Id + 1));

            Assert.True(ex.IsRetryable);
            Assert.Contains("not by the host that was started", ex.Message);
        }
    }

    private static async Task<(NamedPipeServerStream Server, NamedPipeClientStream Client)> ConnectAsync()
    {
        var name = LaunchProtocol.CreatePipeName(12345);
        var server = new NamedPipeServerStream(name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
        var waiting = server.WaitForConnectionAsync();
        var client = new NamedPipeClientStream(".", name, PipeDirection.InOut, PipeOptions.Asynchronous);
        client.Connect(5000);
        await waiting;
        return (server, client);
    }
}

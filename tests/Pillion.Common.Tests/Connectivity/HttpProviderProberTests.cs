using System.Net;
using System.Net.Sockets;
using Pillion.Common.Connectivity;

namespace Pillion.Common.Tests.Connectivity;

public sealed class HttpProviderProberTests
{
    /// <summary>
    /// The probe timeout for tests where the answer arrives at once. A reachable listener (or a refused port) answers in
    /// milliseconds, so a long timeout costs nothing on success; it is long so that a stalled CI machine, which was seen
    /// to freeze a whole test host for about twenty seconds, does not turn a slow answer into a spurious Timeout.
    /// The tests that expect a timeout use short ones.
    /// </summary>
    private static readonly TimeSpan StallTolerantTimeout = TimeSpan.FromSeconds(60);

    private static int FreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            return ((IPEndPoint)listener.LocalEndpoint).Port;
        }
        finally
        {
            listener.Stop();
        }
    }

    private static ProbeTarget Target(int port) => new("test", new Uri($"http://127.0.0.1:{port}/"));

    /// <summary>Starts an <see cref="HttpListener"/> on a random port that answers every request with <paramref name="status"/>.</summary>
    private static (HttpListener Listener, int Port) StartListener(int status)
    {
        for (var attempt = 0; ; attempt++)
        {
            var port = FreePort();
            var listener = new HttpListener();
            listener.Prefixes.Add($"http://127.0.0.1:{port}/");
            try
            {
                listener.Start();
            }
            catch (HttpListenerException) when (attempt < 5)
            {
                listener.Close();
                continue;
            }

            _ = Task.Run(async () =>
            {
                while (listener.IsListening)
                {
                    try
                    {
                        var context = await listener.GetContextAsync();
                        context.Response.StatusCode = status;
                        context.Response.Close();
                    }
                    catch (Exception ex) when (ex is HttpListenerException or ObjectDisposedException or InvalidOperationException)
                    {
                        return;
                    }
                }
            });
            return (listener, port);
        }
    }

    [Theory]
    [InlineData(200)]
    [InlineData(401)]
    [InlineData(403)]
    [InlineData(404)]
    [InlineData(500)]
    [InlineData(302)]
    public async Task AnyHttpAnswer_IsReachable(int status)
    {
        var (listener, port) = StartListener(status);
        try
        {
            using var prober = new HttpProviderProber(StallTolerantTimeout);

            var result = await prober.ProbeAsync(Target(port), CancellationToken.None);

            Assert.True(result.Reachable);
            Assert.Equal(ProbeErrorCategory.None, result.ErrorCategory);
            Assert.NotNull(result.Latency);
        }
        finally
        {
            listener.Close();
        }
    }

    [Fact]
    public async Task ClosedPort_IsUnreachableWithAConnectFailure()
    {
        using var prober = new HttpProviderProber(StallTolerantTimeout);

        var result = await prober.ProbeAsync(Target(FreePort()), CancellationToken.None);

        Assert.False(result.Reachable);
        Assert.Equal(ProbeErrorCategory.Connect, result.ErrorCategory);
    }

    [Fact]
    public async Task UnresolvableName_IsUnreachableWithADnsFailure()
    {
        using var prober = new HttpProviderProber(TimeSpan.FromSeconds(10));

        // The .invalid top-level domain is reserved (RFC 6761) and never resolves.
        var result = await prober.ProbeAsync(new ProbeTarget("test", new Uri("http://pillion-connectivity-test.invalid/")), CancellationToken.None);

        Assert.False(result.Reachable);
        Assert.True(result.ErrorCategory is ProbeErrorCategory.Dns or ProbeErrorCategory.Timeout, result.ErrorCategory.ToString());
    }

    [Fact]
    public async Task ListenerThatNeverAnswers_TimesOut()
    {
        var tcp = new TcpListener(IPAddress.Loopback, 0);
        tcp.Start();
        try
        {
            var port = ((IPEndPoint)tcp.LocalEndpoint).Port;

            // Accepts the connection (the backlog does it) and never writes a response.
            using var prober = new HttpProviderProber(TimeSpan.FromMilliseconds(300));
            var result = await prober.ProbeAsync(Target(port), CancellationToken.None);

            Assert.False(result.Reachable);
            Assert.Equal(ProbeErrorCategory.Timeout, result.ErrorCategory);
        }
        finally
        {
            tcp.Stop();
        }
    }

    [Fact]
    public async Task CancellationByTheCaller_Propagates()
    {
        var tcp = new TcpListener(IPAddress.Loopback, 0);
        tcp.Start();
        try
        {
            var port = ((IPEndPoint)tcp.LocalEndpoint).Port;
            using var prober = new HttpProviderProber(TimeSpan.FromSeconds(30));
            using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));

            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => prober.ProbeAsync(Target(port), cts.Token));
        }
        finally
        {
            tcp.Stop();
        }
    }
}

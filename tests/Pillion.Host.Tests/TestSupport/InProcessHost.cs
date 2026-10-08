using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Pillion.Contracts;
using Pillion.Host.Lifecycle;
using Pillion.Host.Rpc;

namespace Pillion.Host.Tests.TestSupport;

/// <summary>Runs the composed host inside the test process on a random pipe name with an injected token.</summary>
internal sealed class InProcessHost : IAsyncDisposable
{
    private readonly Task _run;
    private readonly TempDirectory _logs;

    private InProcessHost(IHost host, Task run, string pipeName, string token, TempDirectory logs)
    {
        _run = run;
        PipeName = pipeName;
        Token = token;
        _logs = logs;
        Services = host.Services;
        State = Services.GetRequiredService<HostStateMachine>();
        Shutdown = Services.GetRequiredService<HostShutdown>();
        Lifetime = Services.GetRequiredService<IHostApplicationLifetime>();
    }

    public string PipeName { get; }

    public string Token { get; }

    public string LogDirectory => _logs.Path;

    /// <summary>Gets the host's services. Only valid until the host has stopped (RunAsync disposes it).</summary>
    public IServiceProvider Services { get; }

    public HostStateMachine State { get; }

    public HostShutdown Shutdown { get; }

    public IHostApplicationLifetime Lifetime { get; }

    public Task Run => _run;

    public static async Task<InProcessHost> StartAsync(Action<IServiceCollection>? configureServices = null)
    {
        var pipeName = "pillion-test-" + Guid.NewGuid().ToString("N");
        var token = LaunchToken.CreateValue();
        var logs = new TempDirectory();

        var builder = Microsoft.Extensions.Hosting.Host.CreateApplicationBuilder(new HostApplicationBuilderSettings
        {
            Args = [],
            ContentRootPath = AppContext.BaseDirectory,
        });
        builder.AddPillionHost(
            new PillionHostOptions(pipeName, Environment.ProcessId, logs.Path),
            new FixedLaunchTokenSource(token));
        builder.Services.Configure<RpcServerOptions>(o => o.RejectionCloseDelay = TimeSpan.FromSeconds(1));
        configureServices?.Invoke(builder.Services);

        var host = builder.Build();
        var run = host.RunAsync();
        var result = new InProcessHost(host, run, pipeName, token, logs);

        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (result.State.Current != HostState.Ready)
        {
            if (run.IsCompleted || DateTime.UtcNow > deadline)
            {
                throw new InvalidOperationException($"The in-process host did not become ready (state {result.State.Current}).");
            }

            await Task.Delay(10);
        }

        return result;
    }

    public Task<TestRpcClient> ConnectAsync(Action<StreamJsonRpc.JsonRpc>? configure = null, TimeSpan? timeout = null) =>
        TestRpcClient.ConnectAsync(PipeName, configure, timeout);

    public async Task<TestRpcClient> ConnectAndHandshakeAsync(Action<StreamJsonRpc.JsonRpc>? configure = null)
    {
        var client = await ConnectAsync(configure);
        var response = await client.HandshakeAsync(Token);
        Assert.True(response.Accepted, response.Reason);
        return client;
    }

    public async ValueTask DisposeAsync()
    {
        if (!_run.IsCompleted)
        {
            Lifetime.StopApplication();
        }

        try
        {
            // RunAsync disposes the host when it completes.
            await _run.WaitAsync(TimeSpan.FromSeconds(10));
        }
        finally
        {
            _logs.Dispose();
        }
    }
}

internal sealed class FixedLaunchTokenSource(string token) : ILaunchTokenSource
{
    public Task<LaunchToken> GetTokenAsync(CancellationToken cancellationToken) => Task.FromResult(LaunchToken.Parse(token));
}

internal sealed class FixedClientProcessIdProvider(int? processId) : IPipeClientProcessIdProvider
{
    public int? GetClientProcessId(System.IO.Pipes.NamedPipeServerStream pipe) => processId;
}

internal static class ServiceCollectionTestExtensions
{
    public static IServiceCollection UseClientProcessId(this IServiceCollection services, int? processId) =>
        services.Replace(ServiceDescriptor.Singleton<IPipeClientProcessIdProvider>(new FixedClientProcessIdProvider(processId)));
}

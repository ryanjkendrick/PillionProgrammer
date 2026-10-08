using System.Threading.Channels;
using Microsoft.Extensions.DependencyInjection;
using Pillion.Common.Connectivity;
using Pillion.Common.Settings;
using Pillion.Host.Connectivity;
using Pillion.Host.Tests.TestSupport;
using Wire = Pillion.Contracts.ConnectivityState;

namespace Pillion.Host.Tests;

/// <summary>The host tells the extension about connectivity: after each handshake and on every transition.</summary>
public sealed class ConnectivityNotificationTests
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(10);

    private static async Task<InProcessHost> StartHostAsync(HostFakes fakes) =>
        await InProcessHost.StartAsync(services =>
        {
            // Registered first, so AddPillionConnectivity keeps them instead of the real settings, prober and network source.
            services.AddSingleton<IConnectivitySettingsProvider>(fakes.Settings);
            services.AddSingleton<IProviderProber>(fakes.Prober);
            services.AddSingleton<INetworkChangeSource>(fakes.Network);
            services.AddPillionConnectivity();
        });

    /// <summary>Connects and handshakes, collecting <c>connectivityChanged</c> arguments as the integers they are on the wire.</summary>
    private static async Task<(TestRpcClient Client, Channel<int> Notifications)> ConnectAsync(InProcessHost host)
    {
        var notifications = Channel.CreateUnbounded<int>();
        var client = await host.ConnectAndHandshakeAsync(rpc =>
            rpc.AddLocalRpcMethod(
                ConnectivityHostedService.NotificationName,
                new Action<int>(value => notifications.Writer.TryWrite(value))));
        return (client, notifications);
    }

    private static async Task<int> NextAsync(Channel<int> notifications) =>
        await notifications.Reader.ReadAsync().AsTask().WaitAsync(Wait);

    [Fact]
    public void NotificationName_IsConnectivityChanged() =>
        Assert.Equal("connectivityChanged", ConnectivityHostedService.NotificationName);

    [Fact]
    public async Task AfterAnAcceptedHandshake_TheCurrentStateIsSent()
    {
        var fakes = new HostFakes();
        await using var host = await StartHostAsync(fakes);

        var (client, notifications) = await ConnectAsync(host);
        await using (client)
        {
            Assert.Equal((int)Wire.Online, await NextAsync(notifications));
        }
    }

    [Fact]
    public async Task ATransition_IsSentWithTheNewState()
    {
        var fakes = new HostFakes();
        fakes.Settings.Settings.Providers.Add(new ProviderSettings { Id = "openai", Kind = ProviderKind.OpenAi, Enabled = true });
        await using var host = await StartHostAsync(fakes);
        var monitor = host.Services.GetRequiredService<ConnectivityMonitor>();

        var (client, notifications) = await ConnectAsync(host);
        await using (client)
        {
            Assert.Equal((int)Wire.Online, await NextAsync(notifications));

            fakes.Prober.Reachable = false;
            await monitor.ProbeNowAsync(CancellationToken.None).WaitAsync(Wait);
            Assert.Equal(ConnectivityState.Offline, monitor.State);
            Assert.Equal((int)Wire.Offline, await NextAsync(notifications));

            fakes.Prober.Reachable = true;
            await monitor.ProbeNowAsync(CancellationToken.None).WaitAsync(Wait);
            Assert.Equal((int)Wire.Online, await NextAsync(notifications));

            fakes.Settings.Update(s => s.Privacy.LocalOnly = true);
            Assert.Equal((int)Wire.LocalOnly, await NextAsync(notifications));
        }
    }

    [Fact]
    public async Task EachNewConnection_IsToldTheCurrentStateOnce()
    {
        var fakes = new HostFakes();
        fakes.Settings.Settings.Providers.Add(new ProviderSettings { Id = "openai", Kind = ProviderKind.OpenAi, Enabled = true });
        fakes.Prober.Reachable = false;
        await using var host = await StartHostAsync(fakes);
        var monitor = host.Services.GetRequiredService<ConnectivityMonitor>();
        await monitor.ProbeNowAsync(CancellationToken.None).WaitAsync(Wait);
        Assert.Equal(ConnectivityState.Offline, monitor.State);

        var (first, firstNotifications) = await ConnectAsync(host);
        await using (first)
        {
            await WaitForAsync(firstNotifications, (int)Wire.Offline);
        }

        // Wait for the host to notice the disconnect and listen again.
        var (second, secondNotifications) = await ConnectAsync(host);
        await using (second)
        {
            Assert.Equal((int)Wire.Offline, await NextAsync(secondNotifications));
            await Task.Delay(400);
            Assert.False(secondNotifications.Reader.TryRead(out _), "the handshake produced a second notification");
        }
    }

    [Fact]
    public async Task NothingIsSent_BeforeTheHandshake()
    {
        var fakes = new HostFakes();
        await using var host = await StartHostAsync(fakes);
        var notifications = Channel.CreateUnbounded<int>();
        await using var client = await host.ConnectAsync(rpc =>
            rpc.AddLocalRpcMethod(
                ConnectivityHostedService.NotificationName,
                new Action<int>(value => notifications.Writer.TryWrite(value))));

        fakes.Settings.Update(s => s.Privacy.LocalOnly = true);
        await Task.Delay(300);

        Assert.False(notifications.Reader.TryRead(out _));
    }

    [Fact]
    public void ToContract_MapsEveryState()
    {
        foreach (var state in Enum.GetValues<ConnectivityState>())
        {
            Assert.Equal((int)state, (int)ConnectivityHostedService.ToContract(state));
        }
    }

    private static async Task WaitForAsync(Channel<int> notifications, int expected)
    {
        using var cts = new CancellationTokenSource(Wait);
        while (await NextAsync(notifications) != expected)
        {
            cts.Token.ThrowIfCancellationRequested();
        }
    }

    private sealed class HostFakes
    {
        public HostSettingsProvider Settings { get; } = new();

        public HostProber Prober { get; } = new();

        public HostNetworkSource Network { get; } = new();
    }

    private sealed class HostSettingsProvider : IConnectivitySettingsProvider
    {
        public event EventHandler? Changed;

        public PillionSettings Settings { get; } = new();

        public EffectiveSettings GetEffectiveSettings() => EffectiveSettings.Resolve(Settings, policies: null);

        public void Update(Action<PillionSettings> edit)
        {
            edit(Settings);
            Changed?.Invoke(this, EventArgs.Empty);
        }
    }

    private sealed class HostProber : IProviderProber
    {
        public bool Reachable { get; set; } = true;

        public Task<ProbeResult> ProbeAsync(ProbeTarget target, CancellationToken cancellationToken) =>
            Task.FromResult(Reachable ? ProbeResult.Success() : ProbeResult.Failure(ProbeErrorCategory.Connect));
    }

    private sealed class HostNetworkSource : INetworkChangeSource
    {
        public event EventHandler? Changed
        {
            add { }
            remove { }
        }
    }
}

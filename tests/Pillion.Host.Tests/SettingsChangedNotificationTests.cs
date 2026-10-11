using System.Text.Json.Nodes;
using System.Threading.Channels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Pillion.Common;
using Pillion.Common.Connectivity;
using Pillion.Common.Secrets;
using Pillion.Common.Settings;
using Pillion.Contracts;
using Pillion.Host.Settings;
using Pillion.Host.Tests.TestSupport;

namespace Pillion.Host.Tests;

/// <summary>
/// The host tells the extension which top-level settings sections changed (<c>settingsChanged</c>): after a save and
/// after an external edit, once per burst, and only after an accepted handshake.
/// </summary>
/// <remarks>
/// The tests that save through the RPC service run the notifier's debounce on a <see cref="ManualTimeProvider"/>: a
/// save writes the file through to disk (a flush that takes hundreds of milliseconds on a loaded CI runner), so with the
/// real clock the window could end between two saves of one burst and the burst arrived as two notifications. The test
/// ends the window itself, with <see cref="ManualTimeProvider.Advance"/>, once every save has returned. The external
/// edit test keeps the real clock: the store's file watcher raises that change, and a fake clock cannot drive it.
/// </remarks>
public sealed class SettingsChangedNotificationTests : IDisposable
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(10);

    /// <summary>The notifier's debounce (its options' default).</summary>
    private static readonly TimeSpan Debounce = new SettingsChangedNotifierOptions().DebounceInterval;

    /// <summary>
    /// How long a test waits to be sure that a notification that must not come has not come. A notification is only ever
    /// sent by a timer the test advances, so the wait only gives a stray one time to arrive; it can never make a test fail by itself.
    /// </summary>
    private static readonly TimeSpan Quiet = TimeSpan.FromMilliseconds(250);

    private readonly TempDirectory _data = new();

    public void Dispose() => _data.Dispose();

    [Fact]
    public void NotificationName_IsSettingsChanged() =>
        Assert.Equal("settingsChanged", SettingsChangedNotifier.NotificationName);

    [Fact]
    public void ChangedSections_ListsTheSectionsWhoseContentDiffers()
    {
        var before = JsonNode.Parse("""{"a":{"x":1},"b":[1,2],"c":true,"gone":1}""")!.AsObject();
        var after = JsonNode.Parse("""{"a":{"x":1},"b":[1,3],"c":true,"added":{}}""")!.AsObject();

        Assert.Equal(["b", "added", "gone"], SettingsChangedNotifier.ChangedSections(before, after));
        Assert.Empty(SettingsChangedNotifier.ChangedSections(after, after.DeepClone().AsObject()));
        Assert.Equal(["a", "b", "c", "added"], SettingsChangedNotifier.ChangedSections(before: null, after));
    }

    [Fact]
    public async Task AfterASave_TheChangedSectionsAreSentOnce()
    {
        var time = new ManualTimeProvider();
        await using var host = await StartAsync(time);
        var (client, notifications) = await ConnectAsync(host);
        await using (client)
        {
            var settings = client.Attach<ISettingsService>();
            await settings.SetAsync("routing.tasks.chat", "\"localStrong\"", CancellationToken.None);
            await settings.SetAsync("cloudSwitches.autocomplete", "true", CancellationToken.None);
            Assert.False(notifications.Reader.TryRead(out _), "a notification was sent before the debounce window ended");

            time.Advance(Debounce);
            Assert.Equal(["routing", "cloudSwitches"], await NextAsync(notifications));

            time.Advance(Debounce * 10);
            await AssertNothingMoreAsync(notifications);
        }
    }

    [Fact]
    public async Task ABurstOfSaves_IsOneNotificationWithTheUnionOfSections()
    {
        var time = new ManualTimeProvider();
        await using var host = await StartAsync(time);
        var (client, notifications) = await ConnectAsync(host);
        await using (client)
        {
            var settings = client.Attach<ISettingsService>();
            var almostTheWindow = Debounce - TimeSpan.FromMilliseconds(50);
            for (var i = 0; i < 5; i++)
            {
                await settings.SetAsync("indexing.maxMemoryMb", (2000 + i).ToString(System.Globalization.CultureInfo.InvariantCulture), CancellationToken.None);

                // Each save restarts the window: saves that are almost a whole window apart are still one burst.
                time.Advance(almostTheWindow);
            }

            await settings.SetAsync("privacy.localOnly", "true", CancellationToken.None);
            time.Advance(almostTheWindow);
            Assert.False(notifications.Reader.TryRead(out _), "a notification was sent before the debounce window ended");

            time.Advance(Debounce - almostTheWindow);
            Assert.Equal(["indexing", "privacy"], (await NextAsync(notifications)).OrderBy(s => s, StringComparer.Ordinal).ToArray());

            time.Advance(Debounce * 10);
            await AssertNothingMoreAsync(notifications);
        }
    }

    [Fact]
    public async Task ASaveThatChangesNothing_SendsNothing()
    {
        var time = new ManualTimeProvider();
        await using var host = await StartAsync(time);
        var (client, notifications) = await ConnectAsync(host);
        await using (client)
        {
            await client.Attach<ISettingsService>().SetAsync("privacy.localOnly", "false", CancellationToken.None);

            time.Advance(Debounce * 10);
            await AssertNothingMoreAsync(notifications);
        }
    }

    [Fact]
    public async Task ASectionThisVersionDoesNotKnow_IsReportedToo()
    {
        var time = new ManualTimeProvider();
        await using var host = await StartAsync(time);
        var (client, notifications) = await ConnectAsync(host);
        await using (client)
        {
            await client.Attach<ISettingsService>().SetAsync("autocomplete.disabledLanguages", "[\"markdown\"]", CancellationToken.None);

            time.Advance(Debounce);
            Assert.Equal(["autocomplete"], await NextAsync(notifications));
        }
    }

    [Fact]
    public async Task AfterAnExternalEdit_TheChangedSectionsAreSent()
    {
        await using var host = await StartAsync();
        var store = host.Services.GetRequiredService<SettingsStore>();
        var (client, notifications) = await ConnectAsync(host);
        await using (client)
        {
            var text = await File.ReadAllTextAsync(store.Paths.SettingsFile);
            Assert.Contains("\"localOnly\": false", text, StringComparison.Ordinal);
            await File.WriteAllTextAsync(store.Paths.SettingsFile, text.Replace("\"localOnly\": false", "\"localOnly\": true", StringComparison.Ordinal));

            Assert.Equal(["privacy"], await NextAsync(notifications));
            Assert.True(store.GetSettings().Privacy.LocalOnly);
        }
    }

    [Fact]
    public async Task NothingIsSent_BeforeTheHandshake()
    {
        var time = new ManualTimeProvider();
        await using var host = await StartAsync(time);
        var notifications = Channel.CreateUnbounded<string[]>();
        await using var client = await host.ConnectAsync(rpc =>
            rpc.AddLocalRpcMethod(SettingsChangedNotifier.NotificationName, new Action<string[]>(sections => notifications.Writer.TryWrite(sections))));

        host.Services.GetRequiredService<SettingsStore>().SetJson("privacy.localOnly", "true");
        time.Advance(Debounce * 10);

        await AssertNothingMoreAsync(notifications);
    }

    [Fact]
    public async Task AChangeWhileNoClientIsConnected_IsNotSentToALaterClient()
    {
        var time = new ManualTimeProvider();
        await using var host = await StartAsync(time);
        host.Services.GetRequiredService<SettingsStore>().SetJson("privacy.localOnly", "true");
        time.Advance(Debounce * 10);

        var (client, notifications) = await ConnectAsync(host);
        await using (client)
        {
            time.Advance(Debounce * 10);
            await AssertNothingMoreAsync(notifications);
        }
    }

    private static async Task<(TestRpcClient Client, Channel<string[]> Notifications)> ConnectAsync(InProcessHost host)
    {
        var notifications = Channel.CreateUnbounded<string[]>();
        var client = await host.ConnectAndHandshakeAsync(rpc =>
            rpc.AddLocalRpcMethod(SettingsChangedNotifier.NotificationName, new Action<string[]>(sections => notifications.Writer.TryWrite(sections))));
        return (client, notifications);
    }

    private static async Task<string[]> NextAsync(Channel<string[]> notifications) =>
        await notifications.Reader.ReadAsync().AsTask().WaitAsync(Wait);

    private static async Task AssertNothingMoreAsync(Channel<string[]> notifications)
    {
        await Task.Delay(Quiet);
        Assert.False(notifications.Reader.TryRead(out var extra), "an unexpected notification arrived: " + string.Join(",", extra ?? []));
    }

    /// <summary>Starts the host; with <paramref name="time"/>, the notifier's debounce runs on it and nothing else does.</summary>
    private Task<InProcessHost> StartAsync(ManualTimeProvider? time = null) => InProcessHost.StartAsync(services =>
    {
        services.Replace(ServiceDescriptor.Singleton(new PillionPaths(_data.Path)));
        services.Replace(ServiceDescriptor.Singleton<ISecretStore>(new InMemorySecretStore()));
        services.Replace(ServiceDescriptor.Singleton<INetworkChangeSource>(new NoNetworkChanges()));
        if (time is not null)
        {
            services.Remove(services.Single(d => d.ServiceType == typeof(IHostedService) && d.ImplementationType == typeof(SettingsChangedNotifier)));
            services.AddSingleton<IHostedService>(sp => ActivatorUtilities.CreateInstance<SettingsChangedNotifier>(sp, time));
        }
    });

    private sealed class NoNetworkChanges : INetworkChangeSource
    {
        public event EventHandler? Changed
        {
            add { }
            remove { }
        }
    }
}

using System.Text.Json.Nodes;
using System.Threading.Channels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
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
public sealed class SettingsChangedNotificationTests : IDisposable
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(10);

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
        await using var host = await StartAsync();
        var (client, notifications) = await ConnectAsync(host);
        await using (client)
        {
            var settings = client.Attach<ISettingsService>();
            await settings.SetAsync("routing.tasks.chat", "\"localStrong\"", CancellationToken.None);
            await settings.SetAsync("cloudSwitches.autocomplete", "true", CancellationToken.None);

            Assert.Equal(["routing", "cloudSwitches"], await NextAsync(notifications));
            await Task.Delay(600);
            Assert.False(notifications.Reader.TryRead(out var extra), "a second notification arrived: " + string.Join(",", extra ?? []));
        }
    }

    [Fact]
    public async Task ABurstOfSaves_IsOneNotificationWithTheUnionOfSections()
    {
        await using var host = await StartAsync();
        var (client, notifications) = await ConnectAsync(host);
        await using (client)
        {
            var settings = client.Attach<ISettingsService>();
            for (var i = 0; i < 5; i++)
            {
                await settings.SetAsync("indexing.maxMemoryMb", (2000 + i).ToString(System.Globalization.CultureInfo.InvariantCulture), CancellationToken.None);
            }

            await settings.SetAsync("privacy.localOnly", "true", CancellationToken.None);

            Assert.Equal(["indexing", "privacy"], (await NextAsync(notifications)).OrderBy(s => s, StringComparer.Ordinal).ToArray());
            await Task.Delay(600);
            Assert.False(notifications.Reader.TryRead(out _));
        }
    }

    [Fact]
    public async Task ASaveThatChangesNothing_SendsNothing()
    {
        await using var host = await StartAsync();
        var (client, notifications) = await ConnectAsync(host);
        await using (client)
        {
            await client.Attach<ISettingsService>().SetAsync("privacy.localOnly", "false", CancellationToken.None);

            await Task.Delay(700);
            Assert.False(notifications.Reader.TryRead(out _));
        }
    }

    [Fact]
    public async Task ASectionThisVersionDoesNotKnow_IsReportedToo()
    {
        await using var host = await StartAsync();
        var (client, notifications) = await ConnectAsync(host);
        await using (client)
        {
            await client.Attach<ISettingsService>().SetAsync("autocomplete.disabledLanguages", "[\"markdown\"]", CancellationToken.None);

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
        await using var host = await StartAsync();
        var notifications = Channel.CreateUnbounded<string[]>();
        await using var client = await host.ConnectAsync(rpc =>
            rpc.AddLocalRpcMethod(SettingsChangedNotifier.NotificationName, new Action<string[]>(sections => notifications.Writer.TryWrite(sections))));

        host.Services.GetRequiredService<SettingsStore>().SetJson("privacy.localOnly", "true");
        await Task.Delay(700);

        Assert.False(notifications.Reader.TryRead(out _));
    }

    [Fact]
    public async Task AChangeWhileNoClientIsConnected_IsNotSentToALaterClient()
    {
        await using var host = await StartAsync();
        host.Services.GetRequiredService<SettingsStore>().SetJson("privacy.localOnly", "true");
        await Task.Delay(700);

        var (client, notifications) = await ConnectAsync(host);
        await using (client)
        {
            await Task.Delay(500);
            Assert.False(notifications.Reader.TryRead(out _));
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

    private Task<InProcessHost> StartAsync() => InProcessHost.StartAsync(services =>
    {
        services.Replace(ServiceDescriptor.Singleton(new PillionPaths(_data.Path)));
        services.Replace(ServiceDescriptor.Singleton<ISecretStore>(new InMemorySecretStore()));
        services.Replace(ServiceDescriptor.Singleton<INetworkChangeSource>(new NoNetworkChanges()));
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

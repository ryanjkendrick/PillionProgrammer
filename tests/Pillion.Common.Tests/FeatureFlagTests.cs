using System.Text.Json;
using System.Text.Json.Nodes;
using Pillion.Common.Connectivity;
using Pillion.Common.Settings;
using Pillion.Common.Tests.Connectivity;

namespace Pillion.Common.Tests;

public sealed class FeatureFlagTests
{
    private static readonly string[] AllNames =
        ["chat", "modelGateway", "connectivityProbes", "cloudProviders", "ollamaProvider", "openAICompatibleProvider", "anthropicProvider", "geminiProvider", "mistralProvider", "azureOpenAIProvider", "autocomplete", "inlineChat", "mentions", "outboundLogView", "localStore", "indexing", "embeddings", "bundledEmbeddings", "workspaceQueries", "indexWindow"];

    [Fact]
    public void Defaults_AreAllOn_AndTheNamesMatchTheJsonKeys()
    {
        var features = new FeatureSettings();

        Assert.Equal(AllNames, FeatureNames.All);
        foreach (var name in FeatureNames.All)
        {
            Assert.True(features.IsEnabled(name), name);
        }

        var json = (JsonObject)JsonSerializer.SerializeToNode(new PillionSettings(), SettingsJsonForTests())!;
        var section = (JsonObject)json["features"]!;
        Assert.Equal(AllNames, section.Select(p => p.Key));
        Assert.All(section, p => Assert.True(p.Value!.GetValue<bool>()));
    }

    [Fact]
    public void IsEnabled_IgnoresCase_AndTreatsUnknownFeaturesAsOn()
    {
        var features = new FeatureSettings { CloudProviders = false };

        Assert.False(features.IsEnabled("cloudproviders"));
        Assert.False(features.IsEnabled(" CloudProviders "));
        Assert.True(features.IsEnabled("someFutureFeature"));
    }

    [Fact]
    public void ANewStore_WritesTheFeaturesSection_WithEverythingOn()
    {
        using var temp = new TempDirectory();
        using var store = new SettingsStore(temp.NewPaths(), new SettingsStoreOptions { WatchForChanges = false });

        var settings = store.GetSettings();

        Assert.Equal(PillionSettings.CurrentVersion, settings.Version);
        Assert.All(FeatureNames.All, name => Assert.True(settings.Features.IsEnabled(name)));
        Assert.Contains("\"features\"", File.ReadAllText(store.Paths.SettingsFile), StringComparison.Ordinal);
    }

    [Fact]
    public void AnExistingVersion1FileWithoutFeatures_GetsTheDefaults_WithoutAVersionBump_AndKeepsItsValues()
    {
        using var temp = new TempDirectory();
        var paths = temp.NewPaths();
        Directory.CreateDirectory(Path.GetDirectoryName(paths.SettingsFile)!);
        File.WriteAllText(paths.SettingsFile, """{ "version": 1, "privacy": { "localOnly": true } }""");
        using var store = new SettingsStore(paths, new SettingsStoreOptions { WatchForChanges = false });

        var settings = store.GetSettings();

        Assert.Equal(1, settings.Version);
        Assert.True(settings.Privacy.LocalOnly);
        Assert.All(FeatureNames.All, name => Assert.True(settings.Features.IsEnabled(name)));
        Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(paths.SettingsFile)!, "*.bak*"));
    }

    [Fact]
    public void APartialFeaturesSection_KeepsTheUserValues_AndFillsTheRest()
    {
        using var temp = new TempDirectory();
        var paths = temp.NewPaths();
        Directory.CreateDirectory(Path.GetDirectoryName(paths.SettingsFile)!);
        File.WriteAllText(paths.SettingsFile, """{ "version": 1, "features": { "chat": false, "futureFlag": true } }""");
        using var store = new SettingsStore(paths, new SettingsStoreOptions { WatchForChanges = false });

        var settings = store.GetSettings();

        Assert.False(settings.Features.Chat);
        Assert.True(settings.Features.ModelGateway);
        Assert.True(settings.Features.OpenAICompatibleProvider);
        Assert.NotNull(settings.Features.ExtensionData);
        Assert.True(settings.Features.ExtensionData!.ContainsKey("futureFlag"));
    }

    [Fact]
    public void PolicyDisabledFeatures_IsParsed_AndNonStringsAreWarnedAbout()
    {
        var policy = RepositoryPolicy.Parse("""{ "disabledFeatures": ["chat", "cloudProviders", 3, ""] }""");

        Assert.Equal(["chat", "cloudProviders"], policy.DisabledFeatures);
        Assert.Equal(2, policy.Warnings.Count);
        Assert.Empty(RepositoryPolicy.Empty.DisabledFeatures);
        Assert.Contains(RepositoryPolicy.Parse("""{ "disabledFeatures": "chat" }""").Warnings, w => w.Contains("array of strings", StringComparison.Ordinal));
    }

    [Fact]
    public void Resolve_DisabledFeatures_AreUnionedAndOnlyEverSwitchFlagsOff()
    {
        var user = new PillionSettings();
        user.Features.ModelGateway = false;
        var a = RepositoryPolicy.Parse("""{ "disabledFeatures": ["chat", "CloudProviders"] }""");
        var b = RepositoryPolicy.Parse("""{ "disabledFeatures": ["chat", "ollamaProvider", "futureFeature"] }""");
        var empty = RepositoryPolicy.Parse("{}");

        var effective = EffectiveSettings.Resolve(user, [a, empty, b]);

        Assert.Equal(["chat", "CloudProviders", "ollamaProvider", "futureFeature"], effective.DisabledFeatures);
        Assert.False(effective.Settings.Features.Chat);
        Assert.False(effective.Settings.Features.CloudProviders);
        Assert.False(effective.Settings.Features.OllamaProvider);
        Assert.False(effective.Settings.Features.ModelGateway); // A flag the user switched off stays off.
        Assert.True(effective.Settings.Features.ConnectivityProbes);
        Assert.True(effective.Settings.Features.OpenAICompatibleProvider);

        Assert.False(effective.IsFeatureEnabled("chat"));
        Assert.False(effective.IsFeatureEnabled("futureFeature")); // Unknown to this version, but the policy named it.
        Assert.True(effective.IsFeatureEnabled("connectivityProbes"));
        Assert.True(effective.IsFeatureEnabled("anotherFutureFeature"));
    }

    [Fact]
    public void Resolve_APolicyCannotSwitchAFeatureOn()
    {
        var user = new PillionSettings();
        user.Features.Chat = false;
        var policy = RepositoryPolicy.Parse("""{ "disabledFeatures": [], "features": { "chat": true } }""");

        var effective = EffectiveSettings.Resolve(user, [policy]);

        Assert.False(effective.Settings.Features.Chat);
        Assert.Contains(policy.Warnings, w => w.Contains("not a recognised policy key", StringComparison.Ordinal));
    }

    [Fact]
    public void Resolve_DoesNotChangeTheUserFeatures()
    {
        var user = new PillionSettings();

        _ = EffectiveSettings.Resolve(user, [RepositoryPolicy.Parse("""{ "disabledFeatures": ["chat"] }""")]);

        Assert.True(user.Features.Chat);
    }

    [Fact]
    public void FeaturesToJson_IsTheEffectiveObject()
    {
        var user = new PillionSettings();
        var effective = EffectiveSettings.Resolve(user, [RepositoryPolicy.Parse("""{ "disabledFeatures": ["modelGateway"] }""")]);

        var json = JsonNode.Parse(effective.FeaturesToJson())!.AsObject();

        Assert.Equal(AllNames, json.Select(p => p.Key));
        Assert.False(json["modelGateway"]!.GetValue<bool>());
        Assert.True(json["chat"]!.GetValue<bool>());
    }

    [Fact]
    public void SettingsFeatureFlags_FollowsTheSettingsAndPolicies_AndRaisesChanged()
    {
        var settings = new FakeSettingsProvider();
        using var flags = new SettingsFeatureFlags(settings);
        var changes = 0;
        flags.Changed += (_, _) => changes++;

        Assert.True(flags.IsEnabled(FeatureNames.Chat));

        settings.Update(s => s.Features.Chat = false);
        Assert.False(flags.IsEnabled(FeatureNames.Chat));
        Assert.True(flags.IsEnabled(FeatureNames.ModelGateway));
        Assert.Equal(1, changes);

        settings.Update(s => s.Features.Chat = true);
        Assert.True(flags.IsEnabled(FeatureNames.Chat));

        settings.SetPolicies(RepositoryPolicy.Parse("""{ "disabledFeatures": ["chat"] }"""));
        Assert.False(flags.IsEnabled("chat"));
        Assert.Equal(3, changes);

        settings.SetPolicies();
        Assert.True(flags.IsEnabled("chat"));
    }

    [Fact]
    public void SettingsFeatureFlags_CachesUntilSomethingChanges()
    {
        var settings = new CountingSettings();
        using var flags = new SettingsFeatureFlags(settings);

        for (var i = 0; i < 5; i++)
        {
            Assert.True(flags.IsEnabled("chat"));
        }

        Assert.Equal(1, settings.Reads);
        settings.RaiseChanged();
        Assert.True(flags.IsEnabled("chat"));
        Assert.Equal(2, settings.Reads);
    }

    [Fact]
    public void SettingsFeatureFlags_DoesNotGuessWhenTheSettingsCannotBeRead()
    {
        var settings = new FakeSettingsProvider { Throw = true };
        using var flags = new SettingsFeatureFlags(settings);

        Assert.Throws<SettingsValidationException>(() => flags.IsEnabled("chat"));
    }

    [Fact]
    public void SettingsFeatureFlags_StopsListeningWhenDisposed()
    {
        var settings = new CountingSettings();
        var flags = new SettingsFeatureFlags(settings);
        var changes = 0;
        flags.Changed += (_, _) => changes++;

        flags.Dispose();
        settings.RaiseChanged();

        Assert.Equal(0, changes);
    }

    [Fact]
    public async Task ConnectivityProbesOff_StopsProbing_AndTheStateReadsOnline()
    {
        using var f = new MonitorFixture(start: false, configure: s => s.Configure(settings =>
        {
            settings.Providers.Add(FakeSettingsProvider.Provider("openai", ProviderKind.OpenAi));
            settings.Features.ConnectivityProbes = false;
        }));
        f.Prober.Set("openai", ProbeResult.Failure(ProbeErrorCategory.Connect));
        f.Start();
        await f.ProbeAsync();
        f.Time.Advance(TimeSpan.FromMinutes(5));
        await f.IdleAsync();

        Assert.Equal(0, f.Prober.CallCount);
        Assert.Equal(ConnectivityState.Online, f.Monitor.State);
        Assert.Empty(f.Monitor.Snapshot);
        Assert.True(f.Monitor.IsProviderReachable("openai")); // Configured, but not probed, so not reported as down.

        // Switching the flag on starts probing, and the failing probe is now seen.
        f.Settings.Update(s => s.Features.ConnectivityProbes = true);
        await f.IdleAsync();
        Assert.True(f.Prober.CallCount >= 1);
        Assert.Equal(ConnectivityState.Offline, f.Monitor.State);

        // And off again forgets what the probes learned.
        var calls = f.Prober.CallCount;
        f.Settings.Update(s => s.Features.ConnectivityProbes = false);
        await f.IdleAsync();
        f.Time.Advance(TimeSpan.FromMinutes(5));
        await f.IdleAsync();
        Assert.Equal(calls, f.Prober.CallCount);
        Assert.Equal(ConnectivityState.Online, f.Monitor.State);
        Assert.Empty(f.Monitor.Snapshot);
    }

    [Fact]
    public async Task CloudProvidersOff_ProbesNothing_AndCloudProvidersCountAsUnreachable()
    {
        using var f = new MonitorFixture(start: false, configure: s => s.Configure(settings =>
        {
            settings.Providers.Add(FakeSettingsProvider.Provider("openai", ProviderKind.OpenAi));
            settings.Providers.Add(FakeSettingsProvider.Provider("ollama", ProviderKind.Ollama, "http://localhost:11434"));
            settings.Features.CloudProviders = false;
        }));
        f.Start();
        await f.ProbeAsync();
        f.Time.Advance(TimeSpan.FromMinutes(5));
        await f.IdleAsync();

        Assert.Equal(0, f.Prober.CallCount);
        Assert.Equal(ConnectivityState.Online, f.Monitor.State);
        Assert.False(f.Monitor.IsProviderReachable("openai"));
        Assert.True(f.Monitor.IsProviderReachable("ollama"));
    }

    [Fact]
    public async Task APolicyThatDisablesProbes_StopsThemToo()
    {
        using var f = new MonitorFixture(start: false, configure: s => s.Configure(settings =>
            settings.Providers.Add(FakeSettingsProvider.Provider("openai", ProviderKind.OpenAi))));
        f.Start();
        await f.IdleAsync();
        var before = f.Prober.CallCount;
        Assert.True(before >= 1);

        f.Settings.SetPolicies(RepositoryPolicy.Parse("""{ "disabledFeatures": ["connectivityProbes"] }"""));
        await f.IdleAsync();
        f.Time.Advance(TimeSpan.FromMinutes(5));
        await f.IdleAsync();

        Assert.Equal(before, f.Prober.CallCount);
        Assert.Empty(f.Monitor.Snapshot);
    }

    private static JsonSerializerOptions SettingsJsonForTests() => new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private sealed class CountingSettings : IConnectivitySettingsProvider
    {
        public event EventHandler? Changed;

        public int Reads { get; private set; }

        public EffectiveSettings GetEffectiveSettings()
        {
            Reads++;
            return EffectiveSettings.Resolve(new PillionSettings(), policies: null);
        }

        public void RaiseChanged() => Changed?.Invoke(this, EventArgs.Empty);
    }
}

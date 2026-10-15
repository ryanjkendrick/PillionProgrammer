using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using Pillion.Common.Settings;
using Pillion.Contracts;

namespace Pillion.Common.Tests;

public class SettingsStoreTests
{
    private static SettingsStore NewStore(TempDirectory temp, bool watch = false, params ISettingsMigration[] migrations)
        => new(temp.NewPaths(), new SettingsStoreOptions { WatchForChanges = watch, Migrations = migrations });

    private static JsonObject ReadFile(SettingsStore store)
        => (JsonObject)JsonNode.Parse(File.ReadAllText(store.Paths.SettingsFile))!;

    private static void WriteFile(PillionPaths paths, string json)
    {
        Directory.CreateDirectory(paths.Root);
        File.WriteAllText(paths.SettingsFile, json);
    }

    [Fact]
    public void Load_WhenMissing_CreatesFileWithDefaults()
    {
        using var temp = new TempDirectory();
        using var store = NewStore(temp);

        store.Load();

        Assert.True(File.Exists(store.Paths.SettingsFile));
        var file = ReadFile(store);
        Assert.Equal(1, (int)file["version"]!);
        Assert.Equal("version", file.First().Key);

        var settings = store.GetSettings();
        Assert.Equal(1, settings.Version);
        Assert.Empty(settings.Providers);
        Assert.All(Enum.GetValues<TaskClass>(), task => Assert.Equal(Profile.Auto, settings.Routing.Tasks[task]));
        Assert.All(Enum.GetValues<Profile>(), profile => Assert.Equal(string.Empty, settings.Routing.Profiles[profile]));
        Assert.True(settings.CloudSwitches.Chat);
        Assert.True(settings.CloudSwitches.InlineEdit);
        Assert.False(settings.CloudSwitches.Autocomplete);
        Assert.False(settings.CloudSwitches.Agent);
        Assert.False(settings.CloudSwitches.SelfHealing);
        Assert.True(settings.CloudSwitches.Vision);
        Assert.False(settings.CloudSwitches.Embedding);
        Assert.False(settings.Privacy.LocalOnly);
        Assert.False(settings.Privacy.TelemetryOptIn);
        Assert.False(settings.Privacy.RedactForLocalProviders);
        Assert.Equal(ApprovalMode.Ask, settings.Approvals.Mode);
        Assert.Empty(settings.CostCaps);
        Assert.Empty(settings.Indexing.Exclude);
        Assert.Equal(1536, settings.Indexing.MaxMemoryMb);
        Assert.True(settings.Indexing.PauseOnBattery);
        Assert.Equal(30, settings.Indexing.RetentionDays);
        Assert.Empty(settings.Mentions.DocsFolders);
        Assert.Equal(LogLevel.Information, settings.Logging.Level);
        Assert.False(settings.Logging.RpcTrace);
    }

    [Fact]
    public void DefaultFile_UsesCamelCaseNamesAndStringEnums()
    {
        using var temp = new TempDirectory();
        using var store = NewStore(temp);
        store.Load();

        Assert.Equal("auto", store.GetJson("routing.tasks.inlineEdit")?.Trim('"'));
        Assert.Equal("auto", store.GetJson("routing.tasks.autocomplete")?.Trim('"'));
        Assert.Equal("\"\"", store.GetJson("routing.profiles.localStrong"));
        Assert.Equal("\"ask\"", store.GetJson("approvals.mode"));
        Assert.Equal("\"information\"", store.GetJson("logging.level"));
        Assert.Equal("1536", store.GetJson("indexing.maxMemoryMb"));
        Assert.Equal("30", store.GetJson("indexing.retentionDays"));
        Assert.Equal("false", store.GetJson("cloudSwitches.selfHealing"));
        Assert.Equal("false", store.GetJson("cloudSwitches.embedding"));
        Assert.Equal("false", store.GetJson("routing.thinkHarder"));
    }

    [Fact]
    public void RoutingThinkHarder_DefaultsToFalse_AndSurvivesASaveAndReload()
    {
        using var temp = new TempDirectory();
        using var store = NewStore(temp);
        store.Load();
        Assert.False(store.GetSettings().Routing.ThinkHarder);

        store.SetJson("routing.thinkHarder", "true");

        using var reloaded = new SettingsStore(store.Paths, new SettingsStoreOptions { WatchForChanges = false });
        Assert.True(reloaded.GetSettings().Routing.ThinkHarder);
    }

    [Fact]
    public void GetDocument_ReturnsACopyOfEverySection_UnknownKeysIncluded()
    {
        using var temp = new TempDirectory();
        using var store = NewStore(temp);
        store.Load();
        store.SetJson("autocomplete.disabledLanguages", "[\"vb\"]");

        var document = store.GetDocument();

        Assert.Equal("[\"vb\"]", document["autocomplete"]!["disabledLanguages"]!.ToJsonString());
        Assert.NotNull(document["routing"]);
        Assert.NotNull(document["cloudSwitches"]);

        document["routing"] = null; // A copy: the store is unchanged.
        Assert.Equal("false", store.GetJson("routing.thinkHarder"));
    }

    [Fact]
    public void EverySection_RoundTripsThroughANewStore()
    {
        using var temp = new TempDirectory();
        var paths = temp.NewPaths();
        using (var store = new SettingsStore(paths, new SettingsStoreOptions { WatchForChanges = false }))
        {
            store.Set(SettingsSections.Providers, new List<ProviderSettings>
            {
                new() { Id = "openai", Kind = ProviderKind.OpenAi, DisplayName = "OpenAI", Endpoint = "https://api.openai.com/v1", Models = ["gpt-x", "gpt-y"], Enabled = true },
                new() { Id = "local", Kind = ProviderKind.OpenAiCompatible, DisplayName = "LM Studio", Endpoint = "http://localhost:1234/v1", Enabled = false },
                new() { Id = "azure", Kind = ProviderKind.AzureOpenAi },
            });
            store.Set(SettingsSections.Routing, new RoutingSettings
            {
                Tasks = new Dictionary<TaskClass, Profile> { [TaskClass.Chat] = Profile.Frontier, [TaskClass.Autocomplete] = Profile.LocalFast },
                Profiles = new Dictionary<Profile, string> { [Profile.Fast] = "fast-model", [Profile.LocalStrong] = "qwen" },
            });
            store.Set(SettingsSections.CloudSwitches, new CloudSwitchSettings { Chat = false, InlineEdit = false, Autocomplete = true, Agent = true, SelfHealing = true, Vision = false, Embedding = true });
            store.Set(SettingsSections.Privacy, new PrivacySettings { LocalOnly = true, TelemetryOptIn = true, RedactForLocalProviders = true });
            store.Set(SettingsSections.Approvals, new ApprovalSettings { Mode = ApprovalMode.AutoSafe });
            store.Set(SettingsSections.CostCaps, new Dictionary<string, CostCap> { ["openai"] = new() { DailyUsd = 5m, MonthlyUsd = 50.5m }, ["azure"] = new() { DailyUsd = 1m } });
            store.Set(SettingsSections.Indexing, new IndexingSettings { Exclude = ["**/bin/**", "*.log"], MaxMemoryMb = 2048, PauseOnBattery = false, RetentionDays = 7 });
            store.Set(SettingsSections.Mentions, new MentionsSettings { DocsFolders = [@"C:\docs\handbook", "docs/adr"] });
            store.Set(SettingsSections.Logging, new LoggingSettings { Level = LogLevel.Debug, RpcTrace = true });
        }

        using var reloaded = new SettingsStore(paths, new SettingsStoreOptions { WatchForChanges = false });
        var s = reloaded.GetSettings();

        Assert.Equal(["openai", "local", "azure"], s.Providers.Select(p => p.Id));
        Assert.Equal(ProviderKind.OpenAi, s.Providers[0].Kind);
        Assert.Equal(["gpt-x", "gpt-y"], s.Providers[0].Models);
        Assert.False(s.Providers[1].Enabled);
        Assert.Equal(ProviderKind.AzureOpenAi, s.Providers[2].Kind);
        Assert.Equal(Profile.Frontier, s.Routing.Tasks[TaskClass.Chat]);
        Assert.Equal(Profile.LocalFast, s.Routing.Tasks[TaskClass.Autocomplete]);
        Assert.Equal("fast-model", s.Routing.Profiles[Profile.Fast]);
        Assert.Equal("qwen", s.Routing.Profiles[Profile.LocalStrong]);
        Assert.False(s.CloudSwitches.Chat);
        Assert.True(s.CloudSwitches.Agent);
        Assert.True(s.CloudSwitches.SelfHealing);
        Assert.True(s.CloudSwitches.Embedding);
        Assert.True(s.Privacy.LocalOnly);
        Assert.True(s.Privacy.TelemetryOptIn);
        Assert.True(s.Privacy.RedactForLocalProviders);
        Assert.Equal(ApprovalMode.AutoSafe, s.Approvals.Mode);
        Assert.Equal(5m, s.CostCaps["openai"].DailyUsd);
        Assert.Equal(50.5m, s.CostCaps["openai"].MonthlyUsd);
        Assert.Null(s.CostCaps["azure"].MonthlyUsd);
        Assert.Equal(["**/bin/**", "*.log"], s.Indexing.Exclude);
        Assert.Equal(2048, s.Indexing.MaxMemoryMb);
        Assert.False(s.Indexing.PauseOnBattery);
        Assert.Equal(7, s.Indexing.RetentionDays);
        Assert.Equal([@"C:\docs\handbook", "docs/adr"], s.Mentions.DocsFolders);
        Assert.Contains("\"docs/adr\"", reloaded.GetJson("mentions.docsFolders"), StringComparison.Ordinal); // camelCase on disk
        Assert.Equal(LogLevel.Debug, s.Logging.Level);
        Assert.True(s.Logging.RpcTrace);

        // Provider kinds use the documented spellings on disk.
        Assert.Equal("\"azure-openai\"", reloaded.GetJson("providers.2.kind"));
        Assert.Equal("\"openai-compatible\"", reloaded.GetJson("providers.1.kind"));
        Assert.Equal("\"auto-safe\"", reloaded.GetJson("approvals.mode"));
    }

    [Fact]
    public void ProviderEntries_NeverContainKeys()
    {
        using var temp = new TempDirectory();
        using var store = NewStore(temp);
        store.Set(SettingsSections.Providers, new List<ProviderSettings> { new() { Id = "openai", Kind = ProviderKind.OpenAi } });

        var provider = (JsonObject)ReadFile(store)["providers"]![0]!;
        Assert.Equal(["id", "kind", "displayName", "endpoint", "models", "enabled"], provider.Select(p => p.Key));
    }

    [Fact]
    public void UnknownKeys_TopLevelAndNested_ArePreservedAcrossSaves()
    {
        using var temp = new TempDirectory();
        var paths = temp.NewPaths();
        WriteFile(paths, """
            {
              "version": 1,
              "futureFeature": { "a": [1, 2, 3], "b": "x" },
              "privacy": { "localOnly": false, "newPrivacyThing": 42 },
              "providers": [
                { "id": "openai", "kind": "openai", "displayName": "OpenAI", "endpoint": "", "models": [], "enabled": true, "organisation": "acme" }
              ],
              "routing": { "tasks": { "chat": "fast" }, "experimental": true },
              "costCaps": { "openai": { "dailyUsd": 3, "currency": "USD" } }
            }
            """);
        using var store = new SettingsStore(paths, new SettingsStoreOptions { WatchForChanges = false });

        // A typed edit through Get/Set keeps nested unknowns; a key-path edit keeps everything else.
        var privacy = store.Get<PrivacySettings>(SettingsSections.Privacy);
        privacy.TelemetryOptIn = true;
        store.Set(SettingsSections.Privacy, privacy);
        store.SetJson("approvals.mode", "\"autonomous\"");
        var caps = store.Get<Dictionary<string, CostCap>>(SettingsSections.CostCaps);
        caps["openai"].MonthlyUsd = 9;
        store.Set(SettingsSections.CostCaps, caps);

        var file = ReadFile(store);
        Assert.Equal("""{"a":[1,2,3],"b":"x"}""", file["futureFeature"]!.ToJsonString());
        Assert.Equal(42, (int)file["privacy"]!["newPrivacyThing"]!);
        Assert.True((bool)file["privacy"]!["telemetryOptIn"]!);
        Assert.Equal("acme", (string)file["providers"]![0]!["organisation"]!);
        Assert.True((bool)file["routing"]!["experimental"]!);
        Assert.Equal("fast", (string)file["routing"]!["tasks"]!["chat"]!);
        Assert.Equal("USD", (string)file["costCaps"]!["openai"]!["currency"]!);
        Assert.Equal(9, (int)file["costCaps"]!["openai"]!["monthlyUsd"]!);
        Assert.Equal("autonomous", (string)file["approvals"]!["mode"]!);
    }

    [Fact]
    public void Save_KeepsPropertyOrderStableAndIndented()
    {
        using var temp = new TempDirectory();
        using var store = NewStore(temp);
        store.Load();
        var first = File.ReadAllText(store.Paths.SettingsFile);

        store.Save();
        var second = File.ReadAllText(store.Paths.SettingsFile);
        store.SetJson("privacy.localOnly", "false");
        var third = File.ReadAllText(store.Paths.SettingsFile);

        Assert.Equal(first, second);
        Assert.Equal(first, third);
        Assert.Contains("\n  \"version\": 1,", first, StringComparison.Ordinal);
        Assert.Equal(
            ["version", "providers", "routing", "models", "cloudSwitches", "privacy", "approvals", "costCaps", "indexing", "mentions", "logging", "features"],
            ReadFile(store).Select(p => p.Key));
    }

    [Fact]
    public void Save_LeavesNoTempFilesBehind()
    {
        using var temp = new TempDirectory();
        using var store = NewStore(temp);

        store.SetJson("privacy.localOnly", "true");
        store.SetJson("privacy.localOnly", "false");
        store.Save();

        Assert.Equal(["settings.json"], Directory.EnumerateFiles(store.Paths.Root).Select(Path.GetFileName));
    }

    [Fact]
    public void Save_ToAReadOnlyTarget_FailsWithoutDamagingTheOldFileOrLeavingTempFiles()
    {
        using var temp = new TempDirectory();
        using var store = NewStore(temp);
        store.SetJson("privacy.telemetryOptIn", "true");
        var before = File.ReadAllBytes(store.Paths.SettingsFile);

        File.SetAttributes(store.Paths.SettingsFile, FileAttributes.ReadOnly);
        try
        {
            var ex = Assert.ThrowsAny<Exception>(() => store.SetJson("privacy.localOnly", "true"));
            Assert.True(ex is IOException or UnauthorizedAccessException, $"Unexpected exception type {ex.GetType()}");
        }
        finally
        {
            File.SetAttributes(store.Paths.SettingsFile, FileAttributes.Normal);
        }

        Assert.Equal(before, File.ReadAllBytes(store.Paths.SettingsFile));
        Assert.Equal(["settings.json"], Directory.EnumerateFiles(store.Paths.Root).Select(Path.GetFileName));

        // The failed edit was rolled back in memory too.
        Assert.Equal("false", store.GetJson("privacy.localOnly"));
        Assert.Equal("true", store.GetJson("privacy.telemetryOptIn"));
    }

    [Fact]
    public void Load_MigratesAnOlderFile_AndBacksItUpFirst()
    {
        using var temp = new TempDirectory();
        var paths = temp.NewPaths();
        const string original = """{ "version": 0, "privacy": { "localOnly": true }, "oldKey": "keep" }""";
        WriteFile(paths, original);
        var migration = new RecordingMigration(0, root =>
        {
            // Pretend the old format kept the log level at the top level.
            root["logging"] = new JsonObject { ["level"] = "warning" };
        });

        using var store = new SettingsStore(paths, new SettingsStoreOptions { WatchForChanges = false, Migrations = [migration] });
        store.Load();

        Assert.Equal(1, migration.Calls);
        Assert.Equal(original, File.ReadAllText(paths.SettingsFile + ".bak-0"));
        var file = ReadFile(store);
        Assert.Equal(1, (int)file["version"]!);
        Assert.Equal("keep", (string)file["oldKey"]!);
        Assert.True(store.GetSettings().Privacy.LocalOnly);
        Assert.Equal(LogLevel.Warning, store.GetSettings().Logging.Level);
    }

    [Fact]
    public void Load_OlderFileWithNoMigration_FailsAndLeavesTheFileUntouched()
    {
        using var temp = new TempDirectory();
        var paths = temp.NewPaths();
        const string original = """{ "version": 0 }""";
        WriteFile(paths, original);
        using var store = new SettingsStore(paths, new SettingsStoreOptions { WatchForChanges = false });

        var ex = Assert.Throws<SettingsException>(() => store.Load());

        Assert.Contains("no migration", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(original, File.ReadAllText(paths.SettingsFile));
        Assert.Equal(original, File.ReadAllText(paths.SettingsFile + ".bak-0"));
    }

    [Fact]
    public void Load_NewerVersion_IsRefusedAndTheFileIsNotChanged()
    {
        using var temp = new TempDirectory();
        var paths = temp.NewPaths();
        const string original = """{ "version": 2, "somethingNew": true }""";
        WriteFile(paths, original);
        using var store = new SettingsStore(paths, new SettingsStoreOptions { WatchForChanges = false });

        var ex = Assert.Throws<SettingsVersionException>(() => store.Load());

        Assert.Equal(2, ex.FileVersion);
        Assert.Equal(1, ex.SupportedVersion);
        Assert.Contains("newer", ex.Message, StringComparison.Ordinal);
        Assert.Equal(original, File.ReadAllText(paths.SettingsFile));
        Assert.Throws<SettingsVersionException>(() => store.SetJson("privacy.localOnly", "true"));
        Assert.Equal(original, File.ReadAllText(paths.SettingsFile));
    }

    [Fact]
    public void Load_InvalidJson_IsReportedAndTheFileIsNotOverwritten()
    {
        using var temp = new TempDirectory();
        var paths = temp.NewPaths();
        WriteFile(paths, "{ not json");
        using var store = new SettingsStore(paths, new SettingsStoreOptions { WatchForChanges = false });

        Assert.Throws<SettingsValidationException>(() => store.Load());
        Assert.Equal("{ not json", File.ReadAllText(paths.SettingsFile));
    }

    [Fact]
    public void Load_FileWithoutVersion_IsTreatedAsCurrentAndGetsMissingDefaults()
    {
        using var temp = new TempDirectory();
        var paths = temp.NewPaths();
        WriteFile(paths, """{ "privacy": { "localOnly": true } }""");
        using var store = new SettingsStore(paths, new SettingsStoreOptions { WatchForChanges = false });

        var settings = store.GetSettings();

        Assert.Equal(1, settings.Version);
        Assert.True(settings.Privacy.LocalOnly);
        Assert.Equal(1536, settings.Indexing.MaxMemoryMb);
        Assert.Equal("\"auto\"", store.GetJson("routing.tasks.agent"));
    }

    [Fact]
    public async Task Watcher_RaisesChangedForAnExternalEdit()
    {
        using var temp = new TempDirectory();
        using var store = NewStore(temp, watch: true);
        store.Load();

        var external = new TaskCompletionSource<SettingsChangedEventArgs>(TaskCreationOptions.RunContinuationsAsynchronously);
        store.Changed += (_, e) =>
        {
            if (e.Source == SettingsChangeSource.External)
            {
                external.TrySetResult(e);
            }
        };

        var edited = ReadFile(store);
        edited["privacy"]!["localOnly"] = true;
        File.WriteAllText(store.Paths.SettingsFile, edited.ToJsonString());

        var winner = await Task.WhenAny(external.Task, Task.Delay(TimeSpan.FromSeconds(10)));
        Assert.Same(external.Task, winner);
        Assert.True(store.Get<PrivacySettings>(SettingsSections.Privacy).LocalOnly);
    }

    [Fact]
    public async Task Watcher_DoesNotRaiseExternalChangedForOurOwnSaves()
    {
        using var temp = new TempDirectory();
        using var store = NewStore(temp, watch: true);
        store.Load();

        var saves = 0;
        var externals = 0;
        store.Changed += (_, e) =>
        {
            if (e.Source == SettingsChangeSource.Save)
            {
                Interlocked.Increment(ref saves);
            }
            else
            {
                Interlocked.Increment(ref externals);
            }
        };

        store.SetJson("privacy.localOnly", "true");
        store.Set(SettingsSections.Logging, new LoggingSettings { RpcTrace = true });
        store.Save();

        // Wait well past the 250 ms debounce so any spurious event would have arrived.
        await Task.Delay(1200);

        Assert.Equal(3, Volatile.Read(ref saves));
        Assert.Equal(0, Volatile.Read(ref externals));
    }

    [Fact]
    public async Task Watcher_KeepsTheOldSettingsWhenAnExternalEditIsInvalid()
    {
        using var temp = new TempDirectory();
        using var store = NewStore(temp, watch: true);
        store.SetJson("privacy.localOnly", "true");
        var externals = 0;
        store.Changed += (_, e) =>
        {
            if (e.Source == SettingsChangeSource.External)
            {
                Interlocked.Increment(ref externals);
            }
        };

        File.WriteAllText(store.Paths.SettingsFile, "{ broken");
        await Task.Delay(1200);

        Assert.Equal(0, Volatile.Read(ref externals));
        Assert.Equal("true", store.GetJson("privacy.localOnly"));
    }

    [Fact]
    public void KeyPath_GetsNestedValuesAndArrayItems()
    {
        using var temp = new TempDirectory();
        using var store = NewStore(temp);
        store.SetJson("providers", """[{"id":"a","kind":"ollama","displayName":"A","endpoint":"http://localhost:11434","models":["m1","m2"],"enabled":true}]""");

        Assert.Equal("\"auto\"", store.GetJson("routing.tasks.chat"));
        Assert.Equal("\"a\"", store.GetJson("providers.0.id"));
        Assert.Equal("\"m2\"", store.GetJson("providers.0.models.1"));
        Assert.Equal("""["m1","m2"]""", store.GetJson("providers.0.models"));
        Assert.StartsWith("{", store.GetJson("routing")!, StringComparison.Ordinal);
        Assert.Null(store.GetJson("providers.5.id"));
        Assert.Null(store.GetJson("nope.nothing"));
        Assert.Null(store.GetJson("privacy.localOnly.deeper"));
    }

    [Fact]
    public void KeyPath_SetsNestedValuesAndArrayItems()
    {
        using var temp = new TempDirectory();
        using var store = NewStore(temp);

        store.SetJson("routing.tasks.chat", "\"frontier\"");
        store.SetJson("providers", """[{"id":"a","kind":"ollama"}]""");
        store.SetJson("providers.0.endpoint", "\"http://localhost:11434\"");
        store.SetJson("providers.0.models", """["m1"]""");
        store.SetJson("providers.0.models.1", "\"m2\"");      // append at index == Count
        store.SetJson("providers.0.models.0", "\"m0\"");      // replace
        store.SetJson("providers.1", """{"id":"b","kind":"mistral"}""");
        store.SetJson("costCaps.b.dailyUsd", "2.5");          // creates intermediate objects
        store.SetJson("custom.nested.value", "[1,2]");        // unknown keys are allowed and kept

        var s = store.GetSettings();
        Assert.Equal(Profile.Frontier, s.Routing.Tasks[TaskClass.Chat]);
        Assert.Equal(["a", "b"], s.Providers.Select(p => p.Id));
        Assert.Equal("http://localhost:11434", s.Providers[0].Endpoint);
        Assert.Equal(["m0", "m2"], s.Providers[0].Models);
        Assert.Equal(ProviderKind.Mistral, s.Providers[1].Kind);
        Assert.Equal(2.5m, s.CostCaps["b"].DailyUsd);
        Assert.Equal("[1,2]", store.GetJson("custom.nested.value"));

        using var reloaded = new SettingsStore(store.Paths, new SettingsStoreOptions { WatchForChanges = false });
        Assert.Equal("[1,2]", reloaded.GetJson("custom.nested.value"));
        Assert.Equal("\"b\"", reloaded.GetJson("providers.1.id"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("  ")]
    [InlineData("a..b")]
    [InlineData(".a")]
    [InlineData("a.")]
    public void KeyPath_RejectsMalformedKeys(string key)
    {
        using var temp = new TempDirectory();
        using var store = NewStore(temp);

        Assert.Throws<SettingsValidationException>(() => store.GetJson(key));
        Assert.Throws<SettingsValidationException>(() => store.SetJson(key, "1"));
    }

    [Theory]
    [InlineData("privacy.localOnly", "\"yes\"")]
    [InlineData("approvals.mode", "\"yolo\"")]
    [InlineData("indexing.maxMemoryMb", "\"lots\"")]
    [InlineData("providers", "{}")]
    [InlineData("providers.0.kind", "\"carrier-pigeon\"")]
    [InlineData("routing.tasks.chat", "\"turbo\"")]
    public void SetJson_RejectsValuesThatBreakTheSchema_AndChangesNothing(string key, string json)
    {
        using var temp = new TempDirectory();
        using var store = NewStore(temp);
        store.SetJson("providers", """[{"id":"a","kind":"ollama"}]""");
        var before = File.ReadAllText(store.Paths.SettingsFile);

        Assert.Throws<SettingsValidationException>(() => store.SetJson(key, json));

        Assert.Equal(before, File.ReadAllText(store.Paths.SettingsFile));
    }

    [Fact]
    public void SetJson_RejectsBadJsonOutOfRangeIndexesAndTheVersionKey()
    {
        using var temp = new TempDirectory();
        using var store = NewStore(temp);
        store.SetJson("providers", """[{"id":"a","kind":"ollama"}]""");

        Assert.Throws<SettingsValidationException>(() => store.SetJson("privacy.localOnly", "{ nope"));
        Assert.Throws<SettingsValidationException>(() => store.SetJson("providers.3", """{"id":"z","kind":"ollama"}"""));
        Assert.Throws<SettingsValidationException>(() => store.SetJson("providers.0.id.deeper", "1"));
        Assert.Throws<SettingsValidationException>(() => store.SetJson("version", "9"));
        Assert.Throws<SettingsValidationException>(() => store.Set("version", new PrivacySettings()));
        Assert.Equal(1, store.GetSettings().Version);
    }

    [Fact]
    public void Get_ForAnUnknownSection_ReturnsANewDefaultObject()
    {
        using var temp = new TempDirectory();
        using var store = NewStore(temp);

        var result = store.Get<PrivacySettings>("neverHeardOfIt");

        Assert.False(result.LocalOnly);
    }

    private sealed class RecordingMigration(int fromVersion, Action<JsonObject> action) : ISettingsMigration
    {
        public int Calls { get; private set; }

        public int FromVersion => fromVersion;

        public void Migrate(JsonObject root)
        {
            Calls++;
            action(root);
        }
    }
}

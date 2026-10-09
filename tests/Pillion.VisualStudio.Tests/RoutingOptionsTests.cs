using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.Threading;
using Pillion.Contracts;
using Pillion.VisualStudio.Core;
using Pillion.VisualStudio.Core.Autocomplete;

namespace Pillion.VisualStudio.Tests;

// The tests await their own TaskCompletionSource tasks and the view models' tasks; no main thread is involved.
#pragma warning disable VSTHRD003

/// <summary>The Models and routing page's view model and the <c>settingsChanged</c> notification's consumers.</summary>
public sealed class RoutingOptionsTests : IDisposable
{
    private readonly JoinableTaskContext _context = Task.Run(() => new JoinableTaskContext()).Result;
    private readonly InMemorySettingsService _settings = new();
    private readonly ScriptedModelService _models = new();

    public void Dispose() => _context.Dispose();

    [Fact]
    public void ThereIsOneRowPerTaskClass_AndOneProfileRowPerRoutableProfile()
    {
        using var vm = Create();

        Assert.Equal(
            Enum.GetValues(typeof(TaskClass)).Cast<TaskClass>().OrderBy(t => (int)t),
            vm.TaskRoutes.Select(r => r.TaskClass).OrderBy(t => (int)t));
        Assert.Equal(["autocomplete", "inlineEdit", "chat", "agent", "embedding", "summarise", "vision"], vm.TaskRoutes.Select(r => r.Key));
        Assert.Equal(["auto", "fast", "balanced", "frontier", "localFast", "localStrong"], RoutingChoices.Profiles.Select(p => p.Value));
        Assert.Equal(["Auto", "Fast", "Balanced", "Frontier", "Local-fast", "Local-strong"], RoutingChoices.Profiles.Select(p => p.Label));
        Assert.Equal(["fast", "balanced", "frontier", "localFast", "localStrong"], vm.ProfileOverrides.Select(p => p.Key));
        Assert.Equal(["auto", "cpu", "8gb", "12gb", "16gb", "24gb"], vm.GpuTierChoices.Select(t => t.Value));
        Assert.All(vm.TaskRoutes, r => Assert.False(string.IsNullOrWhiteSpace(r.Description)));
        Assert.All(vm.TaskRoutes.Where(r => r.HasCloudSwitch), r => Assert.False(string.IsNullOrWhiteSpace(r.CloudSwitchNote)));
    }

    [Fact]
    public async Task MissingSettings_ShowAutoEverywhere_AndTheHostDefaultSwitches()
    {
        using var vm = Create();

        await vm.LoadAsync();

        Assert.Equal(OptionsLoadState.Loaded, vm.LoadState);
        Assert.False(vm.IsDirty);
        Assert.All(vm.TaskRoutes, r => Assert.Equal("auto", r.SelectedProfile.Value));
        Assert.Equal(
            [false, true, true, false, false, false, true],
            vm.TaskRoutes.Select(r => r.CloudSwitchOn));
        Assert.All(vm.ProfileOverrides, p => Assert.Equal(string.Empty, p.ModelId));
        Assert.False(vm.ThinkHarder);
        Assert.Equal("auto", vm.SelectedGpuTier.Value);
        Assert.Equal("GPU tier: Auto (detected by Pillion Host).", vm.GpuTierText);
    }

    [Fact]
    public async Task Embedding_HasNoCloudSwitch_AndNothingIsEverWrittenForIt()
    {
        using var vm = Create();
        await vm.LoadAsync();

        var embedding = vm.TaskRoutes.Single(r => r.TaskClass == TaskClass.Embedding);
        Assert.False(embedding.HasCloudSwitch);
        Assert.Null(embedding.CloudSwitchKey);

        embedding.CloudSwitchOn = true; // Ignored: the class always runs locally.
        embedding.SelectedProfile = RoutingChoices.Profiles.Single(p => p.Value == "localFast");
        await vm.SaveAsync();

        Assert.Equal(["routing.tasks.embedding"], _settings.Writes.Select(w => w.Key));
        Assert.DoesNotContain(_settings.Writes, w => w.Key.StartsWith("cloudSwitches.embedding", StringComparison.Ordinal));
    }

    [Fact]
    public async Task RoundTrip_SectionsToRowsAndBack_WritesOnlyWhatChanged()
    {
        _settings.Values["routing"] = """
            {"tasks":{"autocomplete":"localFast","inlineEdit":"balanced","chat":"auto","agent":"frontier","embedding":"auto","vision":"auto"},
             "profiles":{"fast":"mistral/codestral-latest","balanced":"","frontier":"","localFast":"qwen2.5-coder:7b","localStrong":""},
             "thinkHarder":true,"experimental":1}
            """;
        _settings.Values["cloudSwitches"] = """{"chat":true,"inlineEdit":true,"autocomplete":false,"agent":true,"selfHealing":false,"vision":true}""";
        _settings.Values["models.gpuTier"] = "\"12gb\"";
        using var vm = Create();

        await vm.LoadAsync();

        Assert.Equal(["localFast", "balanced", "auto", "frontier", "auto", "auto", "auto"], vm.TaskRoutes.Select(r => r.SelectedProfile.Value));
        Assert.Equal([false, true, true, true, false, false, true], vm.TaskRoutes.Select(r => r.CloudSwitchOn));
        Assert.Equal(["mistral/codestral-latest", string.Empty, string.Empty, "qwen2.5-coder:7b", string.Empty], vm.ProfileOverrides.Select(p => p.ModelId));
        Assert.True(vm.ThinkHarder);
        Assert.Equal("12gb", vm.SelectedGpuTier.Value);
        Assert.Equal("GPU tier: 12 GB GPU (set here, not detected).", vm.GpuTierText);
        Assert.False(vm.IsDirty);

        // The case from the manual check: Chat to Local-strong, the autocomplete cloud switch on.
        vm.TaskRoutes.Single(r => r.TaskClass == TaskClass.Chat).SelectedProfile = RoutingChoices.Profiles.Single(p => p.Value == "localStrong");
        vm.TaskRoutes.Single(r => r.TaskClass == TaskClass.Autocomplete).CloudSwitchOn = true;
        vm.ProfileOverrides.Single(p => p.Profile == Profile.Balanced).ModelId = "  openrouter/gpt-test ";
        vm.ProfileOverrides.Single(p => p.Profile == Profile.Fast).ModelId = string.Empty;
        vm.ThinkHarder = false;
        vm.SelectedGpuTier = vm.GpuTierChoices.Single(t => t.Value == "auto");
        Assert.True(vm.IsDirty);

        await vm.SaveAsync();

        Assert.Equal("Saved.", vm.StatusMessage);
        Assert.False(vm.IsDirty);
        Assert.Equal(
            [
                ("cloudSwitches.autocomplete", "true"),
                ("routing.tasks.chat", "\"localStrong\""),
                ("routing.profiles.fast", "\"\""),
                ("routing.profiles.balanced", "\"openrouter/gpt-test\""),
                ("routing.thinkHarder", "false"),
                ("models.gpuTier", "\"auto\""),
            ],
            _settings.Writes.Select(w => (w.Key, w.Json)));

        // Saving again with no edits writes nothing more.
        await vm.SaveAsync();
        Assert.Equal(6, _settings.Writes.Count);
    }

    [Fact]
    public async Task AnUnknownProfileValue_IsKeptUnlessTheUserChangesIt()
    {
        _settings.Values["routing"] = """{"tasks":{"chat":"experimental-profile"}}""";
        using var vm = Create();
        await vm.LoadAsync();

        var chat = vm.TaskRoutes.Single(r => r.TaskClass == TaskClass.Chat);
        Assert.Equal("auto", chat.SelectedProfile.Value);
        vm.ThinkHarder = true;
        await vm.SaveAsync();
        Assert.Equal(["routing.thinkHarder"], _settings.Writes.Select(w => w.Key));

        chat.SelectedProfile = RoutingChoices.Profiles.Single(p => p.Value == "fast");
        await vm.SaveAsync();
        Assert.Equal("\"fast\"", _settings.Values["routing.tasks.chat"]);
    }

    [Fact]
    public async Task TheProfileRows_SuggestTheListedModels_LocalOrCloudByProfile()
    {
        _models.Models = [TestModels.Local, TestModels.Cloud, TestModels.EmbeddingOnly];
        using var vm = Create();

        await vm.LoadAsync();

        Assert.Equal([TestModels.Cloud.Id], vm.ProfileOverrides.Single(p => p.Profile == Profile.Frontier).Suggestions);
        Assert.Equal([TestModels.EmbeddingOnly.Id, TestModels.Local.Id], vm.ProfileOverrides.Single(p => p.Profile == Profile.LocalFast).Suggestions);
        Assert.Contains("Pick a listed model", vm.ProfileOverrides.Single(p => p.Profile == Profile.Fast).Hint, StringComparison.Ordinal);
    }

    [Fact]
    public async Task WhenTheHostCannotListModels_ThePageStillLoads_WithoutSuggestions()
    {
        _models.ListFailure = new InvalidOperationException("no providers");
        using var vm = Create();

        await vm.LoadAsync();

        Assert.Equal(OptionsLoadState.Loaded, vm.LoadState);
        Assert.All(vm.ProfileOverrides, p => Assert.Empty(p.Suggestions));
        Assert.Contains("lists no", vm.ProfileOverrides[0].Hint, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AModelIdWithSpaces_IsRefusedAndNothingIsWritten()
    {
        using var vm = Create();
        await vm.LoadAsync();

        vm.ProfileOverrides[0].ModelId = "two words";
        await vm.SaveAsync();

        Assert.Contains("cannot contain spaces", vm.StatusMessage, StringComparison.Ordinal);
        Assert.Empty(_settings.Writes);
        Assert.True(vm.IsDirty);
    }

    [Fact]
    public async Task LocalOnlyMode_DisablesTheSwitches_AndSaysWhy()
    {
        _settings.Values["privacy.localOnly"] = "true";
        using var vm = Create();

        await vm.LoadAsync();

        Assert.True(vm.LocalOnly);
        Assert.All(vm.TaskRoutes, r => Assert.False(r.CloudSwitchEnabled));
        Assert.Contains("Local-only mode is on", vm.LocalOnlyNote, StringComparison.Ordinal);
    }

    [Fact]
    public void AutocompleteWithAChatOnlyProfile_ShowsAWarning()
    {
        var route = new TaskRouteViewModel(TaskClass.Autocomplete, "Autocomplete", "d", "autocomplete", false, "n");
        Assert.False(route.HasWarning);

        route.SelectedProfile = RoutingChoices.Profiles.Single(p => p.Value == "frontier");

        Assert.True(route.HasWarning);
        Assert.Contains("fill-in-the-middle", route.Warning, StringComparison.Ordinal);
        route.SelectedProfile = RoutingChoices.Profiles.Single(p => p.Value == "localFast");
        Assert.False(route.HasWarning);
    }

    [Fact]
    public async Task WhileTheHostIsNotReady_ThePageSaysSo_ThenLoadsWhenItIs()
    {
        var connection = new FakeHostConnection(HostState.Starting).With<ISettingsService>(_settings).With<IModelService>(_models);
        using var vm = new RoutingOptionsViewModel(connection, _context.Factory);

        await vm.LoadAsync();

        Assert.Equal(OptionsLoadState.HostNotReady, vm.LoadState);
        Assert.True(vm.IsHostNotReady);
        Assert.StartsWith(OptionsPageViewModel.HostNotReadyText, vm.StatusMessage, StringComparison.Ordinal);
        Assert.False(vm.SaveCommand.CanExecute(null));

        await connection.SetStateAsync(HostState.Ready);
        await Wait.UntilAsync(() => vm.LoadState == OptionsLoadState.Loaded, "the page loads when the host is ready");
    }

    [Fact]
    public async Task AnOpenPageWithoutEdits_ReadsAgainWhenItsSectionsChange_ButKeepsEdits()
    {
        var connection = new FakeHostConnection().With<ISettingsService>(_settings).With<IModelService>(_models);
        using var vm = new RoutingOptionsViewModel(connection, _context.Factory);
        await vm.LoadAsync();
        Assert.Equal("auto", vm.TaskRoutes.Single(r => r.TaskClass == TaskClass.Chat).SelectedProfile.Value);

        // An edit of settings.json (or another page's save) shows up without reopening the page.
        _settings.Values["routing"] = """{"tasks":{"chat":"localStrong"}}""";
        await connection.RaiseSettingsChangedAsync("routing");
        await Wait.UntilAsync(() => vm.TaskRoutes.Single(r => r.TaskClass == TaskClass.Chat).SelectedProfile.Value == "localStrong", "the page reads again");

        // A section the page does not show changes nothing.
        _settings.Values["routing"] = """{"tasks":{"chat":"fast"}}""";
        await connection.RaiseSettingsChangedAsync("indexing");
        await Task.Delay(200);
        Assert.Equal("localStrong", vm.TaskRoutes.Single(r => r.TaskClass == TaskClass.Chat).SelectedProfile.Value);

        // Unsaved edits are not thrown away.
        vm.ThinkHarder = true;
        await connection.RaiseSettingsChangedAsync("routing");
        await Task.Delay(200);
        Assert.True(vm.ThinkHarder);
        Assert.Equal("localStrong", vm.TaskRoutes.Single(r => r.TaskClass == TaskClass.Chat).SelectedProfile.Value);
    }

    [Fact]
    public async Task PrivacyAndProvidersPages_ReadAgainOnTheirSections()
    {
        _settings.Values["privacy.localOnly"] = "false";
        _settings.Values["cloudSwitches"] = """{"chat":true}""";
        var connection = new FakeHostConnection().With<ISettingsService>(_settings);
        using var privacy = new PrivacyOptionsViewModel(connection, _context.Factory);
        await privacy.LoadAsync();
        Assert.False(privacy.LocalOnly);

        _settings.Values["privacy.localOnly"] = "true";
        await connection.RaiseSettingsChangedAsync("privacy");

        await Wait.UntilAsync(() => privacy.LocalOnly, "the privacy page reads again");
    }

    [Fact]
    public async Task TheChatModelPicker_ListsModelsAgain_WhenRoutingOrProvidersChange_WithoutTheRefreshButton()
    {
        _settings.Values["privacy.localOnly"] = "false";
        _settings.Values["cloudSwitches.chat"] = "true";
        _settings.Values["providers"] = TestModels.Providers;
        var connection = new FakeHostConnection().With<IModelService>(_models).With<ISettingsService>(_settings);
        using var chat = new ChatViewModel(connection, _context.Factory, new ChatViewModelOptions { FlushInterval = TimeSpan.Zero });
        chat.Start();
        await Wait.UntilAsync(() => chat.ModelPicker.SelectedModel is not null, "the models are listed");

        var listed = Volatile.Read(ref _models.ListCalls);
        _models.Models = [TestModels.Local, TestModels.Cloud, new ModelDescriptor { Id = "ollama/new", ModelId = "new", DisplayName = "New", ProviderId = "ollama", IsLocal = true, IsAvailable = true, Capabilities = ModelCapabilities.Chat }];
        await connection.RaiseSettingsChangedAsync("providers");
        await Wait.UntilAsync(() => Volatile.Read(ref _models.ListCalls) > listed, "the models are listed again");
        await Wait.UntilAsync(() => chat.ModelPicker.Models.Any(m => m.Id == "ollama/new"), "the new model is in the picker");

        // A section that cannot change the list does nothing.
        listed = Volatile.Read(ref _models.ListCalls);
        await connection.RaiseSettingsChangedAsync("indexing");
        await Task.Delay(300);
        Assert.Equal(listed, Volatile.Read(ref _models.ListCalls));

        // Local-only reaches the picker the same way.
        _settings.Values["privacy.localOnly"] = "true";
        await connection.RaiseSettingsChangedAsync("privacy");
        await Wait.UntilAsync(
            () => chat.ModelPicker.Models.SingleOrDefault(m => m.Id == TestModels.Cloud.Id)?.UnavailableReason == ChatModelPolicy.LocalOnlyReason,
            "the cloud model is greyed out");
    }

    [Fact]
    public async Task AutocompleteAvailability_ReadsAgain_WhenFeaturesOrItsSectionChange()
    {
        _settings.Values["effective.features"] = """{"chat":true,"autocomplete":true}""";
        var connection = new FakeHostConnection().With<ISettingsService>(_settings);
        using var availability = new AutocompleteAvailability(connection);
        availability.Start();
        await Wait.UntilAsync(() => availability.Settings.Enabled, "enabled");

        _settings.Values["effective.features"] = """{"chat":true,"autocomplete":false}""";
        await connection.RaiseSettingsChangedAsync("features");
        await Wait.UntilAsync(() => !availability.Settings.Enabled, "disabled by the flag");

        _settings.Values["effective.features"] = """{"chat":true,"autocomplete":true}""";
        await connection.RaiseSettingsChangedAsync("indexing");
        await Task.Delay(200);
        Assert.False(availability.Settings.Enabled);

        await connection.RaiseSettingsChangedAsync("features");
        await Wait.UntilAsync(() => availability.Settings.Enabled, "enabled again");

        _settings.Values["autocomplete"] = """{"disabledLanguages":["vb"]}""";
        await connection.RaiseSettingsChangedAsync("autocomplete");
        await Wait.UntilAsync(() => availability.Settings.DisabledLanguages.Count == 1, "the section is read again");
    }

    [Fact]
    public void SettingsChangedEventArgs_AffectsComparesSectionNamesIgnoringCase()
    {
        var args = new HostSettingsChangedEventArgs(["routing", "cloudSwitches"]);

        Assert.True(args.Affects("CloudSwitches"));
        Assert.True(args.Affects("privacy", "routing"));
        Assert.False(args.Affects("privacy"));
        Assert.False(new HostSettingsChangedEventArgs(null).Affects("routing"));
        Assert.Empty(new HostSettingsChangedEventArgs(null).Sections);
    }

    [Fact]
    public async Task TheSupervisor_ForwardsTheCurrentSessionsSettingsChanged_AndIgnoresAnEndedOne()
    {
        var launcher = new FakeLauncher { InitialState = HostState.Ready };
        using var supervisor = new HostSupervisor(launcher, _context.Factory, new PillionLog(directory: null), new FakeClock());
        var reported = new List<string[]>();
        supervisor.SettingsChanged += (_, e) =>
        {
            lock (reported)
            {
                reported.Add([.. e.Sections]);
            }
        };

        supervisor.Start();
        await Wait.UntilAsync(() => supervisor.HostProcessId is not null, "connected");
        var first = launcher.LastSession;
        first.ReportSettingsChanged("routing", "cloudSwitches");

        lock (reported)
        {
            Assert.Equal(["routing", "cloudSwitches"], Assert.Single(reported));
        }

        first.Crash();
        await Wait.UntilAsync(() => first.Disposed, "the session ended");
        first.ReportSettingsChanged("privacy");

        lock (reported)
        {
            Assert.Single(reported);
        }
    }

    private RoutingOptionsViewModel Create() =>
        new(new FakeHostConnection().With<ISettingsService>(_settings).With<IModelService>(_models), _context.Factory);
}

using System;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.VisualStudio.Threading;
using Newtonsoft.Json.Linq;
using Pillion.Contracts;
using Pillion.VisualStudio.Core;

namespace Pillion.VisualStudio.Tests;

// The tests await their own TaskCompletionSource tasks and the view models' tasks; no main thread is involved.
#pragma warning disable VSTHRD003

public sealed class OptionsViewModelTests : IDisposable
{
    private readonly JoinableTaskContext _context = Task.Run(() => new JoinableTaskContext()).Result;
    private readonly InMemorySettingsService _settings = TestModels.Settings();
    private readonly InMemoryCredentialStore _credentials = new();

    public void Dispose() => _context.Dispose();

    [Fact]
    public async Task Providers_LoadFromSettings_WithKeyPresence()
    {
        _credentials.Keys["openrouter"] = "sk-test-value";
        using var vm = CreateProviders();

        await vm.LoadAsync();

        Assert.Equal(OptionsLoadState.Loaded, vm.LoadState);
        Assert.Equal(["ollama", "openrouter"], vm.Providers.Select(p => p.Id));
        var ollama = vm.Providers[0];
        Assert.Equal("ollama", ollama.Kind);
        Assert.Equal("Ollama", ollama.DisplayName);
        Assert.Equal("http://localhost:11434", ollama.Endpoint);
        Assert.Equal(["qwen2.5-coder:7b"], ollama.Models);
        Assert.True(ollama.Enabled);
        Assert.False(ollama.IsNew);
        Assert.Equal("No key stored", ollama.KeyStatusText);
        Assert.Equal("Key stored", vm.Providers[1].KeyStatusText);
        Assert.Same(ollama, vm.SelectedProvider);
        Assert.False(vm.IsDirty);
    }

    [Fact]
    public async Task Providers_RoundTrip_AddEditRemove_KeepsUnknownFields_AndKeysNeverReachSettings()
    {
        _credentials.Keys["ollama"] = "old-ollama-key";
        using var vm = CreateProviders();
        await vm.LoadAsync();

        // Edit the cloud provider: unknown "proxy" must survive.
        var openRouter = vm.Providers.Single(p => p.Id == "openrouter");
        openRouter.DisplayName = "OpenRouter (work)";
        openRouter.ModelsText = "gpt-test\r\n\r\n  other-model  \ngpt-test";
        openRouter.ApiKey = "sk-or-secret-1234567890";
        Assert.Equal("New key will be stored when you save", openRouter.KeyStatusText);

        // Add a provider with a key.
        vm.Add();
        var added = vm.SelectedProvider!;
        Assert.True(added.IsNew);
        Assert.Equal("provider-1", added.Id);
        added.Id = "lmstudio";
        added.Kind = "openai-compatible";
        added.DisplayName = "LM Studio";
        added.Endpoint = "http://localhost:1234/v1";
        added.ModelsText = "qwen2.5-coder-14b";
        added.Enabled = false;
        added.ApiKey = "lm-secret";

        // Remove Ollama: its key is deleted on save.
        vm.SelectedProvider = vm.Providers.Single(p => p.Id == "ollama");
        vm.Remove();
        Assert.True(vm.IsDirty);

        await vm.SaveAsync();

        Assert.Equal("Saved.", vm.StatusMessage);
        Assert.False(vm.IsDirty);
        var (key, json) = Assert.Single(_settings.Writes);
        Assert.Equal("providers", key);
        Assert.DoesNotContain("secret", json);
        Assert.DoesNotContain("apiKey", json, StringComparison.OrdinalIgnoreCase);

        var saved = JArray.Parse(json);
        Assert.Equal(2, saved.Count);
        var savedRouter = (JObject)saved[0];
        Assert.Equal("openrouter", (string?)savedRouter["id"]);
        Assert.Equal("openai-compatible", (string?)savedRouter["kind"]);
        Assert.Equal("OpenRouter (work)", (string?)savedRouter["displayName"]);
        Assert.Equal(["gpt-test", "other-model"], savedRouter["models"]!.Select(t => (string?)t));
        Assert.Equal("http://proxy.example:8080", (string?)savedRouter["proxy"]?["url"]);
        var savedAdded = (JObject)saved[1];
        Assert.Equal("lmstudio", (string?)savedAdded["id"]);
        Assert.Equal("http://localhost:1234/v1", (string?)savedAdded["endpoint"]);
        Assert.False((bool)savedAdded["enabled"]!);

        Assert.Equal("sk-or-secret-1234567890", _credentials.Keys["openrouter"]);
        Assert.Equal("lm-secret", _credentials.Keys["lmstudio"]);
        Assert.False(_credentials.Keys.ContainsKey("ollama"));
        Assert.Contains("ollama", _credentials.Deleted);

        Assert.All(vm.Providers, p => Assert.Equal(string.Empty, p.ApiKey));
        Assert.All(vm.Providers, p => Assert.Equal("Key stored", p.KeyStatusText));
        Assert.False(added.IsNew);
        added.Id = "renamed";
        Assert.Equal("lmstudio", added.Id);

        // Reload: what was saved is what is shown.
        await vm.LoadAsync();
        Assert.Equal(["openrouter", "lmstudio"], vm.Providers.Select(p => p.Id));
        Assert.Equal("OpenRouter (work)", vm.Providers[0].DisplayName);
    }

    [Fact]
    public async Task Providers_InvalidOrDuplicateIds_AreNotSaved()
    {
        using var vm = CreateProviders();
        await vm.LoadAsync();

        vm.Add();
        vm.SelectedProvider!.Id = "has space";
        await vm.SaveAsync();
        Assert.Contains("must be 1 to 128 letters", vm.StatusMessage);

        vm.SelectedProvider!.Id = "OLLAMA";
        await vm.SaveAsync();
        Assert.Contains("ids must be unique", vm.StatusMessage);

        vm.SelectedProvider!.Id = "ok";
        vm.SelectedProvider.Endpoint = "ftp://example.com";
        await vm.SaveAsync();
        Assert.Contains("not an http or https URL", vm.StatusMessage);

        Assert.Empty(_settings.Writes);
        Assert.Empty(_credentials.Keys);
    }

    [Fact]
    public async Task Providers_KeysAreNotStoredWhenTheSettingsWriteFails()
    {
        using var vm = CreateProviders();
        await vm.LoadAsync();
        vm.Providers[1].ApiKey = "sk-should-not-be-stored";
        _settings.SetFailure = new PillionRpcException(new PillionError { Code = PillionErrorCode.PolicyDenied, Message = "Settings are read-only." });

        await vm.SaveAsync();

        Assert.Equal("Could not save: Settings are read-only.", vm.StatusMessage);
        Assert.Empty(_credentials.Keys);
        Assert.True(vm.IsDirty);
        Assert.DoesNotContain("sk-should", vm.StatusMessage);
    }

    [Fact]
    public async Task Providers_TestConnection_ShowsTheHostsResult_AndAsksToSaveFirst()
    {
        _settings.Tester = id => new ProviderTestResult { ProviderId = id, IsSuccess = id == "ollama", Message = id == "ollama" ? "Ollama 0.12 answered." : "401 Unauthorized." };
        using var vm = CreateProviders();
        await vm.LoadAsync();

        await vm.TestSelectedAsync();
        Assert.Equal("Connection succeeded. Ollama 0.12 answered.", vm.Providers[0].TestResult);

        vm.SelectedProvider = vm.Providers[1];
        await vm.TestSelectedAsync();
        Assert.Equal("Connection failed. 401 Unauthorized.", vm.Providers[1].TestResult);

        vm.Add();
        await vm.TestSelectedAsync();
        Assert.StartsWith("Save first", vm.SelectedProvider!.TestResult, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Pages_ShowHostNotReady_AndLoadOnceTheHostIsReady()
    {
        var connection = new FakeHostConnection(HostState.Starting).With<ISettingsService>(_settings);
        using var vm = new ProvidersOptionsViewModel(connection, _context.Factory, _credentials);

        await vm.LoadAsync();

        Assert.Equal(OptionsLoadState.HostNotReady, vm.LoadState);
        Assert.StartsWith(OptionsPageViewModel.HostNotReadyText, vm.StatusMessage, StringComparison.Ordinal);
        Assert.False(vm.SaveCommand.CanExecute(null));
        Assert.False(vm.AddCommand.CanExecute(null));

        await connection.SetStateAsync(HostState.Ready);
        await Wait.UntilAsync(() => vm.LoadState == OptionsLoadState.Loaded, "the page loads once the host is ready");
        Assert.Equal(2, vm.Providers.Count);
        Assert.True(vm.AddCommand.CanExecute(null));
    }

    [Fact]
    public async Task Privacy_RoundTrip_LocalOnlyAndCloudSwitches_KeepsUnknownSwitches()
    {
        _settings.Values["cloudSwitches"] = "{\"chat\":true,\"inlineEdit\":false,\"autocomplete\":false,\"agent\":false,\"selfHealing\":false,\"vision\":true,\"futureSwitch\":true}";
        using var vm = new PrivacyOptionsViewModel(new FakeHostConnection().With<ISettingsService>(_settings), _context.Factory);

        await vm.LoadAsync();

        Assert.Equal(OptionsLoadState.Loaded, vm.LoadState);
        Assert.False(vm.LocalOnly);
        Assert.Equal(["chat", "inlineEdit", "autocomplete", "agent", "selfHealing", "vision"], vm.CloudSwitches.Select(s => s.Key));
        Assert.Equal([true, false, false, false, false, true], vm.CloudSwitches.Select(s => s.IsOn));
        Assert.All(vm.CloudSwitches, s => Assert.False(string.IsNullOrWhiteSpace(s.Description)));
        Assert.False(vm.IsDirty);

        vm.CloudSwitches.Single(s => s.Key == "agent").IsOn = true;
        vm.LocalOnly = true;
        Assert.True(vm.IsDirty);
        Assert.All(vm.CloudSwitches, s => Assert.False(s.IsEnabled));

        await vm.SaveAsync();

        Assert.Equal("Saved.", vm.StatusMessage);
        Assert.Equal("true", _settings.Values["privacy.localOnly"]);
        var switches = JObject.Parse(_settings.Values["cloudSwitches"]);
        Assert.True((bool)switches["agent"]!);
        Assert.False((bool)switches["inlineEdit"]!);
        Assert.True((bool)switches["futureSwitch"]!);

        using var reloaded = new PrivacyOptionsViewModel(new FakeHostConnection().With<ISettingsService>(_settings), _context.Factory);
        await reloaded.LoadAsync();
        Assert.True(reloaded.LocalOnly);
        Assert.True(reloaded.CloudSwitches.Single(s => s.Key == "agent").IsOn);
    }

    [Fact]
    public async Task Privacy_MissingSettings_UseTheHostDefaults()
    {
        using var vm = new PrivacyOptionsViewModel(new FakeHostConnection().With<ISettingsService>(new InMemorySettingsService()), _context.Factory);

        await vm.LoadAsync();

        Assert.False(vm.LocalOnly);
        Assert.Equal([true, true, false, false, false, true], vm.CloudSwitches.Select(s => s.IsOn));
        Assert.All(vm.CloudSwitches, s => Assert.True(s.IsEnabled));
    }

    private ProvidersOptionsViewModel CreateProviders() =>
        new(new FakeHostConnection().With<ISettingsService>(_settings), _context.Factory, _credentials);
}

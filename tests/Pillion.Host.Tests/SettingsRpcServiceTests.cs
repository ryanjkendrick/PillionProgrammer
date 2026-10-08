using Microsoft.Extensions.DependencyInjection;
using Pillion.Common;
using Pillion.Common.Secrets;
using Pillion.Common.Settings;
using Pillion.Contracts;
using Pillion.Host.Settings;

namespace Pillion.Host.Tests;

public sealed class SettingsRpcServiceTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "pillion-tests", Guid.NewGuid().ToString("N"));
    private readonly SettingsStore _store;
    private readonly SettingsRpcService _service;

    public SettingsRpcServiceTests()
    {
        _store = new SettingsStore(new PillionPaths(Path.Combine(_directory, "data")), new SettingsStoreOptions { WatchForChanges = false });
        _store.Load();
        _service = new SettingsRpcService(_store, new NoProviderTester());
    }

    public void Dispose()
    {
        _store.Dispose();
        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch (IOException)
        {
            // Best effort cleanup of a temp folder.
        }
    }

    [Fact]
    public async Task GetAsync_ReturnsTheJsonAtTheKeyOrNull()
    {
        Assert.Equal("false", await _service.GetAsync("privacy.localOnly", CancellationToken.None));
        Assert.Equal("1536", await _service.GetAsync("indexing.maxMemoryMb", CancellationToken.None));
        Assert.Equal("\"auto\"", await _service.GetAsync("routing.tasks.chat", CancellationToken.None));
        Assert.StartsWith("{", (await _service.GetAsync("privacy", CancellationToken.None))!, StringComparison.Ordinal);
        Assert.Null(await _service.GetAsync("does.not.exist", CancellationToken.None));
    }

    [Fact]
    public async Task SetAsync_AppliesValidatesAndPersists()
    {
        await _service.SetAsync("privacy.localOnly", "true", CancellationToken.None);
        await _service.SetAsync("providers", """[{"id":"ollama","kind":"ollama","displayName":"Ollama","endpoint":"http://localhost:11434","models":["qwen"],"enabled":true}]""", CancellationToken.None);

        Assert.Equal("true", await _service.GetAsync("privacy.localOnly", CancellationToken.None));
        Assert.Equal("\"qwen\"", await _service.GetAsync("providers.0.models.0", CancellationToken.None));
        Assert.True(File.ReadAllText(_store.Paths.SettingsFile).Contains("\"localOnly\": true", StringComparison.Ordinal));

        await Assert.ThrowsAsync<SettingsValidationException>(() => _service.SetAsync("approvals.mode", "\"yolo\"", CancellationToken.None));
        await Assert.ThrowsAsync<SettingsValidationException>(() => _service.SetAsync("privacy.localOnly", "{ nope", CancellationToken.None));
        Assert.Equal("true", await _service.GetAsync("privacy.localOnly", CancellationToken.None));
    }

    [Theory]
    [InlineData("secrets")]
    [InlineData("secrets.openai")]
    [InlineData("Secrets.OpenAI")]
    [InlineData("  secrets.x")]
    public async Task SecretKeys_AreRejectedWithPolicyDenied_ForGetAndSet(string key)
    {
        var get = await Assert.ThrowsAsync<PillionRpcException>(() => _service.GetAsync(key, CancellationToken.None));
        var set = await Assert.ThrowsAsync<PillionRpcException>(() => _service.SetAsync(key, "\"sk-secret-value\"", CancellationToken.None));

        Assert.Equal(PillionErrorCode.PolicyDenied, get.Error.Code);
        Assert.Equal(PillionErrorCode.PolicyDenied, set.Error.Code);
        Assert.DoesNotContain("sk-secret-value", set.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("sk-secret-value", File.ReadAllText(_store.Paths.SettingsFile), StringComparison.Ordinal);
    }

    [Fact]
    public async Task KeysThatMerelyStartWithSecrets_AreNotBlocked()
    {
        // "secretsAreFine" is not under the secrets. prefix; it is just an unknown key.
        await _service.SetAsync("secretsAreFine", "1", CancellationToken.None);

        Assert.Equal("1", await _service.GetAsync("secretsAreFine", CancellationToken.None));
    }

    [Fact]
    public async Task TestProviderAsync_WithTheDefaultTester_ReportsNoTesterRegistered()
    {
        var result = await _service.TestProviderAsync("openai", CancellationToken.None);

        Assert.Equal("openai", result.ProviderId);
        Assert.False(result.IsSuccess);
        Assert.Equal("No provider tester registered", result.Message);
    }

    [Fact]
    public async Task TestProviderAsync_DelegatesToTheRegisteredTester()
    {
        var service = new SettingsRpcService(_store, new FakeTester());

        var result = await service.TestProviderAsync("p1", CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Equal("p1", result.ProviderId);
    }

    [Fact]
    public void AddPillionSettingsRpc_RegistersTheServiceAndItsDependencies()
    {
        var services = new ServiceCollection();
        services.AddPillionSettingsRpc(new PillionPaths(Path.Combine(_directory, "di")));

        using var provider = services.BuildServiceProvider();

        Assert.IsType<SettingsRpcService>(provider.GetRequiredService<ISettingsService>());
        Assert.Same(provider.GetRequiredService<SettingsRpcService>(), provider.GetRequiredService<ISettingsService>());
        Assert.Same(provider.GetRequiredService<SettingsStore>(), provider.GetRequiredService<SettingsStore>());
        Assert.IsType<WindowsCredentialManagerSecretStore>(provider.GetRequiredService<ISecretStore>());
        Assert.IsType<NoProviderTester>(provider.GetRequiredService<IProviderTester>());
        Assert.Equal(Path.Combine(_directory, "di"), provider.GetRequiredService<PillionPaths>().Root);
    }

    [Fact]
    public void AddPillionSettings_KeepsAnEarlierTesterAndSecretStoreRegistration()
    {
        var services = new ServiceCollection();
        var secrets = new InMemorySecretStore();
        services.AddSingleton<ISecretStore>(secrets);
        services.AddSingleton<IProviderTester, FakeTester>();
        services.AddPillionSettings(new PillionPaths(Path.Combine(_directory, "di2")));

        using var provider = services.BuildServiceProvider();

        Assert.Same(secrets, provider.GetRequiredService<ISecretStore>());
        Assert.IsType<FakeTester>(provider.GetRequiredService<IProviderTester>());
    }

    private sealed class FakeTester : IProviderTester
    {
        public Task<ProviderTestResult> TestAsync(string providerId, CancellationToken cancellationToken)
            => Task.FromResult(new ProviderTestResult { ProviderId = providerId, IsSuccess = true, Message = "ok" });
    }
}

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Pillion.CommandRunner;
using Pillion.Common.Connectivity;
using Pillion.Common.Settings;
using Pillion.Contracts;
using Pillion.Host.CommandSessions;
using Pillion.Host.Tests.TestSupport;

namespace Pillion.Host.Tests;

/// <summary>The Command Runner in the composed host (work item 3.11): registered behind <c>features.commandRunner</c>, with the installation path from the handshake.</summary>
public sealed class CommandRunnerHostTests : IDisposable
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(120);
    private readonly TempDirectory _directory = new();
    private readonly FakeSettings _settings = new();

    public void Dispose() => _directory.Dispose();

    [Fact]
    public async Task TheFlagOff_RefusesASession_AndOnCreatesOneThatRunsACommand()
    {
        await using var host = await StartAsync();
        var runner = host.Services.GetRequiredService<ICommandRunner>();
        var sessions = host.Services.GetRequiredService<HostCommandSessions>();
        _settings.Update(s => s.Features.CommandRunner = false);

        var denied = await Assert.ThrowsAsync<PillionRpcException>(() => runner.CreateSessionAsync(Options()));
        Assert.Equal(PillionErrorCode.PolicyDenied, denied.Error.Code);
        Assert.Contains("commandRunner", denied.Error.Message, StringComparison.Ordinal);
        await Assert.ThrowsAsync<PillionRpcException>(() => sessions.CreateAsync(_directory.Path, o => o.LoadDeveloperEnvironment = false));

        _settings.Update(s => s.Features.CommandRunner = true);
        await using var session = await sessions.CreateAsync(_directory.Path, o => o.LoadDeveloperEnvironment = false).WaitAsync(Wait);
        var result = await session.RunAsync("Write-Output host-ok").WaitAsync(Wait);

        Assert.Equal(0, result.ExitCode);
        Assert.Contains("host-ok", result.ModelView.Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task APolicy_CanSwitchTheFlagOff()
    {
        await using var host = await StartAsync();
        var runner = host.Services.GetRequiredService<ICommandRunner>();
        _settings.SetPolicies(RepositoryPolicy.Parse("""{ "disabledFeatures": ["commandRunner"] }"""));

        var denied = await Assert.ThrowsAsync<PillionRpcException>(() => runner.CreateSessionAsync(Options()));

        Assert.Equal(PillionErrorCode.PolicyDenied, denied.Error.Code);
    }

    [Fact]
    public async Task WithoutAHandshake_TheInstallationPath_IsTheNewestFound_OrNone()
    {
        await using var host = await StartAsync();
        var sessions = host.Services.GetRequiredService<HostCommandSessions>();

        var path = sessions.VisualStudioInstallationPath;

        Assert.Equal(CommandRunner.Shells.VisualStudioLocator.FindNewestInstallation(), path);
    }

    private CommandSessionOptions Options() => new(_directory.Path) { LoadDeveloperEnvironment = false };

    private Task<InProcessHost> StartAsync() => InProcessHost.StartAsync(services =>
        services.Replace(ServiceDescriptor.Singleton<IConnectivitySettingsProvider>(_settings)));

    private sealed class FakeSettings : IConnectivitySettingsProvider
    {
        private readonly List<RepositoryPolicy> _policies = [];

        public event EventHandler? Changed;

        public PillionSettings Settings { get; } = new();

        public EffectiveSettings GetEffectiveSettings() => EffectiveSettings.Resolve(Settings, _policies);

        public void Update(Action<PillionSettings> edit)
        {
            edit(Settings);
            Changed?.Invoke(this, EventArgs.Empty);
        }

        public void SetPolicies(params RepositoryPolicy[] policies)
        {
            _policies.Clear();
            _policies.AddRange(policies);
            Changed?.Invoke(this, EventArgs.Empty);
        }
    }
}

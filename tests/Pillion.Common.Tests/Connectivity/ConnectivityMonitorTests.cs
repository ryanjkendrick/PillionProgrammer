using Pillion.Common.Connectivity;
using Pillion.Common.Settings;

namespace Pillion.Common.Tests.Connectivity;

public sealed class ConnectivityMonitorTests
{
    private static readonly TimeSpan Minute = TimeSpan.FromSeconds(60);

    private static void AddCloud(PillionSettings settings, string id = "openai", ProviderKind kind = ProviderKind.OpenAi, string endpoint = "")
    {
        settings.Providers.Add(FakeSettingsProvider.Provider(id, kind, endpoint));
    }

    private static void AddTwoCloudProviders(PillionSettings settings)
    {
        AddCloud(settings, "openai");
        AddCloud(settings, "azure", ProviderKind.AzureOpenAi, "https://contoso.openai.azure.com/openai");
    }

    [Fact]
    public void ContractEnumMatchesTheCommonEnum()
    {
        Assert.Equal(
            Enum.GetNames<ConnectivityState>(),
            Enum.GetNames<Pillion.Contracts.ConnectivityState>());
        foreach (var name in Enum.GetNames<ConnectivityState>())
        {
            Assert.Equal(
                (int)Enum.Parse<ConnectivityState>(name),
                (int)Enum.Parse<Pillion.Contracts.ConnectivityState>(name));
        }
    }

    [Fact]
    public void NoProviders_IsOnlineAndNothingIsProbed()
    {
        using var f = new MonitorFixture();

        Assert.Equal(ConnectivityState.Online, f.Monitor.State);
        Assert.Empty(f.Monitor.Snapshot);
        Assert.Equal(0, f.Prober.CallCount);
        Assert.Empty(f.Events);
    }

    [Fact]
    public async Task OnlyLocalAndDisabledProviders_AreNeverProbed()
    {
        using var f = new MonitorFixture(start: false, configure: s => s.Configure(settings =>
        {
            settings.Providers.Add(FakeSettingsProvider.Provider("ollama", ProviderKind.Ollama, "http://localhost:11434"));
            settings.Providers.Add(FakeSettingsProvider.Provider("lmstudio", ProviderKind.OpenAiCompatible, "http://localhost:1234/v1"));
            settings.Providers.Add(FakeSettingsProvider.Provider("v4", ProviderKind.OpenAiCompatible, "http://127.0.0.1:8080"));
            settings.Providers.Add(FakeSettingsProvider.Provider("v6", ProviderKind.OpenAiCompatible, "http://[::1]:8080"));
            settings.Providers.Add(FakeSettingsProvider.Provider("nas", ProviderKind.OpenAiCompatible, "http://gpu-box.local:8000"));
            settings.Providers.Add(FakeSettingsProvider.Provider("dev", ProviderKind.OpenAiCompatible, "http://llm.localhost"));
            settings.Providers.Add(FakeSettingsProvider.Provider("off", ProviderKind.OpenAi, enabled: false));
        }));
        f.Start();
        await f.ProbeAsync();
        f.Time.Advance(Minute);
        await f.IdleAsync();

        Assert.Equal(0, f.Prober.CallCount);
        Assert.Equal(ConnectivityState.Online, f.Monitor.State);
        Assert.Empty(f.Monitor.Snapshot);
        Assert.True(f.Monitor.IsProviderReachable("ollama"));
        Assert.True(f.Monitor.IsProviderReachable("lmstudio"));
        Assert.True(f.Monitor.IsProviderReachable("v6"));
        Assert.False(f.Monitor.IsProviderReachable("off"));
        Assert.False(f.Monitor.IsProviderReachable("never-configured"));
    }

    [Fact]
    public async Task OpenAiCompatibleOnARemoteHost_IsProbedAsCloud()
    {
        using var f = new MonitorFixture(start: false, configure: s => s.Configure(settings =>
            settings.Providers.Add(FakeSettingsProvider.Provider("gateway", ProviderKind.OpenAiCompatible, "https://llm.example.com:8443/v1/chat/completions?x=1"))));
        f.Start();
        await f.IdleAsync();

        var call = Assert.Single(f.Prober.Calls);
        Assert.Equal("gateway", call.ProviderId);
        Assert.Equal("https://llm.example.com:8443/", call.Origin.AbsoluteUri);
    }

    [Fact]
    public async Task KindsWithADefaultEndpoint_AreProbedAtThatOrigin()
    {
        using var f = new MonitorFixture(start: false, configure: s => s.Configure(settings =>
        {
            AddCloud(settings, "openai", ProviderKind.OpenAi);
            AddCloud(settings, "anthropic", ProviderKind.Anthropic);
            AddCloud(settings, "noendpoint", ProviderKind.AzureOpenAi);
        }));
        f.Start();
        await f.IdleAsync();

        Assert.Equal(
            ["https://api.anthropic.com/", "https://api.openai.com/"],
            f.Prober.Calls.Select(c => c.Origin.AbsoluteUri).Order(StringComparer.Ordinal));

        // Azure OpenAI without an endpoint cannot be probed; it is not reported as down.
        Assert.True(f.Monitor.IsProviderReachable("noendpoint"));
        Assert.DoesNotContain("noendpoint", f.Monitor.Snapshot.Keys);
    }

    [Fact]
    public async Task InitialProbe_RecordsReachabilityWithoutAnEvent()
    {
        using var f = new MonitorFixture(start: false, configure: s => s.Configure(settings => AddCloud(settings)));
        f.Start();
        await f.IdleAsync();

        Assert.Equal(1, f.Prober.CallCount);
        var snapshot = Assert.Single(f.Monitor.Snapshot);
        Assert.Equal("openai", snapshot.Key);
        Assert.True(snapshot.Value.Reachable);
        Assert.Equal(f.Time.GetUtcNow(), snapshot.Value.LastProbeTime);
        Assert.Equal(TimeSpan.FromMilliseconds(10), snapshot.Value.Latency);
        Assert.Equal(ProbeErrorCategory.None, snapshot.Value.ErrorCategory);
        Assert.Equal(ConnectivityState.Online, f.Monitor.State);
        Assert.Empty(f.Events);
    }

    [Fact]
    public async Task ProviderCountsAsReachableUntilItHasBeenProbed()
    {
        using var f = new MonitorFixture(start: false, configure: s => s.Configure(settings => AddCloud(settings)));
        var release = new TaskCompletionSource<ProbeResult>();
        f.Prober.Behavior = (_, _) => release.Task;
        f.Start();
        await MonitorFixture.WaitUntilAsync(() => f.Prober.CallCount == 1, "first probe started");

        Assert.Equal(ConnectivityState.Online, f.Monitor.State);
        Assert.True(f.Monitor.IsProviderReachable("openai"));
        Assert.Null(f.Monitor.Snapshot["openai"].LastProbeTime);

        release.SetResult(ProbeResult.Failure(ProbeErrorCategory.Dns));
        await f.IdleAsync();
        Assert.Equal(ConnectivityState.Offline, f.Monitor.State);
    }

    [Fact]
    public async Task Transitions_GoOnlineDegradedOfflineAndBack_WithReasons()
    {
        using var f = new MonitorFixture(start: false, configure: s => s.Configure(AddTwoCloudProviders));
        f.Start();
        await f.IdleAsync();
        Assert.Equal(ConnectivityState.Online, f.Monitor.State);

        f.Prober.Set("openai", ProbeResult.Failure(ProbeErrorCategory.Timeout));
        await f.ProbeAsync();
        Assert.Equal(ConnectivityState.Degraded, f.Monitor.State);
        Assert.False(f.Monitor.IsProviderReachable("openai"));
        Assert.True(f.Monitor.IsProviderReachable("azure"));

        f.Prober.Set("azure", ProbeResult.Failure(ProbeErrorCategory.Dns));
        await f.ProbeAsync();
        Assert.Equal(ConnectivityState.Offline, f.Monitor.State);

        f.Prober.Set("openai", FakeProber.Ok);
        await f.ProbeAsync();
        Assert.Equal(ConnectivityState.Degraded, f.Monitor.State);

        f.Prober.Set("azure", FakeProber.Ok);
        await f.ProbeAsync();
        Assert.Equal(ConnectivityState.Online, f.Monitor.State);

        Assert.Equal(
            [
                "Online->Degraded: openai unreachable: timeout",
                "Degraded->Offline: azure unreachable: dns failure",
                "Offline->Degraded: openai reachable",
                "Degraded->Online: azure reachable",
            ],
            f.EventSummaries());
    }

    [Fact]
    public async Task EventCarriesTheSnapshotAndRaisesOncePerTransition()
    {
        using var f = new MonitorFixture(start: false, configure: s => s.Configure(AddTwoCloudProviders));
        f.Start();
        await f.IdleAsync();

        f.Prober.Set("openai", ProbeResult.Failure(ProbeErrorCategory.Tls));
        await f.ProbeAsync();
        await f.ProbeAsync();
        await f.ProbeAsync();

        var e = Assert.Single(f.Events);
        Assert.Equal(ConnectivityState.Online, e.OldState);
        Assert.Equal(ConnectivityState.Degraded, e.NewState);
        Assert.False(e.Snapshot["openai"].Reachable);
        Assert.Equal(ProbeErrorCategory.Tls, e.Snapshot["openai"].ErrorCategory);
        Assert.True(e.Snapshot["azure"].Reachable);
    }

    [Fact]
    public async Task SingleProviderDown_IsOffline()
    {
        using var f = new MonitorFixture(start: false, configure: s => s.Configure(settings => AddCloud(settings)));
        f.Prober.Set("openai", ProbeResult.Failure(ProbeErrorCategory.Connect));
        f.Start();
        await f.IdleAsync();

        Assert.Equal(ConnectivityState.Offline, f.Monitor.State);
        Assert.Equal(["Online->Offline: openai unreachable: connection failed"], f.EventSummaries());
    }

    [Fact]
    public async Task Probes_RunPeriodicallyEverySixtySeconds()
    {
        using var f = new MonitorFixture(start: false, configure: s => s.Configure(settings => AddCloud(settings)));
        f.Start();
        await f.IdleAsync();
        Assert.Equal(1, f.Prober.CallCount);

        f.Time.Advance(TimeSpan.FromSeconds(59));
        await f.IdleAsync();
        Assert.Equal(1, f.Prober.CallCount);

        f.Time.Advance(TimeSpan.FromSeconds(1));
        await f.IdleAsync();
        Assert.Equal(2, f.Prober.CallCount);

        f.Time.Advance(Minute);
        await f.IdleAsync();
        Assert.Equal(3, f.Prober.CallCount);
    }

    [Fact]
    public async Task ProbesForDifferentProviders_RunConcurrently()
    {
        using var f = new MonitorFixture(start: false, configure: s => s.Configure(AddTwoCloudProviders));
        var arrived = 0;
        var both = new TaskCompletionSource();
        f.Prober.Behavior = async (_, _) =>
        {
            if (Interlocked.Increment(ref arrived) == 2)
            {
                both.SetResult();
            }

            // Only completes when the other provider's probe has started too.
            await both.Task;
            return FakeProber.Ok;
        };

        f.Start();
        await f.IdleAsync();

        Assert.Equal(2, f.Prober.CallCount);
    }

    [Fact]
    public async Task NetworkChanges_AreDebouncedForOneSecond()
    {
        using var f = new MonitorFixture(start: false, configure: s => s.Configure(settings => AddCloud(settings)));
        f.Start();
        await f.IdleAsync();
        var baseline = f.Prober.CallCount;

        f.Network.Raise();
        f.Time.Advance(TimeSpan.FromMilliseconds(600));
        f.Network.Raise();
        f.Network.Raise();
        f.Time.Advance(TimeSpan.FromMilliseconds(999));
        await f.IdleAsync();
        Assert.Equal(baseline, f.Prober.CallCount);

        f.Time.Advance(TimeSpan.FromMilliseconds(1));
        await f.IdleAsync();
        Assert.Equal(baseline + 1, f.Prober.CallCount);

        // The burst produced exactly one round, and the debounce timer is gone.
        f.Time.Advance(TimeSpan.FromSeconds(30));
        await f.IdleAsync();
        Assert.Equal(baseline + 1, f.Prober.CallCount);
    }

    [Fact]
    public async Task SettingsChange_ToTheProviders_TriggersAProbe()
    {
        using var f = new MonitorFixture(start: false, configure: s => s.Configure(settings => AddCloud(settings)));
        f.Start();
        await f.IdleAsync();
        Assert.Equal(1, f.Prober.CallCount);

        f.Settings.Update(s => AddCloud(s, "azure", ProviderKind.AzureOpenAi, "https://contoso.openai.azure.com"));
        await f.IdleAsync();
        Assert.Equal(1, f.Prober.CallsFor("azure"));
        Assert.Equal(2, f.Prober.CallsFor("openai"));
        Assert.Equal(2, f.Monitor.Snapshot.Count);

        f.Settings.Update(s => s.Providers.Single(p => p.Id == "azure").Endpoint = "https://fabrikam.openai.azure.com");
        await f.IdleAsync();
        Assert.Equal(2, f.Prober.CallsFor("azure"));
        Assert.Equal("https://fabrikam.openai.azure.com/", f.Prober.Calls.Last(c => c.ProviderId == "azure").Origin.AbsoluteUri);
    }

    [Fact]
    public async Task SettingsChange_ThatDoesNotTouchProviders_DoesNotProbe()
    {
        using var f = new MonitorFixture(start: false, configure: s => s.Configure(settings => AddCloud(settings)));
        f.Start();
        await f.IdleAsync();
        var baseline = f.Prober.CallCount;

        f.Settings.Update(s =>
        {
            s.Approvals.Mode = ApprovalMode.AutoSafe;
            s.Providers.Add(FakeSettingsProvider.Provider("ollama", ProviderKind.Ollama, "http://localhost:11434"));
        });
        await f.IdleAsync();

        Assert.Equal(baseline, f.Prober.CallCount);
    }

    [Fact]
    public async Task DisablingTheOnlyUnreachableProvider_ReturnsToOnline()
    {
        using var f = new MonitorFixture(start: false, configure: s => s.Configure(AddTwoCloudProviders));
        f.Prober.Set("azure", ProbeResult.Failure(ProbeErrorCategory.Timeout));
        f.Start();
        await f.IdleAsync();
        Assert.Equal(ConnectivityState.Degraded, f.Monitor.State);

        f.Settings.Update(s => s.Providers.Single(p => p.Id == "azure").Enabled = false);
        await f.IdleAsync();

        Assert.Equal(ConnectivityState.Online, f.Monitor.State);
        Assert.DoesNotContain("azure", f.Monitor.Snapshot.Keys);
        Assert.Equal("Degraded->Online: providers changed", f.EventSummaries().Last());
    }

    [Fact]
    public async Task SettingsThatCannotBeRead_KeepThePreviousConfiguration()
    {
        using var f = new MonitorFixture(start: false, configure: s => s.Configure(settings => AddCloud(settings)));
        f.Start();
        await f.IdleAsync();

        f.Settings.Throw = true;
        f.Settings.Update(_ => { });
        await f.IdleAsync();

        Assert.Equal(ConnectivityState.Online, f.Monitor.State);
        Assert.Single(f.Monitor.Snapshot);
    }

    [Fact]
    public async Task LocalOnlyFromUserSettings_StopsProbesAndTimers()
    {
        using var f = new MonitorFixture(start: false, configure: s => s.Configure(settings => AddCloud(settings)));
        f.Start();
        await f.IdleAsync();
        Assert.Equal(1, f.Time.ActiveTimerCount);
        var baseline = f.Prober.CallCount;

        f.Settings.Update(s => s.Privacy.LocalOnly = true);
        await f.IdleAsync();

        Assert.Equal(ConnectivityState.LocalOnly, f.Monitor.State);
        Assert.Equal("Online->LocalOnly: local-only mode turned on", f.EventSummaries().Last());
        Assert.Empty(f.Monitor.Snapshot);
        Assert.Equal(0, f.Time.ActiveTimerCount);

        f.Time.Advance(TimeSpan.FromMinutes(10));
        f.Network.Raise();
        f.Time.Advance(TimeSpan.FromSeconds(5));
        await f.ProbeAsync();
        await f.IdleAsync();
        Assert.Equal(baseline, f.Prober.CallCount);
        Assert.Equal(0, f.Time.ActiveTimerCount);

        Assert.False(f.Monitor.IsProviderReachable("openai"));
    }

    [Fact]
    public async Task LocalOnlyFromARepositoryPolicy_ForcesTheState()
    {
        using var f = new MonitorFixture(start: false, configure: s => s.Configure(settings => AddCloud(settings)));
        f.Start();
        await f.IdleAsync();

        f.Settings.SetPolicies(new RepositoryPolicy { LocalOnly = true });
        await f.IdleAsync();
        Assert.Equal(ConnectivityState.LocalOnly, f.Monitor.State);

        // A policy that says false never relaxes anything, and removing the policy leaves local-only mode.
        f.Settings.SetPolicies(RepositoryPolicy.Empty);
        await f.IdleAsync();
        Assert.Equal(ConnectivityState.Online, f.Monitor.State);
    }

    [Fact]
    public async Task StartingInLocalOnly_NeverProbes()
    {
        using var f = new MonitorFixture(start: false, configure: s =>
        {
            s.Configure(settings =>
            {
                AddCloud(settings);
                settings.Privacy.LocalOnly = true;
            });
        });
        f.Start();
        await f.IdleAsync();
        f.Time.Advance(TimeSpan.FromMinutes(5));
        await f.ProbeAsync();

        Assert.Equal(ConnectivityState.LocalOnly, f.Monitor.State);
        Assert.Equal(0, f.Prober.CallCount);
        Assert.Equal(0, f.Time.ActiveTimerCount);
        Assert.Equal(["Online->LocalOnly: local-only mode turned on"], f.EventSummaries());
    }

    [Fact]
    public async Task LeavingLocalOnly_ResumesProbingAndTheTimer()
    {
        using var f = new MonitorFixture(start: false, configure: s => s.Configure(settings =>
        {
            AddCloud(settings);
            settings.Privacy.LocalOnly = true;
        }));
        f.Start();
        await f.IdleAsync();
        Assert.Equal(0, f.Prober.CallCount);

        f.Prober.Set("openai", ProbeResult.Failure(ProbeErrorCategory.Timeout));
        f.Settings.Update(s => s.Privacy.LocalOnly = false);
        await f.IdleAsync();

        Assert.Equal(1, f.Prober.CallCount);
        Assert.Equal(1, f.Time.ActiveTimerCount);
        Assert.Equal(ConnectivityState.Offline, f.Monitor.State);

        f.Time.Advance(Minute);
        await f.IdleAsync();
        Assert.Equal(2, f.Prober.CallCount);
    }

    [Fact]
    public async Task EnablingLocalOnly_CancelsProbesInFlightAndDiscardsTheirResults()
    {
        using var f = new MonitorFixture(start: false, configure: s => s.Configure(settings => AddCloud(settings)));
        var cancelled = new TaskCompletionSource();
        f.Prober.Behavior = async (_, ct) =>
        {
            try
            {
                await Task.Delay(Timeout.Infinite, ct);
            }
            catch (OperationCanceledException)
            {
                cancelled.SetResult();
                throw;
            }

            return ProbeResult.Failure(ProbeErrorCategory.Other);
        };
        f.Start();
        await MonitorFixture.WaitUntilAsync(() => f.Prober.CallCount == 1, "probe started");

        f.Settings.Update(s => s.Privacy.LocalOnly = true);

        await cancelled.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await f.IdleAsync();
        Assert.Equal(ConnectivityState.LocalOnly, f.Monitor.State);
        Assert.Empty(f.Monitor.Snapshot);
        Assert.Equal(0, f.Time.ActiveTimerCount);
    }

    [Fact]
    public async Task ThreeFailureReports_MarkTheProviderUnreachable_AndASuccessClearsIt()
    {
        using var f = new MonitorFixture(start: false, configure: s => s.Configure(settings => AddCloud(settings)));
        f.Start();
        await f.IdleAsync();

        f.Monitor.ReportFailure("openai");
        f.Monitor.ReportFailure("openai");
        Assert.True(f.Monitor.IsProviderReachable("openai"));
        Assert.Equal(ConnectivityState.Online, f.Monitor.State);

        f.Monitor.ReportFailure("openai");
        Assert.False(f.Monitor.IsProviderReachable("openai"));
        Assert.Equal(ConnectivityState.Offline, f.Monitor.State);
        Assert.Equal(ProbeErrorCategory.CircuitBreaker, f.Monitor.Snapshot["openai"].ErrorCategory);
        Assert.Equal("Online->Offline: openai unreachable: circuit breaker open", f.EventSummaries().Last());

        f.Monitor.ReportFailure("openai");
        Assert.Single(f.Events);

        f.Monitor.ReportSuccess("openai");
        Assert.True(f.Monitor.IsProviderReachable("openai"));
        Assert.Equal(ConnectivityState.Online, f.Monitor.State);
        Assert.Equal("Offline->Online: openai reachable", f.EventSummaries().Last());
    }

    [Fact]
    public async Task ASuccessReportBetweenFailures_ResetsTheCount()
    {
        using var f = new MonitorFixture(start: false, configure: s => s.Configure(settings => AddCloud(settings)));
        f.Start();
        await f.IdleAsync();

        f.Monitor.ReportFailure("openai");
        f.Monitor.ReportFailure("openai");
        f.Monitor.ReportSuccess("openai");
        f.Monitor.ReportFailure("openai");
        f.Monitor.ReportFailure("openai");

        Assert.True(f.Monitor.IsProviderReachable("openai"));
        Assert.Empty(f.Events);
    }

    [Fact]
    public async Task BreakerMark_StaysUntilTheNextSuccessfulProbe()
    {
        using var f = new MonitorFixture(start: false, configure: s => s.Configure(settings => AddCloud(settings)));
        f.Start();
        await f.IdleAsync();
        for (var i = 0; i < 3; i++)
        {
            f.Monitor.ReportFailure("openai");
        }

        Assert.False(f.Monitor.IsProviderReachable("openai"));

        // A failing probe keeps it down; a successful one clears it.
        f.Prober.Set("openai", ProbeResult.Failure(ProbeErrorCategory.Timeout));
        await f.ProbeAsync();
        Assert.False(f.Monitor.IsProviderReachable("openai"));

        f.Prober.Set("openai", FakeProber.Ok);
        await f.ProbeAsync();
        Assert.True(f.Monitor.IsProviderReachable("openai"));
        Assert.Equal(ConnectivityState.Online, f.Monitor.State);

        // The count restarted, so two more failures are not enough.
        f.Monitor.ReportFailure("openai");
        f.Monitor.ReportFailure("openai");
        Assert.True(f.Monitor.IsProviderReachable("openai"));
    }

    [Fact]
    public async Task FailureReports_ForLocalOrUnknownProvidersOrInLocalOnly_AreIgnored()
    {
        using var f = new MonitorFixture(start: false, configure: s => s.Configure(settings =>
        {
            AddCloud(settings);
            settings.Providers.Add(FakeSettingsProvider.Provider("ollama", ProviderKind.Ollama, "http://localhost:11434"));
        }));
        f.Start();
        await f.IdleAsync();

        for (var i = 0; i < 5; i++)
        {
            f.Monitor.ReportFailure("ollama");
            f.Monitor.ReportFailure("nobody");
        }

        Assert.True(f.Monitor.IsProviderReachable("ollama"));
        Assert.Empty(f.Events);

        f.Settings.Update(s => s.Privacy.LocalOnly = true);
        for (var i = 0; i < 5; i++)
        {
            f.Monitor.ReportFailure("openai");
        }

        Assert.Equal(ConnectivityState.LocalOnly, f.Monitor.State);
        Assert.True(f.Monitor.IsProviderReachable("ollama"));
    }

    [Fact]
    public async Task ProberThatThrows_NeverEscapes_AndCountsAsUnreachable()
    {
        using var f = new MonitorFixture(start: false, configure: s => s.Configure(settings => AddCloud(settings)));
        f.Prober.Behavior = (_, _) => throw new InvalidOperationException("boom");
        f.Start();

        await f.ProbeAsync();
        await f.IdleAsync();

        Assert.Equal(ConnectivityState.Offline, f.Monitor.State);
        Assert.Equal(ProbeErrorCategory.Other, f.Monitor.Snapshot["openai"].ErrorCategory);

        // A prober that returns a faulted task is handled the same way.
        f.Prober.Behavior = (_, _) => Task.FromException<ProbeResult>(new IOException("pipe"));
        await f.ProbeAsync();
        Assert.Equal(ProbeErrorCategory.Other, f.Monitor.Snapshot["openai"].ErrorCategory);
    }

    [Fact]
    public async Task ProberThatHonoursItsToken_TimesOutAfterTwoSeconds()
    {
        using var f = new MonitorFixture(start: false, configure: s => s.Configure(settings => AddCloud(settings)));
        f.Prober.Behavior = async (_, ct) =>
        {
            await Task.Delay(Timeout.Infinite, ct);
            return FakeProber.Ok;
        };
        f.Start();
        await MonitorFixture.WaitUntilAsync(() => f.Time.ActiveTimerCount >= 3, "probe timers armed");

        f.Time.Advance(TimeSpan.FromSeconds(2));
        await f.IdleAsync();

        Assert.Equal(ConnectivityState.Offline, f.Monitor.State);
        Assert.Equal(ProbeErrorCategory.Timeout, f.Monitor.Snapshot["openai"].ErrorCategory);
        Assert.Equal("Online->Offline: openai unreachable: timeout", f.EventSummaries().Last());
    }

    [Fact]
    public async Task ProberThatIgnoresItsToken_IsCutOffByTheHardCap()
    {
        using var f = new MonitorFixture(start: false, configure: s => s.Configure(settings => AddCloud(settings)));
        var never = new TaskCompletionSource<ProbeResult>();
        f.Prober.Behavior = (_, _) => never.Task;
        f.Start();
        await MonitorFixture.WaitUntilAsync(() => f.Time.ActiveTimerCount >= 3, "probe timers armed");

        f.Time.Advance(TimeSpan.FromSeconds(3));
        await f.IdleAsync();

        Assert.Equal(ProbeErrorCategory.Timeout, f.Monitor.Snapshot["openai"].ErrorCategory);
    }

    [Fact]
    public async Task ProbeNowAsync_WaitsForTheRound_AndTheWaitCanBeCancelled()
    {
        using var f = new MonitorFixture(start: false, configure: s => s.Configure(settings => AddCloud(settings)));
        f.Start();
        await f.IdleAsync();
        var release = new TaskCompletionSource<ProbeResult>();
        f.Prober.Behavior = (_, _) => release.Task;

        var probe = f.Monitor.ProbeNowAsync(CancellationToken.None);
        await MonitorFixture.WaitUntilAsync(() => f.Prober.CallCount == 2, "second probe started");
        Assert.False(probe.IsCompleted);

        using var cts = new CancellationTokenSource();
        var cancellable = f.Monitor.ProbeNowAsync(cts.Token);
        await cts.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cancellable);

        release.SetResult(FakeProber.Ok);
        await probe.WaitAsync(TimeSpan.FromSeconds(10));
    }

    [Fact]
    public async Task Stop_CancelsTimersAndUnsubscribes()
    {
        using var f = new MonitorFixture(start: false, configure: s => s.Configure(settings => AddCloud(settings)));
        f.Start();
        await f.IdleAsync();
        Assert.Equal(1, f.Network.SubscriberCount);

        f.Monitor.Stop();
        Assert.Equal(0, f.Network.SubscriberCount);
        Assert.Equal(0, f.Time.ActiveTimerCount);

        var baseline = f.Prober.CallCount;
        f.Time.Advance(TimeSpan.FromMinutes(5));
        await f.ProbeAsync();
        Assert.Equal(baseline, f.Prober.CallCount);
    }

    [Fact]
    public async Task RunAsync_StartsTheMonitor_AndStopsItOnCancellation()
    {
        using var f = new MonitorFixture(start: false, configure: s => s.Configure(settings => AddCloud(settings)));
        using var cts = new CancellationTokenSource();

        var run = f.Monitor.RunAsync(cts.Token);
        await MonitorFixture.WaitUntilAsync(() => f.Prober.CallCount == 1, "initial probe");
        Assert.False(run.IsCompleted);

        await cts.CancelAsync();
        await run.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(0, f.Time.ActiveTimerCount);
    }

    [Fact]
    public async Task AHandlerThatThrows_DoesNotStopOtherHandlersOrTheMonitor()
    {
        using var f = new MonitorFixture(start: false, configure: s => s.Configure(settings => AddCloud(settings)));
        var second = new List<ConnectivityState>();
        f.Monitor.StateChanged += (_, _) => throw new InvalidOperationException("handler bug");
        f.Monitor.StateChanged += (_, e) => second.Add(e.NewState);
        f.Start();
        await f.IdleAsync();

        f.Prober.Set("openai", ProbeResult.Failure(ProbeErrorCategory.Timeout));
        await f.ProbeAsync();

        Assert.Equal([ConnectivityState.Offline], second);
    }
}

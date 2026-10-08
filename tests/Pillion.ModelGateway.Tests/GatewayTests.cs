#pragma warning disable CA1068 // The stream helpers take the token before the text so call sites read naturally.
using System.Diagnostics;
using System.Runtime.CompilerServices;
using Pillion.Contracts;
using Pillion.ModelGateway.Tests.TestSupport;

namespace Pillion.ModelGateway.Tests;

public class GatewayTests
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(10);

    [Fact]
    public async Task Chat_StreamsChunksThatNameTheModelAndProvider()
    {
        using var harness = new GatewayHarness();

        var chunks = await harness.Gateway.ChatAsync(GatewayHarness.ChatRequest("Hi"), TaskClass.Chat, CancellationToken.None).CollectAsync();

        Assert.Equal("Hello from fake", string.Concat(chunks.Select(c => c.Text)));
        Assert.All(chunks, c =>
        {
            Assert.Equal("cloud-balanced", c.ModelId);
            Assert.Equal("cloud", c.ProviderId);
            Assert.Equal("r1", c.RequestId);
        });
        var final = chunks[^1];
        Assert.True(final.IsFinal);
        Assert.Single(chunks, c => c.IsFinal);
        Assert.True(final.PromptTokens > 0);
        Assert.True(final.CompletionTokens > 0);
        Assert.Null(final.Error);
    }

    [Fact]
    public async Task Chat_AddsThePlainChatSystemPrompt_WhenTheRequestHasNone()
    {
        using var harness = new GatewayHarness();

        await harness.Gateway.ChatAsync(GatewayHarness.ChatRequest("Hi"), TaskClass.Chat, CancellationToken.None).CollectAsync();

        Assert.True(harness.Cloud!.ReceivedChats.TryDequeue(out var messages));
        Assert.Equal(Microsoft.Extensions.AI.ChatRole.System, messages[0].Role);
        Assert.Equal(PromptTemplates.Default.ChatSystemPrompt, messages[0].Text);
        Assert.Equal("Hi", messages[1].Text);
    }

    [Fact]
    public async Task Chat_PassesTheRoutedProfileToTheProvider()
    {
        using var harness = new GatewayHarness();
        harness.Settings.Privacy.LocalOnly = true;
        harness.Reload();

        await harness.Gateway.ChatAsync(GatewayHarness.ChatRequest("Hi"), TaskClass.Chat, CancellationToken.None).CollectAsync();

        Assert.True(harness.Local!.ReceivedOptions.TryDequeue(out var options));
        Assert.Equal("qwen2.5-coder:14b", options!.ModelId);
        Assert.Equal(Profile.LocalStrong, options.AdditionalProperties![GatewayOptionKeys.Profile]);
        Assert.Equal(0, harness.Cloud!.ChatCalls);
    }

    [Fact]
    public async Task Chat_ProviderDownBeforeFirstToken_FallsBackAndNamesTheModelThatAnswered()
    {
        using var harness = new GatewayHarness();
        harness.Cloud!.Chat = (_, _, _) => Throwing(FakeProvider.Unreachable());

        var chunks = await harness.Gateway.ChatAsync(GatewayHarness.ChatRequest("Hi"), TaskClass.Chat, CancellationToken.None).CollectAsync();

        // Balanced failed, then Fast on the same (down) provider, then the local fallback answered.
        Assert.All(chunks, c => Assert.Equal("local", c.ProviderId));
        Assert.Contains(harness.Progress.Reports, p => p.Kind == ModelProgressKind.FallingBack);
        var records = harness.OutboundLog.GetRecent();
        Assert.Equal([OutboundOutcome.Failed, OutboundOutcome.Failed, OutboundOutcome.Succeeded], records.Select(r => r.Outcome));
    }

    [Fact]
    public async Task Chat_FailureAfterText_EndsWithAnErrorOnTheFinalChunk()
    {
        using var harness = new GatewayHarness();
        harness.Cloud!.Chat = (_, _, ct) => ThenThrow(FakeProvider.HttpError(500), ct, "partial");

        var chunks = await harness.Gateway.ChatAsync(GatewayHarness.ChatRequest("Hi"), TaskClass.Chat, CancellationToken.None).CollectAsync();

        Assert.Equal("partial", chunks[0].Text);
        Assert.True(chunks[^1].IsFinal);
        Assert.Equal(PillionErrorCode.ProviderUnavailable, chunks[^1].Error!.Code);
        Assert.Equal(1, harness.Cloud.ChatCalls);
    }

    [Fact]
    public async Task Chat_NoRoute_ThrowsPillionRpcException()
    {
        using var harness = new GatewayHarness(withLocal: false);
        harness.Settings.Privacy.LocalOnly = true;
        harness.Reload();

        var ex = await Assert.ThrowsAsync<PillionRpcException>(() => harness.Gateway.ChatAsync(GatewayHarness.ChatRequest("Hi"), TaskClass.Chat, CancellationToken.None).CollectAsync());

        Assert.Equal(PillionErrorCode.PolicyDenied, ex.Error.Code);
        Assert.Equal(0, harness.Cloud!.ChatCalls);
    }

    // Circuit breaker.
    [Fact]
    public async Task CircuitBreaker_OpensAfterThreeFailures_ThenAllowsATrialAfter60s()
    {
        using var harness = new GatewayHarness(withLocal: false);
        harness.Cloud!.Chat = (_, _, _) => Throwing(FakeProvider.Unreachable());
        harness.Settings.Routing.Profiles[Contracts.Profile.Fast] = string.Empty; // one candidate only
        harness.Settings.Providers[0].Models = ["cloud-balanced"];
        harness.Reload();

        for (var i = 0; i < 3; i++)
        {
            var failure = await Assert.ThrowsAsync<PillionRpcException>(() => harness.Gateway.ChatAsync(GatewayHarness.ChatRequest("Hi"), TaskClass.Chat, CancellationToken.None).CollectAsync());
            Assert.Equal(PillionErrorCode.ProviderUnavailable, failure.Error.Code);
        }

        Assert.Equal(3, harness.Cloud.ChatCalls);
        Assert.Equal(CircuitState.Open, harness.Breakers.GetState("cloud"));
        Assert.Contains("opened:cloud", harness.Health.Events);

        // While open, the provider is not called at all.
        await Assert.ThrowsAsync<PillionRpcException>(() => harness.Gateway.ChatAsync(GatewayHarness.ChatRequest("Hi"), TaskClass.Chat, CancellationToken.None).CollectAsync());
        Assert.Equal(3, harness.Cloud.ChatCalls);

        // After 60 s one trial goes through; success closes the circuit.
        harness.Clock.Advance(TimeSpan.FromSeconds(61));
        Assert.Equal(CircuitState.HalfOpen, harness.Breakers.GetState("cloud"));
        harness.Cloud.Chat = (_, _, ct) => FakeProvider.Pieces(ct, "back");
        var chunks = await harness.Gateway.ChatAsync(GatewayHarness.ChatRequest("Hi"), TaskClass.Chat, CancellationToken.None).CollectAsync();

        Assert.Equal("back", chunks[0].Text);
        Assert.Equal(CircuitState.Closed, harness.Breakers.GetState("cloud"));
        Assert.Contains("closed:cloud", harness.Health.Events);
    }

    [Fact]
    public void CircuitBreaker_FailedTrial_ReopensAndOnlyOneTrialAtATime()
    {
        var clock = new ManualClock();
        var breakers = new CircuitBreakerRegistry(new ModelGatewayOptions(), clock);
        for (var i = 0; i < 3; i++)
        {
            Assert.True(breakers.TryAcquire("p"));
            breakers.RecordFailure("p");
        }

        Assert.False(breakers.TryAcquire("p"));
        clock.Advance(TimeSpan.FromSeconds(60));
        Assert.True(breakers.TryAcquire("p"));
        Assert.False(breakers.TryAcquire("p"));
        breakers.RecordFailure("p");
        Assert.Equal(CircuitState.Open, breakers.GetState("p"));
    }

    [Fact]
    public async Task CircuitBreaker_ClientErrorsAndCancellation_DoNotCount()
    {
        using var harness = new GatewayHarness(withLocal: false);
        harness.Cloud!.Chat = (_, _, _) => Throwing(FakeProvider.HttpError(400));

        for (var i = 0; i < 4; i++)
        {
            await Assert.ThrowsAsync<PillionRpcException>(() => harness.Gateway.ChatAsync(GatewayHarness.ChatRequest("Hi"), TaskClass.Chat, CancellationToken.None).CollectAsync());
        }

        Assert.Equal(CircuitState.Closed, harness.Breakers.GetState("cloud"));
    }

    // Concurrency.
    [Fact]
    public async Task Concurrency_ChatAllowsTwoInFlightPerProvider()
    {
        using var harness = new GatewayHarness(withLocal: false);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        harness.Cloud!.Chat = (_, _, ct) => Gated(release.Task, ct, "done");

        var requests = Enumerable.Range(0, 4)
            .Select(i => harness.Gateway.ChatAsync(GatewayHarness.ChatRequest("Hi", requestId: $"r{i}"), TaskClass.Chat, CancellationToken.None).CollectAsync())
            .ToList();

        await WaitUntil(() => harness.Cloud.InFlight == 2);
        await Task.Delay(100);
        Assert.Equal(2, harness.Cloud.InFlight);
        Assert.Equal(0, harness.Limiter.Available("cloud", ConcurrencyLane.Chat));

        release.SetResult();
        await Task.WhenAll(requests).WaitAsync(Wait);
        Assert.Equal(2, harness.Cloud.MaxInFlight);
        Assert.Equal(4, harness.Cloud.ChatCalls);
        Assert.Equal(2, harness.Limiter.Available("cloud", ConcurrencyLane.Chat));
    }

    [Fact]
    public async Task Concurrency_AutocompleteAllowsOneInFlight_AndDoesNotBlockChat()
    {
        using var harness = new GatewayHarness(withCloud: false);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        harness.Local!.Fim = (_, ct) => Gated(release.Task, ct, "x");

        var completions = Enumerable.Range(0, 3)
            .Select(i => harness.Gateway.CompleteAsync(GatewayHarness.CompletionRequest(prefix: $"p{i}", requestId: $"c{i}"), CancellationToken.None).CollectAsync())
            .ToList();
        await WaitUntil(() => harness.Local.InFlight == 1);
        await Task.Delay(100);
        Assert.Equal(1, harness.Local.FimCalls);

        // Chat has its own lane.
        var chat = await harness.Gateway.ChatAsync(GatewayHarness.ChatRequest("Hi"), TaskClass.Chat, CancellationToken.None).CollectAsync().WaitAsync(Wait);
        Assert.Equal("local", chat[0].ProviderId);

        release.SetResult();
        await Task.WhenAll(completions).WaitAsync(Wait);
        Assert.Equal(3, harness.Local.FimCalls);
    }

    [Fact]
    public void Concurrency_Defaults_MatchTheDesign()
    {
        var limiter = new ConcurrencyLimiter(new ModelGatewayOptions());
        Assert.Equal(1, limiter.LimitFor(ConcurrencyLane.Autocomplete));
        Assert.Equal(2, limiter.LimitFor(ConcurrencyLane.Chat));
        Assert.Equal(1, limiter.LimitFor(ConcurrencyLane.Agent));
        Assert.Equal(ConcurrencyLane.Chat, ConcurrencyLimiter.LaneFor(TaskClass.InlineEdit));
        Assert.Equal(ConcurrencyLane.Agent, ConcurrencyLimiter.LaneFor(TaskClass.Agent));
    }

    // Timeouts.
    [Fact]
    public async Task Timeout_CloudAutocomplete_IsDroppedSilentlyAfter3Seconds()
    {
        using var harness = new GatewayHarness(withLocal: false);
        Assert.Equal(TimeSpan.FromSeconds(3), harness.Options.CloudAutocompleteTimeout);
        harness.Settings.CloudSwitches.Autocomplete = true;
        harness.Reload();
        harness.ListAll();
        harness.Cloud!.Fim = (_, ct) => Delayed(TimeSpan.FromSeconds(30), ct, "too late");

        var stopwatch = Stopwatch.StartNew();
        var chunks = await harness.Gateway.CompleteAsync(GatewayHarness.CompletionRequest(), CancellationToken.None).CollectAsync().WaitAsync(Wait);
        stopwatch.Stop();

        Assert.Empty(chunks);
        Assert.InRange(stopwatch.Elapsed, TimeSpan.FromSeconds(2.8), TimeSpan.FromSeconds(6));
        var record = Assert.Single(harness.OutboundLog.GetRecent());
        Assert.Equal(OutboundOutcome.Dropped, record.Outcome);
        Assert.Equal(CircuitState.Closed, harness.Breakers.GetState("cloud"));
        Assert.Equal(1, harness.Cloud.FimCalls); // never retried
    }

    [Fact]
    public async Task Timeout_CloudChatFirstToken_FailsOverToLocal()
    {
        using var harness = new GatewayHarness(configure: o => o.CloudFirstTokenTimeout = TimeSpan.FromMilliseconds(200));
        harness.Cloud!.Chat = (_, _, ct) => Delayed(TimeSpan.FromSeconds(30), ct, "too late");

        var chunks = await harness.Gateway.ChatAsync(GatewayHarness.ChatRequest("Hi"), TaskClass.Chat, CancellationToken.None).CollectAsync().WaitAsync(Wait);

        Assert.All(chunks, c => Assert.Equal("local", c.ProviderId));
        Assert.Contains(harness.OutboundLog.GetRecent(), r => r.ProviderId == "cloud" && r.Outcome == OutboundOutcome.Failed);
        Assert.Equal(TimeSpan.FromSeconds(60), new ModelGatewayOptions().CloudFirstTokenTimeout);
    }

    [Fact]
    public async Task Timeout_SlowLocalModel_ReportsProgressInsteadOfFailing()
    {
        using var harness = new GatewayHarness(withCloud: false, configure: o =>
        {
            o.LocalProgressDelay = TimeSpan.FromMilliseconds(50);
            o.LocalFirstTokenBaseline = TimeSpan.FromMilliseconds(150);
        });
        harness.Local!.Chat = (_, _, ct) => Delayed(TimeSpan.FromMilliseconds(600), ct, "finally");

        var chunks = await harness.Gateway.ChatAsync(GatewayHarness.ChatRequest("Hi"), TaskClass.Chat, CancellationToken.None).CollectAsync().WaitAsync(Wait);

        Assert.Equal("finally", chunks[0].Text);
        Assert.Contains(harness.Progress.Reports, p => p.Kind == ModelProgressKind.WaitingForLocalModel);
        Assert.Contains(harness.Progress.Reports, p => p.Kind == ModelProgressKind.SlowerThanExpected);
        Assert.Equal(OutboundOutcome.Succeeded, harness.OutboundLog.GetRecent().Single().Outcome);
    }

    [Fact]
    public void Timeout_LocalBudget_ScalesWithPromptSize()
    {
        var options = new ModelGatewayOptions();
        Assert.Equal(TimeSpan.FromSeconds(120), options.LocalFirstTokenBudget(100));
        Assert.Equal(TimeSpan.FromSeconds(240), options.LocalFirstTokenBudget(8192));
    }

    // Retries.
    [Theory]
    [InlineData(429)]
    [InlineData(503)]
    public async Task Retry_TransientErrors_AreRetriedWithBackoff(int status)
    {
        using var harness = new GatewayHarness(withLocal: false);
        var calls = 0;
        harness.Cloud!.Chat = (_, _, ct) => Interlocked.Increment(ref calls) <= 2 ? Throwing(FakeProvider.HttpError(status)) : FakeProvider.Pieces(ct, "ok");

        var chunks = await harness.Gateway.ChatAsync(GatewayHarness.ChatRequest("Hi"), TaskClass.Chat, CancellationToken.None).CollectAsync();

        Assert.Equal("ok", chunks[0].Text);
        Assert.Equal(3, harness.Cloud.ChatCalls);
        Assert.Equal(2, harness.Progress.Reports.Count(p => p.Kind == ModelProgressKind.Retrying));
        Assert.Equal(OutboundOutcome.Succeeded, harness.OutboundLog.GetRecent().Single().Outcome);
    }

    [Fact]
    public async Task Retry_ClientErrors_AreNotRetried()
    {
        using var harness = new GatewayHarness(withLocal: false);
        harness.Settings.Providers[0].Models = ["cloud-balanced"];
        harness.Settings.Routing.Profiles[Contracts.Profile.Fast] = string.Empty;
        harness.Reload();
        harness.Cloud!.Chat = (_, _, _) => Throwing(FakeProvider.HttpError(400));

        await Assert.ThrowsAsync<PillionRpcException>(() => harness.Gateway.ChatAsync(GatewayHarness.ChatRequest("Hi"), TaskClass.Chat, CancellationToken.None).CollectAsync());

        Assert.Equal(1, harness.Cloud.ChatCalls);
    }

    [Fact]
    public async Task Retry_AutocompleteIsNeverRetried()
    {
        using var harness = new GatewayHarness(withCloud: false);
        harness.Local!.Fim = (_, _) => Throwing(FakeProvider.HttpError(503));

        await Assert.ThrowsAsync<PillionRpcException>(() => harness.Gateway.CompleteAsync(GatewayHarness.CompletionRequest(), CancellationToken.None).CollectAsync());

        // One call per candidate in the chain (LocalFast then LocalStrong), none repeated.
        Assert.Equal(2, harness.Local.FimCalls);
        Assert.DoesNotContain(harness.Progress.Reports, p => p.Kind == ModelProgressKind.Retrying);
    }

    // Autocomplete cache.
    [Fact]
    public async Task Cache_RepeatedRequest_IsServedWithoutCallingTheProvider()
    {
        using var harness = new GatewayHarness(withCloud: false);

        var first = await harness.Gateway.CompleteAsync(GatewayHarness.CompletionRequest(), CancellationToken.None).CollectAsync();
        var second = await harness.Gateway.CompleteAsync(GatewayHarness.CompletionRequest(requestId: "c2"), CancellationToken.None).CollectAsync();

        Assert.Equal("a + b", string.Concat(first.Select(c => c.Text)));
        var hit = Assert.Single(second);
        Assert.Equal("a + b", hit.Text);
        Assert.True(hit.IsFinal);
        Assert.Equal("c2", hit.RequestId);
        Assert.Equal("qwen2.5-coder:1.5b", hit.ModelId);
        Assert.Equal(1, harness.Local!.FimCalls);
        Assert.Single(harness.OutboundLog.GetRecent());

        await harness.Gateway.CompleteAsync(GatewayHarness.CompletionRequest(suffix: "; // changed"), CancellationToken.None).CollectAsync();
        Assert.Equal(2, harness.Local.FimCalls);
    }

    [Fact]
    public void Cache_KeyUsesPrefixTailAndSuffixHead()
    {
        var tail = new string('x', AutocompleteCache.PrefixTailLength);
        var head = new string('y', AutocompleteCache.SuffixHeadLength);

        var a = AutocompleteCache.CreateKey("p", "m", "AAA" + tail, head + "111");
        var b = AutocompleteCache.CreateKey("p", "m", "BBB" + tail, head + "222");
        var c = AutocompleteCache.CreateKey("p", "m", "AAA" + tail + "!", head);
        var d = AutocompleteCache.CreateKey("p", "other", "AAA" + tail, head);

        Assert.Equal(a, b);
        Assert.NotEqual(a, c);
        Assert.NotEqual(a, d);
    }

    [Fact]
    public void Cache_EvictsLeastRecentlyUsed_At256Entries()
    {
        var cache = new AutocompleteCache(new ModelGatewayOptions());
        Assert.Equal(256, cache.Capacity);
        var keys = Enumerable.Range(0, 257).Select(i => AutocompleteCache.CreateKey("p", "m", $"prefix {i}", string.Empty)).ToList();
        for (var i = 0; i < 256; i++)
        {
            cache.Set(keys[i], $"c{i}");
        }

        Assert.True(cache.TryGet(keys[0], out _)); // touch 0 so 1 is now the oldest
        cache.Set(keys[256], "c256");

        Assert.Equal(256, cache.Count);
        Assert.True(cache.TryGet(keys[0], out var zero));
        Assert.Equal("c0", zero);
        Assert.False(cache.TryGet(keys[1], out _));
        Assert.True(cache.TryGet(keys[256], out _));
    }

    // Redaction.
    [Fact]
    public async Task Redaction_CloudProviderSeesPlaceholders_AndTheReplyIsRestored()
    {
        using var harness = new GatewayHarness();
        var secret = TestSecrets.AwsAccessKey();
        harness.Cloud!.Chat = (messages, _, ct) => FakeProvider.Pieces(ct, "You sent ", messages[^1].Text!);

        var chunks = await harness.Gateway.ChatAsync(GatewayHarness.ChatRequest($"my key is {secret} ok"), TaskClass.Chat, CancellationToken.None).CollectAsync();

        Assert.True(harness.Cloud.ReceivedChats.TryDequeue(out var sent));
        Assert.DoesNotContain(secret, sent[^1].Text, StringComparison.Ordinal);
        Assert.Contains("<redacted:aws-access-key>", sent[^1].Text, StringComparison.Ordinal);
        Assert.Equal($"You sent my key is {secret} ok", string.Concat(chunks.Select(c => c.Text)));
        Assert.Equal(1, harness.OutboundLog.GetRecent().Single().RedactionCount);
    }

    [Fact]
    public async Task Redaction_PlaceholderSplitAcrossTwoChunks_IsRestored()
    {
        using var harness = new GatewayHarness();
        var secret = TestSecrets.AwsAccessKey();
        harness.Cloud!.Chat = (_, _, ct) => FakeProvider.Pieces(ct, "Use <redac", "ted:aws-access-key> here.");

        var chunks = await harness.Gateway.ChatAsync(GatewayHarness.ChatRequest($"key {secret}"), TaskClass.Chat, CancellationToken.None).CollectAsync();

        Assert.Equal($"Use {secret} here.", string.Concat(chunks.Select(c => c.Text)));
        Assert.DoesNotContain(chunks, c => c.Text.Contains("<redac", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Redaction_FimPrefixAndSuffixAreRedactedForCloud()
    {
        using var harness = new GatewayHarness(withLocal: false);
        harness.Settings.CloudSwitches.Autocomplete = true;
        harness.Reload();
        harness.ListAll();
        var secret = TestSecrets.AwsAccessKey();
        harness.Cloud!.Fim = (request, ct) => FakeProvider.Pieces(ct, "\"<redacted:aws", "-access-key>\";");

        var chunks = await harness.Gateway.CompleteAsync(GatewayHarness.CompletionRequest(prefix: $"var a = \"{secret}\"; var b = ", suffix: $" // {secret}"), CancellationToken.None).CollectAsync();

        Assert.True(harness.Cloud.ReceivedFims.TryDequeue(out var sent));
        Assert.DoesNotContain(secret, sent.Prefix, StringComparison.Ordinal);
        Assert.DoesNotContain(secret, sent.Suffix, StringComparison.Ordinal);
        Assert.Equal($"\"{secret}\";", string.Concat(chunks.Select(c => c.Text)));
        Assert.Equal(2, harness.OutboundLog.GetRecent().Single().RedactionCount);
    }

    [Fact]
    public async Task Redaction_LocalProviders_AreNotRedactedByDefault_ButCanBe()
    {
        using var harness = new GatewayHarness(withCloud: false);
        var secret = TestSecrets.AwsAccessKey();

        await harness.Gateway.ChatAsync(GatewayHarness.ChatRequest($"key {secret}"), TaskClass.Chat, CancellationToken.None).CollectAsync();
        Assert.True(harness.Local!.ReceivedChats.TryDequeue(out var plain));
        Assert.Contains(secret, plain[^1].Text, StringComparison.Ordinal);

        harness.Settings.Privacy.RedactForLocalProviders = true;
        harness.Reload();
        await harness.Gateway.ChatAsync(GatewayHarness.ChatRequest($"key {secret}"), TaskClass.Chat, CancellationToken.None).CollectAsync();
        Assert.True(harness.Local.ReceivedChats.TryDequeue(out var redacted));
        Assert.DoesNotContain(secret, redacted[^1].Text, StringComparison.Ordinal);
    }

    // Outbound log.
    [Fact]
    public async Task Outbound_RecordsEveryRequest_WithoutPromptText()
    {
        using var harness = new GatewayHarness();
        var secret = TestSecrets.AwsAccessKey();

        await harness.Gateway.ChatAsync(GatewayHarness.ChatRequest($"Explain this {secret} please"), TaskClass.Chat, CancellationToken.None).CollectAsync();

        var record = Assert.Single(harness.OutboundLog.GetRecent());
        Assert.Equal("cloud", record.ProviderId);
        Assert.Equal("cloud-balanced", record.ModelId);
        Assert.Equal(TaskClass.Chat, record.TaskClass);
        Assert.False(record.IsLocal);
        Assert.True(record.EstimatedPromptTokens > 0);
        Assert.True(record.CompletionTokens > 0);
        Assert.Equal(1, record.RedactionCount);
        Assert.Equal(OutboundOutcome.Succeeded, record.Outcome);
        Assert.DoesNotContain("Explain", record.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain(secret, record.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void Outbound_RingBufferKeepsTheLast500()
    {
        var log = new InMemoryOutboundLog(new ModelGatewayOptions());
        for (var i = 0; i < 510; i++)
        {
            log.Record(new OutboundRecord(DateTimeOffset.UtcNow, $"r{i}", "p", "m", TaskClass.Chat, true, 1, 1, 0, TimeSpan.Zero, OutboundOutcome.Succeeded));
        }

        var recent = log.GetRecent();
        Assert.Equal(500, recent.Count);
        Assert.Equal("r10", recent[0].RequestId);
        Assert.Equal("r509", recent[^1].RequestId);
    }

    [Fact]
    public async Task Cancel_StopsTheStream_AndIsRecorded()
    {
        using var harness = new GatewayHarness(withLocal: false);
        harness.Cloud!.Chat = (_, _, ct) => Delayed(TimeSpan.FromSeconds(30), ct, "never");
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => harness.Gateway.ChatAsync(GatewayHarness.ChatRequest("Hi"), TaskClass.Chat, cts.Token).CollectAsync());

        Assert.Equal(OutboundOutcome.Cancelled, harness.OutboundLog.GetRecent().Single().Outcome);
        Assert.Equal(CircuitState.Closed, harness.Breakers.GetState("cloud"));
        Assert.Equal(2, harness.Limiter.Available("cloud", ConcurrencyLane.Chat));
    }

    // Listing and Test connection.
    [Fact]
    public async Task ListModels_AggregatesProviders_AndMarksAvailability()
    {
        using var harness = new GatewayHarness();
        harness.Reachability.Unreachable.Add("cloud");

        var models = await harness.Gateway.ListModelsAsync(CancellationToken.None);

        var local = models.Single(m => m.Id == "local/qwen2.5-coder:1.5b");
        Assert.True(local.IsAvailable);
        Assert.True(local.IsLocal);
        Assert.Equal("qwen2.5-coder:1.5b", local.ModelId);
        Assert.True(local.Capabilities.HasFlag(ModelCapabilities.Fim));
        Assert.False(models.Single(m => m.Id == "cloud/cloud-balanced").IsAvailable);
        Assert.True(harness.Inventory.TryGet("local", out _));
    }

    [Fact]
    public async Task ListModels_LocalOnly_DoesNotContactCloudProviders()
    {
        using var harness = new GatewayHarness();
        harness.Settings.Privacy.LocalOnly = true;
        harness.Reload();

        var models = await harness.Gateway.ListModelsAsync(CancellationToken.None);

        Assert.Equal(0, harness.Cloud!.ListCalls);
        Assert.All(models.Where(m => m.ProviderId == "cloud"), m => Assert.False(m.IsAvailable));
        Assert.Contains(models, m => m.ProviderId == "local" && m.IsAvailable);
    }

    [Fact]
    public async Task ListModels_ProviderDown_ReportsConfiguredModelsAsUnavailable()
    {
        using var harness = new GatewayHarness();
        harness.Local!.ListFailure = FakeProvider.Unreachable();

        var models = await harness.Gateway.ListModelsAsync(CancellationToken.None);

        Assert.Contains(models, m => m.Id == "local/qwen2.5-coder:1.5b" && !m.IsAvailable);
    }

    [Fact]
    public async Task TestProvider_ProbesTheNamedProvider_ButNotCloudInLocalOnlyMode()
    {
        using var harness = new GatewayHarness();
        var tester = new GatewayProviderTester(harness.Gateway);

        var ok = await tester.TestAsync("cloud", CancellationToken.None);
        Assert.True(ok.IsSuccess);
        Assert.Equal(1, harness.Cloud!.ProbeCalls);

        harness.Settings.Privacy.LocalOnly = true;
        harness.Reload();
        var blocked = await tester.TestAsync("cloud", CancellationToken.None);
        Assert.False(blocked.IsSuccess);
        Assert.Contains("Local-only", blocked.Message, StringComparison.Ordinal);
        Assert.Equal(1, harness.Cloud.ProbeCalls);

        var unknown = await tester.TestAsync("nope", CancellationToken.None);
        Assert.False(unknown.IsSuccess);
    }

    [Fact]
    public async Task Registry_ProviderNeedingAKeyWithoutOne_IsHiddenFromRouting()
    {
        using var harness = new GatewayHarness();
        var keyed = new FakeProviderFactory(Common.Settings.ProviderKind.Anthropic, requiresKey: true);
        keyed.Providers["claude"] = new FakeProvider("claude", isLocal: false, Common.Settings.ProviderKind.Anthropic);
        using var registry = new ProviderRegistry(harness.Source, harness.Secrets, [harness.LocalFactory, harness.CloudFactory, keyed], ModelCatalog.Default);
        harness.Settings.Providers.Add(new Common.Settings.ProviderSettings { Id = "claude", Kind = Common.Settings.ProviderKind.Anthropic });
        harness.Settings.Providers.Add(new Common.Settings.ProviderSettings { Id = "gem", Kind = Common.Settings.ProviderKind.Gemini });
        harness.Reload();

        var snapshot = registry.GetSnapshot();
        Assert.Null(snapshot.Find("claude"));
        Assert.Contains(snapshot.Hidden, h => h.Settings.Id == "claude" && h.Reason.Contains("API key", StringComparison.Ordinal));
        Assert.Contains(snapshot.Hidden, h => h.Settings.Id == "gem" && h.Reason.Contains("not supported", StringComparison.Ordinal));

        harness.Secrets.Set("claude", "test-key-value");
        harness.Reload();
        Assert.NotNull(registry.GetSnapshot().Find("claude"));
    }

    private static async IAsyncEnumerable<string> Throwing(Exception exception)
    {
        await Task.Yield();
        throw exception;
#pragma warning disable CS0162 // Unreachable code: the iterator needs a yield to be one.
        yield break;
#pragma warning restore CS0162
    }

    private static async IAsyncEnumerable<string> ThenThrow(Exception exception, [EnumeratorCancellation] CancellationToken ct, string first)
    {
        await Task.Yield();
        ct.ThrowIfCancellationRequested();
        yield return first;
        throw exception;
    }

    private static async IAsyncEnumerable<string> Delayed(TimeSpan delay, [EnumeratorCancellation] CancellationToken ct, string text)
    {
        await Task.Delay(delay, ct);
        yield return text;
    }

    private static async IAsyncEnumerable<string> Gated(Task gate, [EnumeratorCancellation] CancellationToken ct, string text)
    {
        await gate.WaitAsync(ct);
        yield return text;
    }

    private static async Task WaitUntil(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow + Wait;
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < deadline, "Timed out waiting for the condition.");
            await Task.Delay(10);
        }
    }
}

internal static class TestSecrets
{
    /// <summary>A fabricated AWS-access-key-shaped value, assembled at run time so no secret-shaped literal is in the repository.</summary>
    public static string AwsAccessKey() => "AKIA" + "Q7RZ3MBX" + "5TLW2KNV";
}

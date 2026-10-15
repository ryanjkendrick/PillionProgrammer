using Pillion.Contracts;
using Pillion.ModelGateway.Tests.TestSupport;

namespace Pillion.ModelGateway.Tests.Embeddings;

/// <summary>The embedding path of the gateway: routing, batching, ordering, partial failures, the Outbound log and the privacy rules.</summary>
public class EmbeddingGatewayTests
{
    private const string Qwen = "qwen3-embedding:0.6b";
    private const string Nomic = "nomic-embed-text";
    private const string CloudModel = "text-embedding-3-small";

    private static GatewayHarness Harness(Action<GatewayHarness>? configure = null, Action<ModelGatewayOptions>? options = null)
    {
        var harness = new GatewayHarness(configure: options);
        harness.Local!.Models.Add(harness.Local.Model(Qwen, chat: false, embeddings: true, context: 32768));
        harness.Local.Models.Add(harness.Local.Model(Nomic, chat: false, embeddings: true, context: 8192));
        harness.Cloud!.Models.Add(harness.Cloud.Model(CloudModel, chat: false, embeddings: true, context: 8191));
        harness.Settings.Providers.Single(p => p.Id == "cloud").Models.Add(CloudModel);
        configure?.Invoke(harness);
        harness.ListAll();
        harness.Reload();
        return harness;
    }

    private static string[] Texts(int count, string prefix = "chunk") => [.. Enumerable.Range(0, count).Select(i => $"{prefix} {i}")];

    private static EmbeddingRequest Request(IReadOnlyList<string> inputs, string id = "emb", string? model = null, Profile profile = Profile.Auto) =>
        new() { RequestId = id, Inputs = inputs, ModelId = model, Profile = profile, IncludeFloat32 = true };

    private static float[] Unit(string text)
    {
        var vector = FakeProvider.DefaultVector(text);
        EmbeddingVectors.Normalize(vector);
        return vector;
    }

    // Batching and order.
    [Fact]
    public async Task Embed_SendsBatchesOf32_AndReturnsTheVectorsInInputOrder()
    {
        using var harness = Harness();
        var inputs = Texts(70);

        var result = await harness.Gateway.EmbedAsync(Request(inputs), CancellationToken.None);

        Assert.Equal([32, 32, 6], harness.Local!.ReceivedEmbeds.Select(r => r.Texts.Count));
        Assert.Equal(3, result.Batches);
        Assert.True(result.IsComplete);
        Assert.Equal(70, result.Vectors.Count);
        for (var i = 0; i < inputs.Length; i++)
        {
            // Qwen3 documents take no prefix, so the vector of input i is the vector of that text.
            Assert.Equal(Unit(inputs[i]), result.Vectors[i]!.Normalized);
        }

        Assert.Equal(inputs, harness.Local.ReceivedEmbeds.SelectMany(r => r.Texts));
    }

    [Theory]
    [InlineData(0, 32, 3)]
    [InlineData(10, 32, 7)]
    [InlineData(70, 32, 1)]
    public async Task Embed_TheBatchSizeOfTheRequest_OverridesTheOptions(int requestSize, int optionSize, int expectedBatches)
    {
        using var harness = Harness(options: o => o.EmbeddingBatchSize = optionSize);

        var result = await harness.Gateway.EmbedAsync(Request(Texts(70)) with { BatchSize = requestSize }, CancellationToken.None);

        Assert.Equal(expectedBatches, result.Batches);
        Assert.Equal(expectedBatches, harness.Local!.EmbedCalls);
    }

    [Fact]
    public async Task Embed_TheOptionsBatchSize_IsConfigurable()
    {
        using var harness = Harness(options: o => o.EmbeddingBatchSize = 16);

        var result = await harness.Gateway.EmbedAsync(Request(Texts(40)), CancellationToken.None);

        Assert.Equal([16, 16, 8], harness.Local!.ReceivedEmbeds.Select(r => r.Texts.Count));
        Assert.Equal(3, result.Batches);
    }

    [Fact]
    public async Task Embed_ReturnsUnitLengthInt8Vectors_WithTheirScales_AndFloat32OnlyWhenAsked()
    {
        using var harness = Harness();

        var withFloat = await harness.Gateway.EmbedAsync(Request(["alpha", "beta"]), CancellationToken.None);
        var withoutFloat = await harness.Gateway.EmbedAsync(Request(["alpha", "beta"]) with { IncludeFloat32 = false }, CancellationToken.None);

        foreach (var vector in withFloat.Vectors)
        {
            Assert.NotNull(vector);
            Assert.Equal(1.0, Math.Sqrt(vector.Normalized!.Sum(x => (double)x * x)), 5);
            Assert.Equal(vector.Normalized!.Max(Math.Abs) / 127f, vector.Scale, 7);
            Assert.Equal(4, vector.Dimensions);
        }

        Assert.All(withoutFloat.Vectors, v => Assert.Null(v!.Normalized));
        Assert.Equal(withFloat.Vectors[0]!.Quantized, withoutFloat.Vectors[0]!.Quantized);
    }

    [Fact]
    public async Task Embed_WithNoInputs_ReturnsNothing_AndAsksNoProvider()
    {
        using var harness = Harness();

        var result = await harness.Gateway.EmbedAsync(Request([]), CancellationToken.None);

        Assert.Empty(result.Vectors);
        Assert.Null(result.Model);
        Assert.Equal(0, result.Batches);
        Assert.Equal(0, harness.Local!.EmbedCalls);
        Assert.Empty(harness.OutboundLog.GetRecent());
    }

    [Fact]
    public async Task Embed_AnEmptyInput_IsRejectedBeforeAnythingIsSent()
    {
        using var harness = Harness();

        var ex = await Assert.ThrowsAsync<ArgumentException>(() => harness.Gateway.EmbedAsync(Request(["fine", string.Empty]), CancellationToken.None));

        Assert.Contains("Input 1", ex.Message, StringComparison.Ordinal);
        Assert.Equal(0, harness.Local!.EmbedCalls);
    }

    // The model.
    [Fact]
    public async Task Embed_ReportsTheModelThatAnsweredWithDimensionsFromTheResponse_AndTheCatalogsTokenizer()
    {
        using var harness = Harness();

        var result = await harness.Gateway.EmbedAsync(Request(["x"]), CancellationToken.None);

        Assert.Equal(new EmbeddingModelInfo("local", Qwen, Dimensions: 4, TokenizerFamily: "qwen3-bpe", MaxInputTokens: 32768), result.Model);
        Assert.Equal("local/" + Qwen, result.Model!.QualifiedId);
    }

    [Fact]
    public async Task Embed_AModelTheCatalogDoesNotKnow_HasAnUnknownTokenizerAndTheContextWindowAsItsLimit()
    {
        using var harness = Harness(h =>
        {
            h.Local!.Models.RemoveAll(m => m.ModelId is Qwen or Nomic);
            h.Local.Models.Add(h.Local.Model("acme-vectors", chat: false, embeddings: true, context: 1234));
        });

        var result = await harness.Gateway.EmbedAsync(Request(["x"]), CancellationToken.None);

        Assert.Equal(new EmbeddingModelInfo("local", "acme-vectors", 4, EmbeddingModelInfo.UnknownTokenizer, 1234), result.Model);
    }

    [Fact]
    public async Task Embed_QueriesAndDocuments_GetTheCatalogsPrefixesForTheModel()
    {
        using var harness = Harness();

        await harness.Gateway.EmbedAsync(Request(["find it"], model: "local/" + Nomic) with { Purpose = EmbeddingPurpose.Query }, CancellationToken.None);
        await harness.Gateway.EmbedAsync(Request(["a chunk"], model: "local/" + Nomic) with { Purpose = EmbeddingPurpose.Document }, CancellationToken.None);
        await harness.Gateway.EmbedAsync(Request(["find it"], model: "local/" + Qwen) with { Purpose = EmbeddingPurpose.Query }, CancellationToken.None);
        await harness.Gateway.EmbedAsync(Request(["a chunk"], model: "local/" + Qwen) with { Purpose = EmbeddingPurpose.Document }, CancellationToken.None);

        var sent = harness.Local!.ReceivedEmbeds.Select(r => r.Texts.Single()).ToList();
        Assert.Equal("search_query: find it", sent[0]);
        Assert.Equal("search_document: a chunk", sent[1]);
        Assert.StartsWith("Instruct: ", sent[2], StringComparison.Ordinal);
        Assert.EndsWith("Query: find it", sent[2], StringComparison.Ordinal);
        Assert.Equal("a chunk", sent[3]);
        Assert.Equal(
            [EmbeddingPurpose.Query, EmbeddingPurpose.Document, EmbeddingPurpose.Query, EmbeddingPurpose.Document],
            harness.Local.ReceivedEmbeds.Select(r => (EmbeddingPurpose)r.Options!.AdditionalProperties![GatewayOptionKeys.EmbeddingPurpose]!));
    }

    [Fact]
    public async Task Embed_Dimensions_AreSentOnlyToModelsThatCanBeShortened()
    {
        using var harness = Harness(h => h.Settings.CloudSwitches.Embedding = true);

        await harness.Gateway.EmbedAsync(Request(["x"], model: "local/" + Nomic) with { Dimensions = 256 }, CancellationToken.None);
        await harness.Gateway.EmbedAsync(Request(["x"], model: "local/" + Qwen) with { Dimensions = 256 }, CancellationToken.None);
        await harness.Gateway.EmbedAsync(Request(["x"], model: "cloud/" + CloudModel) with { Dimensions = 256 }, CancellationToken.None);

        Assert.Null(harness.Local!.ReceivedEmbeds.ElementAt(0).Options!.Dimensions);
        Assert.Equal(256, harness.Local.ReceivedEmbeds.ElementAt(1).Options!.Dimensions);
        Assert.Equal(256, harness.Cloud!.ReceivedEmbeds.Single().Options!.Dimensions);
    }

    // The Outbound log.
    [Fact]
    public async Task Embed_RecordsOneOutboundEntryPerBatch()
    {
        using var harness = Harness();
        var inputs = Texts(70);

        await harness.Gateway.EmbedAsync(Request(inputs, id: "idx-1"), CancellationToken.None);

        var records = harness.OutboundLog.GetRecent();
        Assert.Equal(["idx-1/b1", "idx-1/b2", "idx-1/b3"], records.Select(r => r.RequestId));
        Assert.All(records, r =>
        {
            Assert.Equal(TaskClass.Embedding, r.TaskClass);
            Assert.True(r.IsLocal);
            Assert.Equal("local", r.ProviderId);
            Assert.Equal(Qwen, r.ModelId);
            Assert.Equal(OutboundOutcome.Succeeded, r.Outcome);
            Assert.Equal(0, r.CompletionTokens);
            Assert.Equal(0, r.RedactionCount);
            Assert.True(r.Duration >= TimeSpan.Zero);
            Assert.Null(r.ErrorCode);
        });
        Assert.Equal(inputs.Take(32).Sum(t => TokenEstimator.Estimate(t)), records[0].EstimatedPromptTokens);
        Assert.Equal(inputs.Skip(64).Sum(t => TokenEstimator.Estimate(t)), records[2].EstimatedPromptTokens);
    }

    [Fact]
    public async Task Embed_ACloudBatch_IsRecordedAsCloud()
    {
        using var harness = Harness(h => h.Settings.CloudSwitches.Embedding = true);

        await harness.Gateway.EmbedAsync(Request(["x"], model: "cloud/" + CloudModel), CancellationToken.None);

        var record = Assert.Single(harness.OutboundLog.GetRecent());
        Assert.False(record.IsLocal);
        Assert.Equal("cloud", record.ProviderId);
        Assert.Equal(TaskClass.Embedding, record.TaskClass);
    }

    // Failures.
    [Fact]
    public async Task Embed_AFailedBatch_SurfacesAsAFailure_AndTheOtherBatchesStillReturn()
    {
        using var harness = Harness(options: o => o.MaxRetries = 0);
        var call = 0;
        harness.Local!.Embed = (texts, _, _) =>
        {
            return ++call == 2
                ? throw new ProviderException(PillionErrorCode.ContextOverflow, "A text is too long.", isProviderFault: false)
                : Task.FromResult(texts.Select(t => FakeProvider.DefaultVector(t)).ToArray());
        };

        var result = await harness.Gateway.EmbedAsync(Request(Texts(70)), CancellationToken.None);

        Assert.False(result.IsComplete);
        var failure = Assert.Single(result.Failures);
        Assert.Equal(32, failure.Start);
        Assert.Equal(32, failure.Count);
        Assert.False(failure.Skipped);
        Assert.Equal(PillionErrorCode.ContextOverflow, failure.Error.Code);
        Assert.All(result.Vectors.Take(32), v => Assert.NotNull(v));
        Assert.All(result.Vectors.Skip(32).Take(32), v => Assert.Null(v));
        Assert.All(result.Vectors.Skip(64), v => Assert.NotNull(v));
        Assert.NotNull(result.Model);

        var records = harness.OutboundLog.GetRecent();
        Assert.Equal([OutboundOutcome.Succeeded, OutboundOutcome.Failed, OutboundOutcome.Succeeded], records.Select(r => r.Outcome));
        Assert.Equal(PillionErrorCode.ContextOverflow, records[1].ErrorCode);
    }

    [Fact]
    public async Task Embed_AfterThreeBatchesInARowFail_TheRestIsSkipped()
    {
        using var harness = Harness(options: o => o.MaxRetries = 0);
        var call = 0;
        harness.Local!.Embed = (texts, _, _) =>
        {
            return ++call > 1
                ? throw FakeProvider.Unreachable()
                : Task.FromResult(texts.Select(t => FakeProvider.DefaultVector(t)).ToArray());
        };

        var result = await harness.Gateway.EmbedAsync(Request(Texts(32 * 10)), CancellationToken.None);

        Assert.Equal(4, harness.Local.EmbedCalls); // One success, then three failures; the other six batches are never sent.
        Assert.Equal(4, result.Batches);
        Assert.Equal(4, result.Failures.Count);
        Assert.All(result.Failures.Take(3), f => Assert.False(f.Skipped));
        var skipped = result.Failures[3];
        Assert.True(skipped.Skipped);
        Assert.Equal(32 * 4, skipped.Start);
        Assert.Equal(32 * 6, skipped.Count);
        Assert.Equal(PillionErrorCode.ProviderUnavailable, skipped.Error.Code);
    }

    [Fact]
    public async Task Embed_TheSkipRule_CanBeSwitchedOff()
    {
        using var harness = Harness(options: o =>
        {
            o.MaxRetries = 0;
            o.EmbeddingMaxConsecutiveFailures = 0;
            o.CircuitFailureThreshold = 1000;
        });
        harness.Local!.Embed = (texts, _, _) => texts.Count == 32 ? Task.FromException<float[][]>(FakeProvider.Unreachable()) : Task.FromResult(texts.Select(t => FakeProvider.DefaultVector(t)).ToArray());

        var result = await harness.Gateway.EmbedAsync(Request(Texts(32 * 5 + 1)), CancellationToken.None);

        Assert.Equal(6, harness.Local.EmbedCalls);
        Assert.Equal(5, result.Failures.Count);
        Assert.DoesNotContain(result.Failures, f => f.Skipped);
        Assert.NotNull(result.Vectors[^1]);
    }

    [Fact]
    public async Task Embed_WhenEveryBatchFails_TheCallFailsWithTheLastError()
    {
        using var harness = Harness(options: o => o.MaxRetries = 0);
        harness.Local!.Embed = (_, _, _) => Task.FromException<float[][]>(FakeProvider.Unreachable());

        var ex = await Assert.ThrowsAsync<PillionRpcException>(() => harness.Gateway.EmbedAsync(Request(Texts(5)), CancellationToken.None));

        Assert.Equal(PillionErrorCode.ProviderUnavailable, ex.Error.Code);
        Assert.Equal(OutboundOutcome.Failed, Assert.Single(harness.OutboundLog.GetRecent()).Outcome);
    }

    [Fact]
    public async Task Embed_ARetryableFailure_IsRetriedInsideTheBatch_AndRecordedOnce()
    {
        using var harness = Harness();
        var call = 0;
        harness.Local!.Embed = (texts, _, _) =>
        {
            return ++call < 3
                ? throw FakeProvider.HttpError(503)
                : Task.FromResult(texts.Select(t => FakeProvider.DefaultVector(t)).ToArray());
        };

        var result = await harness.Gateway.EmbedAsync(Request(Texts(3)), CancellationToken.None);

        Assert.True(result.IsComplete);
        Assert.Equal(3, harness.Local.EmbedCalls);
        Assert.Equal(1, result.Batches);
        Assert.Equal(OutboundOutcome.Succeeded, Assert.Single(harness.OutboundLog.GetRecent()).Outcome);
    }

    [Theory]
    [InlineData("too few", "returned 7 embeddings for 8 texts")]
    [InlineData("not a number", "not a number")]
    [InlineData("different sizes", "embeddings of different sizes")]
    [InlineData("empty vectors", "embeddings of different sizes")]
    [InlineData("another size than before", "earlier batches of this request had 4")]
    public async Task Embed_ABadAnswerToALaterBatch_FailsThatBatchOnly(string kind, string expectedMessage)
    {
        using var harness = Harness(options: o => o.MaxRetries = 0);
        var call = 0;
        harness.Local!.Embed = (texts, _, _) =>
        {
            if (++call == 1)
            {
                return Task.FromResult(texts.Select(t => FakeProvider.DefaultVector(t)).ToArray());
            }

            float[][] bad = kind switch
            {
                "too few" => [.. texts.Skip(1).Select(t => FakeProvider.DefaultVector(t))],
                "not a number" => [.. texts.Select(_ => new[] { 1f, float.NaN, 2f, 3f })],
                "different sizes" => [.. texts.Select((t, i) => FakeProvider.DefaultVector(t, i == 0 ? 4 : 5))],
                "empty vectors" => [.. texts.Select(_ => Array.Empty<float>())],
                _ => [.. texts.Select(t => FakeProvider.DefaultVector(t, 8))],
            };
            return Task.FromResult(bad);
        };

        var result = await harness.Gateway.EmbedAsync(Request(Texts(40), id: "bad"), CancellationToken.None);

        var failure = Assert.Single(result.Failures);
        Assert.Equal(32, failure.Start);
        Assert.Equal(8, failure.Count);
        Assert.Contains(expectedMessage, failure.Error.Message, StringComparison.Ordinal);
        Assert.All(result.Vectors.Take(32), v => Assert.Equal(4, v!.Dimensions));
        Assert.All(result.Vectors.Skip(32), v => Assert.Null(v));
        Assert.Equal(4, result.Model!.Dimensions);
        Assert.Equal(OutboundOutcome.Failed, harness.OutboundLog.GetRecent()[1].Outcome);
    }
    [Fact]
    public async Task Embed_ABadFirstAnswer_WithNothingElseSucceeding_FailsTheCall()
    {
        using var harness = Harness(options: o => o.MaxRetries = 0);
        harness.Local!.Embed = (texts, _, _) => Task.FromResult(texts.Skip(1).Select(t => FakeProvider.DefaultVector(t)).ToArray());

        var ex = await Assert.ThrowsAsync<PillionRpcException>(() => harness.Gateway.EmbedAsync(Request(Texts(3)), CancellationToken.None));

        Assert.Contains("returned 2 embeddings for 3 texts", ex.Message, StringComparison.Ordinal);
    }

    // Fallbacks and the chosen model.
    [Fact]
    public async Task Embed_BeforeAnyBatchSucceeds_FallsBackToTheNextCandidate_AndReportsIt()
    {
        using var harness = Harness(h => h.Settings.CloudSwitches.Embedding = true, o => o.MaxRetries = 0);
        harness.Cloud!.Embed = (_, _, _) => Task.FromException<float[][]>(FakeProvider.Unreachable());

        var result = await harness.Gateway.EmbedAsync(Request(Texts(3), profile: Profile.Fast), CancellationToken.None);

        Assert.True(result.IsComplete);
        Assert.Equal("local", result.Model!.ProviderId);
        Assert.Equal(1, harness.Cloud.EmbedCalls);
        Assert.Equal(1, harness.Local!.EmbedCalls);
        Assert.Equal([OutboundOutcome.Failed, OutboundOutcome.Succeeded], harness.OutboundLog.GetRecent().Select(r => r.Outcome));
        Assert.Contains(harness.Progress.Reports, p => p.Kind == ModelProgressKind.FallingBack && p.ProviderId == "local");
    }

    [Fact]
    public async Task Embed_OnceABatchHasSucceeded_TheRestOfTheCallStaysOnThatModel()
    {
        using var harness = Harness(h => h.Settings.CloudSwitches.Embedding = true, o => o.MaxRetries = 0);
        var call = 0;
        harness.Cloud!.Embed = (texts, _, _) => ++call == 1
            ? Task.FromResult(texts.Select(t => FakeProvider.DefaultVector(t)).ToArray())
            : Task.FromException<float[][]>(FakeProvider.Unreachable());

        var result = await harness.Gateway.EmbedAsync(Request(Texts(64), profile: Profile.Fast) with { BatchSize = 32 }, CancellationToken.None);

        // Vectors of two models are not comparable: the failed second batch is not sent to the local model.
        Assert.Equal(0, harness.Local!.EmbedCalls);
        Assert.Equal("cloud", result.Model!.ProviderId);
        var failure = Assert.Single(result.Failures);
        Assert.Equal(32, failure.Start);
        Assert.All(result.Vectors.Skip(32), v => Assert.Null(v));
    }

    [Fact]
    public async Task Embed_AProviderThatCannotEmbed_IsSkipped()
    {
        using var harness = Harness(h => h.Settings.CloudSwitches.Embedding = true);
        harness.Cloud!.SupportsEmbeddings = false;

        var result = await harness.Gateway.EmbedAsync(Request(["x"], profile: Profile.Fast), CancellationToken.None);

        Assert.Equal("local", result.Model!.ProviderId);
        Assert.Equal(0, harness.Cloud.EmbedCalls);
    }

    [Fact]
    public async Task Embed_WhenNoCandidateCanEmbed_ExplainsWhy()
    {
        using var harness = Harness();
        harness.Local!.SupportsEmbeddings = false;

        var ex = await Assert.ThrowsAsync<PillionRpcException>(() => harness.Gateway.EmbedAsync(Request(["x"]), CancellationToken.None));

        Assert.Equal(PillionErrorCode.ModelNotInstalled, ex.Error.Code);
        Assert.Contains("does not provide embeddings", ex.Message, StringComparison.Ordinal);
    }

    // Privacy.
    [Fact]
    public async Task Embed_WithTheSwitchOff_NothingIsSentToACloudProvider()
    {
        using var harness = Harness();

        var viaProfile = await harness.Gateway.EmbedAsync(Request(Texts(40), profile: Profile.Frontier), CancellationToken.None);
        var ex = await Assert.ThrowsAsync<PillionRpcException>(() => harness.Gateway.EmbedAsync(Request(["x"], model: "cloud/" + CloudModel), CancellationToken.None));

        Assert.Equal("local", viaProfile.Model!.ProviderId);
        Assert.Equal(PillionErrorCode.PolicyDenied, ex.Error.Code);
        Assert.Equal(0, harness.Cloud!.EmbedCalls);
        Assert.All(harness.OutboundLog.GetRecent(), r => Assert.True(r.IsLocal));
    }

    [Fact]
    public async Task Embed_UnderLocalOnly_NothingIsSentToACloudProvider_EvenWithTheSwitchOn()
    {
        using var harness = Harness(h =>
        {
            h.Settings.CloudSwitches.Embedding = true;
            h.Settings.Privacy.LocalOnly = true;
        });

        var viaProfile = await harness.Gateway.EmbedAsync(Request(Texts(40), profile: Profile.Fast), CancellationToken.None);
        var ex = await Assert.ThrowsAsync<PillionRpcException>(() => harness.Gateway.EmbedAsync(Request(["x"], model: "cloud/" + CloudModel), CancellationToken.None));

        Assert.Equal("local", viaProfile.Model!.ProviderId);
        Assert.Equal(PillionErrorCode.PolicyDenied, ex.Error.Code);
        Assert.Equal(0, harness.Cloud!.EmbedCalls);
    }

    [Fact]
    public async Task Embed_WithTheSwitchOn_ACloudProfileReachesTheCloudModel()
    {
        using var harness = Harness(h => h.Settings.CloudSwitches.Embedding = true);

        var result = await harness.Gateway.EmbedAsync(Request(Texts(3), profile: Profile.Fast), CancellationToken.None);

        Assert.Equal("cloud", result.Model!.ProviderId);
        Assert.Equal(CloudModel, result.Model.ModelId);
        Assert.Equal(1, harness.Cloud!.EmbedCalls);
        Assert.Equal(0, harness.Local!.EmbedCalls);
    }

    [Fact]
    public async Task Embed_EvenIfTheRouterWereToChooseACloudModel_TheGatewayHandsNothingToIt()
    {
        using var harness = Harness();
        var cloud = new RoutedModel("cloud", CloudModel, Profile.Fast, IsLocal: false, "forced by the test");
        var gateway = new ModelGateway(
            harness.Registry,
            new StubRouter(cloud),
            harness.Inventory,
            ModelCatalog.Default,
            PromptTemplates.Default,
            harness.Cache,
            harness.Breakers,
            harness.Limiter,
            harness.OutboundLog,
            harness.Reachability,
            new NoCostCaps(),
            harness.Progress,
            harness.Options,
            harness.Clock);

        var ex = await Assert.ThrowsAsync<PillionRpcException>(() => gateway.EmbedAsync(Request(["x"]), CancellationToken.None));

        Assert.Equal(PillionErrorCode.PolicyDenied, ex.Error.Code);
        Assert.Contains("cloud switch for Embedding is off", ex.Message, StringComparison.Ordinal);
        Assert.Equal(0, harness.Cloud!.EmbedCalls);

        harness.Settings.CloudSwitches.Embedding = true;
        harness.Settings.Privacy.LocalOnly = true;
        harness.Reload();
        ex = await Assert.ThrowsAsync<PillionRpcException>(() => gateway.EmbedAsync(Request(["x"]), CancellationToken.None));
        Assert.Contains("Local-only", ex.Message, StringComparison.Ordinal);
        Assert.Equal(0, harness.Cloud.EmbedCalls);
    }

    [Fact]
    public async Task Embed_SecretsAreRedactedBeforeTheyReachACloudProvider_ButNotALocalOne()
    {
        using var harness = Harness(h => h.Settings.CloudSwitches.Embedding = true);
        var secret = TestSecrets.AwsAccessKey();
        string[] inputs = [$"var key = \"{secret}\";", "int x = 1;"];

        var cloud = await harness.Gateway.EmbedAsync(Request(inputs, model: "cloud/" + CloudModel), CancellationToken.None);
        var local = await harness.Gateway.EmbedAsync(Request(inputs, model: "local/" + Qwen), CancellationToken.None);

        Assert.True(cloud.IsComplete);
        Assert.True(local.IsComplete);
        var sentToCloud = harness.Cloud!.ReceivedEmbeds.Single().Texts;
        Assert.DoesNotContain(secret, sentToCloud[0], StringComparison.Ordinal);
        Assert.Contains("<redacted:", sentToCloud[0], StringComparison.Ordinal);
        Assert.Equal("int x = 1;", sentToCloud[1]);
        Assert.Contains(secret, harness.Local!.ReceivedEmbeds.Single().Texts[0], StringComparison.Ordinal);

        var records = harness.OutboundLog.GetRecent();
        Assert.Equal(1, records[0].RedactionCount);
        Assert.Equal(0, records[1].RedactionCount);
    }

    [Fact]
    public async Task Embed_APolicyThatRedactsForLocalProviders_AppliesToEmbeddingsToo()
    {
        using var harness = Harness(h => h.Settings.Privacy.RedactForLocalProviders = true);
        var secret = TestSecrets.AwsAccessKey();

        await harness.Gateway.EmbedAsync(Request([$"var key = \"{secret}\";"]), CancellationToken.None);

        Assert.DoesNotContain(secret, harness.Local!.ReceivedEmbeds.Single().Texts[0], StringComparison.Ordinal);
    }

    // Features.
    [Fact]
    public async Task Embed_WithTheEmbeddingsFlagOff_IsPolicyDenied_AndNothingIsSent()
    {
        using var harness = Harness(h => h.Settings.Features.Embeddings = false);

        var ex = await Assert.ThrowsAsync<PillionRpcException>(() => harness.Gateway.EmbedAsync(Request(["x"]), CancellationToken.None));

        Assert.Equal(PillionErrorCode.PolicyDenied, ex.Error.Code);
        Assert.Contains("features.embeddings", ex.Message, StringComparison.Ordinal);
        Assert.Equal(0, harness.Local!.EmbedCalls);
    }

    [Fact]
    public async Task Embed_WithTheModelGatewayFlagOff_IsPolicyDenied()
    {
        using var harness = Harness(h => h.Settings.Features.ModelGateway = false);

        var ex = await Assert.ThrowsAsync<PillionRpcException>(() => harness.Gateway.EmbedAsync(Request(["x"]), CancellationToken.None));

        Assert.Equal(PillionErrorCode.PolicyDenied, ex.Error.Code);
        Assert.Contains("modelGateway", ex.Message, StringComparison.Ordinal);
        Assert.Equal(0, harness.Local!.EmbedCalls);
    }

    // Resilience.
    [Fact]
    public async Task Embed_HonoursTheConcurrencyLimitPerProvider()
    {
        using var harness = Harness();
        harness.Local!.Embed = async (texts, _, ct) =>
        {
            await Task.Delay(30, ct);
            return [.. texts.Select(t => FakeProvider.DefaultVector(t))];
        };

        var calls = Enumerable.Range(0, 6).Select(i => harness.Gateway.EmbedAsync(Request([$"t{i}"], id: $"c{i}"), CancellationToken.None));
        await Task.WhenAll(calls);

        Assert.Equal(6, harness.Local.EmbedCalls);
        Assert.Equal(1, harness.Local.MaxInFlight);
        Assert.Equal(1, harness.Limiter.LimitFor(ConcurrencyLane.Embedding));
        Assert.Equal(1, harness.Limiter.Available("local", ConcurrencyLane.Embedding));
    }

    [Fact]
    public async Task Embed_TheLaneIsConfigurable()
    {
        using var harness = Harness(options: o => o.EmbeddingConcurrency = 3);
        harness.Local!.Embed = async (texts, _, ct) =>
        {
            await Task.Delay(50, ct);
            return [.. texts.Select(t => FakeProvider.DefaultVector(t))];
        };

        await Task.WhenAll(Enumerable.Range(0, 6).Select(i => harness.Gateway.EmbedAsync(Request([$"t{i}"], id: $"c{i}"), CancellationToken.None)));

        Assert.Equal(3, harness.Local.MaxInFlight);
        Assert.Equal(ConcurrencyLane.Embedding, ConcurrencyLimiter.LaneFor(TaskClass.Embedding));
        Assert.Equal(ConcurrencyLane.Chat, ConcurrencyLimiter.LaneFor(TaskClass.Summarise));
    }

    [Fact]
    public async Task Embed_ABatchWaitsWhileAnotherEmbeddingBatchRuns_ButChatIsNotBlocked()
    {
        using var harness = Harness();
        var release = new TaskCompletionSource();
        var started = new TaskCompletionSource();
        harness.Local!.Embed = async (texts, _, ct) =>
        {
            started.TrySetResult();
            await release.Task.WaitAsync(ct);
            return [.. texts.Select(t => FakeProvider.DefaultVector(t))];
        };

        var first = harness.Gateway.EmbedAsync(Request(["first"], id: "e1"), CancellationToken.None);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var second = harness.Gateway.EmbedAsync(Request(["second"], id: "e2"), CancellationToken.None);

        // Chat on the same provider gets its answer while the embedding lane is full.
        var chat = await harness.Gateway.ChatAsync(GatewayHarness.ChatRequest("hi", modelId: "local/qwen2.5-coder:14b"), TaskClass.Chat, CancellationToken.None).CollectAsync();
        Assert.True(chat[^1].IsFinal);
        Assert.Equal(1, harness.Local.EmbedCalls);
        Assert.Equal(0, harness.Limiter.Available("local", ConcurrencyLane.Embedding));
        Assert.False(second.IsCompleted);

        release.SetResult();
        await Task.WhenAll(first, second);
        Assert.Equal(2, harness.Local.EmbedCalls);
        Assert.Equal(1, harness.Limiter.Available("local", ConcurrencyLane.Embedding));
        Assert.Equal(2, harness.Limiter.Available("local", ConcurrencyLane.Chat));
    }

    [Fact]
    public async Task Embed_RepeatedProviderFaults_OpenTheCircuit_AndLaterBatchesFailFastWithoutAsking()
    {
        using var harness = Harness(options: o => o.MaxRetries = 0);
        harness.Local!.Embed = (_, _, _) => Task.FromException<float[][]>(FakeProvider.Unreachable());

        // Three failed calls open the circuit (the threshold); the fourth is refused by the router as unreachable.
        for (var i = 0; i < 3; i++)
        {
            await Assert.ThrowsAsync<PillionRpcException>(() => harness.Gateway.EmbedAsync(Request(["x"]), CancellationToken.None));
        }

        Assert.False(harness.Breakers.IsAvailable("local"));
        var ex = await Assert.ThrowsAsync<PillionRpcException>(() => harness.Gateway.EmbedAsync(Request(["x"]), CancellationToken.None));

        Assert.Equal(PillionErrorCode.ProviderUnavailable, ex.Error.Code);
        Assert.Equal(3, harness.Local.EmbedCalls);
    }

    [Fact]
    public async Task Embed_Cancellation_StopsTheCall_AndRecordsTheBatchAsCancelled()
    {
        using var harness = Harness();
        using var cts = new CancellationTokenSource();
        harness.Local!.Embed = async (_, _, ct) =>
        {
            await cts.CancelAsync();
            await Task.Delay(Timeout.Infinite, ct);
            return [];
        };

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => harness.Gateway.EmbedAsync(Request(Texts(64)), cts.Token));

        Assert.Equal(1, harness.Local.EmbedCalls);
        Assert.Equal(OutboundOutcome.Cancelled, Assert.Single(harness.OutboundLog.GetRecent()).Outcome);
        Assert.Equal(harness.Limiter.LimitFor(ConcurrencyLane.Chat), harness.Limiter.Available("local", ConcurrencyLane.Chat));
    }

    private sealed class StubRouter(RoutedModel route) : IModelRouter
    {
        public RoutedModel Resolve(RoutingRequest request) => route;

        public RoutedModel Resolve(TaskClass taskClass, ChatRequest request) => throw new NotSupportedException();

        public RoutedModel Resolve(TaskClass taskClass, FimRequest request) => throw new NotSupportedException();
    }
}

using System.Globalization;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Pillion.Common.Redaction;
using Pillion.Common.Settings;
using Pillion.Contracts;

namespace Pillion.ModelGateway;

/// <summary>The embedding path of the gateway (design section 6 and section 7, Embed).</summary>
public sealed partial class ModelGateway
{
    /// <summary>The message of the error returned while the <see cref="FeatureNames.Embeddings"/> feature is off.</summary>
    internal const string EmbeddingsDisabledMessage =
        "Embeddings are turned off by settings or policy (feature embeddings). "
        + "Set features.embeddings to true in settings.json, or remove it from disabledFeatures in the repository policy.";

    /// <inheritdoc/>
    public async Task<EmbeddingResult> EmbedAsync(EmbeddingRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.Inputs);
        for (var i = 0; i < request.Inputs.Count; i++)
        {
            if (string.IsNullOrEmpty(request.Inputs[i]))
            {
                throw new ArgumentException($"Input {i} of the embedding request is empty.", nameof(request));
            }
        }

        var started = _services.Time.GetTimestamp();
        var snapshot = _registry.GetSnapshot();
        EnsureEmbeddingsEnabled(snapshot.Settings);
        if (request.Inputs.Count == 0)
        {
            return new EmbeddingResult([], null, [], 0, 0, TimeSpan.Zero);
        }

        // Routing only checks that the longest text fits the model's context; the batches are built per candidate (the catalog's
        // prefix for queries and passages depends on the model).
        var route = _router.Resolve(new RoutingRequest
        {
            TaskClass = TaskClass.Embedding,
            Profile = request.Profile,
            ModelId = request.ModelId,
            EstimatedPromptTokens = request.Inputs.Max(text => TokenEstimator.Estimate(text)),
        });
        LogRouted(_logger, request.RequestId, route.QualifiedId, route.Profile, route.Reason);

        var batchSize = request.BatchSize > 0 ? request.BatchSize : Math.Max(1, _services.Options.EmbeddingBatchSize);
        var maxFailures = _services.Options.EmbeddingMaxConsecutiveFailures;
        var vectors = new EmbeddedVector?[request.Inputs.Count];
        var failures = new List<EmbeddingBatchFailure>();
        var allCandidates = new List<RoutedModel> { route };
        allCandidates.AddRange(route.Fallbacks);

        // Vectors of different models cannot be compared, so once a batch has succeeded the rest of the call stays on that model.
        RoutedModel? chosen = null;
        EmbeddingModelInfo? model = null;
        Exception? lastFailure = null;
        var consecutive = 0;
        var batches = 0;
        var promptTokens = 0;

        for (var start = 0; start < request.Inputs.Count; start += batchSize)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var count = Math.Min(batchSize, request.Inputs.Count - start);
            if (maxFailures > 0 && consecutive >= maxFailures && lastFailure is not null)
            {
                failures.Add(new EmbeddingBatchFailure(start, request.Inputs.Count - start, ToError(lastFailure), Skipped: true));
                break;
            }

            var texts = new string[count];
            for (var i = 0; i < count; i++)
            {
                texts[i] = request.Inputs[start + i];
            }

            batches++;
            try
            {
                var batch = await EmbedBatchAsync(
                    snapshot,
                    request,
                    chosen is null ? allCandidates : [chosen],
                    texts,
                    batches,
                    model?.Dimensions,
                    cancellationToken).ConfigureAwait(false);
                promptTokens += batch.PromptTokens;
                if (model is null)
                {
                    chosen = batch.Model;
                    model = DescribeModel(batch.Model, batch.Vectors[0].Dimensions);
                }

                for (var i = 0; i < count; i++)
                {
                    vectors[start + i] = batch.Vectors[i];
                }

                consecutive = 0;
            }
            catch (Exception ex) when (ex is ProviderException or PillionRpcException)
            {
                lastFailure = ex;
                consecutive++;
                failures.Add(new EmbeddingBatchFailure(start, count, ToError(ex), Skipped: false));
            }
        }

        if (model is null && lastFailure is not null)
        {
            // Not one batch succeeded: there is nothing to return, so the call fails with the last error.
            throw ToRpcException(lastFailure);
        }

        return new EmbeddingResult(vectors, model, failures, batches, promptTokens, _services.Time.GetElapsedTime(started));
    }

    private static void EnsureEmbeddingsEnabled(EffectiveSettings settings)
    {
        if (!settings.IsFeatureEnabled(FeatureNames.ModelGateway))
        {
            throw new PillionRpcException(new PillionError
            {
                Code = PillionErrorCode.PolicyDenied,
                Message = "The Model Gateway is turned off by settings or policy (feature modelGateway). "
                    + "Set features.modelGateway to true in settings.json, or remove it from disabledFeatures in the repository policy.",
            });
        }

        if (!settings.IsFeatureEnabled(FeatureNames.Embeddings))
        {
            throw new PillionRpcException(new PillionError { Code = PillionErrorCode.PolicyDenied, Message = EmbeddingsDisabledMessage });
        }
    }

    /// <summary>Embeds one batch on the first candidate that can, redacting what leaves the machine and recording the batch in the Outbound log.</summary>
    private async Task<EmbeddedBatch> EmbedBatchAsync(
        ProviderRegistrySnapshot snapshot,
        EmbeddingRequest request,
        IReadOnlyList<RoutedModel> candidates,
        string[] texts,
        int batchNumber,
        int? expectedDimensions,
        CancellationToken cancellationToken)
    {
        var settings = snapshot.Settings;
        var redactForLocal = settings.Settings.Privacy.RedactForLocalProviders;
        var batchId = string.Create(CultureInfo.InvariantCulture, $"{request.RequestId}/b{batchNumber}");
        Exception? lastFailure = null;

        foreach (var candidate in candidates)
        {
            // The router has already dropped cloud candidates under Local-only or with the Embedding switch off; this is the
            // second lock on the same door: nothing is handed to a cloud provider unless both allow it.
            if (!candidate.IsLocal && (settings.LocalOnly || !settings.Settings.CloudSwitches.Embedding))
            {
                lastFailure ??= new ProviderException(
                    PillionErrorCode.PolicyDenied,
                    $"{candidate.QualifiedId} is a cloud model and {(settings.LocalOnly ? "Local-only mode is on" : "the cloud switch for Embedding is off")}.",
                    isProviderFault: false);
                continue;
            }

            var provider = snapshot.Find(candidate.ProviderId);
            var generator = provider?.GetEmbeddingGenerator(candidate.ModelId);
            if (provider is null || generator is null)
            {
                lastFailure ??= new ProviderException(
                    PillionErrorCode.ModelNotInstalled,
                    $"{candidate.QualifiedId} does not provide embeddings.",
                    isProviderFault: false);
                continue;
            }

            if (lastFailure is not null)
            {
                _services.Progress.Report(new ModelProgress(request.RequestId, candidate.ProviderId, candidate.ModelId, ModelProgressKind.FallingBack, TimeSpan.Zero, $"Trying {candidate.QualifiedId} instead: {lastFailure.Message}"));
            }

            var info = _catalog.GetEmbeddingInfo(candidate.ModelId);
            using var session = !provider.IsLocal || redactForLocal ? new RedactionSession() : null;
            var sent = new List<string>(texts.Length);
            foreach (var text in texts)
            {
                var prefixed = Prefixed(text, request.Purpose, info);
                sent.Add(session is null ? prefixed : session.Redact(prefixed));
            }

            var tokens = sent.Sum(text => TokenEstimator.Estimate(text));
            var generationOptions = new EmbeddingGenerationOptions
            {
                ModelId = candidate.ModelId,

                // A model that cannot be shortened (ada-002, mistral-embed) would reject the parameter.
                Dimensions = info is null || info.SupportsDimensions ? request.Dimensions : null,
                AdditionalProperties = new AdditionalPropertiesDictionary
                {
                    [GatewayOptionKeys.Profile] = candidate.Profile,
                    [GatewayOptionKeys.RequestId] = batchId,
                    [GatewayOptionKeys.EmbeddingPurpose] = request.Purpose,
                },
            };

            var attempt = new StreamAttempt<GeneratedEmbeddings<Embedding<float>>>(
                _services,
                candidate,
                new AttemptPolicy
                {
                    RequestId = batchId,
                    TaskClass = TaskClass.Embedding,
                    PromptTokens = tokens,
                    MaxRetries = _services.Options.MaxRetries,
                    FirstTokenTimeout = candidate.IsLocal ? null : _services.Options.CloudFirstTokenTimeout,
                    LocalBudget = candidate.IsLocal ? _services.Options.LocalFirstTokenBudget(tokens) : null,
                },
                token => Once(() => generator.GenerateAsync(sent, generationOptions, token)),
                _ => true,
                () => session?.Redactions.Count ?? 0,
                cancellationToken);

            try
            {
                await attempt.StartAsync().ConfigureAwait(false);
                if (!await attempt.MoveNextAsync().ConfigureAwait(false))
                {
                    throw new ProviderException(PillionErrorCode.ProviderUnavailable, $"{candidate.QualifiedId} returned no embeddings.");
                }

                var processed = Process(attempt.Current, sent.Count, expectedDimensions, request.IncludeFloat32, candidate);
                attempt.Complete(OutboundOutcome.Succeeded);
                return new EmbeddedBatch(candidate, processed, tokens);
            }
            catch (Exception ex) when (ex is ProviderException or PillionRpcException)
            {
                attempt.Complete(OutboundOutcome.Failed, ex);
                lastFailure = ex;
                LogAttemptFailed(_logger, batchId, candidate.QualifiedId, ex.Message);
            }
            catch (OperationCanceledException ex)
            {
                attempt.Complete(OutboundOutcome.Cancelled, ex);
                throw;
            }
            finally
            {
                await attempt.DisposeAsync().ConfigureAwait(false);
            }
        }

        throw lastFailure ?? new ProviderException(PillionErrorCode.ProviderUnavailable, "No embedding model is available.");
    }

    /// <summary>Checks a provider's answer against the request and turns it into unit-length int8 vectors.</summary>
    private static EmbeddedVector[] Process(GeneratedEmbeddings<Embedding<float>> generated, int expected, int? expectedDimensions, bool keepFloat32, RoutedModel candidate)
    {
        if (generated.Count != expected)
        {
            throw new ProviderException(
                PillionErrorCode.ProviderUnavailable,
                string.Create(CultureInfo.InvariantCulture, $"{candidate.QualifiedId} returned {generated.Count} embeddings for {expected} texts."));
        }

        var result = new EmbeddedVector[expected];
        var dimensions = generated[0].Vector.Length;
        for (var i = 0; i < expected; i++)
        {
            var vector = generated[i].Vector.Span;
            if (vector.Length == 0 || vector.Length != dimensions)
            {
                throw new ProviderException(
                    PillionErrorCode.ProviderUnavailable,
                    string.Create(CultureInfo.InvariantCulture, $"{candidate.QualifiedId} returned embeddings of different sizes ({dimensions} and {vector.Length})."));
            }

            try
            {
                result[i] = EmbeddingVectors.Process(vector, keepFloat32);
            }
            catch (ArgumentException ex)
            {
                throw new ProviderException(PillionErrorCode.ProviderUnavailable, $"{candidate.QualifiedId} returned an embedding with a value that is not a number.", innerException: ex);
            }
        }

        if (expectedDimensions is { } earlier && earlier != dimensions)
        {
            throw new ProviderException(
                PillionErrorCode.ProviderUnavailable,
                string.Create(CultureInfo.InvariantCulture, $"{candidate.QualifiedId} returned embeddings of {dimensions} dimensions, but the earlier batches of this request had {earlier}."),
                isProviderFault: false);
        }

        return result;
    }

    private EmbeddingModelInfo DescribeModel(RoutedModel model, int dimensions)
    {
        var entry = _catalog.GetEmbeddingInfo(model.ModelId);
        if (entry is { Dimensions: > 0 } && entry.Dimensions != dimensions)
        {
            LogDimensionsDiffer(_logger, model.QualifiedId, dimensions, entry.Dimensions);
        }

        var window = _inventory.Find(model.ProviderId, model.ModelId)?.Capabilities.ContextWindow
            ?? _catalog.GetCapabilities(model.ModelId, model.IsLocal).ContextWindow;
        return new EmbeddingModelInfo(
            model.ProviderId,
            model.ModelId,
            dimensions,
            entry?.TokenizerFamily ?? EmbeddingModelInfo.UnknownTokenizer,
            entry?.MaxInputTokens ?? window);
    }

    private static string Prefixed(string text, EmbeddingPurpose purpose, EmbeddingModelEntry? info)
    {
        var prefix = purpose == EmbeddingPurpose.Query ? info?.QueryPrefix : info?.DocumentPrefix;
        return string.IsNullOrEmpty(prefix) ? text : prefix + text;
    }

    private static PillionError ToError(Exception ex) => ex switch
    {
        ProviderException provider => provider.ToError(),
        PillionRpcException rpc => rpc.Error,
        _ => new PillionError { Code = PillionErrorCode.ProviderUnavailable, Message = ex.Message },
    };

    private static async IAsyncEnumerable<T> Once<T>(Func<Task<T>> produce)
    {
        yield return await produce().ConfigureAwait(false);
    }

    [LoggerMessage(EventId = 435, Level = LogLevel.Warning, Message = "Embedding model {Model} returned {Actual} dimensions; the catalog says {Expected}. The size from the response is used.")]
    private static partial void LogDimensionsDiffer(ILogger logger, string model, int actual, int expected);

    /// <summary>One batch embedded: the model that served it, the processed vectors and the estimated tokens sent.</summary>
    private sealed record EmbeddedBatch(RoutedModel Model, EmbeddedVector[] Vectors, int PromptTokens);
}

using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using Pillion.Common.Settings;
using Pillion.Contracts;

namespace Pillion.Host.InlineEdit;

/// <summary>
/// Implements <see cref="IInlineEditService"/> over the <see cref="InlineEditEngine"/>. Every call but cancel needs the
/// <see cref="FeatureNames.ModelGateway"/> and <see cref="FeatureNames.InlineChat"/> features and otherwise fails with
/// <see cref="PillionErrorCode.PolicyDenied"/>; <see cref="CancelEditAsync"/> cancels an in-flight edit by its request id.
/// </summary>
internal sealed class InlineEditRpcService : IInlineEditService
{
    /// <summary>The message of the error returned while the <see cref="FeatureNames.InlineChat"/> feature is off.</summary>
    internal const string InlineChatDisabledMessage =
        "Inline chat is turned off by settings or policy (feature inlineChat). "
        + "Set features.inlineChat to true in settings.json, or remove it from disabledFeatures in the repository policy.";

    private readonly InlineEditEngine _engine;
    private readonly IFeatureFlags? _features;
    private readonly ConcurrentDictionary<string, CancellationTokenSource> _inFlight = new(StringComparer.Ordinal);

    /// <summary>Initializes a new instance of the <see cref="InlineEditRpcService"/> class.</summary>
    /// <param name="engine">The engine.</param>
    /// <param name="features">The feature flags; <see langword="null"/> means no flags.</param>
    public InlineEditRpcService(InlineEditEngine engine, IFeatureFlags? features = null)
    {
        ArgumentNullException.ThrowIfNull(engine);
        _engine = engine;
        _features = features;
    }

    /// <summary>Gets the number of requests in flight (for tests).</summary>
    internal int InFlightCount => _inFlight.Count;

    /// <inheritdoc/>
    public async IAsyncEnumerable<InlineEditChunk> EditAsync(InlineEditRequest request, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        EnsureEnabled(FeatureNames.ModelGateway, Models.ModelRpcService.GatewayDisabledMessage);
        EnsureEnabled(FeatureNames.InlineChat, InlineChatDisabledMessage);

        var key = string.IsNullOrEmpty(request.RequestId) ? Guid.NewGuid().ToString("N") : request.RequestId;
        using var source = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

        // A reused id replaces the older entry; the older request can then only be cancelled by its RPC token.
        _inFlight[key] = source;
        try
        {
            await foreach (var chunk in _engine.RunAsync(request, source.Token).ConfigureAwait(false))
            {
                yield return chunk;
            }
        }
        finally
        {
            _inFlight.TryRemove(new KeyValuePair<string, CancellationTokenSource>(key, source));
        }
    }

    /// <inheritdoc/>
    public async Task CancelEditAsync(string requestId, CancellationToken cancellationToken)
    {
        if (!string.IsNullOrEmpty(requestId) && _inFlight.TryGetValue(requestId, out var source))
        {
            try
            {
                await source.CancelAsync().ConfigureAwait(false);
            }
            catch (ObjectDisposedException)
            {
                // The request finished while it was being cancelled.
            }
        }
    }

    private void EnsureEnabled(string feature, string message)
    {
        if (_features is not null && !_features.IsEnabled(feature))
        {
            throw new PillionRpcException(new PillionError { Code = PillionErrorCode.PolicyDenied, Message = message });
        }
    }
}

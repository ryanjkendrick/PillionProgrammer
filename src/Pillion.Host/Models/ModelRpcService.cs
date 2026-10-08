using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using Pillion.Contracts;
using Pillion.ModelGateway;

namespace Pillion.Host.Models;

/// <summary>
/// Implements <see cref="IModelService"/> over the Model Gateway. <see cref="CompleteAsync"/> and <see cref="ChatAsync"/>
/// stream chunks; <see cref="CancelAsync"/> cancels an in-flight request by its id; <see cref="ListModelsAsync"/>
/// aggregates every provider's models and marks which are usable now.
/// </summary>
internal sealed class ModelRpcService : IModelService
{
    private readonly IModelGateway _gateway;
    private readonly ConcurrentDictionary<string, CancellationTokenSource> _inFlight = new(StringComparer.Ordinal);

    /// <summary>Initializes a new instance of the <see cref="ModelRpcService"/> class.</summary>
    /// <param name="gateway">The gateway.</param>
    public ModelRpcService(IModelGateway gateway)
    {
        ArgumentNullException.ThrowIfNull(gateway);
        _gateway = gateway;
    }

    /// <summary>Gets the number of requests in flight (for tests).</summary>
    internal int InFlightCount => _inFlight.Count;

    /// <inheritdoc/>
    public async IAsyncEnumerable<CompletionChunk> CompleteAsync(CompletionRequest request, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        using var registration = Register(request.RequestId, cancellationToken);
        await foreach (var chunk in _gateway.CompleteAsync(request, registration.Token).ConfigureAwait(false))
        {
            yield return chunk;
        }
    }

    /// <inheritdoc/>
    public async IAsyncEnumerable<ChatChunk> ChatAsync(ChatRequest request, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        using var registration = Register(request.RequestId, cancellationToken);
        await foreach (var chunk in _gateway.ChatAsync(request, TaskClass.Chat, registration.Token).ConfigureAwait(false))
        {
            yield return chunk;
        }
    }

    /// <inheritdoc/>
    public async Task CancelAsync(string requestId, CancellationToken cancellationToken)
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

    /// <inheritdoc/>
    public async Task<ModelDescriptor[]> ListModelsAsync(CancellationToken cancellationToken) =>
        [.. await _gateway.ListModelsAsync(cancellationToken).ConfigureAwait(false)];

    private Registration Register(string requestId, CancellationToken cancellationToken)
    {
        var source = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var key = string.IsNullOrEmpty(requestId) ? Guid.NewGuid().ToString("N") : requestId;

        // A reused id replaces the older entry; the older request can then only be cancelled by its RPC token.
        _inFlight[key] = source;
        return new Registration(this, key, source);
    }

    private sealed class Registration(ModelRpcService owner, string key, CancellationTokenSource source) : IDisposable
    {
        public CancellationToken Token => source.Token;

        public void Dispose()
        {
            owner._inFlight.TryRemove(new KeyValuePair<string, CancellationTokenSource>(key, source));
            source.Dispose();
        }
    }
}

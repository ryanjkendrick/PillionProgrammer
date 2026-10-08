using System.Collections.Concurrent;
using System.Globalization;
using System.Runtime.CompilerServices;
using Pillion.Contracts;

namespace Pillion.FakeHost;

/// <summary>
/// A canned <see cref="IModelService"/>: lists one local and one cloud model and streams a fixed reply, in several
/// chunks, that names the model that answered. Honours both cancellation paths: the stream's token (the client
/// disposing the enumerator) and <see cref="CancelAsync"/> with the request id.
/// </summary>
/// <remarks>
/// A last user message containing <c>/error</c> fails the request with a <see cref="PillionErrorCode.ProviderUnavailable"/>
/// error before any chunk, so the client's inline error path can be exercised. The delay between chunks is
/// <c>PILLION_FAKEHOST_CHUNK_DELAY_MS</c> (120 ms by default). Only ids and counts are written to stderr, never text.
/// </remarks>
internal sealed class FakeModelService : IModelService
{
    public const string LocalModelId = "fake-local-coder";
    public const string CloudModelId = "fake-cloud-chat";

    private static readonly ModelDescriptor[] Models =
    [
        new()
        {
            Id = LocalModelId,
            DisplayName = "Fake Local Coder",
            ProviderId = "ollama",
            IsLocal = true,
            Capabilities = ModelCapabilities.Chat | ModelCapabilities.Fim,
        },
        new()
        {
            Id = CloudModelId,
            DisplayName = "Fake Cloud Chat",
            ProviderId = "fake-cloud",
            IsLocal = false,
            Capabilities = ModelCapabilities.Chat | ModelCapabilities.Tools,
        },
    ];

    private readonly ConcurrentDictionary<string, CancellationTokenSource> _inFlight = new(StringComparer.Ordinal);
    private readonly TimeSpan _chunkDelay = TimeSpan.FromMilliseconds(
        int.TryParse(Environment.GetEnvironmentVariable("PILLION_FAKEHOST_CHUNK_DELAY_MS"), NumberStyles.None, CultureInfo.InvariantCulture, out var ms) ? ms : 120);

    public Task<ModelDescriptor[]> ListModelsAsync(CancellationToken cancellationToken)
    {
        Console.Error.WriteLine("fakehost: list models");
        return Task.FromResult(Models);
    }

    public async IAsyncEnumerable<ChatChunk> ChatAsync(ChatRequest request, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var model = Models.FirstOrDefault(m => m.Id == request.ModelId) ?? Models[0];
        Console.Error.WriteLine(
            "fakehost: chat " + request.RequestId + " model " + model.Id + ", " + request.Messages.Count + " messages");

        var lastUser = request.Messages.LastOrDefault(m => m.Role == ChatRole.User)?.Content ?? string.Empty;
        if (lastUser.Contains("/error", StringComparison.OrdinalIgnoreCase))
        {
            throw new PillionRpcException(new PillionError
            {
                Code = PillionErrorCode.ProviderUnavailable,
                Message = "The fake provider for " + model.DisplayName + " is unreachable.",
                RetryHint = "Check the provider under Tools > Options > Pillion Programmer > Providers and keys, then retry.",
            });
        }

        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _inFlight[request.RequestId] = cancellation;
        try
        {
            foreach (var piece in Split(ReplyFor(model, request.Messages.Count), 14))
            {
                try
                {
                    await Task.Delay(_chunkDelay, cancellation.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    Console.Error.WriteLine("fakehost: chat " + request.RequestId + " cancelled");
                    throw;
                }

                yield return new ChatChunk { RequestId = request.RequestId, Text = piece };
            }

            yield return new ChatChunk { RequestId = request.RequestId, IsFinal = true };
            Console.Error.WriteLine("fakehost: chat " + request.RequestId + " complete");
        }
        finally
        {
            _inFlight.TryRemove(request.RequestId, out _);
        }
    }

    public Task CancelAsync(string requestId, CancellationToken cancellationToken)
    {
        if (_inFlight.TryGetValue(requestId, out var cancellation))
        {
            Console.Error.WriteLine("fakehost: cancel " + requestId);
            cancellation.Cancel();
        }
        else
        {
            Console.Error.WriteLine("fakehost: cancel " + requestId + " (not in flight)");
        }

        return Task.CompletedTask;
    }

    public IAsyncEnumerable<CompletionChunk> CompleteAsync(CompletionRequest request, CancellationToken cancellationToken) =>
        throw new PillionRpcException(new PillionError { Code = PillionErrorCode.PolicyDenied, Message = "The fake host does not complete code." });

    private static string ReplyFor(ModelDescriptor model, int messageCount) =>
        "Hello from " + model.DisplayName + " (model " + model.Id + ", provider " + model.ProviderId + ", "
        + (model.IsLocal ? "local" : "cloud") + "). This is a canned reply from Pillion's fake host, streamed in "
        + "several chunks; the conversation so far has " + messageCount.ToString(CultureInfo.InvariantCulture)
        + " message(s).\n\n```csharp\n// A fenced code block, shown in a monospace block.\nConsole.WriteLine(\"Hello from "
        + model.Id + "\");\n```\n\nThat is the end of the reply from " + model.DisplayName + ".";

    private static IEnumerable<string> Split(string text, int size)
    {
        for (var i = 0; i < text.Length; i += size)
        {
            yield return text.Substring(i, Math.Min(size, text.Length - i));
        }
    }
}

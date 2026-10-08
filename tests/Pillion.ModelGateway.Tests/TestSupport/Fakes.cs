using System.Collections.Concurrent;
using System.Net;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.AI;
using Pillion.Common.Settings;
using Pillion.Contracts;
using AiChatMessage = Microsoft.Extensions.AI.ChatMessage;
using AiChatRole = Microsoft.Extensions.AI.ChatRole;

namespace Pillion.ModelGateway.Tests.TestSupport;

/// <summary>A scripted model provider. Records what it receives; streams what the test tells it to.</summary>
internal sealed class FakeProvider : IModelProvider
{
    public FakeProvider(string id, bool isLocal, ProviderKind kind = ProviderKind.OpenAiCompatible)
    {
        Id = id;
        IsLocal = isLocal;
        Kind = kind;
    }

    public string Id { get; }

    public ProviderKind Kind { get; }

    public string DisplayName => Id;

    public bool IsLocal { get; }

    /// <summary>Gets or sets the models <see cref="ListModelsAsync"/> reports.</summary>
    public List<ProviderModel> Models { get; set; } = [];

    /// <summary>Gets or sets the chat behaviour: messages and options in, text pieces out.</summary>
    public Func<IReadOnlyList<AiChatMessage>, ChatOptions?, CancellationToken, IAsyncEnumerable<string>> Chat { get; set; } =
        (messages, _, ct) => Pieces(ct, "Hello", " from ", "fake");

    /// <summary>Gets or sets the FIM behaviour.</summary>
    public Func<FimRequest, CancellationToken, IAsyncEnumerable<string>> Fim { get; set; } =
        (_, ct) => Pieces(ct, "a + b");

    /// <summary>Gets or sets a value indicating whether <see cref="GetFimClient"/> returns a client.</summary>
    public bool SupportsFim { get; set; } = true;

    public ConcurrentQueue<IReadOnlyList<AiChatMessage>> ReceivedChats { get; } = new();

    public ConcurrentQueue<ChatOptions?> ReceivedOptions { get; } = new();

    public ConcurrentQueue<FimRequest> ReceivedFims { get; } = new();

    public int ChatCalls;

    public int FimCalls;

    public int ListCalls;

    public int ProbeCalls;

    public int InFlight;

    public int MaxInFlight;

    public Exception? ListFailure { get; set; }

    public static async IAsyncEnumerable<string> Pieces([EnumeratorCancellation] CancellationToken ct, params string[] pieces)
    {
        foreach (var piece in pieces)
        {
            await Task.Yield();
            ct.ThrowIfCancellationRequested();
            yield return piece;
        }
    }

    public static ProviderException HttpError(int status) =>
        new(PillionErrorCode.ProviderUnavailable, $"HTTP {status}", (HttpStatusCode)status);

    public static ProviderException Unreachable() =>
        new(PillionErrorCode.ProviderUnavailable, "Connection refused.");

    public Task<IReadOnlyList<ProviderModel>> ListModelsAsync(CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref ListCalls);
        if (ListFailure is not null)
        {
            throw ListFailure;
        }

        return Task.FromResult<IReadOnlyList<ProviderModel>>(Models);
    }

    public IChatClient GetChatClient(string modelId) => new Client(this, modelId);

    public IFimClient? GetFimClient(string modelId) => SupportsFim ? new FimClient(this) : null;

    public Task<ProviderProbeResult> ProbeAsync(CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref ProbeCalls);
        return Task.FromResult(new ProviderProbeResult(true, "1.0", $"Connected to {Id}."));
    }

    public void Dispose()
    {
    }

    public ProviderModel Model(string modelId, bool fim = false, bool vision = false, int context = 32768, bool chat = true, bool embeddings = false) =>
        new(Id, modelId, modelId, new ModelCapabilityDescriptor { Chat = chat, Fim = fim, Vision = vision, Embeddings = embeddings, ContextWindow = context, IsLocal = IsLocal });

    private async IAsyncEnumerable<T> Track<T>(IAsyncEnumerable<T> source, [EnumeratorCancellation] CancellationToken ct)
    {
        var now = Interlocked.Increment(ref InFlight);
        int max;
        do
        {
            max = MaxInFlight;
        }
        while (now > max && Interlocked.CompareExchange(ref MaxInFlight, now, max) != max);

        try
        {
            await foreach (var item in source.WithCancellation(ct))
            {
                yield return item;
            }
        }
        finally
        {
            Interlocked.Decrement(ref InFlight);
        }
    }

    private sealed class Client(FakeProvider owner, string modelId) : IChatClient
    {
        public Task<ChatResponse> GetResponseAsync(IEnumerable<AiChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) =>
            GetStreamingResponseAsync(messages, options, cancellationToken).ToChatResponseAsync(cancellationToken);

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<AiChatMessage> messages,
            ChatOptions? options = null,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref owner.ChatCalls);
            var list = messages.ToList();
            owner.ReceivedChats.Enqueue(list);
            owner.ReceivedOptions.Enqueue(options);
            await foreach (var piece in owner.Track(owner.Chat(list, options, cancellationToken), cancellationToken))
            {
                yield return new ChatResponseUpdate(AiChatRole.Assistant, piece) { ModelId = modelId };
            }
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose()
        {
        }
    }

    private sealed class FimClient(FakeProvider owner) : IFimClient
    {
        public IAsyncEnumerable<string> CompleteAsync(FimRequest request, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref owner.FimCalls);
            owner.ReceivedFims.Enqueue(request);
            return owner.Track(owner.Fim(request, cancellationToken), cancellationToken);
        }
    }
}

/// <summary>Hands out pre-built fake providers by id for one kind.</summary>
internal sealed class FakeProviderFactory(ProviderKind kind, bool requiresKey = false) : IModelProviderFactory
{
    public Dictionary<string, FakeProvider> Providers { get; } = new(StringComparer.OrdinalIgnoreCase);

    public ProviderKind Kind => kind;

    public bool RequiresApiKey(ProviderSettings settings) => requiresKey;

    public IModelProvider Create(ProviderSettings settings, ProviderCreationContext context) =>
        Providers.TryGetValue(settings.Id, out var provider) ? provider : throw new InvalidOperationException($"No fake for {settings.Id}.");
}

internal sealed class FakeReachability : IProviderReachability
{
    public HashSet<string> Unreachable { get; } = new(StringComparer.OrdinalIgnoreCase);

    public bool IsReachable(string providerId) => !Unreachable.Contains(providerId);
}

internal sealed class RecordingProgressSink : IProgressSink
{
    public ConcurrentQueue<ModelProgress> Reports { get; } = new();

    public void Report(ModelProgress progress) => Reports.Enqueue(progress);
}

internal sealed class RecordingHealthSink : IProviderHealthSink
{
    public ConcurrentQueue<string> Events { get; } = new();

    public void CircuitOpened(string providerId, DateTimeOffset retryAt) => Events.Enqueue("opened:" + providerId);

    public void CircuitClosed(string providerId) => Events.Enqueue("closed:" + providerId);
}

/// <summary>A clock whose wall time the test moves; timers and timestamps stay real.</summary>
internal sealed class ManualClock : TimeProvider
{
    private DateTimeOffset _now = new(2026, 10, 8, 12, 0, 0, TimeSpan.Zero);

    public override DateTimeOffset GetUtcNow() => _now;

    public void Advance(TimeSpan by) => _now += by;
}

internal static class AsyncEnumerableExtensions
{
    public static async Task<List<T>> CollectAsync<T>(this IAsyncEnumerable<T> source, CancellationToken cancellationToken = default)
    {
        var list = new List<T>();
        await foreach (var item in source.WithCancellation(cancellationToken))
        {
            list.Add(item);
        }

        return list;
    }
}

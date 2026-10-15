using System.Runtime.CompilerServices;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Pillion.Common;
using Pillion.Common.Secrets;
using Pillion.Common.Settings;
using Pillion.Contracts;
using Pillion.Host.Tests.TestSupport;
using Pillion.ModelGateway;
using AiChatMessage = Microsoft.Extensions.AI.ChatMessage;
using AiChatRole = Microsoft.Extensions.AI.ChatRole;

namespace Pillion.Host.Tests;

/// <summary>The model service through the real pipe server, with a fake provider behind the gateway.</summary>
public sealed class ModelRpcServiceTests : IDisposable
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(10);

    private readonly TempDirectory _data = new();
    private readonly FakeLocalProvider _provider = new();

    public void Dispose() => _data.Dispose();

    [Fact]
    public async Task ListModelsAsync_ReturnsTheProvidersModels()
    {
        await using var host = await StartAsync();
        await using var client = await host.ConnectAndHandshakeAsync();
        var models = client.Attach<IModelService>();

        var descriptors = await models.ListModelsAsync(CancellationToken.None).WaitAsync(Wait);

        // The bundled embedding model (work item 2.7) is listed too, as an embedding model the chat picker leaves out.
        var bundled = Assert.Single(descriptors, d => d.ProviderId == "bundled");
        Assert.Equal(ModelCapabilities.Embeddings, bundled.Capabilities);
        var descriptor = Assert.Single(descriptors, d => d.ProviderId != "bundled");
        Assert.Equal("fake/fake-chat:1b", descriptor.Id);
        Assert.Equal("fake-chat:1b", descriptor.ModelId);
        Assert.Equal("fake", descriptor.ProviderId);
        Assert.True(descriptor.IsLocal);
        Assert.True(descriptor.IsAvailable);
        Assert.Equal(ModelCapabilities.Chat, descriptor.Capabilities);
        Assert.Equal(8192, descriptor.ContextWindow);
        Assert.Equal("sha256:abc", descriptor.Digest);
    }

    [Fact]
    public async Task ChatAsync_StreamsChunksNamingTheModel()
    {
        await using var host = await StartAsync();
        await using var client = await host.ConnectAndHandshakeAsync();
        var models = client.Attach<IModelService>();

        var chunks = new List<ChatChunk>();
        await foreach (var chunk in models.ChatAsync(Request("req-1", "Hello"), CancellationToken.None).WithCancellation(CancellationToken.None))
        {
            chunks.Add(chunk);
        }

        Assert.Equal("echo: Hello", string.Concat(chunks.Select(c => c.Text)));
        Assert.True(chunks.Count >= 3);
        Assert.All(chunks, c =>
        {
            Assert.Equal("req-1", c.RequestId);
            Assert.Equal("fake-chat:1b", c.ModelId);
            Assert.Equal("fake", c.ProviderId);
        });
        Assert.True(chunks[^1].IsFinal);
        Assert.True(chunks[^1].CompletionTokens > 0);
    }

    [Fact]
    public async Task CancelAsync_StopsAnInFlightChat()
    {
        _provider.Hang = true;
        await using var host = await StartAsync();
        await using var client = await host.ConnectAndHandshakeAsync();
        var models = client.Attach<IModelService>();

        var stream = Task.Run(async () =>
        {
            await foreach (var chunk in models.ChatAsync(Request("req-cancel", "Hello"), CancellationToken.None))
            {
                _ = chunk;
            }
        });
        await _provider.Started.Task.WaitAsync(Wait);

        await models.CancelAsync("req-cancel", CancellationToken.None).WaitAsync(Wait);

        await Assert.ThrowsAnyAsync<Exception>(() => stream.WaitAsync(Wait));
        Assert.True(await _provider.Cancelled.Task.WaitAsync(Wait));
    }

    [Fact]
    public async Task TestProviderAsync_ProbesThroughTheGateway()
    {
        await using var host = await StartAsync();
        await using var client = await host.ConnectAndHandshakeAsync();
        var settings = client.Attach<ISettingsService>();

        var result = await settings.TestProviderAsync("fake", CancellationToken.None).WaitAsync(Wait);

        Assert.True(result.IsSuccess);
        Assert.Equal("Fake is fine.", result.Message);
        Assert.IsType<GatewayProviderTester>(host.Services.GetRequiredService<IProviderTester>());
    }

    private static ChatRequest Request(string id, string text) => new()
    {
        RequestId = id,
        Messages = [new Contracts.ChatMessage { Role = Contracts.ChatRole.User, Content = text }],
        Profile = Profile.Auto,
    };

    private Task<InProcessHost> StartAsync() => InProcessHost.StartAsync(services =>
    {
        var settings = new PillionSettings();
        settings.Providers.Add(new ProviderSettings { Id = "fake", Kind = ProviderKind.Mistral, Models = ["fake-chat:1b"] });

        services.Replace(ServiceDescriptor.Singleton(new PillionPaths(_data.Path)));
        services.Replace(ServiceDescriptor.Singleton<ISecretStore>(new InMemorySecretStore()));
        services.Replace(ServiceDescriptor.Singleton<IGatewaySettingsSource>(new StaticGatewaySettingsSource(settings)));
        services.AddSingleton<IModelProviderFactory>(new FakeFactory(_provider));
    });

    private sealed class FakeFactory(FakeLocalProvider provider) : IModelProviderFactory
    {
        public ProviderKind Kind => ProviderKind.Mistral;

        public bool RequiresApiKey(ProviderSettings settings) => false;

        public IModelProvider Create(ProviderSettings settings, ProviderCreationContext context) => provider;
    }

    private sealed class FakeLocalProvider : IModelProvider
    {
        public string Id => "fake";

        public ProviderKind Kind => ProviderKind.Mistral;

        public string DisplayName => "Fake";

        public bool IsLocal => true;

        public bool Hang { get; set; }

        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource<bool> Cancelled { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<IReadOnlyList<ProviderModel>> ListModelsAsync(CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<ProviderModel>>(
                [new ProviderModel("fake", "fake-chat:1b", "Fake chat", new ModelCapabilityDescriptor { ContextWindow = 8192, IsLocal = true }, "sha256:abc")]);

        public IChatClient GetChatClient(string modelId) => new Client(this);

        public IFimClient? GetFimClient(string modelId) => null;

        public Task<ProviderProbeResult> ProbeAsync(CancellationToken cancellationToken) =>
            Task.FromResult(new ProviderProbeResult(true, "1", "Fake is fine."));

        public void Dispose()
        {
        }

        private sealed class Client(FakeLocalProvider owner) : IChatClient
        {
            public Task<ChatResponse> GetResponseAsync(IEnumerable<AiChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) =>
                throw new NotSupportedException();

            public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
                IEnumerable<AiChatMessage> messages,
                ChatOptions? options = null,
                [EnumeratorCancellation] CancellationToken cancellationToken = default)
            {
                owner.Started.TrySetResult();
                if (owner.Hang)
                {
                    try
                    {
                        await Task.Delay(Timeout.Infinite, cancellationToken);
                    }
                    finally
                    {
                        owner.Cancelled.TrySetResult(cancellationToken.IsCancellationRequested);
                    }
                }

                var last = messages.Last().Text;
                foreach (var piece in new[] { "echo", ": ", last })
                {
                    await Task.Yield();
                    yield return new ChatResponseUpdate(AiChatRole.Assistant, piece);
                }
            }

            public object? GetService(Type serviceType, object? serviceKey = null) => null;

            public void Dispose()
            {
            }
        }
    }
}

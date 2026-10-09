using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Pillion.Common;
using Pillion.Common.Secrets;
using Pillion.Common.Settings;
using Pillion.Contracts;
using Pillion.Host.InlineEdit;
using Pillion.Host.Tests.TestSupport;
using Pillion.ModelGateway;
using StreamJsonRpc;
using static Pillion.Host.Tests.InlineEdit.InlineEditTestSupport;
using AiChatMessage = Microsoft.Extensions.AI.ChatMessage;
using AiChatRole = Microsoft.Extensions.AI.ChatRole;

namespace Pillion.Host.Tests.InlineEdit;

/// <summary>The inline edit service through the real pipe, the real Model Gateway and a fake chat provider.</summary>
public sealed partial class InlineEditRpcTests : IDisposable
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(30);

    private readonly TempDirectory _data = new();
    private readonly FakeFlags _flags = new();

    public void Dispose() => _data.Dispose();

    [Fact]
    public async Task EditAsync_StreamsTheProposal_AndRecordsAnInlineEditRequest()
    {
        var provider = new FakeChatProvider(isLocal: true, _ => ExtractReply);
        await using var host = await StartAsync(provider);
        await using var client = await host.ConnectAndHandshakeAsync();
        var service = client.Attach<IInlineEditService>();

        var chunks = await CollectAsync(service.EditAsync(Request(), CancellationToken.None), Wait);

        Assert.Equal(Extracted, Assemble(chunks));
        Assert.True(chunks[^1].IsFinal);
        Assert.Equal("fake-coder", chunks[^1].ModelId);
        var record = Assert.Single(host.Services.GetRequiredService<IOutboundLog>().GetRecent());
        Assert.Equal(TaskClass.InlineEdit, record.TaskClass);
        Assert.Equal(OutboundOutcome.Succeeded, record.Outcome);
        Assert.True(record.IsLocal);
        Assert.Equal(0, record.RedactionCount);

        // The prompt went out as the inline edit template, with the scope numbered from its document line.
        var sent = Assert.Single(provider.Received);
        Assert.Contains("<<<<<<< SEARCH", sent[0].Text, StringComparison.Ordinal);
        Assert.Contains(" 7|     public void Place(Order order)", sent[1].Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ACloudModel_SeesPlaceholders_AndTheProposalGetsTheSecretBack()
    {
        var key = AwsKey();
        var provider = new FakeChatProvider(isLocal: false, messages =>
        {
            // Echo the placeholder the model was shown, as a well-behaved model would.
            var placeholder = Placeholder().Match(messages[^1].Text).Value;
            return $"<<<<<<< SEARCH\n    private const string Key = \"{placeholder}\";\n=======\n    private static readonly string Key = \"{placeholder}\";\n>>>>>>> REPLACE\n";
        });
        await using var host = await StartAsync(provider);
        await using var client = await host.ConnectAndHandshakeAsync();
        var service = client.Attach<IInlineEditService>();

        var chunks = await CollectAsync(service.EditAsync(SecretRequest(key), CancellationToken.None), Wait);

        Assert.Equal($"    private static readonly string Key = \"{key}\";\n    public string GetKey() => Key;\n", Assemble(chunks));
        var sent = Assert.Single(provider.Received);
        Assert.DoesNotContain(sent, m => m.Text.Contains(key, StringComparison.Ordinal));
        Assert.Contains("<redacted:aws-access-key>", sent[^1].Text, StringComparison.Ordinal);
        var record = Assert.Single(host.Services.GetRequiredService<IOutboundLog>().GetRecent());
        Assert.False(record.IsLocal);
        Assert.Equal(TaskClass.InlineEdit, record.TaskClass);
        Assert.True(record.RedactionCount >= 1);
    }

    [Fact]
    public async Task AnInventedPlaceholder_IsSentBack_ThenTheEditIsRejected()
    {
        var key = AwsKey();
        var provider = new FakeChatProvider(isLocal: false, _ =>
            "<<<<<<< SEARCH\n    public string GetKey() => Key;\n=======\n    public string GetKey() => \"<redacted:aws-access-key#7>\";\n>>>>>>> REPLACE\n");
        await using var host = await StartAsync(provider);
        await using var client = await host.ConnectAndHandshakeAsync();
        var service = client.Attach<IInlineEditService>();

        var ex = await Assert.ThrowsAsync<RemoteInvocationException>(() => CollectAsync(service.EditAsync(SecretRequest(key), CancellationToken.None), Wait));

        var error = Assert.IsType<PillionError>(ex.DeserializedErrorData);
        Assert.Equal(PillionErrorCode.PolicyDenied, error.Code);
        Assert.Contains("<redacted:aws-access-key#7>", error.Message, StringComparison.Ordinal);
        Assert.Contains("Nothing was changed", error.Message, StringComparison.Ordinal);
        Assert.Equal(InlineEditEngine.MaxRetries + 1, provider.Received.Count);
        Assert.Contains("does not stand for any secret", provider.Received.Last()[^1].Text, StringComparison.Ordinal);
        Assert.All(host.Services.GetRequiredService<IOutboundLog>().GetRecent(), r => Assert.Equal(TaskClass.InlineEdit, r.TaskClass));
    }

    [Fact]
    public async Task AnInventedPlaceholder_ThatTheModelCorrects_IsAccepted()
    {
        var key = AwsKey();
        var replies = new ConcurrentQueue<string>([
            "<<<<<<< SEARCH\n    public string GetKey() => Key;\n=======\n    public string GetKey() => \"<redacted:aws-access-key#7>\";\n>>>>>>> REPLACE\n",
            "<<<<<<< SEARCH\n    public string GetKey() => Key;\n=======\n    public string GetKey() => Key.Trim();\n>>>>>>> REPLACE\n",
        ]);
        var provider = new FakeChatProvider(isLocal: false, _ => replies.TryDequeue(out var r) ? r : throw new InvalidOperationException());
        await using var host = await StartAsync(provider);
        await using var client = await host.ConnectAndHandshakeAsync();
        var service = client.Attach<IInlineEditService>();

        var chunks = await CollectAsync(service.EditAsync(SecretRequest(key), CancellationToken.None), Wait);

        Assert.Equal($"    private const string Key = \"{key}\";\n    public string GetKey() => Key.Trim();\n", Assemble(chunks));
        Assert.Equal(2, provider.Received.Count);
    }

    [Theory]
    [InlineData(FeatureNames.InlineChat, "feature inlineChat")]
    [InlineData(FeatureNames.ModelGateway, "feature modelGateway")]
    public async Task EditAsync_WithAFlagOff_FailsWithPolicyDenied(string flag, string named)
    {
        _flags.Disabled.Add(flag);
        var provider = new FakeChatProvider(isLocal: true, _ => ExtractReply);
        await using var host = await StartAsync(provider);
        await using var client = await host.ConnectAndHandshakeAsync();
        var service = client.Attach<IInlineEditService>();

        var ex = await Assert.ThrowsAsync<RemoteInvocationException>(() => CollectAsync(service.EditAsync(Request(), CancellationToken.None), Wait));

        var error = Assert.IsType<PillionError>(ex.DeserializedErrorData);
        Assert.Equal(PillionErrorCode.PolicyDenied, error.Code);
        Assert.Contains(named, error.Message, StringComparison.Ordinal);
        Assert.Empty(provider.Received);

        _flags.Disabled.Clear();
        Assert.Equal(Extracted, Assemble(await CollectAsync(service.EditAsync(Request(), CancellationToken.None), Wait)));
    }

    [Fact]
    public async Task CancelEditAsync_StopsAnEditInFlight()
    {
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var provider = new FakeChatProvider(isLocal: true, _ => ExtractReply) { Gate = release.Task };
        await using var host = await StartAsync(provider);
        await using var client = await host.ConnectAndHandshakeAsync();
        var service = client.Attach<IInlineEditService>();
        var request = Request();

        var run = CollectAsync(service.EditAsync(request, CancellationToken.None), Wait);
        await provider.Started.Task.WaitAsync(Wait);
        await service.CancelEditAsync(request.RequestId, CancellationToken.None);

        await Assert.ThrowsAnyAsync<Exception>(() => run);
        Assert.Equal(0, host.Services.GetRequiredService<InlineEditRpcService>().InFlightCount);
        release.SetResult();
    }

    /// <summary>A fabricated AWS access key id, built at run time so no secret-shaped literal is committed.</summary>
    private static string AwsKey()
    {
        const string Base32 = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";
        var rng = new Random(1234);
        return "AK" + "IA" + new string(Enumerable.Range(0, 16).Select(_ => Base32[rng.Next(Base32.Length)]).ToArray());
    }

    private static InlineEditRequest SecretRequest(string key) => new()
    {
        RequestId = "secret-" + Guid.NewGuid().ToString("N")[..8],
        FilePath = @"C:\src\Demo\Config.cs",
        LanguageId = "csharp",
        TextBefore = "public class Config\n{\n",
        ScopeText = $"    private const string Key = \"{key}\";\n    public string GetKey() => Key;\n",
        ScopeStartLine = 2,
        TextAfter = "}\n",
        Instruction = "make the key static readonly",
    };

    private Task<InProcessHost> StartAsync(FakeChatProvider provider) => InProcessHost.StartAsync(services =>
    {
        var settings = new PillionSettings();
        settings.Providers.Add(new ProviderSettings { Id = "fake", Kind = ProviderKind.Mistral, Models = ["fake-coder"] });
        settings.Routing.Profiles[provider.IsLocal ? Profile.LocalStrong : Profile.Balanced] = "fake/fake-coder";

        services.Replace(ServiceDescriptor.Singleton(new PillionPaths(_data.Path)));
        services.Replace(ServiceDescriptor.Singleton<ISecretStore>(new InMemorySecretStore()));
        services.Replace(ServiceDescriptor.Singleton<IGatewaySettingsSource>(new StaticGatewaySettingsSource(settings)));
        services.Replace(ServiceDescriptor.Singleton<IFeatureFlags>(_flags));
        services.AddSingleton<IModelProviderFactory>(new FakeFactory(provider));
    });

    [GeneratedRegex(@"<redacted:[^<>\r\n]{0,80}>")]
    private static partial Regex Placeholder();

    private sealed class FakeFlags : IFeatureFlags
    {
        public event EventHandler? Changed;

        public ConcurrentBag<string> Disabled { get; } = [];

        public bool IsEnabled(string feature) => !Disabled.Contains(feature);

        public void Raise() => Changed?.Invoke(this, EventArgs.Empty);
    }

    private sealed class FakeFactory(FakeChatProvider provider) : IModelProviderFactory
    {
        public ProviderKind Kind => ProviderKind.Mistral;

        public bool RequiresApiKey(ProviderSettings settings) => false;

        public IModelProvider Create(ProviderSettings settings, ProviderCreationContext context) => provider;
    }

    /// <summary>A chat provider whose model answers with a script and records what it was sent.</summary>
    private sealed class FakeChatProvider(bool isLocal, Func<IReadOnlyList<AiChatMessage>, string> reply) : IModelProvider
    {
        public ConcurrentQueue<IReadOnlyList<AiChatMessage>> Received { get; } = new();

        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task? Gate { get; init; }

        public string Id => "fake";

        public ProviderKind Kind => ProviderKind.Mistral;

        public string DisplayName => "Fake";

        public bool IsLocal => isLocal;

        public Task<IReadOnlyList<ProviderModel>> ListModelsAsync(CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<ProviderModel>>(
                [new ProviderModel("fake", "fake-coder", "Fake coder", new ModelCapabilityDescriptor { ContextWindow = 32768, Chat = true, IsLocal = isLocal })]);

        public IChatClient GetChatClient(string modelId) => new Client(this);

        public IFimClient? GetFimClient(string modelId) => null;

        public Task<ProviderProbeResult> ProbeAsync(CancellationToken cancellationToken) =>
            Task.FromResult(new ProviderProbeResult(true, "1", "Fake is fine."));

        public void Dispose()
        {
        }

        private sealed class Client(FakeChatProvider owner) : IChatClient
        {
            public Task<ChatResponse> GetResponseAsync(IEnumerable<AiChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) =>
                throw new NotSupportedException();

            public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
                IEnumerable<AiChatMessage> messages,
                ChatOptions? options = null,
                [EnumeratorCancellation] CancellationToken cancellationToken = default)
            {
                var list = messages.ToList();
                owner.Received.Enqueue(list);
                owner.Started.TrySetResult();
                var text = owner.Reply(list);
                if (owner.Gate is not null)
                {
                    await owner.Gate.WaitAsync(cancellationToken);
                }

                foreach (var piece in ScriptedGateway.Pieces(text))
                {
                    await Task.Yield();
                    cancellationToken.ThrowIfCancellationRequested();
                    yield return new ChatResponseUpdate(AiChatRole.Assistant, piece) { ModelId = "fake-coder" };
                }
            }

            public object? GetService(Type serviceType, object? serviceKey = null) => null;

            public void Dispose()
            {
            }
        }

        private string Reply(IReadOnlyList<AiChatMessage> messages) => reply(messages);
    }
}

using System.Collections.Concurrent;
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

/// <summary>The context service and chat with pins through the real pipe, with a fake provider behind the gateway.</summary>
public sealed class ContextRpcTests : IDisposable
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(10);

    private readonly TempDirectory _data = new();
    private readonly TempDirectory _workspace = new();
    private readonly RecordingProvider _provider = new();

    public void Dispose()
    {
        _data.Dispose();
        _workspace.Dispose();
    }

    [Fact]
    public async Task ResolveMentionAsync_ResolvesAFile_OverThePipe()
    {
        var program = WriteFile("src/Program.cs", "class Program { }\n");
        await using var host = await StartAsync();
        await using var client = await host.ConnectAndHandshakeAsync();
        var context = client.Attach<IContextService>();

        var result = await context.ResolveMentionAsync(
            new MentionRequest { Mention = "@Program.cs", WorkspaceRoots = [_workspace.Path], CandidateFiles = [program], MaxTokens = 1000 },
            CancellationToken.None).WaitAsync(Wait);

        Assert.True(result.IsResolved, result.Reason);
        Assert.Equal("@Program.cs", result.Mention);
        Assert.Equal("file", result.Kind);
        Assert.Equal("Program.cs", result.DisplayName);
        Assert.Equal(program, result.FilePath);
        Assert.Equal("class Program { }\n", result.Content);
        Assert.True(result.TokenCount > 0);
        Assert.False(result.Truncated);
    }

    [Fact]
    public async Task ResolveMentionAsync_AmbiguousAndSecretAnswers_SurviveTheWire()
    {
        var a = WriteFile("src/A/Customer.cs", "class A { }");
        var b = WriteFile("src/B/Customer.cs", "class B { }");
        var env = WriteFile(".env", "KEY=1");
        await using var host = await StartAsync();
        await using var client = await host.ConnectAndHandshakeAsync();
        var context = client.Attach<IContextService>();

        var ambiguous = await context.ResolveMentionAsync(
            new MentionRequest { Mention = "@Customer.cs", WorkspaceRoots = [_workspace.Path], CandidateFiles = [a, b, env] },
            CancellationToken.None).WaitAsync(Wait);
        var secret = await context.ResolveMentionAsync(
            new MentionRequest { Mention = "@file:.env", WorkspaceRoots = [_workspace.Path], CandidateFiles = [a, b, env] },
            CancellationToken.None).WaitAsync(Wait);
        var approved = await context.ResolveMentionAsync(
            new MentionRequest { Mention = "@file:.env", WorkspaceRoots = [_workspace.Path], CandidateFiles = [a, b, env], ApprovedSensitive = true },
            CancellationToken.None).WaitAsync(Wait);

        Assert.False(ambiguous.IsResolved);
        Assert.Equal([a, b], ambiguous.Candidates.Order(StringComparer.Ordinal));
        Assert.False(secret.IsResolved);
        Assert.True(secret.RequiresApproval);
        Assert.True(approved.IsResolved);
        Assert.True(approved.IsSensitive);
        Assert.Equal("KEY=1", approved.Content);
    }

    [Fact]
    public async Task SelectionErrorsSearchAndIndexStatus_AllAnswerOverThePipe()
    {
        await using var host = await StartAsync();
        await using var client = await host.ConnectAndHandshakeAsync();
        var context = client.Attach<IContextService>();

        var selection = await context.ResolveMentionAsync(new MentionRequest { Mention = "@selection", Payload = "int x;" }, CancellationToken.None).WaitAsync(Wait);
        var errors = await context.ResolveMentionAsync(
            new MentionRequest { Mention = "@errors", Payload = """{"severity":"error","id":"CS1","message":"bad","file":"A.cs","line":3}""" },
            CancellationToken.None).WaitAsync(Wait);
        var hits = await context.SearchAsync(new SearchRequest { Query = "x", MaxResults = 3 }, CancellationToken.None).WaitAsync(Wait);
        var status = await context.GetIndexStatusAsync(CancellationToken.None).WaitAsync(Wait);
        await context.PauseIndexingAsync(true, CancellationToken.None).WaitAsync(Wait);

        Assert.Equal("int x;", selection.Content);
        Assert.Equal("error CS1: bad (A.cs:3)", errors.Content);
        Assert.Empty(hits);
        Assert.Equal(0, status.TotalFileCount);
    }

    [Fact]
    public async Task Chat_WithAResolvedPin_SendsItToTheModel_AndTheOutboundRecordCountsTheContext()
    {
        var program = WriteFile("src/Calc.cs", "public static class Calc { public static int Add(int a, int b) => a + b; }\n");
        await using var host = await StartAsync();
        await using var client = await host.ConnectAndHandshakeAsync();
        var context = client.Attach<IContextService>();
        var models = client.Attach<IModelService>();
        var pin = await context.ResolveMentionAsync(
            new MentionRequest { Mention = "@Calc.cs", WorkspaceRoots = [_workspace.Path], CandidateFiles = [program] },
            CancellationToken.None).WaitAsync(Wait);

        var chunks = new List<ChatChunk>();
        var request = new ChatRequest
        {
            RequestId = "chat-1",
            Profile = Profile.Auto,
            ConversationId = "conv",
            Pins = [pin],
            CurrentFile = program,
            CurrentFileRegion = "Calc.Add(1, 2)",
            ProjectRules = "Prefer expression-bodied members.",
            Messages = [new Contracts.ChatMessage { Role = Contracts.ChatRole.User, Content = "What does Add do?" }],
        };
        await foreach (var chunk in models.ChatAsync(request, CancellationToken.None).WithCancellation(CancellationToken.None))
        {
            chunks.Add(chunk);
        }

        Assert.Equal("seen", string.Concat(chunks.Select(c => c.Text)));
        var messages = Assert.Single(_provider.Received);
        Assert.Equal(2, messages.Count);
        var system = messages[0].Text;
        Assert.Contains("public static class Calc", system, StringComparison.Ordinal);
        Assert.Contains("Prefer expression-bodied members.", system, StringComparison.Ordinal);
        Assert.Contains("Calc.Add(1, 2)", system, StringComparison.Ordinal);
        Assert.Equal("What does Add do?", messages[1].Text);

        var record = Assert.Single(host.Services.GetRequiredService<IOutboundLog>().GetRecent());
        Assert.Equal(TaskClass.Chat, record.TaskClass);
        Assert.Equal(5, record.ContextItems); // System prompt, rules, the pin, the current file, the message.
        Assert.Equal(0, record.ContextTrimmed);
        Assert.True(record.EstimatedPromptTokens >= pin.TokenCount);
        Assert.True(chunks[^1].PromptTokens >= pin.TokenCount);
    }

    [Fact]
    public async Task Chat_WithoutContext_StillBehavesAsBefore()
    {
        await using var host = await StartAsync();
        await using var client = await host.ConnectAndHandshakeAsync();
        var models = client.Attach<IModelService>();

        await foreach (var chunk in models.ChatAsync(
            new ChatRequest { RequestId = "plain", Profile = Profile.Auto, Messages = [new Contracts.ChatMessage { Role = Contracts.ChatRole.User, Content = "Hi" }] },
            CancellationToken.None).WithCancellation(CancellationToken.None))
        {
            _ = chunk;
        }

        var messages = Assert.Single(_provider.Received);
        Assert.Equal(PromptTemplates.Default.ChatSystemPrompt, messages[0].Text);
        Assert.Equal(0, Assert.Single(host.Services.GetRequiredService<IOutboundLog>().GetRecent()).ContextItems);
    }

    private string WriteFile(string relative, string content)
    {
        var path = Path.GetFullPath(Path.Combine(_workspace.Path, relative));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
        return path;
    }

    private Task<InProcessHost> StartAsync() => InProcessHost.StartAsync(services =>
    {
        var settings = new PillionSettings();
        settings.Providers.Add(new ProviderSettings { Id = "fake", Kind = ProviderKind.Mistral, Models = ["fake-chat:1b"] });

        services.Replace(ServiceDescriptor.Singleton(new PillionPaths(_data.Path)));
        services.Replace(ServiceDescriptor.Singleton<ISecretStore>(new InMemorySecretStore()));
        services.Replace(ServiceDescriptor.Singleton<IGatewaySettingsSource>(new StaticGatewaySettingsSource(settings)));
        services.AddSingleton<IModelProviderFactory>(new FakeFactory(_provider));
    });

    private sealed class FakeFactory(RecordingProvider provider) : IModelProviderFactory
    {
        public ProviderKind Kind => ProviderKind.Mistral;

        public bool RequiresApiKey(ProviderSettings settings) => false;

        public IModelProvider Create(ProviderSettings settings, ProviderCreationContext context) => provider;
    }

    private sealed class RecordingProvider : IModelProvider
    {
        public ConcurrentQueue<List<AiChatMessage>> Received { get; } = new();

        public string Id => "fake";

        public ProviderKind Kind => ProviderKind.Mistral;

        public string DisplayName => "Fake";

        public bool IsLocal => true;

        public Task<IReadOnlyList<ProviderModel>> ListModelsAsync(CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<ProviderModel>>(
                [new ProviderModel("fake", "fake-chat:1b", "Fake chat", new ModelCapabilityDescriptor { ContextWindow = 16384, IsLocal = true }, "sha256:abc")]);

        public IChatClient GetChatClient(string modelId) => new Client(this);

        public IFimClient? GetFimClient(string modelId) => null;

        public Task<ProviderProbeResult> ProbeAsync(CancellationToken cancellationToken) =>
            Task.FromResult(new ProviderProbeResult(true, "1", "Fake is fine."));

        public void Dispose()
        {
        }

        private sealed class Client(RecordingProvider owner) : IChatClient
        {
            public Task<ChatResponse> GetResponseAsync(IEnumerable<AiChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) =>
                throw new NotSupportedException();

            public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
                IEnumerable<AiChatMessage> messages,
                ChatOptions? options = null,
                [EnumeratorCancellation] CancellationToken cancellationToken = default)
            {
                owner.Received.Enqueue([.. messages]);
                foreach (var piece in new[] { "se", "en" })
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

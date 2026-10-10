using System.Reflection;
using System.Runtime.Serialization;
using MessagePack;
using MessagePack.Resolvers;

namespace Pillion.Contracts.Tests;

/// <summary>
/// Contract 1.1: the fields and the enum it added, and proof that 1.0 and 1.1 peers still understand each other
/// (an older peer ignores the appended fields; a newer peer reads the defaults when they are missing).
/// </summary>
public class ContractV11Tests
{
    private static readonly MessagePackSerializerOptions Options =
        MessagePackSerializerOptions.Standard.WithResolver(StandardResolver.Instance);

    /// <summary>Every member added in 1.1, with the order it was given. Changing any of these is a major change.</summary>
    public static TheoryData<Type, string, int> AddedIn11 => new()
    {
        { typeof(ChatChunk), nameof(ChatChunk.ModelId), 3 },
        { typeof(ChatChunk), nameof(ChatChunk.ProviderId), 4 },
        { typeof(ChatChunk), nameof(ChatChunk.PromptTokens), 5 },
        { typeof(ChatChunk), nameof(ChatChunk.CompletionTokens), 6 },
        { typeof(ChatChunk), nameof(ChatChunk.Error), 7 },
        { typeof(CompletionChunk), nameof(CompletionChunk.ModelId), 3 },
        { typeof(CompletionChunk), nameof(CompletionChunk.ProviderId), 4 },
        { typeof(CompletionChunk), nameof(CompletionChunk.PromptTokens), 5 },
        { typeof(CompletionChunk), nameof(CompletionChunk.CompletionTokens), 6 },
        { typeof(CompletionChunk), nameof(CompletionChunk.Error), 7 },
        { typeof(ModelDescriptor), nameof(ModelDescriptor.ModelId), 5 },
        { typeof(ModelDescriptor), nameof(ModelDescriptor.IsAvailable), 6 },
        { typeof(ModelDescriptor), nameof(ModelDescriptor.ContextWindow), 7 },
        { typeof(ModelDescriptor), nameof(ModelDescriptor.Digest), 8 },
    };

    [Fact]
    public void Version_IsAtLeastOnePointOne()
    {
        // The version itself is pinned by the newest ContractV*Tests class; 1.1 only needs to still be covered.
        Assert.Equal(1, ContractVersion.Major);
        Assert.True(ContractVersion.Minor >= 1);
    }

    [Fact]
    public void AOnePointZeroClient_IsStillAccepted()
    {
        // The handshake accepts any minor of the same major; the host test HandshakeCompatibilityTests sends one.
        Assert.True(ContractVersion.IsCompatible(1, 0));
    }

    [Theory]
    [MemberData(nameof(AddedIn11))]
    public void Field_HasItsOneOneOrder(Type type, string property, int order)
    {
        var member = type.GetProperty(property)?.GetCustomAttribute<DataMemberAttribute>();

        Assert.NotNull(member);
        Assert.Equal(order, member.Order);
    }

    [Fact]
    public void ConnectivityState_HasItsWireValues()
    {
        Assert.Equal(
            [("Online", 0), ("Degraded", 1), ("Offline", 2), ("LocalOnly", 3)],
            Enum.GetValues<ConnectivityState>().Select(s => (s.ToString(), (int)s)));

        foreach (var state in Enum.GetValues<ConnectivityState>())
        {
            var bytes = MessagePackSerializer.Serialize(state, Options);
            Assert.Equal((int)state, MessagePackSerializer.Deserialize<int>(bytes, Options));
            Assert.Equal(state, MessagePackSerializer.Deserialize<ConnectivityState>(bytes, Options));
        }
    }

    [Fact]
    public void ChatChunk_RoundTripsTheNewFields()
    {
        var chunk = new ChatChunk
        {
            RequestId = "r",
            Text = "po",
            ModelId = "gemma4:e2b",
            ProviderId = "ollama",
            PromptTokens = 12,
            CompletionTokens = 2,
            IsFinal = true,
            Error = new PillionError { Code = PillionErrorCode.ProviderUnavailable, Message = "cut off" },
        };

        var copy = RoundTrip(chunk);

        Assert.Equal("gemma4:e2b", copy.ModelId);
        Assert.Equal("ollama", copy.ProviderId);
        Assert.Equal(12, copy.PromptTokens);
        Assert.Equal(2, copy.CompletionTokens);
        Assert.Equal(PillionErrorCode.ProviderUnavailable, copy.Error?.Code);
        Assert.Equal("cut off", copy.Error?.Message);
    }

    [Fact]
    public void CompletionChunk_RoundTripsTheNewFields()
    {
        var copy = RoundTrip(new CompletionChunk { RequestId = "r", Text = "x", ModelId = "m", ProviderId = "p", PromptTokens = 7, CompletionTokens = 1 });

        Assert.Equal("m", copy.ModelId);
        Assert.Equal("p", copy.ProviderId);
        Assert.Equal(7, copy.PromptTokens);
        Assert.Equal(1, copy.CompletionTokens);
        Assert.Null(copy.Error);
    }

    [Fact]
    public void ModelDescriptor_RoundTripsTheNewFields()
    {
        var copy = RoundTrip(new ModelDescriptor
        {
            Id = "ollama/gemma4:e2b",
            ModelId = "gemma4:e2b",
            ProviderId = "ollama",
            IsAvailable = true,
            ContextWindow = 131072,
            Digest = "sha256:abc",
        });

        Assert.Equal("gemma4:e2b", copy.ModelId);
        Assert.True(copy.IsAvailable);
        Assert.Equal(131072, copy.ContextWindow);
        Assert.Equal("sha256:abc", copy.Digest);
    }

    [Fact]
    public void AOnePointZeroChatChunk_ReadsWithDefaultsForTheNewFields()
    {
        var bytes = MessagePackSerializer.Serialize(new ChatChunkV10 { RequestId = "r", Text = "hi", IsFinal = true }, Options);

        var chunk = MessagePackSerializer.Deserialize<ChatChunk>(bytes, Options);

        Assert.Equal("r", chunk.RequestId);
        Assert.Equal("hi", chunk.Text);
        Assert.True(chunk.IsFinal);
        Assert.Equal(string.Empty, chunk.ModelId);
        Assert.Equal(string.Empty, chunk.ProviderId);
        Assert.Equal(0, chunk.PromptTokens);
        Assert.Null(chunk.Error);
    }

    [Fact]
    public void AOnePointOneChatChunk_IsReadByAOnePointZeroPeer()
    {
        var bytes = MessagePackSerializer.Serialize(
            new ChatChunk { RequestId = "r", Text = "hi", IsFinal = true, ModelId = "m", ProviderId = "p", Error = new PillionError { Message = "e" } },
            Options);

        var old = MessagePackSerializer.Deserialize<ChatChunkV10>(bytes, Options);

        Assert.Equal("r", old.RequestId);
        Assert.Equal("hi", old.Text);
        Assert.True(old.IsFinal);
    }

    [Fact]
    public void ModelDescriptors_CrossBetweenOnePointZeroAndOnePointOne()
    {
        var newer = new ModelDescriptor { Id = "ollama/m", DisplayName = "M", ProviderId = "ollama", IsLocal = true, Capabilities = ModelCapabilities.Chat, ModelId = "m", IsAvailable = true };
        var old = MessagePackSerializer.Deserialize<ModelDescriptorV10>(MessagePackSerializer.Serialize(newer, Options), Options);
        Assert.Equal("ollama/m", old.Id);
        Assert.Equal(ModelCapabilities.Chat, old.Capabilities);

        var fromOld = MessagePackSerializer.Deserialize<ModelDescriptor>(MessagePackSerializer.Serialize(old, Options), Options);
        Assert.Equal("ollama/m", fromOld.Id);
        Assert.Equal(string.Empty, fromOld.ModelId);
        Assert.False(fromOld.IsAvailable);
        Assert.Null(fromOld.Digest);
    }

    private static T RoundTrip<T>(T value) =>
        MessagePackSerializer.Deserialize<T>(MessagePackSerializer.Serialize(value, Options), Options);

    /// <summary><see cref="ChatChunk"/> as contract 1.0 defined it.</summary>
    [DataContract]
    public sealed class ChatChunkV10
    {
        [DataMember(Order = 0)]
        public string RequestId { get; set; } = string.Empty;

        [DataMember(Order = 1)]
        public string Text { get; set; } = string.Empty;

        [DataMember(Order = 2)]
        public bool IsFinal { get; set; }
    }

    /// <summary><see cref="ModelDescriptor"/> as contract 1.0 defined it.</summary>
    [DataContract]
    public sealed class ModelDescriptorV10
    {
        [DataMember(Order = 0)]
        public string Id { get; set; } = string.Empty;

        [DataMember(Order = 1)]
        public string DisplayName { get; set; } = string.Empty;

        [DataMember(Order = 2)]
        public string ProviderId { get; set; } = string.Empty;

        [DataMember(Order = 3)]
        public bool IsLocal { get; set; }

        [DataMember(Order = 4)]
        public ModelCapabilities Capabilities { get; set; }
    }
}

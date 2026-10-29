using System.Reflection;
using System.Runtime.Serialization;
using MessagePack;
using MessagePack.Resolvers;

namespace Pillion.Contracts.Tests;

/// <summary>
/// The mention and context-assembly additions of contract 1.2 (work item 1.10): the orders they were given, and proof
/// that a peer that does not know them still reads and writes the older shape.
/// </summary>
public class ContextContractTests
{
    private static readonly MessagePackSerializerOptions Options =
        MessagePackSerializerOptions.Standard.WithResolver(StandardResolver.Instance);

    /// <summary>Every member added for mentions and context assembly, with the order it was given.</summary>
    public static TheoryData<Type, string, int> Added => new()
    {
        { typeof(MentionRequest), nameof(MentionRequest.Kind), 1 },
        { typeof(MentionRequest), nameof(MentionRequest.WorkspaceRoots), 2 },
        { typeof(MentionRequest), nameof(MentionRequest.CandidateFiles), 3 },
        { typeof(MentionRequest), nameof(MentionRequest.Payload), 4 },
        { typeof(MentionRequest), nameof(MentionRequest.MaxTokens), 5 },
        { typeof(MentionRequest), nameof(MentionRequest.ApprovedSensitive), 6 },
        { typeof(MentionResolution), nameof(MentionResolution.Kind), 4 },
        { typeof(MentionResolution), nameof(MentionResolution.DisplayName), 5 },
        { typeof(MentionResolution), nameof(MentionResolution.TokenCount), 6 },
        { typeof(MentionResolution), nameof(MentionResolution.Truncated), 7 },
        { typeof(MentionResolution), nameof(MentionResolution.Candidates), 8 },
        { typeof(MentionResolution), nameof(MentionResolution.Reason), 9 },
        { typeof(MentionResolution), nameof(MentionResolution.RequiresApproval), 10 },
        { typeof(MentionResolution), nameof(MentionResolution.IsSensitive), 11 },
        { typeof(ChatRequest), nameof(ChatRequest.Pins), 4 },
        { typeof(ChatRequest), nameof(ChatRequest.CurrentFile), 5 },
        { typeof(ChatRequest), nameof(ChatRequest.CurrentFileRegion), 6 },
        { typeof(ChatRequest), nameof(ChatRequest.ConversationId), 7 },
        { typeof(ChatRequest), nameof(ChatRequest.ProjectRules), 8 },
    };

    [Theory]
    [MemberData(nameof(Added))]
    public void Field_HasItsOrder(Type type, string property, int order)
    {
        var member = type.GetProperty(property)?.GetCustomAttribute<DataMemberAttribute>();

        Assert.NotNull(member);
        Assert.Equal(order, member.Order);
    }

    [Fact]
    public void Summarise_KeepsTheOlderWireValues()
    {
        // Summarise was the last task class until contract 1.4 appended AgentPlanning (ContractV14Tests pins that one).
        Assert.Equal(6, (int)TaskClass.Summarise);
        Assert.Equal(
            [("Autocomplete", 0), ("InlineEdit", 1), ("Chat", 2), ("Agent", 3), ("Embedding", 4), ("Vision", 5), ("Summarise", 6)],
            Enum.GetValues<TaskClass>().Take(7).Select(t => (t.ToString(), (int)t)));
    }

    [Fact]
    public void MentionResolution_RoundTripsTheNewFields()
    {
        var copy = RoundTrip(new MentionResolution
        {
            Mention = "@Customer",
            Kind = "file",
            DisplayName = "Customer.cs",
            TokenCount = 42,
            Truncated = true,
            Candidates = ["a", "b"],
            Reason = "Two files match.",
            RequiresApproval = true,
            IsSensitive = true,
        });

        Assert.Equal("file", copy.Kind);
        Assert.Equal("Customer.cs", copy.DisplayName);
        Assert.Equal(42, copy.TokenCount);
        Assert.True(copy.Truncated);
        Assert.Equal(["a", "b"], copy.Candidates);
        Assert.Equal("Two files match.", copy.Reason);
        Assert.True(copy.RequiresApproval);
        Assert.True(copy.IsSensitive);
    }

    [Fact]
    public void AnOlderChatRequest_ReadsWithEmptyContext()
    {
        var bytes = MessagePackSerializer.Serialize(
            new ChatRequestOlder { RequestId = "r", Messages = [new ChatMessage { Role = ChatRole.User, Content = "hi" }], Profile = Profile.Balanced },
            Options);

        var request = MessagePackSerializer.Deserialize<ChatRequest>(bytes, Options);

        Assert.Equal("r", request.RequestId);
        Assert.Single(request.Messages);
        Assert.Empty(request.Pins);
        Assert.Equal(string.Empty, request.CurrentFile);
        Assert.Null(request.CurrentFileRegion);
        Assert.Equal(string.Empty, request.ConversationId);
        Assert.Null(request.ProjectRules);
    }

    [Fact]
    public void ANewerChatRequest_IsReadByAnOlderPeer()
    {
        var newer = new ChatRequest
        {
            RequestId = "r",
            Messages = [new ChatMessage { Role = ChatRole.User, Content = "hi" }],
            Profile = Profile.Fast,
            Pins = [new MentionResolution { Mention = "@file:a", IsResolved = true, Content = "x" }],
            CurrentFile = "a.cs",
            ConversationId = "c",
        };

        var old = MessagePackSerializer.Deserialize<ChatRequestOlder>(MessagePackSerializer.Serialize(newer, Options), Options);

        Assert.Equal("r", old.RequestId);
        Assert.Equal(Profile.Fast, old.Profile);
    }

    [Fact]
    public void AnOlderMentionRequest_ReadsWithDefaults_AndANewerOneIsReadByTheOlderPeer()
    {
        var fromOld = MessagePackSerializer.Deserialize<MentionRequest>(
            MessagePackSerializer.Serialize(new MentionRequestOlder { Mention = "@file:a" }, Options),
            Options);
        Assert.Equal("@file:a", fromOld.Mention);
        Assert.Equal(string.Empty, fromOld.Kind);
        Assert.Empty(fromOld.CandidateFiles);
        Assert.Null(fromOld.Payload);
        Assert.False(fromOld.ApprovedSensitive);

        var old = MessagePackSerializer.Deserialize<MentionRequestOlder>(
            MessagePackSerializer.Serialize(new MentionRequest { Mention = "@selection", Kind = "selection", Payload = "x" }, Options),
            Options);
        Assert.Equal("@selection", old.Mention);
    }

    private static T RoundTrip<T>(T value) =>
        MessagePackSerializer.Deserialize<T>(MessagePackSerializer.Serialize(value, Options), Options);

    /// <summary><see cref="ChatRequest"/> as contract 1.1 defined it.</summary>
    [DataContract]
    public sealed class ChatRequestOlder
    {
        [DataMember(Order = 0)]
        public string RequestId { get; set; } = string.Empty;

        [DataMember(Order = 1)]
        public IReadOnlyList<ChatMessage> Messages { get; set; } = [];

        [DataMember(Order = 2)]
        public string? ModelId { get; set; }

        [DataMember(Order = 3)]
        public Profile Profile { get; set; }
    }

    /// <summary><see cref="MentionRequest"/> as contract 1.1 defined it.</summary>
    [DataContract]
    public sealed class MentionRequestOlder
    {
        [DataMember(Order = 0)]
        public string Mention { get; set; } = string.Empty;
    }
}

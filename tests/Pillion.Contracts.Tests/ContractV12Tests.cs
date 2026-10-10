using System.Reflection;
using System.Runtime.Serialization;
using MessagePack;
using MessagePack.Resolvers;

namespace Pillion.Contracts.Tests;

/// <summary>
/// Contract 1.2: the version, the members whose order is not pinned by the more specific test classes
/// (<see cref="ContextContractTests"/> for mentions, pins and context, <c>ContractV12InlineEditTests</c> for inline edits),
/// and proof that 1.0, 1.1 and 1.2 peers still understand each other.
/// </summary>
public class ContractV12Tests
{
    private static readonly MessagePackSerializerOptions Options =
        MessagePackSerializerOptions.Standard.WithResolver(StandardResolver.Instance);

    /// <summary>Members added in 1.2 by the completion request and the diagnostics counters, with their orders.</summary>
    public static TheoryData<Type, string, int> AddedIn12 => new()
    {
        { typeof(CompletionRequest), nameof(CompletionRequest.CaretLine), 7 },
        { typeof(CompletionRequest), nameof(CompletionRequest.CaretColumn), 8 },
        { typeof(CompletionRequest), nameof(CompletionRequest.EnclosingDeclaration), 9 },
        { typeof(CompletionRequest), nameof(CompletionRequest.OpenDocuments), 10 },
        { typeof(CompletionRequest), nameof(CompletionRequest.RecentEdits), 11 },
        { typeof(CompletionRequest), nameof(CompletionRequest.AllowMultiLine), 12 },
        { typeof(CompletionRequest), nameof(CompletionRequest.StopSequences), 13 },
        { typeof(AutocompleteHostCounters), nameof(AutocompleteHostCounters.ResidentModels), 21 },
        { typeof(ResidentModelInfo), nameof(ResidentModelInfo.ProviderId), 0 },
        { typeof(ResidentModelInfo), nameof(ResidentModelInfo.ModelId), 1 },
        { typeof(ResidentModelInfo), nameof(ResidentModelInfo.ContextLength), 2 },
        { typeof(ResidentModelInfo), nameof(ResidentModelInfo.VramBytes), 3 },
        { typeof(ResidentModelInfo), nameof(ResidentModelInfo.KeepAlive), 4 },
        { typeof(ResidentModelInfo), nameof(ResidentModelInfo.IsPinned), 5 },
    };

    [Fact]
    public void Version_IsOnePointTwo()
    {
        Assert.Equal(1, ContractVersion.Major);
        Assert.Equal(2, ContractVersion.Minor);
        Assert.Equal("1.2", ContractVersion.Text);
    }

    [Theory]
    [InlineData(1, 0)]
    [InlineData(1, 1)]
    [InlineData(1, 2)]
    [InlineData(1, 3)]
    public void PeersOfTheSameMajor_AreCompatible_WhateverTheirMinor(int major, int minor)
    {
        Assert.True(ContractVersion.IsCompatible(major, minor));
    }

    [Theory]
    [InlineData(0, 2)]
    [InlineData(2, 0)]
    public void PeersOfAnotherMajor_AreNotCompatible(int major, int minor)
    {
        Assert.False(ContractVersion.IsCompatible(major, minor));
    }

    [Theory]
    [MemberData(nameof(AddedIn12))]
    public void Member_HasItsOnePointTwoOrder(Type type, string property, int order)
    {
        var member = type.GetProperty(property)?.GetCustomAttribute<DataMemberAttribute>();

        Assert.NotNull(member);
        Assert.Equal(order, member.Order);
    }

    [Fact]
    public void AOnePointOneCompletionRequest_ReadsWithDefaultsForTheContextFields()
    {
        var bytes = MessagePackSerializer.Serialize(
            new CompletionRequestV11 { RequestId = "r", Prefix = "a", Suffix = "b", LanguageId = "csharp", FilePath = @"C:\a.cs", MaxTokens = 64, TaskClass = TaskClass.Autocomplete },
            Options);

        var request = MessagePackSerializer.Deserialize<CompletionRequest>(bytes, Options);

        Assert.Equal("r", request.RequestId);
        Assert.Equal(64, request.MaxTokens);
        Assert.Equal(0, request.CaretLine);
        Assert.Equal(0, request.CaretColumn);
        Assert.Null(request.EnclosingDeclaration);
        Assert.Empty(request.OpenDocuments);
        Assert.Empty(request.RecentEdits);
        Assert.False(request.AllowMultiLine);
        Assert.Empty(request.StopSequences);
    }

    [Fact]
    public void AOnePointTwoCompletionRequest_IsReadByAOnePointOnePeer()
    {
        var bytes = MessagePackSerializer.Serialize(
            new CompletionRequest { RequestId = "r", Prefix = "a", CaretLine = 4, CaretColumn = 2, EnclosingDeclaration = "void M()", OpenDocuments = [@"C:\a.cs"], AllowMultiLine = true, StopSequences = ["}"] },
            Options);

        var old = MessagePackSerializer.Deserialize<CompletionRequestV11>(bytes, Options);

        Assert.Equal("r", old.RequestId);
        Assert.Equal("a", old.Prefix);
    }

    [Fact]
    public void ACountersReplyFromBeforeResidentModels_ReadsWithNone_AndANewerReplyIsReadByAnOlderReader()
    {
        var older = MessagePackSerializer.Serialize(new AutocompleteHostCountersV12Early { Requests = 7, HitRate = 0.5 }, Options);
        var counters = MessagePackSerializer.Deserialize<AutocompleteHostCounters>(older, Options);
        Assert.Equal(7, counters.Requests);
        Assert.Empty(counters.ResidentModels);

        var newer = MessagePackSerializer.Serialize(
            new AutocompleteHostCounters { Requests = 3, ResidentModels = [new ResidentModelInfo { ProviderId = "ollama", ModelId = "m", ContextLength = 4096, KeepAlive = "-1", IsPinned = true }] },
            Options);
        var early = MessagePackSerializer.Deserialize<AutocompleteHostCountersV12Early>(newer, Options);
        Assert.Equal(3, early.Requests);
    }

    /// <summary><see cref="CompletionRequest"/> as contract 1.1 defined it.</summary>
    [DataContract]
    public sealed class CompletionRequestV11
    {
        [DataMember(Order = 0)]
        public string RequestId { get; set; } = string.Empty;

        [DataMember(Order = 1)]
        public string Prefix { get; set; } = string.Empty;

        [DataMember(Order = 2)]
        public string Suffix { get; set; } = string.Empty;

        [DataMember(Order = 3)]
        public string LanguageId { get; set; } = string.Empty;

        [DataMember(Order = 4)]
        public string FilePath { get; set; } = string.Empty;

        [DataMember(Order = 5)]
        public int MaxTokens { get; set; }

        [DataMember(Order = 6)]
        public TaskClass TaskClass { get; set; }
    }

    /// <summary>The first members of <see cref="AutocompleteHostCounters"/>, before <c>ResidentModels</c> was appended.</summary>
    [DataContract]
    public sealed class AutocompleteHostCountersV12Early
    {
        [DataMember(Order = 0)]
        public long Requests { get; set; }

        [DataMember(Order = 1)]
        public long CacheHits { get; set; }

        [DataMember(Order = 2)]
        public long TypeThroughHits { get; set; }

        [DataMember(Order = 3)]
        public long CacheMisses { get; set; }

        [DataMember(Order = 4)]
        public double HitRate { get; set; }
    }
}

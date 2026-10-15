using System.Reflection;
using System.Runtime.Serialization;
using MessagePack;
using MessagePack.Resolvers;

namespace Pillion.Contracts.Tests;

/// <summary>
/// Contract 1.3 (phase 2): the version, the orders of every member appended to an existing DTO, the new enum values, the
/// uniqueness of RPC method names across the services that share a JSON-RPC namespace, and proof that 1.2 and 1.3 peers
/// still understand each other. The new DTOs themselves are round-tripped by <see cref="DtoRoundTripTests"/> (their samples
/// are in <see cref="DtoSamples"/>).
/// </summary>
public class ContractV13Tests
{
    private static readonly MessagePackSerializerOptions Options =
        MessagePackSerializerOptions.Standard.WithResolver(StandardResolver.Instance);

    /// <summary>The VSIX-to-host services: Pillion Host registers them all on one JsonRpc under their CLR method names.</summary>
    private static readonly Type[] VsixToHost =
    [
        typeof(IHostLifecycleService), typeof(IModelService), typeof(IInlineEditService), typeof(IContextService), typeof(IAgentService),
        typeof(ISettingsService), typeof(IDiagnosticsService), typeof(IConversationService),
    ];

    /// <summary>The host-to-VSIX services: the VSIX registers them all on one JsonRpc under their CLR method names.</summary>
    private static readonly Type[] HostToVsix = [typeof(IEditorService), typeof(IApprovalService), typeof(IWorkspaceQueryService)];

    /// <summary>The notifications the host sends to the VSIX (method names on the VSIX's JsonRpc).</summary>
    private static readonly string[] Notifications = ["hostStateChanged", "connectivityChanged", "settingsChanged", "indexStatusChanged"];

    /// <summary>Every method contract 1.3 added, with its interface.</summary>
    public static TheoryData<Type, string> MethodsAddedIn13 => new()
    {
        { typeof(IConversationService), nameof(IConversationService.ListConversationsAsync) },
        { typeof(IConversationService), nameof(IConversationService.GetConversationAsync) },
        { typeof(IConversationService), nameof(IConversationService.CreateConversationAsync) },
        { typeof(IConversationService), nameof(IConversationService.RenameConversationAsync) },
        { typeof(IConversationService), nameof(IConversationService.DeleteConversationAsync) },
        { typeof(IConversationService), nameof(IConversationService.DeleteAllConversationsAsync) },
        { typeof(IConversationService), nameof(IConversationService.SetPinsAsync) },
        { typeof(IContextService), nameof(IContextService.SearchDetailedAsync) },
        { typeof(IContextService), nameof(IContextService.ListExcludedFilesAsync) },
        { typeof(IContextService), nameof(IContextService.RebuildIndexAsync) },
        { typeof(IContextService), nameof(IContextService.DeleteIndexAsync) },
        { typeof(IContextService), nameof(IContextService.ListIndexesAsync) },
        { typeof(IWorkspaceQueryService), nameof(IWorkspaceQueryService.CallersOfAsync) },
    };

    /// <summary>Every member contract 1.3 appended to a DTO that 1.2 already had, with its order.</summary>
    public static TheoryData<Type, string, int> AppendedIn13 => new()
    {
        { typeof(IndexStatus), nameof(IndexStatus.Phase), 5 },
        { typeof(IndexStatus), nameof(IndexStatus.PauseReason), 6 },
        { typeof(IndexStatus), nameof(IndexStatus.Roots), 7 },
        { typeof(IndexStatus), nameof(IndexStatus.ExcludedSecretFileCount), 8 },
        { typeof(IndexStatus), nameof(IndexStatus.LastError), 9 },
        { typeof(IndexStatus), nameof(IndexStatus.Progress), 10 },
        { typeof(SearchRequest), nameof(SearchRequest.Roots), 2 },
        { typeof(SearchRequest), nameof(SearchRequest.Partitions), 3 },
        { typeof(SearchRequest), nameof(SearchRequest.Languages), 4 },
        { typeof(SearchRequest), nameof(SearchRequest.Mode), 5 },
        { typeof(SearchRequest), nameof(SearchRequest.MaxPerFile), 6 },
        { typeof(SearchRequest), nameof(SearchRequest.IncludeText), 7 },
        { typeof(SearchRequest), nameof(SearchRequest.FocusFiles), 8 },
        { typeof(SearchResult), nameof(SearchResult.ChunkId), 5 },
        { typeof(SearchResult), nameof(SearchResult.Breadcrumb), 6 },
        { typeof(SearchResult), nameof(SearchResult.Kind), 7 },
        { typeof(SearchResult), nameof(SearchResult.Partition), 8 },
        { typeof(SearchResult), nameof(SearchResult.Language), 9 },
        { typeof(SearchResult), nameof(SearchResult.LexicalRank), 10 },
        { typeof(SearchResult), nameof(SearchResult.VectorRank), 11 },
        { typeof(SearchResult), nameof(SearchResult.FusedScore), 12 },
        { typeof(SearchResult), nameof(SearchResult.Text), 13 },
        { typeof(ChatChunk), nameof(ChatChunk.Citations), 8 },
        { typeof(ChatRequest), nameof(ChatRequest.RetrievalMode), 9 },
        { typeof(ChatRequest), nameof(ChatRequest.MaxRetrievedChunks), 10 },
        { typeof(ChatRequest), nameof(ChatRequest.ExcludedContextKeys), 11 },
        { typeof(ContextReportItemDto), nameof(ContextReportItemDto.ContextKey), 9 },
        { typeof(MentionRequest), nameof(MentionRequest.Qualifier), 7 },
        { typeof(MentionRequest), nameof(MentionRequest.Arguments), 8 },
        { typeof(MentionResolution), nameof(MentionResolution.Citations), 12 },
    };

    [Fact]
    public void Version_IsOnePointThree()
    {
        Assert.Equal(1, ContractVersion.Major);
        Assert.Equal(3, ContractVersion.Minor);
        Assert.Equal("1.3", ContractVersion.Text);
    }

    [Theory]
    [InlineData(1, 0)]
    [InlineData(1, 1)]
    [InlineData(1, 2)]
    [InlineData(1, 3)]
    [InlineData(1, 4)]
    public void PeersOfTheSameMajor_AreCompatible_AOnePointTwoPeerIncluded(int major, int minor)
    {
        Assert.True(ContractVersion.IsCompatible(major, minor));
    }

    [Theory]
    [InlineData(0, 3)]
    [InlineData(2, 2)]
    public void PeersOfAnotherMajor_AreNotCompatible(int major, int minor)
    {
        Assert.False(ContractVersion.IsCompatible(major, minor));
    }

    [Theory]
    [MemberData(nameof(AppendedIn13))]
    public void AppendedMember_HasItsOnePointThreeOrder(Type type, string property, int order)
    {
        var member = type.GetProperty(property)?.GetCustomAttribute<DataMemberAttribute>();

        Assert.NotNull(member);
        Assert.Equal(order, member.Order);
    }

    [Theory]
    [MemberData(nameof(MethodsAddedIn13))]
    public void EveryNewMethod_HasANameUsedNowhereElseInTheContract(Type contract, string name)
    {
        Assert.NotNull(contract.GetMethod(name));

        var users = VsixToHost.Concat(HostToVsix)
            .SelectMany(t => t.GetMethods().Select(m => (Contract: t.Name, m.Name)))
            .Where(m => m.Name == name)
            .Select(m => m.Contract)
            .ToArray();

        Assert.Equal([contract.Name], users);
        Assert.DoesNotContain(name, Notifications);
    }

    [Fact]
    public void VsixToHostMethodNames_AreUniqueAcrossEveryHostService()
    {
        // ContractV12InlineEditTests checks the phase 1 services; this covers IDiagnosticsService and IConversationService too.
        Assert.Empty(Duplicates(VsixToHost));
    }

    [Fact]
    public void HostToVsixMethodNames_AreUniqueAcrossEveryVsixService_AndDoNotClashWithNotifications()
    {
        Assert.Empty(Duplicates(HostToVsix));
        Assert.Empty(HostToVsix.SelectMany(t => t.GetMethods()).Select(m => m.Name).Intersect(Notifications, StringComparer.Ordinal));
    }

    [Fact]
    public void EveryServiceInterface_IsListedAsOneDirection()
    {
        var all = typeof(ContractVersion).Assembly.GetExportedTypes().Where(t => t.IsInterface).OrderBy(t => t.Name, StringComparer.Ordinal);

        Assert.Equal(all, VsixToHost.Concat(HostToVsix).OrderBy(t => t.Name, StringComparer.Ordinal));
    }

    [Fact]
    public void TheNewErrorCodes_AreAppended_AndTheOlderWireValuesAreKept()
    {
        Assert.Equal(
            [("ProviderUnavailable", 0), ("ModelNotInstalled", 1), ("ContextOverflow", 2), ("PolicyDenied", 3), ("ApprovalDenied", 4), ("HostRestarting", 5), ("Cancelled", 6), ("StorageUnavailable", 7), ("NotImplemented", 8)],
            Enum.GetValues<PillionErrorCode>().Select(c => (c.ToString(), (int)c)));
    }

    [Fact]
    public void AnErrorWithANewCode_IsReadByAOnePointTwoPeerAsAnUnknownNumber()
    {
        var bytes = MessagePackSerializer.Serialize(new PillionError { Code = PillionErrorCode.StorageUnavailable, Message = "m" }, Options);

        Assert.Equal("[7,\"m\",null,null]", MessagePackSerializer.ConvertToJson(bytes, Options));
    }

    [Fact]
    public void MentionKinds_AreTheLowerCaseWireValues_OldAndNew()
    {
        var values = typeof(MentionKinds).GetFields(BindingFlags.Public | BindingFlags.Static).Select(f => (string)f.GetRawConstantValue()!).ToArray();

        Assert.Equal(["file", "folder", "selection", "errors", "clipboard", "symbol", "docs", "solution", "git", "output", "build", "tests"], values);
    }

    [Fact]
    public void DefaultsThatMeanUnknown_AreMinusOne()
    {
        Assert.Equal(-1, new Citation().ReplyOffset);
        Assert.Equal(-1, new IndexProgress().EstimatedRemainingSeconds);
        Assert.Equal(-1, new CallersOfRequest().Line);
    }

    [Fact]
    public void AOnePointTwoIndexStatus_ReadsAsIdleWithNoRoots_AndAOnePointThreeStatusIsReadByAOnePointTwoPeer()
    {
        var older = MessagePackSerializer.Serialize(new IndexStatusV12 { IsIndexing = true, IndexedFileCount = 3, TotalFileCount = 9 }, Options);
        var status = MessagePackSerializer.Deserialize<IndexStatus>(older, Options);
        Assert.Equal(9, status.TotalFileCount);
        Assert.Equal(IndexPhase.Idle, status.Phase);
        Assert.Equal(IndexPauseReason.None, status.PauseReason);
        Assert.Empty(status.Roots);
        Assert.Null(status.LastError);
        Assert.Null(status.Progress);

        var newer = MessagePackSerializer.Serialize(
            new IndexStatus { IndexedFileCount = 5, Phase = IndexPhase.Embedding, Roots = [new IndexRootStatus { RootPath = @"C:\src", Role = IndexRole.Writer }], Progress = new IndexProgress { FilesDone = 5, FilesTotal = 10 } },
            Options);
        var old = MessagePackSerializer.Deserialize<IndexStatusV12>(newer, Options);
        Assert.Equal(5, old.IndexedFileCount);
    }

    [Fact]
    public void AOnePointTwoChatChunk_ReadsWithNoCitations_AndAOnePointThreeFinalChunkIsReadByAOnePointTwoPeer()
    {
        var older = MessagePackSerializer.Serialize(new ChatChunkV12 { RequestId = "r", Text = "t", IsFinal = true }, Options);
        var chunk = MessagePackSerializer.Deserialize<ChatChunk>(older, Options);
        Assert.True(chunk.IsFinal);
        Assert.Empty(chunk.Citations);

        var newer = MessagePackSerializer.Serialize(
            new ChatChunk { RequestId = "r", IsFinal = true, Citations = [new Citation { FilePath = @"C:\src\A.cs", StartLine = 1, EndLine = 2 }] },
            Options);
        var old = MessagePackSerializer.Deserialize<ChatChunkV12>(newer, Options);
        Assert.Equal("r", old.RequestId);
        Assert.True(old.IsFinal);
    }

    [Fact]
    public void AOnePointTwoSearchRequest_ReadsAsAnUnfilteredHybridSearch()
    {
        var bytes = MessagePackSerializer.Serialize(new SearchRequestV12 { Query = "q", MaxResults = 5 }, Options);

        var request = MessagePackSerializer.Deserialize<SearchRequest>(bytes, Options);

        Assert.Equal("q", request.Query);
        Assert.Equal(SearchMode.Hybrid, request.Mode);
        Assert.Empty(request.Roots);
        Assert.Empty(request.Partitions);
        Assert.Empty(request.Languages);
        Assert.Empty(request.FocusFiles);
        Assert.Equal(0, request.MaxPerFile);
        Assert.False(request.IncludeText);
    }

    [Fact]
    public void AOnePointThreeSearchResult_IsReadByAOnePointTwoPeer_AndAOnePointTwoResultReadsWithDefaults()
    {
        var newer = MessagePackSerializer.Serialize(
            new SearchResult { FilePath = @"C:\src\A.cs", StartLine = 1, EndLine = 3, Snippet = "s", Score = 0.5, ChunkId = "x:1", LexicalRank = 2, Text = "full" },
            Options);
        var old = MessagePackSerializer.Deserialize<SearchResultV12>(newer, Options);
        Assert.Equal(@"C:\src\A.cs", old.FilePath);
        Assert.Equal(0.5, old.Score);

        var older = MessagePackSerializer.Serialize(new SearchResultV12 { FilePath = "f", Score = 0.25 }, Options);
        var result = MessagePackSerializer.Deserialize<SearchResult>(older, Options);
        Assert.Equal(string.Empty, result.ChunkId);
        Assert.Equal(0, result.LexicalRank);
        Assert.Equal(0, result.VectorRank);
        Assert.Null(result.Text);
    }

    [Fact]
    public void AOnePointTwoMentionRequest_ReadsWithNoQualifierOrArguments()
    {
        var bytes = MessagePackSerializer.Serialize(new MentionRequestV12 { Mention = "@file:A.cs", Kind = "file" }, Options);

        var request = MessagePackSerializer.Deserialize<MentionRequest>(bytes, Options);

        Assert.Equal("file", request.Kind);
        Assert.Equal(string.Empty, request.Qualifier);
        Assert.Empty(request.Arguments);
    }

    [Fact]
    public void ARequestWithoutExclusions_AndAReportItemWithoutAKey_ReadWithEmptyDefaults()
    {
        var request = MessagePackSerializer.Deserialize<ChatRequest>(MessagePackSerializer.Serialize(new RequestIdOnly { RequestId = "r" }, Options), Options);
        Assert.Equal("r", request.RequestId);
        Assert.Empty(request.ExcludedContextKeys);

        var item = MessagePackSerializer.Deserialize<ContextReportItemDto>(MessagePackSerializer.Serialize(new RequestIdOnly { RequestId = "pin" }, Options), Options);
        Assert.Equal("pin", item.Kind);
        Assert.Equal(string.Empty, item.ContextKey);
        Assert.Equal(-1, item.PinIndex);
    }

    private static string[] Duplicates(IEnumerable<Type> contracts) => contracts
        .SelectMany(t => t.GetMethods().Select(m => (Contract: t.Name, m.Name)))
        .GroupBy(m => m.Name, StringComparer.Ordinal)
        .Where(g => g.Count() > 1)
        .Select(g => $"{g.Key}: {string.Join(", ", g.Select(m => m.Contract))}")
        .ToArray();

    /// <summary><see cref="IndexStatus"/> as contract 1.2 defined it.</summary>
    [DataContract]
    public sealed class IndexStatusV12
    {
        [DataMember(Order = 0)]
        public bool IsIndexing { get; set; }

        [DataMember(Order = 1)]
        public bool IsPaused { get; set; }

        [DataMember(Order = 2)]
        public int IndexedFileCount { get; set; }

        [DataMember(Order = 3)]
        public int TotalFileCount { get; set; }

        [DataMember(Order = 4)]
        public DateTime? LastUpdatedUtc { get; set; }
    }

    /// <summary>A DTO with only its first member, as a peer that knows none of the later ones sends it.</summary>
    [DataContract]
    public sealed class RequestIdOnly
    {
        [DataMember(Order = 0)]
        public string RequestId { get; set; } = string.Empty;
    }

    /// <summary><see cref="ChatChunk"/> as contract 1.2 defined it.</summary>
    [DataContract]
    public sealed class ChatChunkV12
    {
        [DataMember(Order = 0)]
        public string RequestId { get; set; } = string.Empty;

        [DataMember(Order = 1)]
        public string Text { get; set; } = string.Empty;

        [DataMember(Order = 2)]
        public bool IsFinal { get; set; }

        [DataMember(Order = 3)]
        public string ModelId { get; set; } = string.Empty;

        [DataMember(Order = 4)]
        public string ProviderId { get; set; } = string.Empty;

        [DataMember(Order = 5)]
        public int PromptTokens { get; set; }

        [DataMember(Order = 6)]
        public int CompletionTokens { get; set; }

        [DataMember(Order = 7)]
        public PillionError? Error { get; set; }
    }

    /// <summary><see cref="SearchRequest"/> as contract 1.2 defined it.</summary>
    [DataContract]
    public sealed class SearchRequestV12
    {
        [DataMember(Order = 0)]
        public string Query { get; set; } = string.Empty;

        [DataMember(Order = 1)]
        public int MaxResults { get; set; }
    }

    /// <summary><see cref="SearchResult"/> as contract 1.2 defined it.</summary>
    [DataContract]
    public sealed class SearchResultV12
    {
        [DataMember(Order = 0)]
        public string FilePath { get; set; } = string.Empty;

        [DataMember(Order = 1)]
        public int StartLine { get; set; }

        [DataMember(Order = 2)]
        public int EndLine { get; set; }

        [DataMember(Order = 3)]
        public string Snippet { get; set; } = string.Empty;

        [DataMember(Order = 4)]
        public double Score { get; set; }
    }

    /// <summary><see cref="MentionRequest"/> as contract 1.2 defined it.</summary>
    [DataContract]
    public sealed class MentionRequestV12
    {
        [DataMember(Order = 0)]
        public string Mention { get; set; } = string.Empty;

        [DataMember(Order = 1)]
        public string Kind { get; set; } = string.Empty;

        [DataMember(Order = 2)]
        public string[] WorkspaceRoots { get; set; } = [];

        [DataMember(Order = 3)]
        public string[] CandidateFiles { get; set; } = [];

        [DataMember(Order = 4)]
        public string? Payload { get; set; }

        [DataMember(Order = 5)]
        public int MaxTokens { get; set; }

        [DataMember(Order = 6)]
        public bool ApprovedSensitive { get; set; }
    }
}

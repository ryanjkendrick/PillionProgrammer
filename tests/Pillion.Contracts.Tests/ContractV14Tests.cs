using System.Reflection;
using System.Runtime.Serialization;
using MessagePack;
using MessagePack.Resolvers;

namespace Pillion.Contracts.Tests;

/// <summary>
/// Contract 1.4 (phase 3, work item 3.5): the version, the orders of every member appended to an existing DTO, the appended
/// enum values, the uniqueness of every new method name across the services that share a JSON-RPC namespace and the
/// notifications, proof that 1.3 and 1.4 peers still understand each other, and a snapshot of every DTO's member order that
/// fails if an existing order changes. The new DTOs themselves are round-tripped by <see cref="DtoRoundTripTests"/> (their
/// samples are in <see cref="DtoSamples"/>).
/// </summary>
public class ContractV14Tests
{
    private static readonly MessagePackSerializerOptions Options =
        MessagePackSerializerOptions.Standard.WithResolver(StandardResolver.Instance);

    /// <summary>The VSIX-to-host services: Pillion Host registers them all on one JsonRpc under their CLR method names.</summary>
    private static readonly Type[] VsixToHost =
    [
        typeof(IHostLifecycleService), typeof(IModelService), typeof(IInlineEditService), typeof(IContextService), typeof(IAgentService),
        typeof(ISettingsService), typeof(IDiagnosticsService), typeof(IConversationService), typeof(ITerminalService), typeof(IJournalService),
    ];

    /// <summary>The host-to-VSIX services: the VSIX registers them all on one JsonRpc under their CLR method names.</summary>
    private static readonly Type[] HostToVsix = [typeof(IEditorService), typeof(IApprovalService), typeof(IWorkspaceQueryService), typeof(ITrustService)];

    /// <summary>The notifications the host sends to the VSIX (method names on the VSIX's JsonRpc), 1.4's last.</summary>
    private static readonly string[] Notifications =
    [
        "hostStateChanged", "connectivityChanged", "settingsChanged", "indexStatusChanged",
        "agentSessionChanged", "agentToolCall", "agentChangesetReady", "terminalScreenChanged", "backgroundProcessChanged",
    ];

    /// <summary>The notifications the VSIX sends to the host (method names on the host's JsonRpc). 1.4 adds none.</summary>
    private static readonly string[] ClientNotifications = ["ideActivityChanged", "documentSaved", "openDocumentsChanged"];

    /// <summary>Every method contract 1.4 added, with its interface.</summary>
    public static TheoryData<Type, string> MethodsAddedIn14 => new()
    {
        { typeof(IAgentService), nameof(IAgentService.AnswerApprovalAsync) },
        { typeof(IAgentService), nameof(IAgentService.ListSessionsAsync) },
        { typeof(IAgentService), nameof(IAgentService.GetSessionAsync) },
        { typeof(IAgentService), nameof(IAgentService.SendMessageAsync) },
        { typeof(IAgentService), nameof(IAgentService.SetApprovalModeAsync) },
        { typeof(IAgentService), nameof(IAgentService.RevertSessionAsync) },
        { typeof(ITerminalService), nameof(ITerminalService.SendInputAsync) },
        { typeof(ITerminalService), nameof(ITerminalService.ResizeAsync) },
        { typeof(ITerminalService), nameof(ITerminalService.OpenDeveloperShellAsync) },
        { typeof(ITerminalService), nameof(ITerminalService.GetScreenAsync) },
        { typeof(ITerminalService), nameof(ITerminalService.ListTerminalsAsync) },
        { typeof(ITerminalService), nameof(ITerminalService.CloseTerminalAsync) },
        { typeof(IJournalService), nameof(IJournalService.BeginApplyAsync) },
        { typeof(IJournalService), nameof(IJournalService.CommitApplyAsync) },
        { typeof(IJournalService), nameof(IJournalService.AbortApplyAsync) },
        { typeof(IJournalService), nameof(IJournalService.ListInterruptedAppliesAsync) },
        { typeof(IEditorService), nameof(IEditorService.ReadDocumentAsync) },
        { typeof(IWorkspaceQueryService), nameof(IWorkspaceQueryService.PlanRenameAsync) },
        { typeof(ITrustService), nameof(ITrustService.RequestTrustAsync) },
    };

    /// <summary>Every member contract 1.4 appended to a DTO that 1.3 already had, with its order.</summary>
    public static TheoryData<Type, string, int> AppendedIn14 => new()
    {
        { typeof(AgentSessionRequest), nameof(AgentSessionRequest.ConversationId), 3 },
        { typeof(AgentSessionRequest), nameof(AgentSessionRequest.Pins), 4 },
        { typeof(AgentSessionRequest), nameof(AgentSessionRequest.ApprovalMode), 5 },
        { typeof(ApprovalDecision), nameof(ApprovalDecision.RememberForSession), 3 },
        { typeof(ApprovalDecision), nameof(ApprovalDecision.AddToAllowList), 4 },
        { typeof(ApprovalDecision), nameof(ApprovalDecision.Preview), 5 },
        { typeof(ApprovalRequest), nameof(ApprovalRequest.SessionId), 6 },
        { typeof(ApprovalRequest), nameof(ApprovalRequest.ToolCallId), 7 },
        { typeof(ApprovalRequest), nameof(ApprovalRequest.ProvenanceKind), 8 },
        { typeof(ApprovalRequest), nameof(ApprovalRequest.ChangesetId), 9 },
        { typeof(ApprovalRequest), nameof(ApprovalRequest.RequestedUtc), 10 },
        { typeof(Changeset), nameof(Changeset.Creates), 3 },
        { typeof(Changeset), nameof(Changeset.Deletes), 4 },
        { typeof(Changeset), nameof(Changeset.Renames), 5 },
        { typeof(Changeset), nameof(Changeset.ProjectItemChanges), 6 },
        { typeof(Changeset), nameof(Changeset.SensitivePaths), 7 },
        { typeof(Changeset), nameof(Changeset.RevertsChangesetId), 8 },
        { typeof(PreviewDecision), nameof(PreviewDecision.AcceptedEditIndices), 2 },
        { typeof(PreviewDecision), nameof(PreviewDecision.AcceptedCreateIndices), 3 },
        { typeof(PreviewDecision), nameof(PreviewDecision.AcceptedDeleteIndices), 4 },
        { typeof(PreviewDecision), nameof(PreviewDecision.AcceptedRenameIndices), 5 },
        { typeof(ApplyResult), nameof(ApplyResult.Files), 4 },
        { typeof(ApplyResult), nameof(ApplyResult.RolledBack), 5 },
        { typeof(ApplyResult), nameof(ApplyResult.CheckpointId), 6 },
        { typeof(ApplyResult), nameof(ApplyResult.JournalId), 7 },
        { typeof(HandshakeRequest), nameof(HandshakeRequest.VisualStudioInstallationPath), 4 },
        { typeof(HandshakeRequest), nameof(HandshakeRequest.VisualStudioVersion), 5 },
    };

    /// <summary>
    /// Every DTO's members in <c>Order</c>, as contract 1.4 defines them. A later minor may append a member at the end of a line
    /// or add a line; any other change to this text is a major bump (README, Versioning rule).
    /// </summary>
    private const string OrderSnapshot = """
        AgentBudgetUsage: Steps, MaxSteps, Tokens, MaxTokens, Cost, MaxCost, WallSeconds, MaxWallSeconds
        AgentChangesetReady: SessionId, Changeset, ApprovalId, StepIndex
        AgentChangesetSummary: ChangesetId, Description, FileCount, IsApplied, IsReverted, CheckpointId, JournalId, AppliedUtc
        AgentMessageRequest: SessionId, Message, Pins
        AgentPlanStep: Title, Status, Paths, SensitivePaths, Note
        AgentSessionListRequest: ConversationId, IncludeFinished, Limit
        AgentSessionRequest: Goal, Profile, ModelId, ConversationId, Pins, ApprovalMode
        AgentSessionState: SessionId, ConversationId, Goal, Status, ApprovalMode, Steps, Constraints, Budgets, PendingApproval, Changesets, StartedUtc, UpdatedUtc, Summary, Error, ModelId
        AgentSessionSummary: SessionId, ConversationId, Goal, Status, ApprovalMode, StartedUtc, UpdatedUtc, ChangesetCount
        AgentToolCallRecord: ToolCallId, SessionId, Step, ToolName, Tier, ArgumentsSummary, Provenance, ProvenanceDetail, TaintReason, Status, ResultSummary, DurationMs, StartedUtc, ApprovalId, IsAutomatic
        ApplyBeginRequest: ChangesetId, SessionId, Files, Decision
        ApplyBeginResponse: CheckpointId, JournalId
        ApplyCommitRequest: JournalId, Files
        ApplyFileHash: Path, PostApplyHash
        ApplyFileSnapshot: Path, Kind, PreEditText, NewPath
        ApplyJournalEntry: JournalId, CheckpointId, ChangesetId, SessionId, Status, BeganUtc, EndedUtc, Files, Changeset, Decision
        ApplyJournalFile: Path, Kind, PreEditText, PostApplyHash, NewPath
        ApplyResult: ChangesetId, IsSuccess, FailedPaths, Message, Files, RolledBack, CheckpointId, JournalId
        ApprovalDecision: ApprovalId, IsApproved, Reason, RememberForSession, AddToAllowList, Preview
        ApprovalRequest: ApprovalId, ToolName, Tier, ArgumentsSummary, Provenance, TaintReason, SessionId, ToolCallId, ProvenanceKind, ChangesetId, RequestedUtc
        AutocompleteHostCounters: Requests, CacheHits, TypeThroughHits, CacheMisses, HitRate, ModelRequests, Dropped, Failed, Cancelled, WindowSeconds, WindowRequests, WindowCacheHits, FirstTokenP50Ms, FirstTokenP95Ms, TotalP50Ms, TotalP95Ms, PromptCountedRequests, PromptEstimatedRequests, PromptTokensCounted, PromptTokensEstimated, PromptTokensMax, ResidentModels, PromptPrefixChecks, PromptPrefixReuses, PromptPrefixReuseRate
        BackgroundProcessInfo: BackgroundProcessId, SessionId, TerminalId, CommandLine, Status, Port, Url, ProcessId, StartedUtc, ExitCode, Message
        CallersOfRequest: SymbolName, Container, FilePath, Line, MaxResults
        CallersOfResponse: IsFound, Callers, Truncated
        CallSite: FilePath, Line, Snippet, CallerName, IsApproximate
        Changeset: Id, Edits, Description, Creates, Deletes, Renames, ProjectItemChanges, SensitivePaths, RevertsChangesetId
        ChatChunk: RequestId, Text, IsFinal, ModelId, ProviderId, PromptTokens, CompletionTokens, Error, Citations
        ChatMessage: Role, Content
        ChatRequest: RequestId, Messages, ModelId, Profile, Pins, CurrentFile, CurrentFileRegion, ConversationId, ProjectRules, RetrievalMode, MaxRetrievedChunks, ExcludedContextKeys
        Citation: FilePath, StartLine, EndLine, ChunkId, Breadcrumb, ReplyOffset
        CompletionChunk: RequestId, Text, IsFinal, ModelId, ProviderId, PromptTokens, CompletionTokens, Error
        CompletionRequest: RequestId, Prefix, Suffix, LanguageId, FilePath, MaxTokens, TaskClass, CaretLine, CaretColumn, EnclosingDeclaration, OpenDocuments, RecentEdits, AllowMultiLine, StopSequences
        ContextReportDto: ModelId, ProviderId, IsLocal, ContextWindow, OutputReserve, BudgetTokens, TotalTokens, Items, IsAssembled, Redacts, RedactionCount
        ContextReportItemDto: Kind, Name, Tokens, OriginalTokens, Truncated, Dropped, Note, PinIndex, RedactionCount, ContextKey
        ConversationCreateRequest: Id, Title, ModelId
        ConversationDetail: Conversation, Messages, Pins
        ConversationListRequest: Offset, Limit
        ConversationMessage: Id, Role, Content, ModelId, ProviderId, PromptTokens, CompletionTokens, CreatedUtc, Citations, Cancelled
        ConversationPage: Items, HasMore
        ConversationPin: Kind, Target, DisplayName, TokenCount
        ConversationSummary: Id, Title, CreatedUtc, UpdatedUtc, LastModelId, MessageCount
        DeleteAllConversationsResult: Conversations, Messages, Pins, OutboundEntries
        DiagnosticInfo: Id, Message, Severity, Location
        DiagnosticsRequest: FilePath
        DiagnosticsResponse: Diagnostics
        DocumentText: Path, IsOpen, IsDirty, Text, Hash
        ExcludedFile: Path, Reason, Description, IsSecretBearing, IsDirectory
        ExcludedFilesPage: Items, TotalCount, HasMore
        ExcludedFilesRequest: RootPath, Offset, Limit, SecretOnly
        FileApplyOutcome: Path, Kind, IsApplied, PostApplyHash, Message
        FileCreate: Path, Content, ExpectedHash
        FileDelete: Path, ExpectedHash
        FileEdit: Path, SearchText, ReplaceText
        FileRename: OldPath, NewPath
        FindReferencesRequest: Location
        FindReferencesResponse: References
        HandshakeRequest: ContractMajor, ContractMinor, ClientProcessId, ProtocolToken, VisualStudioInstallationPath, VisualStudioVersion
        HandshakeResponse: ContractMajor, ContractMinor, HostVersion, HostProcessId, Accepted, Reason
        IndexInfo: IndexId, RootPath, EmbeddingModelId, ChunkerVersion, DiskBytes, LastUsedUtc, RootExists, IsInUse, IsOpen
        IndexProgress: FilesDone, FilesTotal, ChunksDone, EstimatedRemainingSeconds
        IndexRootStatus: RootPath, IndexId, Role, IndexedFileCount, TotalFileCount, ExcludedFileCount, SecretFileCount, ChunkCount, DiskBytes, LastFullSweepUtc, EmbeddingModelId, ChunkerVersion, SchemaVersion
        IndexStatus: IsIndexing, IsPaused, IndexedFileCount, TotalFileCount, LastUpdatedUtc, Phase, PauseReason, Roots, ExcludedSecretFileCount, LastError, Progress
        InlineEditChunk: RequestId, Text, IsFinal, Explanation, ModelId, ProviderId, PromptTokens, CompletionTokens, Error, RejectedBlocks, Warning, ResetText
        InlineEditDiagnostic: Line, Severity, Code, Message
        InlineEditRequest: RequestId, FilePath, LanguageId, ScopeText, ScopeStartLine, ScopeStartColumn, TextBefore, TextAfter, Instruction, Command, Diagnostics, PreviousProposal, ModelId, Profile, ConversationId
        MentionRequest: Mention, Kind, WorkspaceRoots, CandidateFiles, Payload, MaxTokens, ApprovedSensitive, Qualifier, Arguments
        MentionResolution: Mention, IsResolved, FilePath, Content, Kind, DisplayName, TokenCount, Truncated, Candidates, Reason, RequiresApproval, IsSensitive, Citations
        ModelDescriptor: Id, DisplayName, ProviderId, IsLocal, Capabilities, ModelId, IsAvailable, ContextWindow, Digest
        OpenDeveloperShellRequest: Shell, WorkingDirectory, Columns, Rows
        OpenDocument: Path, LanguageId, IsDirty, IsActive
        OutboundRecordDto: TimestampUtc, RequestId, ProviderId, ModelId, TaskClass, IsLocal, PromptTokens, CompletionTokens, RedactionCount, ContextItems, ContextTrimmed, DurationMs, Outcome, ErrorCode, CacheHit
        PillionError: Code, Message, RetryHint, Details
        PreviewDecision: ChangesetId, IsAccepted, AcceptedEditIndices, AcceptedCreateIndices, AcceptedDeleteIndices, AcceptedRenameIndices
        ProjectItemChange: Kind, ProjectPath, ItemPath, NewItemPath
        ProviderTestResult: ProviderId, IsSuccess, Message
        RenameConflict: Location, Message
        RenamePlan: IsSuccess, SymbolName, Edits, SensitivePaths, Conflicts, Message, Truncated
        RenameSymbolRequest: Location, NewName
        RenameSymbolResponse: IsSuccess, ChangedFiles, Message
        ResidentModelInfo: ProviderId, ModelId, ContextLength, VramBytes, KeepAlive, IsPinned
        RunTestsRequest: Filter
        RunTestsResponse: Passed, Failed, Skipped, FailedTests
        SearchRequest: Query, MaxResults, Roots, Partitions, Languages, Mode, MaxPerFile, IncludeText, FocusFiles
        SearchResponse: Results, LexicalMs, VectorMs, FusionMs, TotalMs, Degraded
        SearchResult: FilePath, StartLine, EndLine, Snippet, Score, ChunkId, Breadcrumb, Kind, Partition, Language, LexicalRank, VectorRank, FusedScore, Text
        SourceLocation: FilePath, Line, Column
        SymbolRequest: Location
        SymbolResponse: IsFound, Name, Kind, Definition
        TerminalInfo: TerminalId, SessionId, Title, Shell, Columns, Rows, ProcessId, IsRunning, BackgroundProcessId, OpenedUtc
        TerminalScreen: TerminalId, Sequence, Columns, Rows, Lines, CursorRow, CursorColumn, ScrollbackDelta, ScrollbackTotal, IsWaitingForInput, ExitCode, Title
        TrustDecision: RepositoryRoot, ContentHash, IsTrusted, Remember
        TrustFile: RelativePath, Content, SizeBytes, IsTruncated
        TrustRequest: RepositoryRoot, PillionFolder, ContentHash, Files, PreviouslyTrustedHash, Reason
        WorkspaceDescription: SolutionPath, ProjectPaths, FolderPath
        """;

    [Fact]
    public void Version_IsOnePointFour()
    {
        Assert.Equal(1, ContractVersion.Major);
        Assert.Equal(4, ContractVersion.Minor);
        Assert.Equal("1.4", ContractVersion.Text);
    }

    [Theory]
    [InlineData(1, 0)]
    [InlineData(1, 3)]
    [InlineData(1, 4)]
    [InlineData(1, 5)]
    public void PeersOfTheSameMajor_AreCompatible_AOnePointThreePeerIncluded(int major, int minor)
    {
        Assert.True(ContractVersion.IsCompatible(major, minor));
    }

    [Theory]
    [MemberData(nameof(AppendedIn14))]
    public void AppendedMember_HasItsOnePointFourOrder(Type type, string property, int order)
    {
        var member = type.GetProperty(property)?.GetCustomAttribute<DataMemberAttribute>();

        Assert.NotNull(member);
        Assert.Equal(order, member.Order);
    }

    [Theory]
    [MemberData(nameof(MethodsAddedIn14))]
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
        Assert.DoesNotContain(name, ClientNotifications);
    }

    [Fact]
    public void MethodNames_AreUniqueWithinEachDirection_AndDoNotClashWithNotifications()
    {
        Assert.Empty(Duplicates(VsixToHost));
        Assert.Empty(Duplicates(HostToVsix));
        Assert.Empty(VsixToHost.SelectMany(t => t.GetMethods()).Select(m => m.Name).Intersect(ClientNotifications, StringComparer.Ordinal));
        Assert.Empty(HostToVsix.SelectMany(t => t.GetMethods()).Select(m => m.Name).Intersect(Notifications, StringComparer.Ordinal));
        Assert.Equal(Notifications.Length, Notifications.Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public void EveryServiceInterface_IsListedAsOneDirection()
    {
        var all = typeof(ContractVersion).Assembly.GetExportedTypes().Where(t => t.IsInterface).OrderBy(t => t.Name, StringComparer.Ordinal);

        Assert.Equal(all, VsixToHost.Concat(HostToVsix).OrderBy(t => t.Name, StringComparer.Ordinal));
    }

    [Fact]
    public void TheAppendedEnumValues_KeepEveryOlderWireValue()
    {
        Assert.Equal(9, (int)PillionErrorCode.BudgetExhausted);
        Assert.Equal(8, (int)PillionErrorCode.NotImplemented);
        Assert.Equal(7, (int)TaskClass.AgentPlanning);
        Assert.Equal(6, (int)TaskClass.Summarise);
        Assert.Equal(3, (int)TaskClass.Agent);
        Assert.Equal(0, (int)AgentApprovalMode.Ask);
        Assert.Equal(2, (int)AgentApprovalMode.Autonomous);
        Assert.Equal(0, (int)ProvenanceKind.Unknown);
    }

    [Fact]
    public void MentionKinds_EndWithTerminal()
    {
        var values = typeof(MentionKinds).GetFields(BindingFlags.Public | BindingFlags.Static).Select(f => (string)f.GetRawConstantValue()!).ToArray();

        Assert.Equal(13, values.Length);
        Assert.Equal("terminal", values[^1]);
        Assert.Equal("terminal", MentionKinds.Terminal);
    }

    [Fact]
    public void EveryDataMemberOrder_MatchesTheOnePointFourSnapshot()
    {
        var actual = string.Join(
            Environment.NewLine,
            typeof(ContractVersion).Assembly.GetExportedTypes()
                .Where(t => t.GetCustomAttribute<DataContractAttribute>() is not null)
                .OrderBy(t => t.Name, StringComparer.OrdinalIgnoreCase)
                .ThenBy(t => t.Name, StringComparer.Ordinal)
                .Select(t => t.Name + ": " + string.Join(
                    ", ",
                    t.GetProperties(BindingFlags.Public | BindingFlags.Instance)
                        .Select(p => (p.Name, p.GetCustomAttribute<DataMemberAttribute>()!.Order))
                        .OrderBy(p => p.Order)
                        .Select(p => p.Name))));

        var expected = string.Join(Environment.NewLine, OrderSnapshot.Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0));

        // A failure here means a member was renamed, removed or reordered (a major bump), or a new member was added without
        // updating this snapshot (append it to the end of its line).
        Assert.Equal(expected, actual);
    }

    [Fact]
    public void AOnePointThreeAgentSessionRequest_ReadsWithAskModeNoPinsAndNoConversation()
    {
        var older = MessagePackSerializer.Serialize(new AgentSessionRequestV13 { Goal = "g", Profile = Profile.Balanced, ModelId = "m" }, Options);

        var request = MessagePackSerializer.Deserialize<AgentSessionRequest>(older, Options);

        Assert.Equal("g", request.Goal);
        Assert.Equal(AgentApprovalMode.Ask, request.ApprovalMode);
        Assert.Empty(request.Pins);
        Assert.Equal(string.Empty, request.ConversationId);
    }

    [Fact]
    public void AOnePointFourHandshake_IsReadByAOnePointThreeHost_AndAOnePointThreeHandshakeReadsWithNoVisualStudio()
    {
        var newer = MessagePackSerializer.Serialize(
            new HandshakeRequest { ContractMajor = 1, ContractMinor = 4, ClientProcessId = 7, ProtocolToken = "t", VisualStudioInstallationPath = @"C:\VS", VisualStudioVersion = "18.0" },
            Options);
        var old = MessagePackSerializer.Deserialize<HandshakeRequestV13>(newer, Options);
        Assert.Equal((1, 4, 7, "t"), (old.ContractMajor, old.ContractMinor, old.ClientProcessId, old.ProtocolToken));

        var older = MessagePackSerializer.Serialize(new HandshakeRequestV13 { ContractMajor = 1, ContractMinor = 3, ClientProcessId = 7, ProtocolToken = "t" }, Options);
        var request = MessagePackSerializer.Deserialize<HandshakeRequest>(older, Options);
        Assert.Equal(3, request.ContractMinor);
        Assert.Equal(string.Empty, request.VisualStudioInstallationPath);
        Assert.Equal(string.Empty, request.VisualStudioVersion);
    }

    [Fact]
    public void AOnePointThreeApprovalDecision_ReadsAsAPlainYesOrNo()
    {
        var older = MessagePackSerializer.Serialize(new ApprovalDecisionV13 { ApprovalId = "a", IsApproved = true, Reason = "r" }, Options);

        var decision = MessagePackSerializer.Deserialize<ApprovalDecision>(older, Options);

        Assert.True(decision.IsApproved);
        Assert.False(decision.RememberForSession);
        Assert.False(decision.AddToAllowList);
        Assert.Null(decision.Preview);
    }

    [Fact]
    public void AOnePointThreeChangesetAndPreview_ReadWithEditsOnly_AndAOnePointFourChangesetIsReadByAOnePointThreePeer()
    {
        var older = MessagePackSerializer.Serialize(new ChangesetV13 { Id = "cs", Edits = [new FileEdit { Path = "p", SearchText = "a", ReplaceText = "b" }], Description = "d" }, Options);
        var changeset = MessagePackSerializer.Deserialize<Changeset>(older, Options);
        Assert.Single(changeset.Edits);
        Assert.Empty(changeset.Creates);
        Assert.Empty(changeset.Deletes);
        Assert.Empty(changeset.Renames);
        Assert.Empty(changeset.ProjectItemChanges);
        Assert.Empty(changeset.SensitivePaths);
        Assert.Null(changeset.RevertsChangesetId);

        var newer = MessagePackSerializer.Serialize(
            new Changeset { Id = "cs", Description = "d", Creates = [new FileCreate { Path = "n", Content = "c" }], RevertsChangesetId = "cs-0" },
            Options);
        var old = MessagePackSerializer.Deserialize<ChangesetV13>(newer, Options);
        Assert.Equal("cs", old.Id);
        Assert.Empty(old.Edits);

        var preview = MessagePackSerializer.Deserialize<PreviewDecision>(MessagePackSerializer.Serialize(new PreviewDecisionV13 { ChangesetId = "cs", IsAccepted = true }, Options), Options);
        Assert.True(preview.IsAccepted);
        Assert.Null(preview.AcceptedEditIndices);
        Assert.Null(preview.AcceptedRenameIndices);
    }

    [Fact]
    public void AOnePointThreeApplyResult_ReadsWithNoFilesAndNoIds()
    {
        var older = MessagePackSerializer.Serialize(new ApplyResultV13 { ChangesetId = "cs", IsSuccess = true }, Options);

        var result = MessagePackSerializer.Deserialize<ApplyResult>(older, Options);

        Assert.True(result.IsSuccess);
        Assert.Empty(result.Files);
        Assert.False(result.RolledBack);
        Assert.Equal(string.Empty, result.CheckpointId);
        Assert.Equal(string.Empty, result.JournalId);
    }

    [Fact]
    public void AnErrorWithBudgetExhausted_IsReadByAOnePointThreePeerAsAnUnknownNumber()
    {
        var bytes = MessagePackSerializer.Serialize(new PillionError { Code = PillionErrorCode.BudgetExhausted, Message = "25 steps" }, Options);

        Assert.Equal("[9,\"25 steps\",null,null]", MessagePackSerializer.ConvertToJson(bytes, Options));
    }

    [Fact]
    public void DefaultsThatMeanUnknown_AreMinusOne()
    {
        Assert.Equal(-1, new AgentChangesetReady().StepIndex);
    }

    private static string[] Duplicates(IEnumerable<Type> contracts) => contracts
        .SelectMany(t => t.GetMethods().Select(m => (Contract: t.Name, m.Name)))
        .GroupBy(m => m.Name, StringComparer.Ordinal)
        .Where(g => g.Count() > 1)
        .Select(g => $"{g.Key}: {string.Join(", ", g.Select(m => m.Contract))}")
        .ToArray();

    /// <summary><see cref="AgentSessionRequest"/> as contract 1.3 defined it.</summary>
    [DataContract]
    public sealed class AgentSessionRequestV13
    {
        [DataMember(Order = 0)]
        public string Goal { get; set; } = string.Empty;

        [DataMember(Order = 1)]
        public Profile Profile { get; set; }

        [DataMember(Order = 2)]
        public string? ModelId { get; set; }
    }

    /// <summary><see cref="HandshakeRequest"/> as contract 1.3 defined it.</summary>
    [DataContract]
    public sealed class HandshakeRequestV13
    {
        [DataMember(Order = 0)]
        public int ContractMajor { get; set; }

        [DataMember(Order = 1)]
        public int ContractMinor { get; set; }

        [DataMember(Order = 2)]
        public int ClientProcessId { get; set; }

        [DataMember(Order = 3)]
        public string ProtocolToken { get; set; } = string.Empty;
    }

    /// <summary><see cref="ApprovalDecision"/> as contract 1.3 defined it.</summary>
    [DataContract]
    public sealed class ApprovalDecisionV13
    {
        [DataMember(Order = 0)]
        public string ApprovalId { get; set; } = string.Empty;

        [DataMember(Order = 1)]
        public bool IsApproved { get; set; }

        [DataMember(Order = 2)]
        public string? Reason { get; set; }
    }

    /// <summary><see cref="Changeset"/> as contract 1.3 defined it.</summary>
    [DataContract]
    public sealed class ChangesetV13
    {
        [DataMember(Order = 0)]
        public string Id { get; set; } = string.Empty;

        [DataMember(Order = 1)]
        public FileEdit[] Edits { get; set; } = [];

        [DataMember(Order = 2)]
        public string Description { get; set; } = string.Empty;
    }

    /// <summary><see cref="PreviewDecision"/> as contract 1.3 defined it.</summary>
    [DataContract]
    public sealed class PreviewDecisionV13
    {
        [DataMember(Order = 0)]
        public string ChangesetId { get; set; } = string.Empty;

        [DataMember(Order = 1)]
        public bool IsAccepted { get; set; }
    }

    /// <summary><see cref="ApplyResult"/> as contract 1.3 defined it.</summary>
    [DataContract]
    public sealed class ApplyResultV13
    {
        [DataMember(Order = 0)]
        public string ChangesetId { get; set; } = string.Empty;

        [DataMember(Order = 1)]
        public bool IsSuccess { get; set; }

        [DataMember(Order = 2)]
        public string[] FailedPaths { get; set; } = [];

        [DataMember(Order = 3)]
        public string? Message { get; set; }
    }
}

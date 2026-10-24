namespace Pillion.Contracts.Tests;

/// <summary>
/// One fully populated instance of every DTO in Pillion.Contracts. Every member is set to a non-default value so
/// a round trip that drops or mis-keys a member is detected. <see cref="DtoRoundTripTests"/> checks that this list
/// covers every [DataContract] type in the assembly.
/// </summary>
internal static class DtoSamples
{
    private static SourceLocation Location(int line) => new() { FilePath = @"C:\src\Foo.cs", Line = line, Column = 4 };

    public static IReadOnlyList<object> All { get; } =
    [
        new PillionError
        {
            Code = PillionErrorCode.ContextOverflow,
            Message = "The prompt is too long.",
            RetryHint = "Remove some attached files.",
            Details = "tokens=140000",
        },
        new HandshakeRequest { ContractMajor = 1, ContractMinor = 3, ClientProcessId = 4242, ProtocolToken = "token-abc" },
        new HandshakeResponse
        {
            ContractMajor = 1,
            ContractMinor = 2,
            HostVersion = "0.1.0+abc123",
            HostProcessId = 9001,
            Accepted = true,
            Reason = "ok",
        },
        new CompletionRequest
        {
            RequestId = "req-1",
            Prefix = "public int Add(int a, int b) => ",
            Suffix = ";\n}",
            LanguageId = "csharp",
            FilePath = @"C:\src\Calc.cs",
            MaxTokens = 64,
            TaskClass = TaskClass.InlineEdit,
            CaretLine = 12,
            CaretColumn = 34,
            EnclosingDeclaration = "public int Add(int a, int b)",
            OpenDocuments = [@"C:\src\Calc.cs", @"C:\src\Program.cs"],
            RecentEdits = ["Calc.cs:12 + return"],
            AllowMultiLine = true,
            StopSequences = ["\n\n"],
        },
        new CompletionChunk
        {
            RequestId = "req-1",
            Text = "a + b",
            IsFinal = true,
            ModelId = "qwen2.5-coder:7b",
            ProviderId = "ollama",
            PromptTokens = 120,
            CompletionTokens = 3,
            Error = new PillionError { Code = PillionErrorCode.ProviderUnavailable, Message = "The stream was cut off." },
        },
        new ChatMessage { Role = ChatRole.Assistant, Content = "Hello." },
        new ChatRequest
        {
            RequestId = "req-2",
            Messages =
            [
                new ChatMessage { Role = ChatRole.System, Content = "Be brief." },
                new ChatMessage { Role = ChatRole.User, Content = "Explain this method." },
            ],
            ModelId = "local/qwen-coder",
            Profile = Profile.LocalStrong,
            Pins =
            [
                new MentionResolution { Mention = "@file:Calc.cs", IsResolved = true, FilePath = @"C:\src\Calc.cs", Content = "class Calc { }", Kind = "file", DisplayName = "Calc.cs", TokenCount = 5 },
            ],
            CurrentFile = @"C:\src\Program.cs",
            CurrentFileRegion = "var x = Add(1, 2);",
            ConversationId = "conv-1",
            ProjectRules = "Use file-scoped namespaces.",
            RetrievalMode = RetrievalMode.On,
            MaxRetrievedChunks = 12,
            ExcludedContextKeys = ["0123456789abcdef0123456789abcdef:7", "repoMap"],
        },
        new ChatChunk
        {
            RequestId = "req-2",
            Text = "It adds.",
            IsFinal = true,
            ModelId = "qwen2.5-coder:14b",
            ProviderId = "ollama",
            PromptTokens = 2048,
            CompletionTokens = 40,
            Error = new PillionError { Code = PillionErrorCode.Cancelled, Message = "Stopped." },
            Citations = [Cite(-1), Cite(42)],
        },
        new InlineEditDiagnostic { Line = 14, Severity = DiagnosticSeverity.Error, Code = "CS0103", Message = "The name 'x' does not exist." },
        new InlineEditRequest
        {
            RequestId = "ie-1",
            FilePath = @"C:\src\Calc.cs",
            LanguageId = "csharp",
            ScopeText = "public int Add(int a, int b) => a + b;",
            ScopeStartLine = 12,
            ScopeStartColumn = 4,
            TextBefore = "public class Calc\n{\n",
            TextAfter = "\n}\n",
            Instruction = "Check for overflow.",
            Command = InlineEditCommand.Fix,
            Diagnostics = [new InlineEditDiagnostic { Line = 12, Severity = DiagnosticSeverity.Warning, Code = "CA1062", Message = "Validate arguments." }],
            PreviousProposal = "public int Add(int a, int b) => checked(a + b);",
            ModelId = "ollama/qwen2.5-coder:7b",
            Profile = Profile.LocalStrong,
            ConversationId = "conv-1",
        },
        new InlineEditChunk
        {
            RequestId = "ie-1",
            Text = "public int Add(int a, int b) => checked(a + b);",
            IsFinal = true,
            Explanation = "Uses checked arithmetic.",
            ModelId = "qwen2.5-coder:7b",
            ProviderId = "ollama",
            PromptTokens = 900,
            CompletionTokens = 60,
            Error = new PillionError { Code = PillionErrorCode.PolicyDenied, Message = "Unresolved placeholder." },
            RejectedBlocks = 1,
            Warning = "One more syntax error than the original.",
            ResetText = true,
        },
        new ModelDescriptor
        {
            Id = "local/qwen-coder",
            DisplayName = "Qwen Coder",
            ProviderId = "ollama",
            IsLocal = true,
            Capabilities = ModelCapabilities.Chat | ModelCapabilities.Fim | ModelCapabilities.Tools,
            ModelId = "qwen-coder",
            IsAvailable = true,
            ContextWindow = 32768,
            Digest = "sha256:7fbdbf8f5e45",
        },
        new AutocompleteHostCounters
        {
            Requests = 120,
            CacheHits = 40,
            TypeThroughHits = 20,
            CacheMisses = 60,
            HitRate = 0.5,
            ModelRequests = 61,
            Dropped = 1,
            Failed = 2,
            Cancelled = 30,
            WindowSeconds = 300,
            WindowRequests = 80,
            WindowCacheHits = 35,
            FirstTokenP50Ms = 410.5,
            FirstTokenP95Ms = 980.25,
            TotalP50Ms = 520.5,
            TotalP95Ms = 1210.25,
            PromptCountedRequests = 40,
            PromptEstimatedRequests = 5,
            PromptTokensCounted = 36000,
            PromptTokensEstimated = 4200,
            PromptTokensMax = 1010,
            ResidentModels = [new ResidentModelInfo { ProviderId = "ollama", ModelId = "qwen2.5-coder:7b", ContextLength = 4096, VramBytes = 4748056984, KeepAlive = "-1", IsPinned = true }],
            PromptPrefixChecks = 40,
            PromptPrefixReuses = 31,
            PromptPrefixReuseRate = 0.775,
        },
        new ResidentModelInfo { ProviderId = "ollama", ModelId = "gemma4:e2b", ContextLength = 4096, VramBytes = 1708392447, KeepAlive = "-1", IsPinned = true },
        new OutboundRecordDto
        {
            TimestampUtc = new DateTime(2026, 10, 10, 9, 30, 15, 250, DateTimeKind.Utc),
            RequestId = "req-42",
            ProviderId = "ollama",
            ModelId = "gemma4:e2b",
            TaskClass = TaskClass.Chat,
            IsLocal = true,
            PromptTokens = 1200,
            CompletionTokens = 340,
            RedactionCount = 2,
            ContextItems = 5,
            ContextTrimmed = 1,
            DurationMs = 2450,
            Outcome = "Failed",
            ErrorCode = PillionErrorCode.ModelNotInstalled,
            CacheHit = true,
        },
        new ContextReportDto
        {
            ModelId = "gemma4:e2b",
            ProviderId = "ollama",
            IsLocal = true,
            ContextWindow = 8192,
            OutputReserve = 2048,
            BudgetTokens = 5900,
            TotalTokens = 3100,
            Items = [new ContextReportItemDto { Kind = "pin", Name = "Program.cs", Tokens = 900, OriginalTokens = 1500, Truncated = true, Dropped = true, Note = "Cut to fit the pin cap.", PinIndex = 0, RedactionCount = 1 }],
            IsAssembled = true,
            Redacts = true,
            RedactionCount = 1,
        },
        new ContextReportItemDto { Kind = "history", Name = "Recent history (4 messages)", Tokens = 400, OriginalTokens = 800, Truncated = true, Dropped = true, Note = "Summarised.", PinIndex = 2, RedactionCount = 3, ContextKey = "repoMap" },

        new SearchRequest
        {
            Query = "where is the pipe opened",
            MaxResults = 10,
            Roots = [@"C:\src"],
            Partitions = ["src/Pillion.Host"],
            Languages = ["csharp"],
            Mode = SearchMode.Lexical,
            MaxPerFile = 2,
            IncludeText = true,
            FocusFiles = [@"C:\src\Pipe.cs"],
        },
        Hit(),
        new SearchResponse { Results = [Hit()], LexicalMs = 4.5, VectorMs = 7.25, FusionMs = 0.5, TotalMs = 13.75, Degraded = true },
        new MentionRequest
        {
            Mention = "@file:Program.cs",
            Kind = "file",
            WorkspaceRoots = [@"C:\src"],
            CandidateFiles = [@"C:\src\Program.cs", @"C:\src\Other\Program.cs"],
            Payload = "selected text",
            MaxTokens = 2000,
            ApprovedSensitive = true,
            Qualifier = MentionKinds.Git,
            Arguments = ["log", "5"],
        },
        new MentionResolution
        {
            Mention = "@file:Program.cs",
            IsResolved = true,
            FilePath = @"C:\src\Program.cs",
            Content = "// program",
            Kind = "file",
            DisplayName = "Program.cs",
            TokenCount = 3,
            Truncated = true,
            Candidates = [@"C:\src\Program.cs", @"C:\src\Other\Program.cs"],
            Reason = "Two files match.",
            RequiresApproval = true,
            IsSensitive = true,
            Citations = [Cite(-1)],
        },
        new IndexStatus
        {
            IsIndexing = true,
            IsPaused = true,
            IndexedFileCount = 120,
            TotalFileCount = 300,
            LastUpdatedUtc = new DateTime(2026, 10, 8, 12, 34, 56, 789, DateTimeKind.Utc),
            Phase = IndexPhase.Paused,
            PauseReason = IndexPauseReason.Battery,
            Roots = [Root()],
            ExcludedSecretFileCount = 3,
            LastError = "The embedding model could not be loaded.",
            Progress = Progress(),
        },
        Root(),
        Progress(),
        new ExcludedFilesRequest { RootPath = @"C:\src", Offset = 200, Limit = 100, SecretOnly = true },
        Excluded(),
        new ExcludedFilesPage { Items = [Excluded()], TotalCount = 7, HasMore = true },
        new IndexInfo
        {
            IndexId = "0123456789abcdef0123456789abcdef",
            RootPath = @"C:\src",
            EmbeddingModelId = "bge-small-en-v1.5",
            ChunkerVersion = "2",
            DiskBytes = 15_000_000,
            LastUsedUtc = new DateTime(2026, 10, 1, 7, 0, 0, DateTimeKind.Utc),
            RootExists = true,
            IsInUse = true,
            IsOpen = true,
        },
        new WorkspaceDescription
        {
            SolutionPath = @"C:\src\Shop\Shop.sln",
            ProjectPaths = [@"C:\src\Shop\Api\Api.csproj", @"C:\src\Shop\Web\Web.csproj"],
            FolderPath = @"C:\src\Shop",
        },
        Cite(17),
        new ConversationListRequest { Offset = 50, Limit = 25 },
        Summary(),
        new ConversationPage { Items = [Summary()], HasMore = true },
        Message(),
        Pin(),
        new ConversationDetail { Conversation = Summary(), Messages = [Message()], Pins = [Pin()] },
        new ConversationCreateRequest { Id = "conv-9", Title = "Order service", ModelId = "ollama/qwen2.5-coder:14b" },
        new DeleteAllConversationsResult { Conversations = 4, Messages = 40, Pins = 6, OutboundEntries = 120 },
        new AgentSessionRequest { Goal = "Add a unit test.", Profile = Profile.Frontier, ModelId = "cloud/frontier" },
        new ProviderTestResult { ProviderId = "openai", IsSuccess = true, Message = "Connected." },
        new FileEdit { Path = @"C:\src\Foo.cs", SearchText = "int x", ReplaceText = "long x" },
        new Changeset
        {
            Id = "cs-1",
            Edits =
            [
                new FileEdit { Path = @"C:\src\Foo.cs", SearchText = "a", ReplaceText = "b" },
                new FileEdit { Path = @"C:\src\Bar.cs", SearchText = "c", ReplaceText = "d" },
            ],
            Description = "Rename variables.",
        },
        new ApplyResult { ChangesetId = "cs-1", IsSuccess = true, FailedPaths = [@"C:\src\Bar.cs"], Message = "One edit failed." },
        new PreviewDecision { ChangesetId = "cs-1", IsAccepted = true },
        new OpenDocument { Path = @"C:\src\Foo.cs", LanguageId = "csharp", IsDirty = true, IsActive = true },
        new ApprovalRequest
        {
            ApprovalId = "ap-1",
            ToolName = "run_command",
            Tier = PermissionTier.T2,
            ArgumentsSummary = "dotnet test",
            Provenance = "agent step 3",
            TaintReason = "Arguments include text from a web page.",
        },
        new ApprovalDecision { ApprovalId = "ap-1", IsApproved = true, Reason = "Looks safe." },
        Location(7),
        new FindReferencesRequest { Location = Location(1) },
        new FindReferencesResponse { References = [Location(2), Location(3)] },
        new DiagnosticsRequest { FilePath = @"C:\src\Foo.cs" },
        new DiagnosticInfo { Id = "CS0103", Message = "The name 'x' does not exist.", Severity = DiagnosticSeverity.Error, Location = Location(5) },
        new DiagnosticsResponse
        {
            Diagnostics =
            [
                new DiagnosticInfo { Id = "CS0168", Message = "Unused variable.", Severity = DiagnosticSeverity.Warning, Location = Location(6) },
            ],
        },
        new SymbolRequest { Location = Location(8) },
        new SymbolResponse { IsFound = true, Name = "Pillion.Foo.Bar()", Kind = "Method", Definition = Location(9) },
        new RenameSymbolRequest { Location = Location(10), NewName = "Baz" },
        new RenameSymbolResponse { IsSuccess = true, ChangedFiles = [@"C:\src\Foo.cs", @"C:\src\Bar.cs"], Message = "Renamed." },
        new RunTestsRequest { Filter = "FullyQualifiedName~Pillion" },
        new RunTestsResponse { Passed = 10, Failed = 2, Skipped = 1, FailedTests = ["A.B", "C.D"] },
        new CallersOfRequest { SymbolName = "PlaceOrderAsync", Container = "Shop.Orders.OrderService", FilePath = @"C:\src\OrderService.cs", Line = 41, MaxResults = 20 },
        Caller(),
        new CallersOfResponse { IsFound = true, Callers = [Caller()], Truncated = true },
    ];

    private static Citation Cite(int replyOffset) => new()
    {
        FilePath = @"C:\src\Pipe.cs",
        StartLine = 10,
        EndLine = 20,
        ChunkId = "0123456789abcdef0123456789abcdef:7",
        Breadcrumb = "src/Pipe.cs > Pillion > Pipe > Open",
        ReplyOffset = replyOffset,
    };

    private static SearchResult Hit() => new()
    {
        FilePath = @"C:\src\Pipe.cs",
        StartLine = 10,
        EndLine = 20,
        Snippet = "new NamedPipeServerStream(",
        Score = 0.875,
        ChunkId = "0123456789abcdef0123456789abcdef:7",
        Breadcrumb = "src/Pipe.cs > Pillion > Pipe > Open",
        Kind = "method",
        Partition = "src",
        Language = "csharp",
        LexicalRank = 1,
        VectorRank = 3,
        FusedScore = 0.0325,
        Text = "public void Open() { }",
    };

    private static IndexRootStatus Root() => new()
    {
        RootPath = @"C:\src",
        IndexId = "0123456789abcdef0123456789abcdef",
        Role = IndexRole.Reader,
        IndexedFileCount = 100,
        TotalFileCount = 150,
        ExcludedFileCount = 30,
        SecretFileCount = 3,
        ChunkCount = 9000,
        DiskBytes = 13_500_000,
        LastFullSweepUtc = new DateTime(2026, 10, 14, 18, 0, 0, DateTimeKind.Utc),
        EmbeddingModelId = "bge-small-en-v1.5",
        ChunkerVersion = "2",
        SchemaVersion = 2,
    };

    private static IndexProgress Progress() => new() { FilesDone = 20, FilesTotal = 50, ChunksDone = 1800, EstimatedRemainingSeconds = 95 };

    private static ExcludedFile Excluded() => new()
    {
        Path = ".env",
        Reason = "SecretBearing",
        Description = "Not indexed: the file may hold secrets (.env).",
        IsSecretBearing = true,
        IsDirectory = true,
    };

    private static ConversationSummary Summary() => new()
    {
        Id = "conv-1",
        Title = "Why does the build fail?",
        CreatedUtc = new DateTime(2026, 10, 14, 9, 0, 0, DateTimeKind.Utc),
        UpdatedUtc = new DateTime(2026, 10, 14, 9, 5, 0, DateTimeKind.Utc),
        LastModelId = "ollama/qwen2.5-coder:14b",
        MessageCount = 6,
    };

    private static ConversationMessage Message() => new()
    {
        Id = 41,
        Role = ChatRole.Assistant,
        Content = "It calls a missing method.",
        ModelId = "qwen2.5-coder:14b",
        ProviderId = "ollama",
        PromptTokens = 1900,
        CompletionTokens = 60,
        CreatedUtc = new DateTime(2026, 10, 14, 9, 5, 0, DateTimeKind.Utc),
        Citations = [Cite(12)],
        Cancelled = true,
    };

    private static ConversationPin Pin() => new() { Kind = MentionKinds.File, Target = @"C:\src\Program.cs", DisplayName = "Program.cs", TokenCount = 300 };

    private static CallSite Caller() => new()
    {
        FilePath = @"C:\src\CheckoutController.cs",
        Line = 77,
        Snippet = "await _orders.PlaceOrderAsync(cart, ct);",
        CallerName = "Shop.Web.CheckoutController.Checkout",
        IsApproximate = true,
    };
}

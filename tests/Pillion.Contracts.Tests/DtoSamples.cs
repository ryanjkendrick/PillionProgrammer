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
        },
        new SearchRequest { Query = "where is the pipe opened", MaxResults = 10 },
        new SearchResult { FilePath = @"C:\src\Pipe.cs", StartLine = 10, EndLine = 20, Snippet = "new NamedPipeServerStream(", Score = 0.875 },
        new MentionRequest { Mention = "@file:Program.cs" },
        new MentionResolution { Mention = "@file:Program.cs", IsResolved = true, FilePath = @"C:\src\Program.cs", Content = "// program" },
        new IndexStatus
        {
            IsIndexing = true,
            IsPaused = true,
            IndexedFileCount = 120,
            TotalFileCount = 300,
            LastUpdatedUtc = new DateTime(2026, 10, 8, 12, 34, 56, 789, DateTimeKind.Utc),
        },
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
    ];
}

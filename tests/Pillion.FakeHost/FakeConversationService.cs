using Pillion.Contracts;

namespace Pillion.FakeHost;

/// <summary>
/// An in-memory <see cref="IConversationService"/> (contract 1.3) for the VSIX tests of work item 2.2. It starts with two canned
/// conversations (<see cref="WelcomeId"/>, older, and <see cref="BuildId"/>, newer) and follows the contract's rules: newest
/// first, offset paging, create returns an existing conversation unchanged, rename ignores an empty title, set pins and rename
/// and delete answer <see langword="false"/> for an unknown id. Like Pillion Host, it has no append method:
/// <see cref="FakeModelService"/> calls <see cref="Append"/> for every chat request with a conversation id, creating the
/// conversation on first use. Only ids and counts are written to stderr, never text.
/// </summary>
internal sealed class FakeConversationService : IConversationService
{
    /// <summary>The id of the older canned conversation.</summary>
    public const string WelcomeId = "fake-conversation-welcome";

    /// <summary>The id of the newer canned conversation.</summary>
    public const string BuildId = "fake-conversation-build";

    private readonly Lock _gate = new();
    private readonly Dictionary<string, Entry> _conversations = new(StringComparer.Ordinal);
    private long _nextMessageId = 1;
    private int _outboundEntries;

    public FakeConversationService()
    {
        var day = new DateTime(2026, 10, 14, 9, 0, 0, DateTimeKind.Utc);
        var welcome = Add(WelcomeId, "Welcome to Pillion", day, FakeModelService.LocalModelId);
        AddMessage(welcome, ChatRole.User, "What can you do?", day, null, null, 0, 0);
        AddMessage(welcome, ChatRole.Assistant, "I can explain, edit and search your code.", day.AddSeconds(5), "fake-local-coder", "ollama", 40, 12);

        var build = Add(BuildId, "Fix the build", day.AddHours(3), FakeModelService.CloudModelId);
        AddMessage(build, ChatRole.User, "Why does the build fail?", day.AddHours(3), null, null, 0, 0);
        var reply = AddMessage(build, ChatRole.Assistant, "Program.cs calls a method that no longer exists.", day.AddHours(3).AddSeconds(4), "fake-cloud-chat", "fake-cloud", 900, 30);
        reply.Citations = [new Citation { FilePath = @"C:\fake\src\Program.cs", StartLine = 11, EndLine = 14, ChunkId = FakeContextService.CannedIndexId + ":7", Breadcrumb = "src/Program.cs > Fake > Program > Main", ReplyOffset = 48 }];
        build.Pins.Add(new ConversationPin { Kind = MentionKinds.File, Target = @"C:\fake\src\Program.cs", DisplayName = "Program.cs", TokenCount = 120 });
    }

    /// <summary>Counts an Outbound record, so Delete all can report it (the fake keeps the records in <see cref="FakeRecorder"/>).</summary>
    public void CountOutbound()
    {
        lock (_gate)
        {
            _outboundEntries++;
        }
    }

    public Task<ConversationPage> ListConversationsAsync(ConversationListRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var limit = request.Limit <= 0 ? 50 : Math.Min(request.Limit, 500);
        var offset = Math.Max(0, request.Offset);
        lock (_gate)
        {
            var ordered = _conversations.Values.OrderByDescending(c => c.Summary.UpdatedUtc).ThenBy(c => c.Summary.Id, StringComparer.Ordinal).ToList();
            var page = new ConversationPage
            {
                Items = [.. ordered.Skip(offset).Take(limit).Select(c => Copy(c.Summary))],
                HasMore = offset + limit < ordered.Count,
            };
            Console.Error.WriteLine("fakehost: conversations list " + page.Items.Length + " of " + ordered.Count);
            return Task.FromResult(page);
        }
    }

    public Task<ConversationDetail?> GetConversationAsync(string conversationId, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            if (!_conversations.TryGetValue(conversationId ?? string.Empty, out var entry))
            {
                Console.Error.WriteLine("fakehost: conversation get unknown");
                return Task.FromResult<ConversationDetail?>(null);
            }

            Console.Error.WriteLine("fakehost: conversation get " + entry.Summary.Id + ", " + entry.Messages.Count + " messages");
            return Task.FromResult<ConversationDetail?>(new ConversationDetail
            {
                Conversation = Copy(entry.Summary),
                Messages = [.. entry.Messages],
                Pins = [.. entry.Pins],
            });
        }
    }

    public Task<ConversationSummary> CreateConversationAsync(ConversationCreateRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        lock (_gate)
        {
            var id = string.IsNullOrWhiteSpace(request.Id) ? Guid.NewGuid().ToString("N") : request.Id;
            if (!_conversations.TryGetValue(id, out var entry))
            {
                entry = Add(id, string.IsNullOrWhiteSpace(request.Title) ? "New conversation" : request.Title.Trim(), DateTime.UtcNow, request.ModelId);
                Console.Error.WriteLine("fakehost: conversation created " + id);
            }

            return Task.FromResult(Copy(entry.Summary));
        }
    }

    public Task<bool> RenameConversationAsync(string conversationId, string title, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            if (!_conversations.TryGetValue(conversationId ?? string.Empty, out var entry))
            {
                return Task.FromResult(false);
            }

            if (!string.IsNullOrWhiteSpace(title))
            {
                entry.Summary.Title = title.Trim();
            }

            Console.Error.WriteLine("fakehost: conversation renamed " + entry.Summary.Id);
            return Task.FromResult(true);
        }
    }

    public Task<bool> DeleteConversationAsync(string conversationId, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            var deleted = _conversations.Remove(conversationId ?? string.Empty);
            Console.Error.WriteLine("fakehost: conversation delete " + (deleted ? "done" : "unknown"));
            return Task.FromResult(deleted);
        }
    }

    public Task<DeleteAllConversationsResult> DeleteAllConversationsAsync(CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            var result = new DeleteAllConversationsResult
            {
                Conversations = _conversations.Count,
                Messages = _conversations.Values.Sum(c => c.Messages.Count),
                Pins = _conversations.Values.Sum(c => c.Pins.Count),
                OutboundEntries = _outboundEntries,
            };
            _conversations.Clear();
            _outboundEntries = 0;
            Console.Error.WriteLine("fakehost: conversations deleted " + result.Conversations);
            return Task.FromResult(result);
        }
    }

    public Task<bool> SetPinsAsync(string conversationId, ConversationPin[] pins, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            if (!_conversations.TryGetValue(conversationId ?? string.Empty, out var entry))
            {
                return Task.FromResult(false);
            }

            // A repeated kind and target keeps the last, in the position of the first.
            var kept = new List<ConversationPin>();
            foreach (var pin in pins ?? [])
            {
                var index = kept.FindIndex(p => p.Kind == pin.Kind && string.Equals(p.Target, pin.Target, StringComparison.OrdinalIgnoreCase));
                if (index >= 0)
                {
                    kept[index] = pin;
                }
                else
                {
                    kept.Add(pin);
                }
            }

            entry.Pins.Clear();
            entry.Pins.AddRange(kept);
            Console.Error.WriteLine("fakehost: conversation pins " + entry.Summary.Id + " " + kept.Count);
            return Task.FromResult(true);
        }
    }

    /// <summary>Appends a chat turn as Pillion Host does while a chat runs: the last user message and the reply.</summary>
    /// <param name="request">The chat request; nothing happens when it has no conversation id.</param>
    /// <param name="final">The reply's final chunk (model, provider, tokens, citations).</param>
    /// <param name="replyText">The reply text.</param>
    public void Append(ChatRequest request, ChatChunk final, string replyText)
    {
        if (string.IsNullOrEmpty(request.ConversationId))
        {
            return;
        }

        var user = request.Messages.LastOrDefault(m => m.Role == ChatRole.User)?.Content ?? string.Empty;
        lock (_gate)
        {
            var now = DateTime.UtcNow;
            if (!_conversations.TryGetValue(request.ConversationId, out var entry))
            {
                var title = user.Length == 0 ? "New conversation" : user.Length > 40 ? user[..40] : user;
                entry = Add(request.ConversationId, title, now, null);
            }

            AddMessage(entry, ChatRole.User, user, now, null, null, 0, 0);
            var reply = AddMessage(entry, ChatRole.Assistant, replyText, now, final.ModelId, final.ProviderId, final.PromptTokens, final.CompletionTokens);
            reply.Citations = final.Citations;
            entry.Summary.LastModelId = final.ProviderId + "/" + final.ModelId;
            Console.Error.WriteLine("fakehost: conversation " + entry.Summary.Id + " now " + entry.Messages.Count + " messages");
        }
    }

    private static ConversationSummary Copy(ConversationSummary summary) => new()
    {
        Id = summary.Id,
        Title = summary.Title,
        CreatedUtc = summary.CreatedUtc,
        UpdatedUtc = summary.UpdatedUtc,
        LastModelId = summary.LastModelId,
        MessageCount = summary.MessageCount,
    };

    private Entry Add(string id, string title, DateTime created, string? modelId)
    {
        var entry = new Entry(new ConversationSummary { Id = id, Title = title, CreatedUtc = created, UpdatedUtc = created, LastModelId = modelId });
        _conversations[id] = entry;
        return entry;
    }

    private ConversationMessage AddMessage(Entry entry, ChatRole role, string content, DateTime created, string? modelId, string? providerId, int promptTokens, int completionTokens)
    {
        var message = new ConversationMessage
        {
            Id = _nextMessageId++,
            Role = role,
            Content = content,
            ModelId = modelId,
            ProviderId = providerId,
            PromptTokens = promptTokens,
            CompletionTokens = completionTokens,
            CreatedUtc = created,
        };
        entry.Messages.Add(message);
        entry.Summary.MessageCount = entry.Messages.Count;
        entry.Summary.UpdatedUtc = created > entry.Summary.UpdatedUtc ? created : entry.Summary.UpdatedUtc;
        return message;
    }

    private sealed record Entry(ConversationSummary Summary)
    {
        public List<ConversationMessage> Messages { get; } = [];

        public List<ConversationPin> Pins { get; } = [];
    }
}

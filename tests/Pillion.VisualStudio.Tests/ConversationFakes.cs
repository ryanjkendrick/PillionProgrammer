using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Pillion.Contracts;

namespace Pillion.VisualStudio.Tests;

/// <summary>
/// An in-memory <see cref="IConversationService"/> for the Chat window's tests: it keeps conversations as the host does (newest
/// first, offset paging, create returns an existing conversation, empty rename keeps the title), records every call, and fails or
/// waits on request. The host's own behaviour is tested in Pillion.Host.Tests; the real fake host process is used by
/// <c>ConversationRealHostTests</c>.
/// </summary>
internal sealed class ScriptedConversationService : IConversationService
{
    private readonly object _gate = new();
    private readonly Dictionary<string, Entry> _entries = new(StringComparer.Ordinal);
    private long _nextMessageId = 1;

    /// <summary>Gets every call as <c>name:argument</c>, in order (<c>list:0/50</c>, <c>get:id</c>, <c>rename:id:title</c>, <c>delete:id</c>, <c>create:id:title</c>, <c>pins:id:n</c>, <c>deleteAll</c>).</summary>
    public ConcurrentQueue<string> Calls { get; } = new();

    /// <summary>Gets or sets an exception every call throws (the host cannot save conversations).</summary>
    public Exception? Failure { get; set; }

    /// <summary>Gets or sets an exception only <see cref="GetConversationAsync"/> throws.</summary>
    public Exception? GetFailure { get; set; }

    /// <summary>Gets or sets a task <see cref="GetConversationAsync"/> waits for before answering.</summary>
    public Task? GetGate { get; set; }

    /// <summary>Gets the last pins set for each conversation.</summary>
    public ConcurrentDictionary<string, ConversationPin[]> PinsSet { get; } = new(StringComparer.Ordinal);

    public int CallCount(string prefix) => Calls.Count(c => c.StartsWith(prefix, StringComparison.Ordinal));

    /// <summary>Adds a conversation the host already has.</summary>
    public ConversationSummary Seed(string id, string title, DateTime updatedUtc, IEnumerable<ConversationMessage>? messages = null, IEnumerable<ConversationPin>? pins = null)
    {
        lock (_gate)
        {
            var entry = new Entry(new ConversationSummary { Id = id, Title = title, CreatedUtc = updatedUtc, UpdatedUtc = updatedUtc });
            entry.Messages.AddRange(messages ?? []);
            entry.Pins.AddRange(pins ?? []);
            entry.Summary.MessageCount = entry.Messages.Count;
            _entries[id] = entry;
            return entry.Summary;
        }
    }

    public ConversationMessage Message(ChatRole role, string content, string? modelId = null, string? providerId = null, params Citation[] citations)
    {
        lock (_gate)
        {
            return new ConversationMessage
            {
                Id = _nextMessageId++,
                Role = role,
                Content = content,
                ModelId = modelId,
                ProviderId = providerId,
                CreatedUtc = DateTime.UtcNow,
                Citations = citations,
            };
        }
    }

    public bool Has(string id)
    {
        lock (_gate)
        {
            return _entries.ContainsKey(id);
        }
    }

    public string TitleOf(string id)
    {
        lock (_gate)
        {
            return _entries[id].Summary.Title;
        }
    }

    public Task<ConversationPage> ListConversationsAsync(ConversationListRequest request, CancellationToken cancellationToken)
    {
        var limit = request.Limit <= 0 ? 50 : Math.Min(request.Limit, 500);
        Calls.Enqueue("list:" + request.Offset + "/" + limit);
        if (Failure is { } failure)
        {
            return Task.FromException<ConversationPage>(failure);
        }

        lock (_gate)
        {
            var ordered = _entries.Values.OrderByDescending(e => e.Summary.UpdatedUtc).ThenBy(e => e.Summary.Id, StringComparer.Ordinal).ToList();
            return Task.FromResult(new ConversationPage
            {
                Items = [.. ordered.Skip(request.Offset).Take(limit).Select(e => Copy(e.Summary))],
                HasMore = request.Offset + limit < ordered.Count,
            });
        }
    }

    public async Task<ConversationDetail?> GetConversationAsync(string conversationId, CancellationToken cancellationToken)
    {
        Calls.Enqueue("get:" + conversationId);
        if (GetGate is { } gate)
        {
            await gate.ConfigureAwait(false);
        }

        if (Failure is { } failure)
        {
            throw failure;
        }

        if (GetFailure is { } getFailure)
        {
            throw getFailure;
        }

        lock (_gate)
        {
            return _entries.TryGetValue(conversationId, out var entry)
                ? new ConversationDetail { Conversation = Copy(entry.Summary), Messages = [.. entry.Messages], Pins = [.. entry.Pins] }
                : null;
        }
    }

    public Task<ConversationSummary> CreateConversationAsync(ConversationCreateRequest request, CancellationToken cancellationToken)
    {
        Calls.Enqueue("create:" + request.Id + ":" + request.Title);
        if (Failure is { } failure)
        {
            return Task.FromException<ConversationSummary>(failure);
        }

        lock (_gate)
        {
            if (!_entries.TryGetValue(request.Id, out var entry))
            {
                entry = new Entry(new ConversationSummary { Id = request.Id, Title = request.Title, CreatedUtc = DateTime.UtcNow, UpdatedUtc = DateTime.UtcNow });
                _entries[request.Id] = entry;
            }

            return Task.FromResult(Copy(entry.Summary));
        }
    }

    public Task<bool> RenameConversationAsync(string conversationId, string title, CancellationToken cancellationToken)
    {
        Calls.Enqueue("rename:" + conversationId + ":" + title);
        if (Failure is { } failure)
        {
            return Task.FromException<bool>(failure);
        }

        lock (_gate)
        {
            if (!_entries.TryGetValue(conversationId, out var entry))
            {
                return Task.FromResult(false);
            }

            if (!string.IsNullOrWhiteSpace(title))
            {
                entry.Summary.Title = title.Trim();
            }

            return Task.FromResult(true);
        }
    }

    public Task<bool> DeleteConversationAsync(string conversationId, CancellationToken cancellationToken)
    {
        Calls.Enqueue("delete:" + conversationId);
        if (Failure is { } failure)
        {
            return Task.FromException<bool>(failure);
        }

        lock (_gate)
        {
            return Task.FromResult(_entries.Remove(conversationId));
        }
    }

    public Task<DeleteAllConversationsResult> DeleteAllConversationsAsync(CancellationToken cancellationToken)
    {
        Calls.Enqueue("deleteAll");
        if (Failure is { } failure)
        {
            return Task.FromException<DeleteAllConversationsResult>(failure);
        }

        lock (_gate)
        {
            var result = new DeleteAllConversationsResult
            {
                Conversations = _entries.Count,
                Messages = _entries.Values.Sum(e => e.Messages.Count),
                Pins = _entries.Values.Sum(e => e.Pins.Count),
                OutboundEntries = 7,
            };
            _entries.Clear();
            return Task.FromResult(result);
        }
    }

    public Task<bool> SetPinsAsync(string conversationId, ConversationPin[] pins, CancellationToken cancellationToken)
    {
        Calls.Enqueue("pins:" + conversationId + ":" + pins.Length);
        if (Failure is { } failure)
        {
            return Task.FromException<bool>(failure);
        }

        lock (_gate)
        {
            if (!_entries.TryGetValue(conversationId, out var entry))
            {
                return Task.FromResult(false);
            }

            entry.Pins.Clear();
            entry.Pins.AddRange(pins);
            PinsSet[conversationId] = pins;
            return Task.FromResult(true);
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

    private sealed class Entry(ConversationSummary summary)
    {
        public ConversationSummary Summary { get; } = summary;

        public List<ConversationMessage> Messages { get; } = [];

        public List<ConversationPin> Pins { get; } = [];
    }
}

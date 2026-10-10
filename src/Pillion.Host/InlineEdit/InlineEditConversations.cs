namespace Pillion.Host.InlineEdit;

/// <summary>
/// The instructions of recent inline chat sessions, by <c>ConversationId</c>, so a refinement can show the model what
/// was asked before. In memory only (phase 1 keeps conversations in memory; decision 2026-10-08) and bounded: the
/// oldest session is forgotten first, and each keeps its latest instructions.
/// </summary>
internal sealed class InlineEditConversations
{
    /// <summary>The most sessions remembered.</summary>
    public const int MaxConversations = 64;

    /// <summary>The most instructions remembered per session.</summary>
    public const int MaxInstructions = 8;

    private readonly object _gate = new();
    private readonly Dictionary<string, List<string>> _byId = new(StringComparer.Ordinal);
    private readonly LinkedList<string> _order = new();

    /// <summary>Gets the earlier instructions of a session, oldest first.</summary>
    /// <param name="conversationId">The session id, or <see langword="null"/>.</param>
    /// <returns>The instructions; empty for an unknown or missing id.</returns>
    public IReadOnlyList<string> Get(string? conversationId)
    {
        if (string.IsNullOrEmpty(conversationId))
        {
            return [];
        }

        lock (_gate)
        {
            return _byId.TryGetValue(conversationId, out var list) ? [.. list] : [];
        }
    }

    /// <summary>Records an instruction that produced a proposal.</summary>
    /// <param name="conversationId">The session id, or <see langword="null"/> (then nothing is recorded).</param>
    /// <param name="instruction">The instruction, as shown to the model later.</param>
    public void Add(string? conversationId, string instruction)
    {
        if (string.IsNullOrEmpty(conversationId) || string.IsNullOrWhiteSpace(instruction))
        {
            return;
        }

        lock (_gate)
        {
            if (!_byId.TryGetValue(conversationId, out var list))
            {
                list = [];
                _byId[conversationId] = list;
                _order.AddLast(conversationId);
                while (_order.Count > MaxConversations)
                {
                    _byId.Remove(_order.First!.Value);
                    _order.RemoveFirst();
                }
            }

            list.Add(instruction);
            if (list.Count > MaxInstructions)
            {
                list.RemoveAt(0);
            }
        }
    }
}

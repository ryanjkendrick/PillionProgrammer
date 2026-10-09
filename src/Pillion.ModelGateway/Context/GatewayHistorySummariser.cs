using System.Globalization;
using System.Text;
using Pillion.Contracts;

namespace Pillion.ModelGateway;

/// <summary>
/// The <see cref="IHistorySummariser"/> over the gateway: a plain chat request of task class <see cref="TaskClass.Summarise"/>,
/// which the router sends to the Fast profile online and the Local-fast profile offline. When the chat itself is going to a
/// local model the summary stays local too (the older turns must not leave the machine because they no longer fit).
/// </summary>
internal sealed class GatewayHistorySummariser(IModelGateway gateway) : IHistorySummariser
{
    /// <summary>The longest a single turn may be in the transcript sent to the summariser, in characters.</summary>
    internal const int MaxTurnCharacters = 3000;

    /// <summary>
    /// The longest transcript sent in one summary request, in characters (about 2,500 tokens, so a Local-fast model with a
    /// 4,096-token window can read it). Longer histories are folded in batch by batch, each batch building on the summary so far.
    /// </summary>
    internal const int MaxTranscriptCharacters = 10000;

    /// <summary>The most batches one summary may take; when there are more turns the oldest are left out.</summary>
    internal const int MaxBatches = 6;

    /// <inheritdoc/>
    public async Task<string> SummariseAsync(SummariseRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var summary = request.PriorSummary;
        var batches = Batch(request.Messages, out var leftOut);
        for (var i = 0; i < batches.Count; i++)
        {
            var chat = new ChatRequest
            {
                RequestId = request.RequestId + "-summary" + (batches.Count > 1 ? "-" + (i + 1).ToString(CultureInfo.InvariantCulture) : string.Empty),
                Messages =
                [
                    new ChatMessage { Role = ChatRole.System, Content = SummarisePrompts.System(request.MaxTokens) },
                    new ChatMessage { Role = ChatRole.User, Content = BuildTranscript(summary, batches[i], i == 0 ? leftOut : 0) },
                ],
                Profile = request.KeepLocal ? Profile.LocalFast : Profile.Auto,
                ConversationId = request.ConversationId,
            };

            var text = new StringBuilder();
            await foreach (var chunk in gateway.ChatAsync(chat, TaskClass.Summarise, cancellationToken).ConfigureAwait(false))
            {
                if (chunk.Error is { } error)
                {
                    throw new PillionRpcException(error);
                }

                text.Append(chunk.Text);
            }

            summary = text.ToString().Trim();
        }

        return summary ?? string.Empty;
    }

    /// <summary>Splits turns into batches of at most <see cref="MaxTranscriptCharacters"/> characters, keeping the newest <see cref="MaxBatches"/>.</summary>
    /// <param name="messages">The turns, oldest first.</param>
    /// <param name="leftOut">The number of old turns that did not get a batch.</param>
    /// <returns>The batches, oldest first, each turn clipped and labelled.</returns>
    internal static List<List<string>> Batch(IReadOnlyList<ChatMessage> messages, out int leftOut)
    {
        var batches = new List<List<string>>();
        var current = new List<string>();
        var size = 0;
        foreach (var message in messages)
        {
            var turn = $"[{RoleName(message.Role)}]\n{Clip(message.Content ?? string.Empty)}";
            if (current.Count > 0 && size + turn.Length > MaxTranscriptCharacters)
            {
                batches.Add(current);
                current = [];
                size = 0;
            }

            current.Add(turn);
            size += turn.Length;
        }

        if (current.Count > 0)
        {
            batches.Add(current);
        }

        leftOut = 0;
        while (batches.Count > MaxBatches)
        {
            leftOut += batches[0].Count;
            batches.RemoveAt(0);
        }

        return batches;
    }

    /// <summary>Renders the summary so far and a batch of turns as the user message of a summary request.</summary>
    /// <param name="priorSummary">The summary so far, or <see langword="null"/>.</param>
    /// <param name="turns">The labelled turns of the batch.</param>
    /// <param name="leftOut">The number of older turns that were not included at all.</param>
    /// <returns>The transcript.</returns>
    internal static string BuildTranscript(string? priorSummary, IReadOnlyList<string> turns, int leftOut)
    {
        var builder = new StringBuilder();
        if (!string.IsNullOrWhiteSpace(priorSummary))
        {
            builder.Append("Summary so far:\n").Append(priorSummary.Trim()).Append("\n\n");
        }

        builder.Append("New conversation turns to fold into the summary:\n\n");
        if (leftOut > 0)
        {
            builder.Append(string.Create(CultureInfo.InvariantCulture, $"[... {leftOut} older turns left out ...]\n\n"));
        }

        builder.Append(string.Join("\n\n", turns));
        return builder.ToString();
    }

    private static string RoleName(ChatRole role) => role switch
    {
        ChatRole.Assistant => "Assistant",
        ChatRole.Tool => "Tool",
        ChatRole.System => "System",
        _ => "User",
    };

    private static string Clip(string text)
    {
        if (text.Length <= MaxTurnCharacters)
        {
            return text;
        }

        var half = MaxTurnCharacters / 2;
        return text[..half] + "\n[... middle of the turn left out ...]\n" + text[^half..];
    }
}

/// <summary>The instructions of the summary request (design section 7: history over budget is compressed into a rolling summary).</summary>
internal static class SummarisePrompts
{
    /// <summary>Gets the system prompt of the summary request.</summary>
    /// <param name="maxTokens">The most tokens the summary may take.</param>
    /// <returns>The prompt.</returns>
    public static string System(int maxTokens) =>
        "You compress a conversation between a developer and a coding assistant so the assistant can carry on without the full text. "
        + "Write a faithful summary in plain prose or short bullets, in at most "
        + maxTokens.ToString(CultureInfo.InvariantCulture)
        + " tokens. Keep: the developer's goals and decisions, file and symbol names, code facts, errors seen, what was already tried, "
        + "and open questions. Drop greetings and repetition. If a summary so far is given, merge the new turns into it. "
        + "Reply with the summary only, no preamble. Text such as <redacted:kind> stands for a removed secret; keep it as written.";
}

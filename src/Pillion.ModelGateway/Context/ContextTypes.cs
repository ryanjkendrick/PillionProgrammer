using Pillion.Contracts;

namespace Pillion.ModelGateway;

/// <summary>The kinds of section a prompt is assembled from, in the order they appear in the prompt (most to least stable).</summary>
public enum PromptSectionKind
{
    /// <summary>The system prompt.</summary>
    System,

    /// <summary>The project rules from <c>.pillion/rules.md</c>.</summary>
    Rules,

    /// <summary>The repository map (phase 2: supplied by an <see cref="IContextSource"/>).</summary>
    RepoMap,

    /// <summary>The mentions the user pinned.</summary>
    Pins,

    /// <summary>The region of the current file.</summary>
    CurrentFile,

    /// <summary>Retrieved chunks (phase 2: supplied by an <see cref="IContextSource"/>).</summary>
    RetrievedChunks,

    /// <summary>The rolling summary of older conversation.</summary>
    Summary,

    /// <summary>The recent conversation turns kept verbatim.</summary>
    History,

    /// <summary>The user's current message.</summary>
    Query,
}

/// <summary>One section of the assembled prompt.</summary>
/// <param name="Kind">What the section is.</param>
/// <param name="Text">The text sent to the model (for <see cref="PromptSectionKind.History"/>, the turns joined for display; the turns themselves are in <see cref="AssembledContext.Messages"/>).</param>
/// <param name="Tokens">The tokens the section takes.</param>
public sealed record PromptSection(PromptSectionKind Kind, string Text, int Tokens);

/// <summary>What a <see cref="ContextReportItem"/> is.</summary>
public enum ContextItemKind
{
    /// <summary>The system prompt.</summary>
    SystemPrompt,

    /// <summary>The project rules.</summary>
    Rules,

    /// <summary>The repository map.</summary>
    RepoMap,

    /// <summary>A pinned mention.</summary>
    Pin,

    /// <summary>The current file region.</summary>
    CurrentFile,

    /// <summary>Retrieved chunks.</summary>
    RetrievedChunks,

    /// <summary>The rolling summary of older turns.</summary>
    Summary,

    /// <summary>Conversation turns.</summary>
    History,

    /// <summary>The user's current message.</summary>
    Query,
}

/// <summary>One line of the <see cref="ContextReport"/>: something that went into the prompt, or was left out of it.</summary>
/// <param name="Kind">What it is.</param>
/// <param name="Name">The name to show (a file name, <c>Project rules</c>, ...).</param>
/// <param name="Tokens">The tokens it takes in the prompt (0 when dropped).</param>
/// <param name="OriginalTokens">The tokens it would take unshortened.</param>
/// <param name="Truncated">Whether part of it was cut.</param>
/// <param name="Dropped">Whether all of it was left out.</param>
/// <param name="Note">Why it was truncated or dropped; empty otherwise.</param>
public sealed record ContextReportItem(
    ContextItemKind Kind,
    string Name,
    int Tokens,
    int OriginalTokens,
    bool Truncated,
    bool Dropped,
    string Note);

/// <summary>
/// Everything context assembly decided for one request: every item with its token count and whether it was truncated or
/// dropped. The Context Inspector (work item 1.13) shows it; the Outbound record keeps its counts.
/// </summary>
public sealed class ContextReport
{
    /// <summary>Initializes a new instance of the <see cref="ContextReport"/> class.</summary>
    /// <param name="items">The items, in prompt order.</param>
    /// <param name="modelId">The model the prompt was assembled for.</param>
    /// <param name="contextWindow">The model's usable context window in tokens.</param>
    /// <param name="outputReserve">The tokens kept free for the answer.</param>
    /// <param name="budgetTokens">The tokens available for context: window minus reserve minus system prompt.</param>
    /// <param name="assemblyTime">How long assembly took, including any summary request.</param>
    public ContextReport(
        IReadOnlyList<ContextReportItem> items,
        string modelId,
        int contextWindow,
        int outputReserve,
        int budgetTokens,
        TimeSpan assemblyTime)
    {
        Items = items;
        ModelId = modelId;
        ContextWindow = contextWindow;
        OutputReserve = outputReserve;
        BudgetTokens = budgetTokens;
        AssemblyTime = assemblyTime;
    }

    /// <summary>Gets the items, in prompt order; dropped items follow the section they would have joined.</summary>
    public IReadOnlyList<ContextReportItem> Items { get; }

    /// <summary>Gets the model the prompt was assembled for.</summary>
    public string ModelId { get; }

    /// <summary>Gets the model's usable context window in tokens.</summary>
    public int ContextWindow { get; }

    /// <summary>Gets the tokens kept free for the answer.</summary>
    public int OutputReserve { get; }

    /// <summary>Gets the tokens available for context: window minus reserve minus the system prompt.</summary>
    public int BudgetTokens { get; }

    /// <summary>Gets how long assembly took, including any summary request.</summary>
    public TimeSpan AssemblyTime { get; }

    /// <summary>Gets the number of items (the figure the Outbound record keeps).</summary>
    public int ItemCount => Items.Count;

    /// <summary>Gets the number of items that were truncated or dropped (the figure the Outbound record keeps).</summary>
    public int TrimmedCount => Items.Count(i => i.Truncated || i.Dropped);

    /// <summary>Gets the tokens of everything that went into the prompt.</summary>
    public int TotalTokens => Items.Sum(i => i.Tokens);
}

/// <summary>The prompt context assembly produced.</summary>
public sealed class AssembledContext
{
    /// <summary>Initializes a new instance of the <see cref="AssembledContext"/> class.</summary>
    /// <param name="sections">The sections, from most to least stable.</param>
    /// <param name="messages">The messages to send: one system message, the recent turns, the query.</param>
    /// <param name="report">The report.</param>
    public AssembledContext(IReadOnlyList<PromptSection> sections, IReadOnlyList<ChatMessage> messages, ContextReport report)
    {
        Sections = sections;
        Messages = messages;
        Report = report;
    }

    /// <summary>Gets the sections, ordered from most to least stable (system, rules, repo map, pins, current file, retrieved chunks, summary, history, query).</summary>
    public IReadOnlyList<PromptSection> Sections { get; }

    /// <summary>Gets the messages to send: one system message holding the sections up to the summary, then the recent turns, then the query.</summary>
    public IReadOnlyList<ChatMessage> Messages { get; }

    /// <summary>Gets the report.</summary>
    public ContextReport Report { get; }

    /// <summary>Gets the prompt size in tokens: the sum of the sections plus a few tokens of framing per message.</summary>
    public int PromptTokens => Sections.Sum(s => s.Tokens) + (Messages.Count * ContextAssembler.MessageOverheadTokens);
}

/// <summary>What the assembler needs to know about the model it assembles for.</summary>
/// <param name="ModelId">The model id, for the token counter.</param>
/// <param name="ContextWindow">The tokens the model can read (for a local runtime, the fixed <c>num_ctx</c> if smaller than the model's window).</param>
/// <param name="IsLocal">Whether the model runs on this machine; sensitive pins are only sent to such models.</param>
/// <param name="SystemPrompt">The system prompt used when the request carries none.</param>
/// <param name="ExcludeGlobs">The effective <c>indexing.exclude</c> globs.</param>
public sealed record AssemblyTarget(
    string ModelId,
    int ContextWindow,
    bool IsLocal,
    string SystemPrompt,
    IReadOnlyList<string> ExcludeGlobs);

/// <summary>A request to compress older conversation turns.</summary>
/// <param name="ConversationId">The conversation.</param>
/// <param name="RequestId">The chat request that triggered the summary.</param>
/// <param name="PriorSummary">The summary of the turns before <paramref name="Messages"/>, or <see langword="null"/>.</param>
/// <param name="Messages">The turns to fold in.</param>
/// <param name="MaxTokens">The most tokens the summary may take.</param>
/// <param name="KeepLocal">Whether the summary must be produced by a local model (the chat itself is going to one).</param>
public sealed record SummariseRequest(
    string ConversationId,
    string RequestId,
    string? PriorSummary,
    IReadOnlyList<ChatMessage> Messages,
    int MaxTokens,
    bool KeepLocal);

/// <summary>Produces the rolling history summary; the gateway implements it with a <see cref="TaskClass.Summarise"/> request.</summary>
public interface IHistorySummariser
{
    /// <summary>Summarises conversation turns.</summary>
    /// <param name="request">The turns and limits.</param>
    /// <param name="cancellationToken">A token that cancels the call.</param>
    /// <returns>The summary text.</returns>
    Task<string> SummariseAsync(SummariseRequest request, CancellationToken cancellationToken);
}

/// <summary>Where a context source's text is placed. Phase 2 sources (retrieval, the repository map) plug in here.</summary>
public enum ContextSlot
{
    /// <summary>A map of the repository: after the rules, before the pins, capped at 10% of the budget.</summary>
    RepoMap,

    /// <summary>Retrieved chunks by fused score: after the current file, before the summary, capped at 20% of the budget.</summary>
    RetrievedChunks,
}

/// <summary>Text a context source adds to the prompt.</summary>
/// <param name="Name">The name for the report, for example <c>Repository map</c>.</param>
/// <param name="Text">The text.</param>
public sealed record ContextContribution(string Name, string Text);

/// <summary>
/// The hook phase 2 uses for retrieved chunks and the repository map (design section 7, Context assembly). The assembler asks
/// each registered source for its contribution, truncates it to its slot's cap and the room left, and reports it. Phase 1 registers none.
/// </summary>
public interface IContextSource
{
    /// <summary>Gets the slot the contribution goes into.</summary>
    ContextSlot Slot { get; }

    /// <summary>Gets the contribution for a request.</summary>
    /// <param name="request">The chat request (the query is its last message).</param>
    /// <param name="target">The model the prompt is assembled for.</param>
    /// <param name="cancellationToken">A token that cancels the call.</param>
    /// <returns>The contribution, or <see langword="null"/> when there is nothing to add.</returns>
    Task<ContextContribution?> GetAsync(ChatRequest request, AssemblyTarget target, CancellationToken cancellationToken);
}

/// <summary>Tunable limits of context assembly (design section 7).</summary>
public sealed class ContextAssemblerOptions
{
    /// <summary>Gets or sets the tokens kept free for the answer. Default 4,000; never more than a quarter of the model's window.</summary>
    public int OutputReserveTokens { get; set; } = 4000;

    /// <summary>Gets or sets the share of the budget one pin may take before it is reduced to its first and last parts. Default 0.25.</summary>
    public double PinCapFraction { get; set; } = 0.25;

    /// <summary>Gets or sets the share of the budget the current file region may take. Default 0.15.</summary>
    public double CurrentFileCapFraction { get; set; } = 0.15;

    /// <summary>Gets or sets the share of the budget the project rules may take. Default 0.10.</summary>
    public double RulesCapFraction { get; set; } = 0.10;

    /// <summary>Gets or sets the share of the budget the repository map may take. Default 0.10.</summary>
    public double RepoMapCapFraction { get; set; } = 0.10;

    /// <summary>Gets or sets the share of the budget the retrieved chunks may take. Default 0.20.</summary>
    public double RetrievedCapFraction { get; set; } = 0.20;

    /// <summary>Gets or sets the most tokens a rolling summary may take. Default 512.</summary>
    public int SummaryMaxTokens { get; set; } = 512;

    /// <summary>
    /// Gets or sets how much of the room left for history the recent turns may take when history is compressed, so that
    /// the next few turns fit without another summary request. Default 0.5.
    /// </summary>
    public double HistoryKeepFraction { get; set; } = 0.5;

    /// <summary>Gets or sets the number of conversations whose summary is cached. Default 64.</summary>
    public int SummaryCacheCapacity { get; set; } = 64;

    /// <summary>Gets or sets the smallest share worth truncating an item into; below it the item is dropped. Default 64 tokens.</summary>
    public int MinItemTokens { get; set; } = 64;
}

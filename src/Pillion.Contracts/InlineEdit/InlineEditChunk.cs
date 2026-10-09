using System.Runtime.Serialization;

namespace Pillion.Contracts;

/// <summary>
/// A streamed piece of an inline edit proposal. The proposal is the full replacement text for the request's scope
/// (plain code, no Markdown fences, in the scope's own line endings), validated before the final chunk is sent.
/// </summary>
/// <remarks>
/// <para>
/// Text can arrive before the model has finished, as an early preview. When the host has to change text it already
/// sent (a later edit block landed above it, or a retry changed the result), it sends a chunk with
/// <see cref="ResetText"/> set: discard everything received so far and start again from that chunk's
/// <see cref="Text"/>. After the final chunk the accumulated text is the validated proposal.
/// </para>
/// <para>
/// The stream either ends with a final chunk (success) or fails: every failure, before or after preview text, fails the
/// call with a <see cref="PillionRpcException"/> whose <see cref="PillionError"/> says why. Discard any preview then.
/// </para>
/// </remarks>
[DataContract]
public sealed class InlineEditChunk
{
    /// <summary>Gets or sets the id of the request this chunk belongs to.</summary>
    [DataMember(Order = 0)]
    public string RequestId { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the next piece of the proposed replacement, appended to the text received so far (or replacing it
    /// when <see cref="ResetText"/> is set). Never present for <c>/explain</c>; for <c>/test</c> it is the new test code.
    /// </summary>
    [DataMember(Order = 1)]
    public string Text { get; set; } = string.Empty;

    /// <summary>Gets or sets a value indicating whether this is the last chunk of the stream.</summary>
    [DataMember(Order = 2)]
    public bool IsFinal { get; set; }

    /// <summary>
    /// Gets or sets the model's explanation, on the final chunk: the answer for <c>/explain</c>, where the test belongs
    /// for <c>/test</c>, or a note the model added to an edit; otherwise <see langword="null"/>.
    /// </summary>
    [DataMember(Order = 3)]
    public string? Explanation { get; set; }

    /// <summary>Gets or sets the provider's id of the model that produced the proposal.</summary>
    [DataMember(Order = 4)]
    public string ModelId { get; set; } = string.Empty;

    /// <summary>Gets or sets the id of the provider that produced the proposal.</summary>
    [DataMember(Order = 5)]
    public string ProviderId { get; set; } = string.Empty;

    /// <summary>Gets or sets the prompt tokens of every model call the request made, on the final chunk (0 when unknown).</summary>
    [DataMember(Order = 6)]
    public int PromptTokens { get; set; }

    /// <summary>Gets or sets the generated tokens of every model call the request made, on the final chunk (0 when unknown).</summary>
    [DataMember(Order = 7)]
    public int CompletionTokens { get; set; }

    /// <summary>
    /// Gets or sets an error to show with the final chunk. Kept for parity with <see cref="ChatChunk.Error"/>; hosts that
    /// implement contract 1.2 leave it <see langword="null"/> and fail the call instead (see the remarks), so a client
    /// treats a final chunk with an error like a failed call.
    /// </summary>
    [DataMember(Order = 8)]
    public PillionError? Error { get; set; }

    /// <summary>
    /// Gets or sets how many edit blocks the host sent back to the model because they did not match the code exactly
    /// once or were malformed, on the final chunk (for the UI's information).
    /// </summary>
    [DataMember(Order = 9)]
    public int RejectedBlocks { get; set; }

    /// <summary>
    /// Gets or sets a warning about a proposal that is returned anyway, on the final chunk: it still has more syntax
    /// errors than the original after a retry, or the model proposed no change; otherwise <see langword="null"/>.
    /// </summary>
    [DataMember(Order = 10)]
    public string? Warning { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether the text received so far must be discarded before this chunk's
    /// <see cref="Text"/> is appended (the early preview changed).
    /// </summary>
    [DataMember(Order = 11)]
    public bool ResetText { get; set; }
}

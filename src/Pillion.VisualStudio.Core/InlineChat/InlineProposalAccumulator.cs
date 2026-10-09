using System;
using System.Text;
using Pillion.Contracts;

namespace Pillion.VisualStudio.Core.InlineChat;

/// <summary>
/// Builds the proposal from the chunks of <see cref="IInlineEditService.EditAsync"/>: appends each chunk's text, starts
/// again when a chunk has <see cref="InlineEditChunk.ResetText"/> set, and keeps what the final chunk says (the
/// explanation, warning, rejected blocks and the model that answered). Chunks of another request are ignored. Not
/// thread-safe: one stream feeds it on one thread at a time.
/// </summary>
public sealed class InlineProposalAccumulator
{
    private readonly StringBuilder _text = new();
    private bool _hasLineBreak;

    /// <summary>Initializes a new instance of the <see cref="InlineProposalAccumulator"/> class.</summary>
    /// <param name="requestId">The request whose chunks are accepted.</param>
    public InlineProposalAccumulator(string requestId)
    {
        RequestId = requestId ?? string.Empty;
    }

    /// <summary>Gets the request whose chunks are accepted.</summary>
    public string RequestId { get; }

    /// <summary>Gets the proposal so far (the validated proposal once <see cref="IsFinal"/>).</summary>
    public string Text => _text.ToString();

    /// <summary>Gets the number of characters received so far.</summary>
    public int Length => _text.Length;

    /// <summary>Gets a number that changes every time <see cref="Text"/> changes.</summary>
    public int Version { get; private set; }

    /// <summary>Gets how many times the host reset the text.</summary>
    public int Resets { get; private set; }

    /// <summary>Gets a value indicating whether the final chunk has arrived.</summary>
    public bool IsFinal { get; private set; }

    /// <summary>Gets a value indicating whether the text holds at least one complete line (or the final text).</summary>
    public bool HasFirstLine => IsFinal || _hasLineBreak;

    /// <summary>Gets the provider's id of the model that answered, once a chunk names it.</summary>
    public string? ModelId { get; private set; }

    /// <summary>Gets the provider that answered, once a chunk names it.</summary>
    public string? ProviderId { get; private set; }

    /// <summary>Gets the explanation from the final chunk, or <see langword="null"/>.</summary>
    public string? Explanation { get; private set; }

    /// <summary>Gets the warning from the final chunk, or <see langword="null"/>.</summary>
    public string? Warning { get; private set; }

    /// <summary>Gets how many edit blocks the host sent back to the model.</summary>
    public int RejectedBlocks { get; private set; }

    /// <summary>Gets an error on the final chunk (hosts before contract 1.2), or <see langword="null"/>.</summary>
    public PillionError? Error { get; private set; }

    /// <summary>Applies a chunk.</summary>
    /// <param name="chunk">The chunk.</param>
    /// <returns><see langword="true"/> when the chunk belonged to the request and was applied.</returns>
    public bool Add(InlineEditChunk? chunk)
    {
        if (chunk is null || IsFinal
            || (!string.IsNullOrEmpty(chunk.RequestId) && !string.Equals(chunk.RequestId, RequestId, StringComparison.Ordinal)))
        {
            return false;
        }

        if (chunk.ResetText)
        {
            _text.Clear();
            _hasLineBreak = false;
            Resets++;
            Version++;
        }

        if (!string.IsNullOrEmpty(chunk.Text))
        {
            _text.Append(chunk.Text);
            _hasLineBreak |= chunk.Text.IndexOf('\n') >= 0;
            Version++;
        }

        if (!string.IsNullOrEmpty(chunk.ModelId))
        {
            ModelId = chunk.ModelId;
            ProviderId = string.IsNullOrEmpty(chunk.ProviderId) ? ProviderId : chunk.ProviderId;
        }

        if (chunk.IsFinal)
        {
            IsFinal = true;
            Explanation = string.IsNullOrWhiteSpace(chunk.Explanation) ? null : chunk.Explanation!.Trim();
            Warning = string.IsNullOrWhiteSpace(chunk.Warning) ? null : chunk.Warning!.Trim();
            RejectedBlocks = Math.Max(0, chunk.RejectedBlocks);
            Error = chunk.Error;
        }

        return true;
    }
}

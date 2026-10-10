using System;
using System.Collections.Generic;

namespace Pillion.VisualStudio.Core.Markdown;

/// <summary>
/// What a view must do to go from the blocks it shows to newly parsed blocks (design doc section 13: tool windows render
/// Markdown incrementally per streamed chunk). Blocks before <see cref="FirstChanged"/> are kept as they are; the block at
/// <see cref="FirstChanged"/> is updated in place when <see cref="UpdateFirstInPlace"/> is set (a paragraph or code block
/// that grew, a list that gained an item) or replaced otherwise; every later old block is removed and every later new
/// block is added. While a reply streams only its last block changes, so a chunk costs one block, not the whole message.
/// </summary>
public readonly struct MarkdownRenderPlan : IEquatable<MarkdownRenderPlan>
{
    /// <summary>Initializes a new instance of the <see cref="MarkdownRenderPlan"/> struct.</summary>
    /// <param name="firstChanged">The index of the first block that differs.</param>
    /// <param name="updateFirstInPlace">Whether that block's element can be updated in place.</param>
    /// <param name="oldCount">How many blocks the view shows now.</param>
    /// <param name="newCount">How many blocks it must show.</param>
    public MarkdownRenderPlan(int firstChanged, bool updateFirstInPlace, int oldCount, int newCount)
    {
        FirstChanged = firstChanged;
        UpdateFirstInPlace = updateFirstInPlace;
        OldCount = oldCount;
        NewCount = newCount;
    }

    /// <summary>Gets the index of the first block that differs (equal to both counts when nothing changed).</summary>
    public int FirstChanged { get; }

    /// <summary>Gets a value indicating whether the element at <see cref="FirstChanged"/> is updated in place rather than replaced.</summary>
    public bool UpdateFirstInPlace { get; }

    /// <summary>Gets how many blocks the view shows now.</summary>
    public int OldCount { get; }

    /// <summary>Gets how many blocks it must show.</summary>
    public int NewCount { get; }

    /// <summary>Gets a value indicating whether nothing needs to be rendered.</summary>
    public bool IsUnchanged => FirstChanged == OldCount && FirstChanged == NewCount;

    /// <summary>Gets how many blocks are rendered from scratch (added, or replaced).</summary>
    public int RenderedCount => NewCount - FirstChanged - (UpdateFirstInPlace ? 1 : 0);

    /// <summary>Compares the blocks a view shows with newly parsed ones.</summary>
    /// <param name="current">The blocks shown now.</param>
    /// <param name="next">The newly parsed blocks.</param>
    /// <param name="definitionsChanged">
    /// Whether the message's link reference definitions changed (<see cref="MarkdownMessage.DefinitionsKey"/>); then every
    /// block is re-rendered, because a definition can change how an earlier link renders.
    /// </param>
    /// <returns>The plan.</returns>
    public static MarkdownRenderPlan Compare(IReadOnlyList<MarkdownBlock> current, IReadOnlyList<MarkdownBlock> next, bool definitionsChanged = false)
    {
        if (current is null)
        {
            throw new ArgumentNullException(nameof(current));
        }

        if (next is null)
        {
            throw new ArgumentNullException(nameof(next));
        }

        var first = 0;
        if (!definitionsChanged)
        {
            var common = Math.Min(current.Count, next.Count);
            while (first < common && current[first].RendersSameAs(next[first]))
            {
                first++;
            }
        }

        var inPlace = first < current.Count && first < next.Count && current[first].CanUpdateTo(next[first]);
        return new MarkdownRenderPlan(first, inPlace, current.Count, next.Count);
    }

    /// <summary>Compares two messages.</summary>
    /// <param name="current">The message shown now.</param>
    /// <param name="next">The newly parsed message.</param>
    /// <returns>The plan.</returns>
    public static MarkdownRenderPlan Compare(MarkdownMessage current, MarkdownMessage next)
    {
        if (current is null)
        {
            throw new ArgumentNullException(nameof(current));
        }

        if (next is null)
        {
            throw new ArgumentNullException(nameof(next));
        }

        return Compare(current.Blocks, next.Blocks, !string.Equals(current.DefinitionsKey, next.DefinitionsKey, StringComparison.Ordinal));
    }

    /// <inheritdoc/>
    public bool Equals(MarkdownRenderPlan other) =>
        FirstChanged == other.FirstChanged && UpdateFirstInPlace == other.UpdateFirstInPlace && OldCount == other.OldCount && NewCount == other.NewCount;

    /// <inheritdoc/>
    public override bool Equals(object? obj) => obj is MarkdownRenderPlan other && Equals(other);

    /// <inheritdoc/>
    public override int GetHashCode() => (FirstChanged * 397) ^ (OldCount * 31) ^ NewCount ^ (UpdateFirstInPlace ? 1 << 30 : 0);

    /// <inheritdoc/>
    public override string ToString() =>
        "first changed " + FirstChanged + (UpdateFirstInPlace ? " (in place)" : string.Empty) + ", " + OldCount + " -> " + NewCount;

    /// <summary>Compares two plans.</summary>
    /// <param name="left">The first plan.</param>
    /// <param name="right">The second plan.</param>
    /// <returns><see langword="true"/> when they are equal.</returns>
    public static bool operator ==(MarkdownRenderPlan left, MarkdownRenderPlan right) => left.Equals(right);

    /// <summary>Compares two plans.</summary>
    /// <param name="left">The first plan.</param>
    /// <param name="right">The second plan.</param>
    /// <returns><see langword="true"/> when they differ.</returns>
    public static bool operator !=(MarkdownRenderPlan left, MarkdownRenderPlan right) => !left.Equals(right);
}

using Microsoft.VisualStudio.Text;
using Pillion.VisualStudio.Core.Autocomplete;

namespace Pillion.VisualStudio.Autocomplete;

/// <summary>An <see cref="IDocumentText"/> over an editor snapshot, which is immutable and safe to read on any thread.</summary>
internal sealed class SnapshotDocumentText : IDocumentText
{
    /// <summary>Initializes a new instance of the <see cref="SnapshotDocumentText"/> class.</summary>
    /// <param name="snapshot">The snapshot.</param>
    public SnapshotDocumentText(ITextSnapshot snapshot)
    {
        Snapshot = snapshot;
    }

    /// <summary>Gets the snapshot.</summary>
    public ITextSnapshot Snapshot { get; }

    /// <inheritdoc/>
    public int Length => Snapshot.Length;

    /// <inheritdoc/>
    public int LineCount => Snapshot.LineCount;

    /// <inheritdoc/>
    public string GetText(int start, int length) => Snapshot.GetText(start, length);

    /// <inheritdoc/>
    public int GetLineNumber(int position) => Snapshot.GetLineNumberFromPosition(position);

    /// <inheritdoc/>
    public int GetLineStart(int lineNumber) => Snapshot.GetLineFromLineNumber(lineNumber).Start.Position;

    /// <inheritdoc/>
    public string GetLineText(int lineNumber) => Snapshot.GetLineFromLineNumber(lineNumber).GetText();
}

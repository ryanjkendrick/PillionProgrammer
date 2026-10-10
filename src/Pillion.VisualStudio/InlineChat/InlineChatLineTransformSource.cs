using System.ComponentModel.Composition;
using Microsoft.VisualStudio.Text;
using Microsoft.VisualStudio.Text.Editor;
using Microsoft.VisualStudio.Text.Formatting;
using Microsoft.VisualStudio.Utilities;

namespace Pillion.VisualStudio.InlineChat;

/// <summary>Gives every document view an <see cref="InlineChatLineTransformSource"/>.</summary>
[Export(typeof(ILineTransformSourceProvider))]
[ContentType("text")]
[TextViewRole(PredefinedTextViewRoles.Document)]
internal sealed class InlineChatLineTransformSourceProvider : ILineTransformSourceProvider
{
    /// <inheritdoc/>
    public ILineTransformSource Create(IWpfTextView textView) => InlineChatLineTransformSource.For(textView);
}

/// <summary>
/// Space negotiation for the inline chat adornment: while inline chat is open, the first line of its scope gets extra
/// space above it (a <see cref="LineTransform"/> with top space), so the code moves down and the control sits in that
/// space without covering anything. The buffer is never touched, unlike an intra-text adornment, which needs a tag at a
/// buffer position and lays out within the line. Every other line keeps its default transform.
/// </summary>
internal sealed class InlineChatLineTransformSource : ILineTransformSource
{
    private ITrackingPoint? _anchor;
    private double _space;

    /// <summary>Gets the source of a view, creating it on first use (by the provider or by inline chat, whichever comes first).</summary>
    /// <param name="view">The view.</param>
    /// <returns>The source.</returns>
    public static InlineChatLineTransformSource For(ITextView view) =>
        view.Properties.GetOrCreateSingletonProperty(typeof(InlineChatLineTransformSource), () => new InlineChatLineTransformSource());

    /// <summary>Gets the space reserved above the anchored line, in pixels (0 when nothing is reserved).</summary>
    public double Space => _anchor is null ? 0 : _space;

    /// <summary>Reserves space above the line that starts at <paramref name="anchor"/>. Call <see cref="Relayout"/> afterwards.</summary>
    /// <param name="anchor">The start of the scope's first line.</param>
    /// <param name="space">The height to reserve, in pixels.</param>
    public void Reserve(ITrackingPoint anchor, double space)
    {
        _anchor = anchor;
        _space = space < 0 ? 0 : space;
    }

    /// <summary>Releases the reserved space. Call <see cref="Relayout"/> afterwards.</summary>
    public void Release()
    {
        _anchor = null;
        _space = 0;
    }

    /// <inheritdoc/>
    public LineTransform GetLineTransform(ITextViewLine line, double yPosition, ViewRelativePosition placement)
    {
        if (_anchor is { } anchor && _space > 0 && line.Start == anchor.GetPoint(line.Snapshot))
        {
            var normal = line.DefaultLineTransform;
            return new LineTransform(normal.TopSpace + _space, normal.BottomSpace, normal.VerticalScale);
        }

        return line.DefaultLineTransform;
    }

    /// <summary>Lays the visible lines out again, keeping the first visible line where it is, so a new transform takes effect.</summary>
    /// <param name="view">The view.</param>
    public static void Relayout(ITextView view)
    {
        if (view.IsClosed || view.InLayout || view.TextViewLines is not { Count: > 0 } lines)
        {
            return;
        }

        var first = lines.FirstVisibleLine;
        view.DisplayTextLineContainingBufferPosition(first.Start, first.Top - view.ViewportTop, ViewRelativePosition.Top);
    }
}

using System;
using System.ComponentModel.Composition;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Microsoft.VisualStudio.Text;
using Microsoft.VisualStudio.Text.Classification;
using Microsoft.VisualStudio.Text.Differencing;
using Microsoft.VisualStudio.Text.Editor;
using Microsoft.VisualStudio.Text.Formatting;
using Microsoft.VisualStudio.Utilities;

namespace Pillion.VisualStudio.InlineChat;

/// <summary>Adds <see cref="InlineDiffMarkerMargin"/> to Pillion's inline difference views (and nothing else).</summary>
[Export(typeof(IWpfTextViewMarginProvider))]
[Name(InlineDiffMarkerMargin.MarginName)]
[MarginContainer(PredefinedMarginNames.Left)]
[ContentType("text")]
[TextViewRole(InlineChatEditorServices.DiffViewRole)]
internal sealed class InlineDiffMarkerMarginProvider : IWpfTextViewMarginProvider
{
    private readonly IWpfDifferenceViewerFactoryService _viewers;
    private readonly IEditorFormatMapService _formatMaps;

    /// <summary>Initializes a new instance of the <see cref="InlineDiffMarkerMarginProvider"/> class.</summary>
    /// <param name="viewers">Finds the difference viewer of a view.</param>
    /// <param name="formatMaps">The editor's colours.</param>
    [ImportingConstructor]
    public InlineDiffMarkerMarginProvider(IWpfDifferenceViewerFactoryService viewers, IEditorFormatMapService formatMaps)
    {
        _viewers = viewers;
        _formatMaps = formatMaps;
    }

    /// <inheritdoc/>
    public IWpfTextViewMargin? CreateMargin(IWpfTextViewHost wpfTextViewHost, IWpfTextViewMargin marginContainer)
    {
        var view = wpfTextViewHost.TextView;
        return view.Roles.Contains(DifferenceViewerRoles.InlineViewTextViewRole)
            ? new InlineDiffMarkerMargin(view, _viewers, _formatMaps.GetEditorFormatMap(view))
            : null;
    }
}

/// <summary>
/// A narrow margin that marks each line of the inline difference view with <c>+</c> (added), <c>-</c> (removed) or
/// nothing (unchanged), so the change never depends on colour alone (design section 5, Accessibility). The mark comes
/// from the difference buffer: an inline line maps to the left (old) or right (new) side, and the side's line numbers
/// are checked against the line differences.
/// </summary>
internal sealed class InlineDiffMarkerMargin : Canvas, IWpfTextViewMargin
{
    /// <summary>The margin's name.</summary>
    public const string MarginName = "PillionInlineDiffMarkers";

    private readonly IWpfTextView _view;
    private readonly IWpfDifferenceViewerFactoryService _viewers;
    private readonly IEditorFormatMap _formatMap;
    private IDifferenceBuffer? _buffer;
    private bool _disposed;

    /// <summary>Initializes a new instance of the <see cref="InlineDiffMarkerMargin"/> class.</summary>
    /// <param name="view">The inline view.</param>
    /// <param name="viewers">Finds the difference viewer of the view.</param>
    /// <param name="formatMap">The view's colours.</param>
    public InlineDiffMarkerMargin(IWpfTextView view, IWpfDifferenceViewerFactoryService viewers, IEditorFormatMap formatMap)
    {
        _view = view;
        _viewers = viewers;
        _formatMap = formatMap;
        Width = 14;
        ClipToBounds = true;
        IsHitTestVisible = false;
        _view.LayoutChanged += OnLayoutChanged;
        _view.Closed += OnClosed;
    }

    /// <inheritdoc/>
    public FrameworkElement VisualElement => this;

    /// <inheritdoc/>
    public double MarginSize => ActualWidth;

    /// <inheritdoc/>
    public bool Enabled => true;

    /// <inheritdoc/>
    public ITextViewMargin? GetTextViewMargin(string marginName) =>
        string.Equals(marginName, MarginName, StringComparison.OrdinalIgnoreCase) ? this : null;

    /// <inheritdoc/>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _view.LayoutChanged -= OnLayoutChanged;
        _view.Closed -= OnClosed;
        if (_buffer is not null)
        {
            _buffer.SnapshotDifferenceChanged -= OnDifferenceChanged;
        }
    }

    /// <summary>Gets the mark of a line: <c>+</c>, <c>-</c> or <see langword="null"/>.</summary>
    internal static string? MarkOf(ISnapshotDifference difference, IWpfTextView view, ITextViewLine line)
    {
        var inline = view.BufferGraph.MapDownToSnapshot(line.Start, PointTrackingMode.Positive, difference.InlineBufferSnapshot, PositionAffinity.Successor);
        if (inline is not { } point)
        {
            return null;
        }

        var source = difference.MapToSourceSnapshot(point);
        var left = source.Snapshot.TextBuffer == difference.LeftBufferSnapshot.TextBuffer;
        var number = source.GetContainingLine().LineNumber;
        return difference.LineDifferences.Any(d => left ? d.Left.Contains(number) : d.Right.Contains(number))
            ? left ? "-" : "+"
            : null;
    }

    private void OnClosed(object? sender, EventArgs e) => Dispose();

    private void OnDifferenceChanged(object? sender, SnapshotDifferenceChangeEventArgs e) => Redraw();

    private void OnLayoutChanged(object? sender, TextViewLayoutChangedEventArgs e) => Redraw();

    private void Redraw()
    {
        if (_disposed)
        {
            return;
        }

        if (_buffer is null && _viewers.TryGetViewerForTextView(_view) is { } viewer)
        {
            _buffer = viewer.DifferenceBuffer;
            _buffer.SnapshotDifferenceChanged += OnDifferenceChanged;
        }

        Children.Clear();
        if (_buffer?.CurrentSnapshotDifference is not { } difference || _view.IsClosed || _view.InLayout)
        {
            return;
        }

        var properties = _formatMap.GetProperties("Plain Text");
        var foreground = properties[EditorFormatDefinition.ForegroundBrushId] as Brush ?? SystemColors.WindowTextBrush;
        foreach (var line in _view.TextViewLines)
        {
            string? mark;
            try
            {
                mark = MarkOf(difference, _view, line);
            }
            catch (ArgumentException)
            {
                return; // The difference is being recomputed for a newer snapshot; the next change redraws.
            }
            catch (InvalidOperationException)
            {
                return;
            }

            if (mark is null)
            {
                continue;
            }

            var block = new TextBlock
            {
                Text = mark,
                Foreground = foreground,
                FontWeight = FontWeights.Bold,
                FontFamily = new FontFamily("Consolas"),
                FontSize = _view.FormattedLineSource?.DefaultTextProperties.FontRenderingEmSize ?? 12,
            };
            SetLeft(block, 3);
            SetTop(block, line.TextTop - _view.ViewportTop);
            Children.Add(block);
        }
    }
}

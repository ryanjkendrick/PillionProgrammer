using System;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Automation;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Text;
using Microsoft.VisualStudio.Text.Differencing;
using Microsoft.VisualStudio.Text.Editor;
using Microsoft.VisualStudio.Utilities;

namespace Pillion.VisualStudio.InlineChat;

/// <summary>
/// The proposal shown as an inline difference view (design section 5; work item 1.7): a difference buffer over two
/// private buffers, the scope's text on the left and the proposal on the right, shown by an <see cref="IWpfDifferenceViewer"/>
/// in <see cref="DifferenceViewMode.Inline"/> mode with the theme's difference colours, and Pillion's +/- margin
/// (<see cref="InlineDiffMarkerMarginProvider"/>) so changes never depend on colour alone. The document's own buffer is
/// not involved: Roslyn, the file watcher and Live Unit Testing never see the proposal. For <c>/test</c> the left side is
/// empty, so the new code shows as added lines. Created and used on the UI thread.
/// </summary>
internal sealed class InlineDiffPresenter : IDisposable
{
    /// <summary>The most lines shown before the view scrolls.</summary>
    public const int MaxVisibleLines = 24;

    private readonly ITextBuffer _left;
    private readonly ITextBuffer _right;
    private readonly IDifferenceBuffer _difference;
    private readonly IWpfDifferenceViewer _viewer;
    private readonly double _lineHeight;
    private string _text = string.Empty;
    private bool _disposed;

    /// <summary>Initializes a new instance of the <see cref="InlineDiffPresenter"/> class.</summary>
    /// <param name="services">The editor services.</param>
    /// <param name="contentType">The document's content type, so both sides are classified like the document.</param>
    /// <param name="original">The scope's text (empty for <c>/test</c>, whose code is new).</param>
    /// <param name="documentView">The document's view, whose line height sizes the difference view.</param>
    public InlineDiffPresenter(InlineChatEditorServices services, IContentType contentType, string original, IWpfTextView documentView)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        _left = services.Buffers.CreateTextBuffer(original, contentType);
        _right = services.Buffers.CreateTextBuffer(string.Empty, contentType);
        _difference = services.DifferenceBuffers.CreateDifferenceBuffer(
            _left,
            _right,
            new StringDifferenceOptions(StringDifferenceTypes.Line | StringDifferenceTypes.Word, locality: 0, ignoreTrimWhiteSpace: false),
            disableEditing: true);

        var roles = services.Editors.CreateTextViewRoleSet(
            PredefinedTextViewRoles.Analyzable,
            PredefinedTextViewRoles.Interactive,
            PredefinedTextViewRoles.Zoomable,
            InlineChatEditorServices.DiffViewRole);
        _viewer = services.DifferenceViewers.CreateDifferenceView(_difference, roles);
        _viewer.ViewMode = DifferenceViewMode.Inline;
        TrySet(_viewer.Options, DifferenceViewerOptions.ShowDiffOverviewMarginId, false);
        TrySet(_viewer.Options, DifferenceViewerOptions.ScrollToFirstDiffId, false);
        TrySet(_viewer.Options, DifferenceViewerOptions.HighlightModeId, DifferenceHighlightMode.WholeLine);

        var inline = _viewer.InlineView;
        TrySet(inline.Options, DefaultTextViewHostOptions.LineNumberMarginId, false);
        TrySet(inline.Options, DefaultTextViewHostOptions.GlyphMarginId, false);
        TrySet(inline.Options, DefaultTextViewHostOptions.OutliningMarginId, false);
        TrySet(inline.Options, DefaultTextViewHostOptions.SelectionMarginId, false);
        TrySet(inline.Options, DefaultTextViewHostOptions.ChangeTrackingId, false);
        TrySet(inline.Options, DefaultTextViewOptions.WordWrapStyleId, WordWrapStyles.None);
        TrySet(inline.Options, DefaultTextViewOptions.UseVisibleWhitespaceId, false);

        // The adornment is inside the document view, which applies its own zoom to everything in it.
        inline.ZoomLevel = 100;
        _lineHeight = documentView.LineHeight > 0 ? documentView.LineHeight : 16;

        var element = _viewer.VisualElement;
        AutomationProperties.SetAutomationId(element, "PillionInlineChatDiff");
        AutomationProperties.SetName(element, "Proposed change");
        _difference.SnapshotDifferenceChanged += OnDifferenceChanged;
        UpdateHeight();
    }

    /// <summary>Gets the element to host in the adornment.</summary>
    public FrameworkElement Element => _viewer.VisualElement;

    /// <summary>Gets the inline view (for focus and keyboard scrolling).</summary>
    public IWpfTextView InlineView => _viewer.InlineView;

    /// <summary>Gets the text shown on the right (the proposal so far).</summary>
    public string Text => _text;

    /// <summary>Gets a short text alternative for the change, for example "3 lines added, 1 line removed".</summary>
    public string Summary { get; private set; } = string.Empty;

    /// <summary>Raised when <see cref="Summary"/> or the height changed.</summary>
    public event EventHandler? Changed;

    /// <summary>Shows a new proposal: appends when it extends the text shown, otherwise replaces it.</summary>
    /// <param name="text">The proposal so far.</param>
    public void Update(string text)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        if (_disposed || string.Equals(text, _text, StringComparison.Ordinal))
        {
            return;
        }

        var snapshot = _right.CurrentSnapshot;
        if (text.StartsWith(_text, StringComparison.Ordinal) && snapshot.Length == _text.Length)
        {
            _right.Insert(snapshot.Length, text.Substring(_text.Length));
        }
        else
        {
            _right.Replace(new Span(0, snapshot.Length), text);
        }

        _text = text;
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _difference.SnapshotDifferenceChanged -= OnDifferenceChanged;
        if (!_viewer.IsClosed)
        {
            _viewer.Close();
        }
    }

    /// <summary>
    /// Sets an editor option where the options object accepts it: some options are defined for a scope that is not the
    /// one a version of Visual Studio gives the difference viewer ("not valid in this scope"); the default is kept then.
    /// </summary>
    private static void TrySet<T>(IEditorOptions options, EditorOptionKey<T> key, T value)
    {
        try
        {
            if (options.IsOptionDefined(key, localScopeOnly: false))
            {
                options.SetOptionValue(key, value);
            }
        }
        catch (ArgumentException)
        {
            // Not valid in this scope on this version.
        }
    }

    private void OnDifferenceChanged(object? sender, SnapshotDifferenceChangeEventArgs e)
    {
        if (_disposed)
        {
            return;
        }

        var difference = _difference.CurrentSnapshotDifference;
        if (difference is not null)
        {
            var differences = difference.LineDifferences;
            var added = differences.Sum(d => d.Right.Length);
            var removed = differences.Sum(d => d.Left.Length);
            Summary = added == 0 && removed == 0
                ? "No change"
                : string.Format(CultureInfo.CurrentCulture, "{0} line{1} added, {2} line{3} removed", added, added == 1 ? string.Empty : "s", removed, removed == 1 ? string.Empty : "s");
            AutomationProperties.SetName(_viewer.VisualElement, "Proposed change: " + Summary);
        }

        UpdateHeight();
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Sizes the view to its lines (both sides of every change), up to <see cref="MaxVisibleLines"/>, plus a horizontal scroll bar.</summary>
    private void UpdateHeight()
    {
        var lines = _difference.CurrentInlineBufferSnapshot?.LineCount ?? Math.Max(1, _left.CurrentSnapshot.LineCount);
        var shown = Math.Max(1, Math.Min(MaxVisibleLines, lines));
        var height = (shown * _lineHeight) + SystemParameters.HorizontalScrollBarHeight + 6;
        if (Math.Abs(_viewer.VisualElement.Height - height) > 0.5 || double.IsNaN(_viewer.VisualElement.Height))
        {
            _viewer.VisualElement.Height = height;
        }
    }
}

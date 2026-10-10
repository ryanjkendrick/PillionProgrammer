using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using Microsoft.VisualStudio;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using Microsoft.VisualStudio.Text;
using Microsoft.VisualStudio.Text.Classification;
using Microsoft.VisualStudio.Text.Editor;
using Microsoft.VisualStudio.Text.Formatting;
using Microsoft.VisualStudio.Text.Operations;
using Microsoft.VisualStudio.Threading;
using Pillion.VisualStudio.Autocomplete;
using Pillion.VisualStudio.Core;
using Pillion.VisualStudio.Core.InlineChat;
using Pillion.VisualStudio.Core.Signals;

namespace Pillion.VisualStudio.InlineChat;

/// <summary>
/// Inline chat on one document view (work items 1.6 and 1.7): chooses the scope, highlights it, shows the adornment
/// above its first line in space the view makes for it (<see cref="InlineChatLineTransformSource"/>), hosts the inline
/// difference view while a proposal streams or is shown, and writes the result into the document only when the user
/// applies it (<see cref="IInlineChatEditor"/>). One inline chat at a time per view; running the command again focuses it.
/// </summary>
/// <remarks>
/// The adornment closes when the user applies or discards, when the document view closes, and when the caret moves
/// further from the scope than the number of lines the view shows (the user has gone to work elsewhere). Everything here
/// runs on the UI thread; the request itself runs on the thread pool inside <see cref="InlineChatSession"/>.
/// </remarks>
internal sealed class InlineChatController : IInlineChatEditor, IDisposable
{
    /// <summary>The description of the undo unit an applied proposal makes.</summary>
    public const string UndoDescription = "Pillion inline edit";

    /// <summary>How long the Roslyn member lookup may take before the scope falls back to the caret's line.</summary>
    private static readonly TimeSpan MemberLookupBudget = TimeSpan.FromMilliseconds(500);

    private const double Gap = 2;

    private readonly IWpfTextView _view;
    private readonly InlineChatEditorServices _services;
    private readonly InlineChatFeature _feature;
    private readonly InlineChatLineTransformSource _transform;
    private InlineChatSession? _session;
    private InlineChatAdornment? _control;
    private InlineDiffPresenter? _diff;
    private ITrackingSpan? _scopeSpan;
    private ITrackingPoint? _anchor;
    private DispatcherTimer? _timer;
    private Brush? _highlight;
    private Brush? _accent;
    private string _filePath = string.Empty;
    private bool _opening;

    private InlineChatController(IWpfTextView view, InlineChatEditorServices services, InlineChatFeature feature)
    {
        _view = view;
        _services = services;
        _feature = feature;
        _transform = InlineChatLineTransformSource.For(view);
    }

    /// <summary>Gets the open session, or <see langword="null"/>.</summary>
    public InlineChatSession? Session => _session;

    /// <summary>Gets the adornment, or <see langword="null"/>.</summary>
    public InlineChatAdornment? Control => _control;

    /// <summary>Gets the difference view, or <see langword="null"/>.</summary>
    public InlineDiffPresenter? Diff => _diff;

    /// <summary>Gets a value indicating whether keyboard focus is in the adornment (including the difference view and drop-downs).</summary>
    public bool OwnsKeyboardFocus => _control is { } control && control.Contains(Keyboard.FocusedElement as DependencyObject);

    /// <summary>Gets the controller of a view, creating it on first use.</summary>
    /// <param name="view">The view.</param>
    /// <param name="services">The editor services.</param>
    /// <param name="feature">The package's inline chat feature.</param>
    /// <returns>The controller.</returns>
    public static InlineChatController GetOrCreate(IWpfTextView view, InlineChatEditorServices services, InlineChatFeature feature) =>
        view.Properties.GetOrCreateSingletonProperty(typeof(InlineChatController), () => new InlineChatController(view, services, feature));

    /// <summary>Gets the controller of a view, if it has one.</summary>
    /// <param name="view">The view.</param>
    /// <param name="controller">The controller.</param>
    /// <returns><see langword="true"/> when the view has one.</returns>
    public static bool TryGet(ITextView? view, out InlineChatController controller)
    {
        controller = null!;
        return view is not null && view.Properties.TryGetProperty(typeof(InlineChatController), out controller);
    }

    /// <summary>Gets a value indicating whether a view is one inline chat works in: an editable document view.</summary>
    /// <param name="view">The view.</param>
    /// <returns><see langword="true"/> for document views.</returns>
    public static bool IsDocumentView(ITextView view) =>
        view.Roles.Contains(PredefinedTextViewRoles.Document)
        && view.Roles.Contains(PredefinedTextViewRoles.Editable)
        && !view.Roles.Contains(PredefinedTextViewRoles.EmbeddedPeekTextView);

    /// <summary>Opens inline chat on the selection, the enclosing member or the caret's line; focuses it when it is open already.</summary>
    public void Open()
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        if (_control is not null)
        {
            _control.FocusPrompt();
            return;
        }

        if (_opening || _view.IsClosed)
        {
            return;
        }

        _opening = true;
        _ = _feature.JoinableTaskFactory.RunAsync(async () =>
        {
            try
            {
                await OpenAsync().ConfigureAwait(true);
            }
#pragma warning disable CA1031 // A failure to open must not break the editor; it is logged.
            catch (Exception ex)
#pragma warning restore CA1031
            {
                _feature.Log.Error("inlinechat.open.failed", "Inline chat could not open.", ("error", ex.GetType().Name), ("detail", ex.Message));
                Teardown(focusView: true);
            }
            finally
            {
                _opening = false;
            }
        });
    }

    /// <summary>
    /// Routes a key the editor turned into a command while focus is in the adornment (see <see cref="InlineChatCommandFilter"/>).
    /// </summary>
    /// <param name="key">The key.</param>
    /// <param name="shift">Whether Shift is down.</param>
    /// <returns><see langword="true"/> when the key was used.</returns>
    public bool HandleKey(Key key, bool shift)
    {
        if (_control is not { } control)
        {
            return false;
        }

        if (key is Key.Up or Key.Down && _diff is { } diff && diff.InlineView.HasAggregateFocus)
        {
            diff.InlineView.ViewScroller.ScrollViewportVerticallyByLine(key == Key.Up ? ScrollDirection.Up : ScrollDirection.Down);
            return true;
        }

        return control.HandleKey(key, shift);
    }

    /// <inheritdoc/>
    public string? ReplaceScope(string expectedScopeText, string proposal)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        if (_scopeSpan is null)
        {
            return "Inline chat is closed.";
        }

        var buffer = _view.TextBuffer;
        var span = _scopeSpan.GetSpan(buffer.CurrentSnapshot);
        if (!string.Equals(span.GetText(), expectedScopeText, StringComparison.Ordinal))
        {
            return "The code changed while the proposal was shown. Press Esc and ask again.";
        }

        if (string.Equals(proposal, expectedScopeText, StringComparison.Ordinal))
        {
            return null; // No change: nothing to write and nothing to undo.
        }

        return Write(buffer, span, proposal);
    }

    /// <inheritdoc/>
    public string? InsertAtCaret(string text)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        var buffer = _view.TextBuffer;
        var caret = _view.Caret.Position.BufferPosition;
        var line = caret.GetContainingLine();
        var newline = line.LineBreakLength > 0 ? line.GetLineBreakText() : Environment.NewLine;

        // At the start of the caret's line, as whole lines: test code is a member or a class, never part of a statement.
        var insert = text.EndsWith("\n", StringComparison.Ordinal) ? text : text + newline;
        return Write(buffer, new SnapshotSpan(line.Start, 0), insert);
    }

    /// <inheritdoc/>
    public void Copy(string text)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        try
        {
            Clipboard.SetText(text);
        }
        catch (ExternalException ex)
        {
            _feature.Log.Warning("inlinechat.copy.failed", "Copying to the clipboard failed.", ("detail", ex.Message));
        }
    }

    /// <inheritdoc/>
    public void Close()
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        Teardown(focusView: true);
    }

    /// <summary>Closes inline chat without moving focus (the view is closing).</summary>
    public void Dispose()
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        Teardown(focusView: false);
    }

    private async Task OpenAsync()
    {
        await _feature.JoinableTaskFactory.SwitchToMainThreadAsync();
        var snapshot = _view.TextSnapshot;
        var caret = _view.Caret.Position.BufferPosition.TranslateTo(snapshot, PointTrackingMode.Positive).Position;
        var selection = _view.Selection.IsEmpty ? new SnapshotSpan(snapshot, caret, 0) : _view.Selection.StreamSelectionSpan.SnapshotSpan.TranslateTo(snapshot, SpanTrackingMode.EdgeInclusive);
        _filePath = _services.Documents.TryGetTextDocument(_view.TextDataModel.DocumentBuffer, out var document) ? document.FilePath ?? string.Empty : string.Empty;
        var languageId = DocumentLanguage.FromPath(_filePath);

        (int Start, int End)? member = null;
        if (selection.IsEmpty && RoslynInlineScope.Supports(languageId))
        {
            member = await FindMemberAsync(snapshot, caret).ConfigureAwait(true);
            await _feature.JoinableTaskFactory.SwitchToMainThreadAsync();
        }

        if (_view.IsClosed || _control is not null)
        {
            return;
        }

        var text = new SnapshotDocumentText(snapshot);
        var scope = InlineScopeSelector.Select(text, selection.Start.Position, selection.End.Position, caret, member?.Start, member?.End);
        var inlineDocument = new InlineChatDocument(_filePath, languageId, text, scope);
        Show(snapshot, inlineDocument);
    }

    private async Task<(int Start, int End)?> FindMemberAsync(ITextSnapshot snapshot, int caret)
    {
        await TaskScheduler.Default;
        using var budget = new CancellationTokenSource(MemberLookupBudget);
        try
        {
            return await RoslynInlineScope.FindMemberAsync(snapshot, caret, budget.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            _feature.Log.Debug("inlinechat.member.timeout", "The enclosing member was not found in time; using the caret's line.");
            return null;
        }
#pragma warning disable CA1031 // Without Roslyn the scope is the caret's line.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            _feature.Log.Debug("inlinechat.member.failed", "Finding the enclosing member failed.", ("error", ex.GetType().Name));
            return null;
        }
    }

    private void Show(ITextSnapshot snapshot, InlineChatDocument document)
    {
        var scope = document.Scope;
        _scopeSpan = snapshot.CreateTrackingSpan(scope.Start, scope.Length, SpanTrackingMode.EdgeInclusive);
        _anchor = snapshot.CreateTrackingPoint(scope.Start, PointTrackingMode.Negative);

        var options = new InlineChatSessionOptions
        {
            Log = _feature.Log,
            History = _feature.History,
            Availability = _feature.Availability,
        };
        // /fix: the IDE signals service (Error List and Roslyn diagnostics of the active document) when the package has it,
        // else the compiler's diagnostics read from Roslyn here, for C# and Visual Basic.
        var signals = Package.GetGlobalService(typeof(IIdeSignals)) as IIdeSignals;
        var roslyn = RoslynInlineScope.Supports(document.LanguageId);
        if (signals is not null || roslyn)
        {
            options.Diagnostics = token => ReadDiagnosticsAsync(signals, roslyn, snapshot, scope, token);
        }

        var session = new InlineChatSession(_feature.Connection, _feature.JoinableTaskFactory, document, this, options);
        _session = session;
        session.ProposalChanged += OnProposalChanged;
        session.PropertyChanged += OnSessionPropertyChanged;

        var control = new InlineChatAdornment(session);
        _control = control;
        CreateBrushes();
        _view.GetAdornmentLayer(InlineChatEditorServices.AdornmentLayer)
            .AddAdornment(AdornmentPositioningBehavior.OwnerControlled, visualSpan: null, tag: this, control, removedCallback: null);

        var width = Width();
        control.Width = width;
        control.Measure(new Size(width, double.PositiveInfinity));
        _transform.Reserve(_anchor, control.DesiredSize.Height + Gap);
        control.SizeChanged += OnControlSizeChanged;

        _view.LayoutChanged += OnLayoutChanged;
        _view.Closed += OnViewClosed;
        _view.Caret.PositionChanged += OnCaretMoved;

        _timer = new DispatcherTimer(DispatcherPriority.Background, control.Dispatcher) { Interval = TimeSpan.FromMilliseconds(200) };
        _timer.Tick += (_, _) => _session?.Tick();
        session.Start();
        InlineChatLineTransformSource.Relayout(_view);
        EnsureScopeVisible();
        control.FocusPrompt();
        _feature.Log.Info("inlinechat.opened", "Inline chat opened.", ("scope", scope.Kind.ToString()), ("lines", scope.LineCount), ("language", document.LanguageId));
    }

    private async Task<IReadOnlyList<Contracts.InlineEditDiagnostic>> ReadDiagnosticsAsync(
        IIdeSignals? signals, bool roslyn, ITextSnapshot snapshot, InlineScope scope, CancellationToken token)
    {
        if (signals is not null)
        {
            try
            {
                var found = await signals.GetDiagnosticsAsync(DiagnosticScope.ActiveDocument, token).ConfigureAwait(false);
                return InlineChatDiagnostics.FromSignals(found, _filePath);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
#pragma warning disable CA1031 // Fall back to Roslyn below.
            catch (Exception ex)
#pragma warning restore CA1031
            {
                _feature.Log.Debug("inlinechat.diagnostics.signals_failed", "The IDE signals could not list the diagnostics.", ("error", ex.GetType().Name));
            }
        }

        return roslyn ? await RoslynInlineScope.GetDiagnosticsAsync(snapshot, scope.Start, scope.End, token).ConfigureAwait(false) : [];
    }

    private void Teardown(bool focusView)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        _view.LayoutChanged -= OnLayoutChanged;
        _view.Closed -= OnViewClosed;
        _view.Caret.PositionChanged -= OnCaretMoved;
        _timer?.Stop();
        _timer = null;

        var hadFocus = OwnsKeyboardFocus;
        if (_control is { } control)
        {
            control.SizeChanged -= OnControlSizeChanged;
            control.SetDiff(null, visible: false);
            _control = null;
        }

        _diff?.Dispose();
        _diff = null;

        if (_session is { } session)
        {
            session.ProposalChanged -= OnProposalChanged;
            session.PropertyChanged -= OnSessionPropertyChanged;
            _session = null;
            session.Dispose();
        }

        _scopeSpan = null;
        _anchor = null;
        if (!_view.IsClosed)
        {
            _view.GetAdornmentLayer(InlineChatEditorServices.AdornmentLayer).RemoveAllAdornments();
            _view.GetAdornmentLayer(InlineChatEditorServices.ScopeLayer).RemoveAllAdornments();
            _transform.Release();
            InlineChatLineTransformSource.Relayout(_view);
            if (focusView || hadFocus)
            {
                _ = _view.VisualElement.Focus();
                Keyboard.Focus(_view.VisualElement);
            }
        }
        else
        {
            _transform.Release();
        }
    }

    private string? Write(ITextBuffer buffer, SnapshotSpan span, string text)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        if (!QueryEdit())
        {
            return "Visual Studio could not make " + (System.IO.Path.GetFileName(_filePath) is { Length: > 0 } name ? name : "the document")
                + " editable (source control refused the checkout, or the file is read-only).";
        }

        if (buffer.IsReadOnly(span.Span))
        {
            return "The document is read-only.";
        }

        // One ITextEdit inside one named undo transaction: one Undo restores the original.
        ITextUndoTransaction? transaction = null;
        try
        {
            if (_services.UndoHistories.TryGetHistory(buffer, out var history))
            {
                transaction = history.CreateTransaction(UndoDescription);
            }

            using (var edit = buffer.CreateEdit())
            {
                edit.Replace(span.Span, text);
                edit.Apply();
                if (edit.Canceled)
                {
                    transaction?.Cancel();
                    transaction = null;
                    return "Another extension cancelled the edit.";
                }
            }

            transaction?.Complete();
            transaction = null;
            _feature.Log.Info("inlinechat.written", "Inline chat wrote to the document.", ("chars", text.Length));
            return null;
        }
        finally
        {
            transaction?.Dispose();
        }
    }

    /// <summary>Asks source control to check the file out (or confirms it is editable) before any write, as the design requires.</summary>
    private bool QueryEdit()
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        if (string.IsNullOrEmpty(_filePath) || !File.Exists(_filePath)
            || Package.GetGlobalService(typeof(SVsQueryEditQuerySave)) is not IVsQueryEditQuerySave2 queryEdit)
        {
            return true; // A new, unsaved document has nothing to check out.
        }

        var hr = queryEdit.QueryEditFiles(
            (uint)tagVSQueryEditFlags.QEF_AllowInMemoryEdits,
            1,
            [_filePath],
            null,
            null,
            out var verdict,
            out _);
        return ErrorHandler.Succeeded(hr) && verdict == (uint)tagVSQueryEditResult.QER_EditOK;
    }

    private void OnSessionPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        if (_session is not { } session || _control is not { } control)
        {
            return;
        }

        switch (e.PropertyName)
        {
            case nameof(InlineChatSession.State):
                if (session.State == InlineChatState.Streaming)
                {
                    // A new request: a fresh difference view; /test compares against nothing, so its code shows as added.
                    _diff?.Dispose();
                    var original = session.Command == Contracts.InlineEditCommand.Test ? string.Empty : session.Document.ScopeText;
                    _diff = null;
                    try
                    {
                        _diff = new InlineDiffPresenter(_services, _view.TextBuffer.ContentType, original, _view);
                    }
#pragma warning disable CA1031 // Without a difference view the proposal still streams; the failure is logged.
                    catch (Exception ex)
#pragma warning restore CA1031
                    {
                        _feature.Log.Error("inlinechat.diff.failed", "The inline difference view could not be created.", ("error", ex.GetType().Name), ("detail", ex.Message));
                    }

                    _timer?.Start();
                }
                else
                {
                    _timer?.Stop();
                    session.Tick();
                }

                UpdateDiff();
                break;

            case nameof(InlineChatSession.ShowsDiff):
                UpdateDiff();
                break;
        }
    }

    private void OnProposalChanged(object? sender, EventArgs e)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        UpdateDiff();
    }

    private void UpdateDiff()
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        if (_session is not { } session || _control is not { } control)
        {
            return;
        }

        if (_diff is { } diff && session.ShowsDiff)
        {
            diff.Update(session.ProposalText);
            control.SetDiff(diff.Element, visible: session.ProposalText.Length > 0);
        }
        else
        {
            control.SetDiff(_diff?.Element, visible: false);
        }
    }

    private void OnControlSizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (_anchor is null || !e.HeightChanged)
        {
            return;
        }

        var space = e.NewSize.Height + Gap;
        if (Math.Abs(space - _transform.Space) > 0.5)
        {
            _transform.Reserve(_anchor, space);
            InlineChatLineTransformSource.Relayout(_view);
        }
    }

    private void OnLayoutChanged(object? sender, TextViewLayoutChangedEventArgs e)
    {
        Position();
        DrawScope();
    }

    private void OnViewClosed(object? sender, EventArgs e)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        _feature.Log.Info("inlinechat.closed", "Inline chat closed with its document.");
        Dispose();
    }

    /// <summary>Closes inline chat once the caret is further from the scope than the view has lines: the user has moved on.</summary>
    private void OnCaretMoved(object? sender, CaretPositionChangedEventArgs e)
    {
        if (_scopeSpan is null || _session is null || _view.TextViewLines is not { Count: > 0 } lines)
        {
            return;
        }

        var snapshot = e.NewPosition.BufferPosition.Snapshot;
        var span = _scopeSpan.GetSpan(snapshot);
        var caretLine = e.NewPosition.BufferPosition.GetContainingLine().LineNumber;
        var first = snapshot.GetLineNumberFromPosition(span.Start);
        var last = snapshot.GetLineNumberFromPosition(span.End);
        var distance = caretLine < first ? first - caretLine : caretLine > last ? caretLine - last : 0;
        if (distance > lines.Count)
        {
            _feature.Log.Info("inlinechat.closed", "Inline chat closed: the caret moved away from the scope.", ("lines", distance));
            _session.Discard();
        }
    }

    private double Width() => Math.Max(360, _view.ViewportWidth - 24);

    /// <summary>Places the control in the space above the scope's first line, aligned with its indentation.</summary>
    private void Position()
    {
        if (_control is not { } control || _anchor is null || _view.IsClosed || _view.TextViewLines is null)
        {
            return;
        }

        var point = _anchor.GetPoint(_view.TextSnapshot);
        var line = _view.TextViewLines.GetTextViewLineContainingBufferPosition(point);
        if (line is null)
        {
            control.Visibility = Visibility.Hidden;
            return;
        }

        var text = line.Extent.GetText();
        var indent = 0;
        while (indent < text.Length && char.IsWhiteSpace(text[indent]))
        {
            indent++;
        }

        var indentLeft = indent < text.Length ? line.GetCharacterBounds(line.Start + indent).Left : line.TextLeft;
        var left = Math.Max(_view.ViewportLeft, Math.Min(indentLeft, _view.ViewportRight - 400));
        var width = Math.Max(360, _view.ViewportRight - left - 24);
        if (Math.Abs(control.Width - width) > 0.5)
        {
            control.Width = width;
        }

        Canvas.SetLeft(control, left);
        Canvas.SetTop(control, line.TextTop - _transform.Space);
        control.Visibility = Visibility.Visible;
    }

    /// <summary>Highlights the scope's lines: a light wash of the selection colour and a bar at the left.</summary>
    private void DrawScope()
    {
        if (_scopeSpan is null || _view.IsClosed || _view.TextViewLines is null)
        {
            return;
        }

        var layer = _view.GetAdornmentLayer(InlineChatEditorServices.ScopeLayer);
        layer.RemoveAllAdornments();
        var span = _scopeSpan.GetSpan(_view.TextSnapshot);
        IEnumerable<ITextViewLine> lines = span.Length > 0
            ? _view.TextViewLines.GetTextViewLinesIntersectingSpan(span)
            : _view.TextViewLines.GetTextViewLineContainingBufferPosition(span.Start) is { } single ? [single] : [];
        foreach (var line in lines)
        {
            var wash = new Rectangle { Width = _view.ViewportWidth, Height = line.TextHeight, Fill = _highlight, IsHitTestVisible = false };
            Canvas.SetLeft(wash, _view.ViewportLeft);
            Canvas.SetTop(wash, line.TextTop);
            layer.AddAdornment(AdornmentPositioningBehavior.OwnerControlled, null, this, wash, null);

            var bar = new Rectangle { Width = 3, Height = line.TextHeight, Fill = _accent, IsHitTestVisible = false };
            Canvas.SetLeft(bar, _view.ViewportLeft);
            Canvas.SetTop(bar, line.TextTop);
            layer.AddAdornment(AdornmentPositioningBehavior.OwnerControlled, null, this, bar, null);
        }
    }

    private void CreateBrushes()
    {
        var map = _services.FormatMaps.GetEditorFormatMap(_view);
        var selected = map.GetProperties("Selected Text")[EditorFormatDefinition.BackgroundBrushId] as SolidColorBrush;
        var color = selected?.Color ?? SystemColors.HighlightColor;
        _highlight = Freeze(new SolidColorBrush(color) { Opacity = 0.18 });
        _accent = Freeze(new SolidColorBrush(color));

        static Brush Freeze(Brush brush)
        {
            brush.Freeze();
            return brush;
        }
    }

    /// <summary>Scrolls so the adornment and the start of the scope are in view.</summary>
    private void EnsureScopeVisible()
    {
        if (_scopeSpan is null || _view.TextViewLines is null)
        {
            return;
        }

        var span = _scopeSpan.GetSpan(_view.TextSnapshot);
        var line = _view.TextViewLines.GetTextViewLineContainingBufferPosition(span.Start);
        if (line is null || line.Top < _view.ViewportTop || line.TextBottom > _view.ViewportBottom)
        {
            _view.DisplayTextLineContainingBufferPosition(span.Start, Math.Min(_view.ViewportHeight / 4, 80), ViewRelativePosition.Top);
        }
    }
}

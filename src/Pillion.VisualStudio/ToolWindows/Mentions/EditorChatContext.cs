using System;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using Microsoft.VisualStudio;
using Microsoft.VisualStudio.ComponentModelHost;
using Microsoft.VisualStudio.Editor;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Text;
using Microsoft.VisualStudio.Text.Editor;
using Microsoft.VisualStudio.TextManager.Interop;
using Microsoft.VisualStudio.Threading;
using Pillion.VisualStudio.Core;
using Pillion.VisualStudio.Core.Mentions;
using Pillion.VisualStudio.Core.Signals;

namespace Pillion.VisualStudio.ToolWindows;

/// <summary>
/// The editor side of the chat window's mentions (work items 1.9 and 2.13): the active editor's selection (<c>@selection</c>),
/// the clipboard (<c>@clipboard</c>), the solution's diagnostics from <see cref="IIdeSignals"/> (<c>@errors</c>), the Output
/// panes (<c>@output</c>), the last build (<c>@build</c>) and the Tests pane's failures (<c>@tests</c>), and the current
/// file region sent with each message (the selection, else 60 lines around the caret). The active editor is the last text
/// view that had focus, so it is found while the chat window has the keyboard.
/// </summary>
internal sealed class EditorChatContext : IChatEditorContext
{
    /// <summary>The lines around the caret sent as the current file region when nothing is selected.</summary>
    public const int RegionLines = 60;

    /// <summary>The most characters taken from a selection or region (the host caps it again by tokens).</summary>
    public const int MaxRegionChars = 200_000;

    private readonly JoinableTaskFactory _joinableTaskFactory;
    private readonly Func<IIdeSignals?> _signals;
    private readonly PillionLog? _log;
    private IVsTextManager? _textManager;
    private IVsEditorAdaptersFactoryService? _adapters;
    private ITextDocumentFactoryService? _documents;
    private bool _servicesRead;

    /// <summary>Initializes a new instance of the <see cref="EditorChatContext"/> class. The editor services are found on first use, on the UI thread.</summary>
    /// <param name="joinableTaskFactory">The package's joinable task factory.</param>
    /// <param name="signals">Supplies the IDE signals (diagnostics); may return <see langword="null"/> before they start.</param>
    /// <param name="log">The extension log; text is never logged.</param>
    public EditorChatContext(JoinableTaskFactory joinableTaskFactory, Func<IIdeSignals?> signals, PillionLog? log)
    {
        _joinableTaskFactory = joinableTaskFactory;
        _signals = signals;
        _log = log;
    }

    /// <inheritdoc/>
    public async Task<string?> GetSelectionAsync(CancellationToken cancellationToken)
    {
        await _joinableTaskFactory.SwitchToMainThreadAsync(cancellationToken);
        var view = ActiveView();
        if (view is null || view.Selection.IsEmpty)
        {
            return null;
        }

        var text = string.Join("\n", view.Selection.SelectedSpans.Select(s => s.GetText()));
        return text.Length == 0 ? null : Cap(text);
    }

    /// <inheritdoc/>
    public async Task<CurrentFileContext?> GetCurrentFileAsync(CancellationToken cancellationToken)
    {
        await _joinableTaskFactory.SwitchToMainThreadAsync(cancellationToken);
        var view = ActiveView();
        if (view is null || _documents is null || !_documents.TryGetTextDocument(view.TextDataModel.DocumentBuffer, out var document) || string.IsNullOrEmpty(document.FilePath))
        {
            return null;
        }

        string region;
        if (!view.Selection.IsEmpty)
        {
            region = string.Join("\n", view.Selection.SelectedSpans.Select(s => s.GetText()));
        }
        else
        {
            var snapshot = view.TextSnapshot;
            var caretLine = view.Caret.Position.BufferPosition.GetContainingLine().LineNumber;
            var first = Math.Max(0, caretLine - (RegionLines / 2));
            var last = Math.Min(snapshot.LineCount - 1, first + RegionLines - 1);
            first = Math.Max(0, Math.Min(first, last - RegionLines + 1));
            var span = new SnapshotSpan(snapshot.GetLineFromLineNumber(first).Start, snapshot.GetLineFromLineNumber(last).End);
            region = span.GetText();
        }

        return new CurrentFileContext(document.FilePath, Cap(region));
    }

    /// <inheritdoc/>
    public async Task<string?> GetClipboardTextAsync(CancellationToken cancellationToken)
    {
        await _joinableTaskFactory.SwitchToMainThreadAsync(cancellationToken);
        try
        {
            return Clipboard.ContainsText() ? Cap(Clipboard.GetText()) : null;
        }
        catch (Exception ex) when (ex is ExternalException or COMException)
        {
            _log?.Debug("chat.clipboard.failed", "Reading the clipboard failed.", ("error", ex.GetType().Name));
            return null;
        }
    }

    /// <inheritdoc/>
    public async Task<string> GetErrorsPayloadAsync(CancellationToken cancellationToken)
    {
        if (_signals() is not { } signals)
        {
            return string.Empty;
        }

        await TaskScheduler.Default;
        var diagnostics = await signals.GetDiagnosticsAsync(DiagnosticScope.Solution, cancellationToken).ConfigureAwait(false);
        return ErrorsPayload.Format(diagnostics);
    }

    /// <inheritdoc/>
    public async Task<string?> GetOutputPaneTextAsync(string pane, CancellationToken cancellationToken)
    {
        if (_signals() is not { } signals)
        {
            return null;
        }

        await TaskScheduler.Default;
        var text = await signals.GetOutputPaneTextAsync(pane, IdeSignalsHub.MentionPaneChars, cancellationToken).ConfigureAwait(false);
        return string.IsNullOrWhiteSpace(text) ? null : text;
    }

    /// <inheritdoc/>
    public async Task<string?> GetLastBuildAsync(CancellationToken cancellationToken)
    {
        if (_signals() is not { } signals || signals.LastBuild is not { } build)
        {
            return null;
        }

        // The tail kept when the build finished; while it is still being read, the pane as it is now.
        var output = build.Output ?? await GetOutputPaneTextAsync("Build", cancellationToken).ConfigureAwait(false) ?? string.Empty;
        return build.Summary() + "\n" + output;
    }

    /// <inheritdoc/>
    public async Task<string?> GetTestResultsAsync(CancellationToken cancellationToken)
    {
        // Test Explorer has no documented API for results (design section 5), so the Tests pane's failures stand in.
        var text = await GetOutputPaneTextAsync("Tests", cancellationToken).ConfigureAwait(false);
        return text is null ? null : TestOutput.FailuresOrTail(text);
    }

    private static string Cap(string text) => text.Length <= MaxRegionChars ? text : text.Substring(0, MaxRegionChars);

    private IWpfTextView? ActiveView()
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        if (!_servicesRead)
        {
            _servicesRead = true;
            _textManager = ServiceProvider.GlobalProvider.GetService(typeof(SVsTextManager)) as IVsTextManager;
            var componentModel = ServiceProvider.GlobalProvider.GetService(typeof(SComponentModel)) as IComponentModel;
            _adapters = componentModel?.GetService<IVsEditorAdaptersFactoryService>();
            _documents = componentModel?.GetService<ITextDocumentFactoryService>();
            if (_textManager is null || _adapters is null)
            {
                _log?.Warning("chat.editor.unavailable", "The editor services are unavailable; @selection and the current file have nothing to send.");
            }
        }

        if (_textManager is null || _adapters is null
            || ErrorHandler.Failed(_textManager.GetActiveView(0, null, out var vsView))
            || vsView is null)
        {
            return null;
        }

        return _adapters.GetWpfTextView(vsView) is { IsClosed: false } view ? view : null;
    }
}

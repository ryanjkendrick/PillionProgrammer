using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Microsoft.VisualStudio.ComponentModelHost;
using Microsoft.VisualStudio.Editor;
using Microsoft.VisualStudio.PlatformUI;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Text;
using Microsoft.VisualStudio.Text.Editor;
using Microsoft.VisualStudio.TextManager.Interop;
using Microsoft.VisualStudio.Utilities;
using Pillion.VisualStudio.Core;

namespace Pillion.VisualStudio.ToolWindows;

/// <summary>The state <see cref="PillionPackage"/> hands to <see cref="ChatToolWindow"/> when it creates the window.</summary>
internal sealed class ChatToolWindowState
{
    /// <summary>Initializes a new instance of the <see cref="ChatToolWindowState"/> class.</summary>
    /// <param name="viewModel">The chat view model (not yet started).</param>
    /// <param name="ownedResources">Disposed with the window (the solution file list the mentions use).</param>
    public ChatToolWindowState(ChatViewModel viewModel, params IDisposable[] ownedResources)
    {
        ViewModel = viewModel;
        OwnedResources = ownedResources ?? [];
    }

    /// <summary>Gets the chat view model.</summary>
    public ChatViewModel ViewModel { get; }

    /// <summary>Gets what is disposed with the window.</summary>
    public IReadOnlyList<IDisposable> OwnedResources { get; }
}

/// <summary>
/// The Pillion Chat tool window (View &gt; Other Windows &gt; Pillion Chat, or Tools &gt; Pillion: Open Chat). Created
/// asynchronously by <see cref="PillionPackage"/>, which supplies a <see cref="ChatToolWindowState"/>.
/// </summary>
[Guid(PackageGuids.ChatToolWindowString)]
internal sealed class ChatToolWindow : ToolWindowPane
{
    /// <summary>The window caption.</summary>
    public const string Title = "Pillion Chat";

    private readonly ChatViewModel _viewModel;
    private readonly ChatToolWindowControl _control;
    private readonly IReadOnlyList<IDisposable> _owned;

    /// <summary>Initializes a new instance of the <see cref="ChatToolWindow"/> class. Runs on the UI thread.</summary>
    /// <param name="state">The state from <see cref="PillionPackage"/>.</param>
    public ChatToolWindow(ChatToolWindowState state)
        : base(null)
    {
        if (state is null)
        {
            throw new ArgumentNullException(nameof(state));
        }

        ThreadHelper.ThrowIfNotOnUIThread();
        Caption = Title;
        _viewModel = state.ViewModel;
        _owned = state.OwnedResources;
        var log = PillionPackage.Instance?.Log;
        var actions = CreateActions(log);
        _viewModel.ConfirmDelete = actions.ConfirmDelete;
        _control = new ChatToolWindowControl(_viewModel, CreateRenderContext(actions, log));
        Content = _control;
        _viewModel.Start();
    }

    /// <inheritdoc/>
    public override void OnToolWindowCreated()
    {
        base.OnToolWindowCreated();
        _control.FocusInput();
    }

    /// <inheritdoc/>
    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _control.Dispose();
            _viewModel.Dispose();
            foreach (var resource in _owned)
            {
                resource.Dispose();
            }
        }

        base.Dispose(disposing);
    }

    /// <summary>Puts text on the clipboard (the Outbound log's Copy). Must be called on the UI thread.</summary>
    /// <param name="text">The text.</param>
    public static void CopyToClipboard(string text)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        try
        {
            System.Windows.Clipboard.SetText(text ?? string.Empty);
        }
        catch (Exception ex) when (ex is ExternalException or COMException)
        {
            PillionPackage.Instance?.Log?.Warning("chat.copy.failed", "Copying to the clipboard failed.", ("error", ex.GetType().Name));
        }
    }

    /// <summary>Opens a file in the editor and puts the caret on a line (a cited span under a reply). Must be called on the UI thread.</summary>
    /// <param name="path">The full path of the file.</param>
    /// <param name="line">The zero-based line.</param>
    public static void OpenCitation(string path, int line)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        OpenCitationSpan(path, line, line);
    }

    /// <summary>
    /// Opens a file in the editor with a cited span selected, its first line centred (work item 2.12). A one-line span puts the
    /// caret at the start of the line. Must be called on the UI thread.
    /// </summary>
    /// <param name="path">The full path of the file.</param>
    /// <param name="startLine">The zero-based first line.</param>
    /// <param name="endLine">The zero-based last line (inclusive).</param>
    public static void OpenCitationSpan(string path, int startLine, int endLine)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        var line = startLine;
        if (string.IsNullOrWhiteSpace(path) || !System.IO.File.Exists(path))
        {
            PillionPackage.Instance?.Log?.Info("chat.citation.missing", "A cited file is not on disk any more.");
            return;
        }

        try
        {
            VsShellUtilities.OpenDocument(ServiceProvider.GlobalProvider, path, Guid.Empty, out _, out _, out var frame, out var view);
            frame?.Show();
            if (view is not null)
            {
                var target = Math.Max(0, line);
                var last = Math.Max(target, endLine);
                if (last > target && view.GetBuffer(out var lines) == Microsoft.VisualStudio.VSConstants.S_OK && lines is not null
                    && lines.GetLineCount(out var count) == Microsoft.VisualStudio.VSConstants.S_OK && count > 0)
                {
                    // Select whole lines: from the start of the first to the end of the last (clamped to the file).
                    last = Math.Min(last, count - 1);
                    target = Math.Min(target, last);
                    lines.GetLengthOfLine(last, out var length);
                    view.SetSelection(target, 0, last, length);
                    view.CenterLines(target, Math.Max(1, last - target + 1));
                }
                else
                {
                    view.SetCaretPos(target, 0);
                    view.CenterLines(target, 1);
                }
            }
        }
        catch (Exception ex) when (ex is COMException or System.IO.IOException or InvalidOperationException)
        {
            PillionPackage.Instance?.Log?.Warning("chat.citation.failed", "Opening a cited span failed.", ("error", ex.GetType().Name));
        }
    }

    private static ChatEditorActions CreateActions(PillionLog? log)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        var componentModel = ServiceProvider.GlobalProvider.GetService(typeof(SComponentModel)) as IComponentModel;
        return new ChatEditorActions(
            ServiceProvider.GlobalProvider,
            ServiceProvider.GlobalProvider.GetService(typeof(SVsTextManager)) as IVsTextManager,
            componentModel?.GetService<IVsEditorAdaptersFactoryService>(),
            componentModel?.GetService<ITextDocumentFactoryService>(),
            log);
    }

    /// <summary>
    /// Themes the Markdown renderer with Visual Studio's colour keys and gives code blocks embedded editor views (falling
    /// back to a monospace text box when the editor services are missing).
    /// </summary>
    private static MarkdownRenderContext CreateRenderContext(ChatEditorActions actions, PillionLog? log)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        var theme = new MarkdownTheme
        {
            TextBrushKey = VsBrushes.ToolWindowTextKey,
            SecondaryTextBrushKey = VsBrushes.GrayTextKey,
            LinkBrushKey = EnvironmentColors.ControlLinkTextBrushKey,
            BorderBrushKey = VsBrushes.ToolWindowBorderKey,
            CodeBackgroundBrushKey = VsBrushes.WindowKey,
            CodeTextBrushKey = VsBrushes.WindowTextKey,
            QuoteBarBrushKey = VsBrushes.GrayTextKey,
        };
        var context = new MarkdownRenderContext
        {
            Theme = theme,
            OpenLink = actions.ConfirmAndOpen,
            InsertAtCaret = actions.CanInsert ? actions.InsertAtCaret : null,
        };

        var componentModel = ServiceProvider.GlobalProvider.GetService(typeof(SComponentModel)) as IComponentModel;
        var editors = componentModel?.GetService<ITextEditorFactoryService>();
        var buffers = componentModel?.GetService<ITextBufferFactoryService>();
        var contentTypes = componentModel?.GetService<IContentTypeRegistryService>();
        var extensions = componentModel?.GetService<IFileExtensionRegistryService>();
        if (editors is not null && buffers is not null && contentTypes is not null && extensions is not null)
        {
            context.CodeViews = new EditorCodeViewFactory(editors, buffers, contentTypes, extensions, new PlainCodeViewFactory(theme), () => RoslynCodeWorkspaces.TryCreate(componentModel!, log), log);
        }
        else
        {
            log?.Warning("chat.codeview.unavailable", "The editor services are unavailable; chat code blocks use a text box.");
        }

        return context;
    }
}

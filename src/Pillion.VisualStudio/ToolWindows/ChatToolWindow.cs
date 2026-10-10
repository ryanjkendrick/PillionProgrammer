using System;
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
    public ChatToolWindowState(ChatViewModel viewModel)
    {
        ViewModel = viewModel;
    }

    /// <summary>Gets the chat view model.</summary>
    public ChatViewModel ViewModel { get; }
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
        }

        base.Dispose(disposing);
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

using System;
using System.Runtime.InteropServices;
using Microsoft.VisualStudio.Shell;
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

        Caption = Title;
        _viewModel = state.ViewModel;
        _control = new ChatToolWindowControl(_viewModel);
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
            _viewModel.Dispose();
        }

        base.Dispose(disposing);
    }
}

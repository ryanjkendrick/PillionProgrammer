using System.Windows.Controls;
using System.Windows.Input;
using Pillion.VisualStudio.Core;

namespace Pillion.VisualStudio.ToolWindows;

/// <summary>The Pillion Chat tool window's content. All behaviour is in <see cref="ChatViewModel"/>.</summary>
internal sealed partial class ChatToolWindowControl : UserControl
{
    private readonly ChatViewModel _viewModel;
    private bool _followTail = true;

    /// <summary>Initializes a new instance of the <see cref="ChatToolWindowControl"/> class.</summary>
    /// <param name="viewModel">The chat view model.</param>
    public ChatToolWindowControl(ChatViewModel viewModel)
    {
        _viewModel = viewModel;
        DataContext = viewModel;
        InitializeComponent();
    }

    /// <summary>Moves keyboard focus to the message box (when the window is shown).</summary>
    public void FocusInput() => Input.Focus();

    /// <summary>Enter sends; Shift+Enter falls through to the text box, which inserts a new line.</summary>
    private void OnInputPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || (Keyboard.Modifiers & ModifierKeys.Shift) != 0)
        {
            return;
        }

        e.Handled = true;
        if (_viewModel.SendCommand.CanExecute(null))
        {
            _viewModel.SendCommand.Execute(null);
        }
    }

    /// <summary>Keeps the newest text in view while the reader is at the bottom of the transcript.</summary>
    private void OnTranscriptScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        if (e.ExtentHeightChange == 0)
        {
            // The reader scrolled: follow the tail again only once they are back at the bottom.
            _followTail = TranscriptScroller.VerticalOffset >= TranscriptScroller.ScrollableHeight - 1;
        }
        else if (_followTail)
        {
            TranscriptScroller.ScrollToVerticalOffset(TranscriptScroller.ExtentHeight);
        }
    }
}

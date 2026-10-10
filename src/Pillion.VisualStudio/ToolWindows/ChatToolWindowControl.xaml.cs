using System;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Pillion.VisualStudio.Core;

namespace Pillion.VisualStudio.ToolWindows;

/// <summary>
/// The Pillion Chat tool window's content. Behaviour is in <see cref="ChatViewModel"/>; this class keeps the transcript
/// scrolled to the newest text, owns the cache of rendered replies and handles the rename box's keys.
/// </summary>
internal sealed partial class ChatToolWindowControl : UserControl, IDisposable
{
    private readonly ChatViewModel _viewModel;
    private readonly MessageRenderCache _renders;
    private ObservableCollection<ChatTurnViewModel>? _turns;
    private ScrollViewer? _scroller;
    private bool _followTail = true;

    /// <summary>Initializes a new instance of the <see cref="ChatToolWindowControl"/> class.</summary>
    /// <param name="viewModel">The chat view model.</param>
    /// <param name="renderContext">The theme and services the replies are rendered with.</param>
    public ChatToolWindowControl(ChatViewModel viewModel, MarkdownRenderContext renderContext)
    {
        _viewModel = viewModel;
        DataContext = viewModel;
        InitializeComponent();

        renderContext.BaseFontSize = () => FontSize;
        _renders = new MessageRenderCache(renderContext);
        MarkdownMessageView.SetCache(Transcript, _renders);
        _viewModel.PropertyChanged += OnViewModelPropertyChanged;
        WatchTurns();
        Transcript.Loaded += (_, _) => _scroller ??= Transcript.Template?.FindName("PART_Scroller", Transcript) as ScrollViewer;
    }

    /// <summary>Moves keyboard focus to the message box (when the window is shown).</summary>
    public void FocusInput() => Input.Focus();

    /// <inheritdoc/>
    public void Dispose()
    {
        _viewModel.PropertyChanged -= OnViewModelPropertyChanged;
        if (_turns is not null)
        {
            _turns.CollectionChanged -= OnTurnsChanged;
        }

        _renders.Dispose();
    }

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
        _scroller ??= Transcript.Template?.FindName("PART_Scroller", Transcript) as ScrollViewer;
        if (_scroller is null || !ReferenceEquals(e.OriginalSource, _scroller))
        {
            return; // A code block or table scrolled sideways.
        }

        if (e.ExtentHeightChange == 0)
        {
            // The reader scrolled: follow the tail again only once they are back at the bottom.
            _followTail = _scroller.VerticalOffset >= _scroller.ScrollableHeight - 1;
        }
        else if (_followTail)
        {
            _scroller.ScrollToEnd();
        }
    }

    /// <summary>Enter saves the name, Esc cancels.</summary>
    private void OnRenameKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            e.Handled = true;
            _viewModel.CommitRename();
            ConversationPicker.Focus();
        }
        else if (e.Key == Key.Escape)
        {
            e.Handled = true;
            _viewModel.CancelRename();
            ConversationPicker.Focus();
        }
    }

    /// <summary>Leaving the box saves the name, as in Solution Explorer.</summary>
    private void OnRenameLostFocus(object sender, KeyboardFocusChangedEventArgs e) => _viewModel.CommitRename();

    private void OnRenameVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (RenameBox.IsVisible)
        {
            RenameBox.Focus();
            RenameBox.SelectAll();
        }
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ChatViewModel.Turns))
        {
            // Another conversation: release the replies rendered for the previous one and start at its newest text.
            WatchTurns();
            _renders.Retain(_viewModel.Turns);
            _followTail = true;
            _scroller?.ScrollToEnd();
        }
    }

    private void WatchTurns()
    {
        if (_turns is not null)
        {
            _turns.CollectionChanged -= OnTurnsChanged;
        }

        _turns = _viewModel.Turns;
        _turns.CollectionChanged += OnTurnsChanged;
    }

    private void OnTurnsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.Action is NotifyCollectionChangedAction.Remove or NotifyCollectionChangedAction.Replace or NotifyCollectionChangedAction.Reset)
        {
            _renders.Retain(_viewModel.Turns);
        }
        else if (e.Action == NotifyCollectionChangedAction.Add)
        {
            // A new message always brings the newest text into view.
            _followTail = true;
        }
    }
}

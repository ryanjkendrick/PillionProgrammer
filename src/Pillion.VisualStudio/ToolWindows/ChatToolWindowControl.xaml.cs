using System;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Pillion.VisualStudio.Core;
using Pillion.VisualStudio.Core.Mentions;

namespace Pillion.VisualStudio.ToolWindows;

/// <summary>
/// The Pillion Chat tool window's content. Behaviour is in <see cref="ChatViewModel"/>; this class keeps the transcript
/// scrolled to the newest text, owns the cache of rendered replies, handles the rename box's keys, and connects the input
/// to the mention picker (its text and caret, its keys, the popup at the caret and the caret after a pick).
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
        _viewModel.InputCaretRequested += OnInputCaretRequested;
        _viewModel.Mentions.Picker.PropertyChanged += OnPickerPropertyChanged;
        WatchTurns();
        Transcript.Loaded += (_, _) => _scroller ??= Transcript.Template?.FindName("PART_Scroller", Transcript) as ScrollViewer;
    }

    /// <summary>Moves keyboard focus to the message box (when the window is shown).</summary>
    public void FocusInput() => Input.Focus();

    /// <inheritdoc/>
    public void Dispose()
    {
        _viewModel.PropertyChanged -= OnViewModelPropertyChanged;
        _viewModel.InputCaretRequested -= OnInputCaretRequested;
        _viewModel.Mentions.Picker.PropertyChanged -= OnPickerPropertyChanged;
        if (_turns is not null)
        {
            _turns.CollectionChanged -= OnTurnsChanged;
        }

        _renders.Dispose();
    }

    /// <summary>
    /// While the mention picker is open, Up, Down, Enter, Tab and Esc drive it. Otherwise Enter sends; Shift+Enter falls
    /// through to the text box, which inserts a new line.
    /// </summary>
    private void OnInputPreviewKeyDown(object sender, KeyEventArgs e)
    {
        var modifiers = Keyboard.Modifiers;
        MentionKey? key = e.Key switch
        {
            Key.Down when modifiers == ModifierKeys.None => MentionKey.Down,
            Key.Up when modifiers == ModifierKeys.None => MentionKey.Up,
            Key.Enter when modifiers == ModifierKeys.None => MentionKey.Enter,
            Key.Tab when modifiers == ModifierKeys.None => MentionKey.Tab,
            Key.Escape => MentionKey.Escape,
            _ => null,
        };
        if (key is { } mentionKey && _viewModel.Mentions.HandleKey(mentionKey))
        {
            e.Handled = true;
            return;
        }

        if (e.Key != Key.Enter || (modifiers & ModifierKeys.Shift) != 0)
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

    /// <summary>Follows the text for the mention picker.</summary>
    private void OnInputTextChanged(object sender, TextChangedEventArgs e) => UpdateMentions();

    /// <summary>Follows the caret for the mention picker.</summary>
    private void OnInputSelectionChanged(object sender, RoutedEventArgs e) => UpdateMentions();

    /// <summary>The picker belongs to the input: it closes when the keyboard leaves the window's input.</summary>
    private void OnInputLostKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (e.NewFocus is not System.Windows.Media.Visual target || !IsAncestorOf(target))
        {
            _viewModel.Mentions.Picker.Close();
        }
    }

    /// <summary>A click on a picker entry picks it; the keyboard stays in the input.</summary>
    private void OnMentionItemClicked(object sender, MouseButtonEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: MentionPickerItem item })
        {
            e.Handled = true;
            _viewModel.Mentions.Picker.Select(item);
            _viewModel.Mentions.HandleKey(MentionKey.Enter);
            Input.Focus();
        }
    }

    private void UpdateMentions()
    {
        _viewModel.Mentions.UpdateInput(Input.Text, Input.CaretIndex);
        PlacePopup();
    }

    /// <summary>Puts the picker above the mention's <c>@</c> (it flips below when there is no room above).</summary>
    private void PlacePopup()
    {
        if (!_viewModel.Mentions.Picker.IsOpen)
        {
            return;
        }

        var index = _viewModel.Mentions.Picker.Token?.Start ?? Input.CaretIndex;
        var rect = Input.GetRectFromCharacterIndex(Math.Max(0, Math.Min(index, Input.Text.Length)));
        MentionPopup.PlacementRectangle = rect.IsEmpty ? new Rect(0, 0, 0, 0) : rect;
        MentionPopup.HorizontalOffset = MentionPopup.HorizontalOffset == 0 ? 0.0001 : 0; // Nudges an open popup to the new place.
    }

    private void OnInputCaretRequested(object? sender, int caret)
    {
        Input.CaretIndex = Math.Max(0, Math.Min(caret, Input.Text.Length));
        Input.Focus();
        UpdateMentions();
    }

    private void OnPickerPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MentionPickerViewModel.SelectedIndex) && _viewModel.Mentions.Picker.SelectedItem is { } item)
        {
            MentionList.ScrollIntoView(item);
        }
        else if (e.PropertyName == nameof(MentionPickerViewModel.IsOpen))
        {
            PlacePopup();
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

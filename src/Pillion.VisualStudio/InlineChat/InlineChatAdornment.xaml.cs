using System;
using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using Pillion.VisualStudio.Core.InlineChat;

namespace Pillion.VisualStudio.InlineChat;

/// <summary>
/// The inline chat adornment's content (work item 1.6). All behaviour is in <see cref="InlineChatSession"/>; this class
/// maps keys to it: Enter sends (or applies the proposal when the prompt is empty), Esc discards, Up and Down move in
/// the slash-command list or recall recent prompts. Keys reach it two ways, and both end here: as WPF key events, and as
/// editor commands that Visual Studio translated from the key before WPF saw it, which <see cref="InlineChatCommandFilter"/>
/// routes to <see cref="HandleKey"/>.
/// </summary>
internal sealed partial class InlineChatAdornment : UserControl
{
    private readonly InlineChatSession _session;
    private int _recall = -1;
    private bool _recalling;
    private bool _committingRecent;

    /// <summary>Initializes a new instance of the <see cref="InlineChatAdornment"/> class.</summary>
    /// <param name="session">The session.</param>
    public InlineChatAdornment(InlineChatSession session)
    {
        _session = session;
        DataContext = session;
        InitializeComponent();
        PreviewKeyDown += OnPreviewKeyDown;
        Prompt.TextChanged += (_, _) =>
        {
            if (!_recalling)
            {
                _recall = -1;
            }
        };
        Recent.DropDownClosed += (_, _) => CommitRecent();
    }

    /// <summary>Gets the prompt text box.</summary>
    public TextBox PromptBox => Prompt;

    /// <summary>Moves keyboard focus to the prompt, with the caret at the end (once the control is loaded).</summary>
    public void FocusPrompt()
    {
        void Focus()
        {
            Prompt.Focus();
            Keyboard.Focus(Prompt);
            Prompt.CaretIndex = Prompt.Text.Length;
        }

        if (IsLoaded)
        {
            Focus();
            return;
        }

        void OnLoaded(object sender, RoutedEventArgs e)
        {
            Loaded -= OnLoaded;
            Focus();
        }

        Loaded += OnLoaded;
    }

    /// <summary>Shows or hides the difference view.</summary>
    /// <param name="element">The difference view, or <see langword="null"/> to remove it.</param>
    /// <param name="visible">Whether it is shown.</param>
    public void SetDiff(FrameworkElement? element, bool visible)
    {
        if (!ReferenceEquals(DiffHost.Content, element))
        {
            DiffHost.Content = element;
        }

        DiffHost.Visibility = element is not null && visible ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>Gets a value indicating whether an element is inside this control (including its drop-down popups).</summary>
    /// <param name="element">The element, usually <see cref="Keyboard.FocusedElement"/>.</param>
    /// <returns><see langword="true"/> when it is.</returns>
    public bool Contains(DependencyObject? element)
    {
        for (var current = element; current is not null; current = ParentOf(current))
        {
            if (ReferenceEquals(current, this))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Handles a navigation key inside the adornment: Enter, Esc, Up, Down and Tab (Shift+Tab with <paramref name="shift"/>).
    /// Text editing keys are left to the focused text box.
    /// </summary>
    /// <param name="key">The key.</param>
    /// <param name="shift">Whether Shift is down.</param>
    /// <returns><see langword="true"/> when the key was used.</returns>
    public bool HandleKey(Key key, bool shift)
    {
        var focused = Keyboard.FocusedElement as DependencyObject;
        var combo = OpenComboBox(focused);
        switch (key)
        {
            case Key.Enter:
                if (focused is ButtonBase button)
                {
                    Invoke(button);
                    return true;
                }

                if (combo is not null)
                {
                    if (focused is ComboBoxItem item && item.IsEnabled)
                    {
                        combo.SelectedItem = combo.ItemContainerGenerator.ItemFromContainer(item);
                    }

                    combo.IsDropDownOpen = false;
                    combo.Focus();
                    return true;
                }

                if (ReferenceEquals(focused, Recent) && Recent.SelectedItem is not null)
                {
                    CommitRecent();
                    return true;
                }

                if (focused is ComboBox closed)
                {
                    closed.IsDropDownOpen = true;
                    return true;
                }

                _session.HandleEnter();
                return true;

            case Key.Escape:
                if (combo is not null)
                {
                    combo.IsDropDownOpen = false;
                    combo.Focus();
                    return true;
                }

                _session.HandleEscape();
                return true;

            case Key.Up:
            case Key.Down:
                var delta = key == Key.Up ? -1 : 1;
                if (ReferenceEquals(focused, Prompt))
                {
                    if (!_session.MoveSuggestion(delta))
                    {
                        Recall(delta);
                    }

                    return true;
                }

                if (combo is not null && focused is UIElement inList)
                {
                    inList.MoveFocus(new TraversalRequest(delta < 0 ? FocusNavigationDirection.Up : FocusNavigationDirection.Down));
                    return true;
                }

                if (focused is ComboBox picker)
                {
                    SelectAdjacent(picker, delta);
                    return true;
                }

                return false;

            case Key.Tab:
                if (focused is UIElement current)
                {
                    current.MoveFocus(new TraversalRequest(shift ? FocusNavigationDirection.Previous : FocusNavigationDirection.Next));
                }

                return true;

            default:
                return false;
        }
    }

    private static DependencyObject? ParentOf(DependencyObject element) =>
        (element is Visual or System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(element) : null)
        ?? LogicalTreeHelper.GetParent(element)
        ?? (element is FrameworkElement { Parent: null, TemplatedParent: { } templated } ? templated : null);

    /// <summary>Gets the combo box whose drop-down is open and holds (or is) the focused element, or <see langword="null"/>.</summary>
    private static ComboBox? OpenComboBox(DependencyObject? focused)
    {
        for (var current = focused; current is not null; current = ParentOf(current))
        {
            if (current is ComboBox { IsDropDownOpen: true } combo)
            {
                return combo;
            }

            if (current is ComboBoxItem item && ItemsControl.ItemsControlFromItemContainer(item) is ComboBox { IsDropDownOpen: true } owner)
            {
                return owner;
            }
        }

        return null;
    }

    private static void Invoke(ButtonBase button)
    {
        if (!button.IsEnabled)
        {
            return;
        }

        if (UIElementAutomationPeer.CreatePeerForElement(button)?.GetPattern(PatternInterface.Invoke) is IInvokeProvider invoke)
        {
            invoke.Invoke();
        }
    }

    /// <summary>Selects the next or previous enabled item of a closed combo box.</summary>
    private static void SelectAdjacent(ComboBox combo, int delta)
    {
        for (var i = combo.SelectedIndex + delta; i >= 0 && i < combo.Items.Count; i += delta)
        {
            if (combo.ItemContainerGenerator.ContainerFromIndex(i) is not UIElement { IsEnabled: false })
            {
                combo.SelectedIndex = i;
                return;
            }
        }
    }

    /// <summary>The WPF path: keys that reached WPF. Buttons and combo boxes keep their own Enter, Space and arrows.</summary>
    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        var focused = Keyboard.FocusedElement as DependencyObject;
        var shift = (Keyboard.Modifiers & ModifierKeys.Shift) != 0;
        switch (e.Key)
        {
            case Key.Enter when focused is ButtonBase || OpenComboBox(focused) is not null:
                return;
            case Key.Enter when ReferenceEquals(focused, Recent) && Recent.SelectedItem is null:
                return;
            case Key.Enter:
            case Key.Escape when OpenComboBox(focused) is null:
            case Key.Up or Key.Down when ReferenceEquals(focused, Prompt):
                e.Handled = HandleKey(e.Key, shift);
                return;
        }
    }

    private void Recall(int delta)
    {
        var prompts = _session.RecentPrompts;
        if (prompts.Count == 0)
        {
            return;
        }

        // Up goes back in time (older prompts), Down forward, past the newest to an empty prompt.
        _recall = Math.Max(-1, Math.Min(prompts.Count - 1, _recall + (delta < 0 ? 1 : -1)));
        _recalling = true;
        try
        {
            _session.PromptText = _recall < 0 ? string.Empty : prompts[_recall];
            Prompt.CaretIndex = Prompt.Text.Length;
        }
        finally
        {
            _recalling = false;
        }
    }

    private void OnRecentSelected(object sender, SelectionChangedEventArgs e)
    {
        // Mouse users pick from the open list; keyboard users choose with Up and Down and press Enter.
        if (!_committingRecent && !Recent.IsDropDownOpen && Recent.IsMouseOver && Recent.SelectedItem is not null)
        {
            CommitRecent();
        }
    }

    private void CommitRecent()
    {
        if (_committingRecent || Recent.SelectedItem is not string prompt)
        {
            return;
        }

        _committingRecent = true;
        try
        {
            _session.UseRecentPrompt(prompt);
            Recent.SelectedIndex = -1;
        }
        finally
        {
            _committingRecent = false;
        }

        FocusPrompt();
    }

    private void OnSuggestionClicked(object sender, MouseButtonEventArgs e)
    {
        if ((e.OriginalSource as DependencyObject) is { } source
            && ItemsControl.ContainerFromElement(Suggestions, source) is ListBoxItem { DataContext: SlashCommandInfo suggestion })
        {
            _session.AcceptSuggestion(suggestion);
            FocusPrompt();
            e.Handled = true;
        }
    }
}

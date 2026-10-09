using System;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Pillion.VisualStudio.Core;
using Pillion.VisualStudio.Core.Markdown;

namespace Pillion.VisualStudio.ToolWindows;

/// <summary>
/// The resource keys the Markdown renderer themes its elements with. In Visual Studio they are VsBrushes and
/// EnvironmentColors keys, so replies follow the light, dark, blue and high-contrast themes; tests pass their own keys.
/// </summary>
/// <remarks>This file has no Visual Studio dependency: it is also compiled into Pillion.VisualStudio.Tests.</remarks>
internal sealed class MarkdownTheme
{
    /// <summary>Gets or sets the key of the body text brush.</summary>
    public object? TextBrushKey { get; set; }

    /// <summary>Gets or sets the key of secondary text (code block language, notes).</summary>
    public object? SecondaryTextBrushKey { get; set; }

    /// <summary>Gets or sets the key of link text.</summary>
    public object? LinkBrushKey { get; set; }

    /// <summary>Gets or sets the key of borders (code blocks, tables, rules).</summary>
    public object? BorderBrushKey { get; set; }

    /// <summary>Gets or sets the key of the background behind inline code and fallback code blocks.</summary>
    public object? CodeBackgroundBrushKey { get; set; }

    /// <summary>Gets or sets the key of inline code and fallback code text.</summary>
    public object? CodeTextBrushKey { get; set; }

    /// <summary>Gets or sets the key of the block quote bar.</summary>
    public object? QuoteBarBrushKey { get; set; }

    /// <summary>Gets or sets the monospace font for inline code and the fallback code view.</summary>
    public FontFamily CodeFontFamily { get; set; } = new("Cascadia Mono, Consolas, Courier New");
}

/// <summary>Shows the code of one fenced block. In Visual Studio this is a read-only editor view; elsewhere a text box.</summary>
internal interface ICodeView : IDisposable
{
    /// <summary>Gets the element that shows the code.</summary>
    FrameworkElement Element { get; }

    /// <summary>Shows the block's code (the block grew while the reply streamed, or closed).</summary>
    /// <param name="block">The block; its language is the one the view was created for.</param>
    void Update(MarkdownCodeBlock block);
}

/// <summary>Creates the views that show fenced code blocks.</summary>
internal interface ICodeViewFactory
{
    /// <summary>Creates a view for a block.</summary>
    /// <param name="block">The block.</param>
    /// <returns>The view; the caller disposes it.</returns>
    ICodeView Create(MarkdownCodeBlock block);
}

/// <summary>What the renderer needs from its host: theme keys, code views and the actions behind links and code buttons.</summary>
internal sealed class MarkdownRenderContext
{
    /// <summary>Gets or sets the theme keys.</summary>
    public MarkdownTheme Theme { get; set; } = new();

    private ICodeViewFactory? _codeViews;

    /// <summary>Gets or sets the factory for code block views (the monospace fallback, themed by <see cref="Theme"/>, by default).</summary>
    public ICodeViewFactory CodeViews
    {
        get => _codeViews ??= new PlainCodeViewFactory(Theme);
        set => _codeViews = value;
    }

    /// <summary>
    /// Gets or sets what clicking a safe link does: the host asks for confirmation showing the address, then opens it.
    /// <see langword="null"/> leaves links inert.
    /// </summary>
    public Action<Uri>? OpenLink { get; set; }

    /// <summary>
    /// Gets or sets Insert at cursor: inserts the code into the active document and returns the note to show (for
    /// example "Inserted into Program.cs."). <see langword="null"/> hides the button.
    /// </summary>
    public Func<string, string>? InsertAtCaret { get; set; }

    /// <summary>Gets or sets Copy: puts the code on the clipboard and returns the note to show.</summary>
    public Func<string, string> CopyCode { get; set; } = ClipboardCopy;

    /// <summary>Gets or sets the body font size; headings scale from it (the environment font size in Visual Studio).</summary>
    public Func<double> BaseFontSize { get; set; } = () => SystemFonts.MessageFontSize;

    /// <summary>Copies text to the clipboard, retrying briefly while another process holds it open.</summary>
    /// <param name="text">The text.</param>
    /// <returns>The note to show.</returns>
    public static string ClipboardCopy(string text)
    {
        for (var attempt = 0; attempt < 5; attempt++)
        {
            try
            {
                Clipboard.SetDataObject(text ?? string.Empty, copy: true);
                return ChatStrings.CodeCopied;
            }
            catch (ExternalException) when (attempt < 4)
            {
                Thread.Sleep(20);
            }
            catch (ExternalException)
            {
                break;
            }
        }

        return ChatStrings.CopyFailed;
    }
}

/// <summary>The fallback code view: a read-only monospace text box (used when the editor factory is unavailable, and in tests).</summary>
internal sealed class PlainCodeViewFactory : ICodeViewFactory
{
    private readonly MarkdownTheme _theme;

    /// <summary>Initializes a new instance of the <see cref="PlainCodeViewFactory"/> class.</summary>
    /// <param name="theme">The theme keys for the text box.</param>
    public PlainCodeViewFactory(MarkdownTheme theme)
    {
        _theme = theme ?? new MarkdownTheme();
    }

    /// <inheritdoc/>
    public ICodeView Create(MarkdownCodeBlock block) => new PlainCodeView(block, _theme);

    private sealed class PlainCodeView : ICodeView
    {
        private readonly TextBox _box;

        public PlainCodeView(MarkdownCodeBlock block, MarkdownTheme theme)
        {
            _box = new TextBox
            {
                IsReadOnly = true,
                IsReadOnlyCaretVisible = true,
                TextWrapping = TextWrapping.NoWrap,
                AcceptsReturn = true,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
                VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
                BorderThickness = new Thickness(0),
                Padding = new Thickness(6, 4, 6, 4),
                FontFamily = theme.CodeFontFamily,
                Text = block.Code,
            };
            SetBrush(_box, Control.BackgroundProperty, theme.CodeBackgroundBrushKey);
            SetBrush(_box, Control.ForegroundProperty, theme.CodeTextBrushKey);
            AutomationPropertiesHelper.SetName(_box, ChatStrings.Format(ChatStrings.CodeBlockNameFormat, block.Language.NameFor(block.Info)));
        }

        public FrameworkElement Element => _box;

        public void Update(MarkdownCodeBlock block)
        {
            if (!string.Equals(_box.Text, block.Code, StringComparison.Ordinal))
            {
                _box.Text = block.Code;
            }
        }

        public void Dispose()
        {
        }

        private static void SetBrush(FrameworkElement element, DependencyProperty property, object? key)
        {
            if (key is not null)
            {
                element.SetResourceReference(property, key);
            }
        }
    }
}

/// <summary>Small helpers shared by the renderer's elements.</summary>
internal static class AutomationPropertiesHelper
{
    /// <summary>Sets an element's screen-reader name.</summary>
    /// <param name="element">The element.</param>
    /// <param name="name">The name.</param>
    public static void SetName(DependencyObject element, string name) =>
        System.Windows.Automation.AutomationProperties.SetName(element, name ?? string.Empty);

    /// <summary>
    /// Passes mouse wheel turns over a nested scrolling element (a code view) on to the transcript, so a code block never
    /// traps the wheel while the reader scrolls past it.
    /// </summary>
    /// <param name="element">The nested element.</param>
    public static void ForwardMouseWheel(UIElement element)
    {
        element.PreviewMouseWheel += (sender, e) =>
        {
            if (e.Handled || sender is not DependencyObject source || VisualTreeHelper.GetParent(source) is not UIElement parent)
            {
                return;
            }

            e.Handled = true;
            parent.RaiseEvent(new MouseWheelEventArgs(e.MouseDevice, e.Timestamp, e.Delta) { RoutedEvent = UIElement.MouseWheelEvent, Source = sender });
        };
    }
}

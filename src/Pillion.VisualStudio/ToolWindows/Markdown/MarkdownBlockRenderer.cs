using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using Pillion.VisualStudio.Core;
using Pillion.VisualStudio.Core.Markdown;

namespace Pillion.VisualStudio.ToolWindows;

/// <summary>
/// Renders a list of Markdown blocks into a panel and keeps it up to date incrementally: on each update it applies a
/// <see cref="MarkdownRenderPlan"/>, so the elements of unchanged blocks are kept, the first changed block is updated in
/// place when it can be (a growing paragraph, code block or list), and only what follows is created. Containers (lists,
/// list items, quotes) render their children with a nested renderer, so a growing list re-renders only its last item.
/// </summary>
/// <remarks>UI thread only. This file has no Visual Studio dependency: it is also compiled into Pillion.VisualStudio.Tests.</remarks>
internal sealed class MarkdownBlockRenderer : IDisposable
{
    private readonly Panel _panel;
    private readonly MarkdownRenderContext _context;
    private readonly List<MarkdownBlock> _blocks = [];
    private readonly List<RenderedBlock> _rendered = [];

    /// <summary>Initializes a new instance of the <see cref="MarkdownBlockRenderer"/> class.</summary>
    /// <param name="panel">The panel the block elements are added to (the renderer owns its children).</param>
    /// <param name="context">The theme and services.</param>
    public MarkdownBlockRenderer(Panel panel, MarkdownRenderContext context)
    {
        _panel = panel ?? throw new ArgumentNullException(nameof(panel));
        _context = context ?? throw new ArgumentNullException(nameof(context));
    }

    /// <summary>Gets the blocks shown.</summary>
    public IReadOnlyList<MarkdownBlock> Blocks => _blocks;

    /// <summary>Gets the element shown for each block, in order (for tests).</summary>
    public IReadOnlyList<FrameworkElement> Elements
    {
        get
        {
            var elements = new List<FrameworkElement>(_rendered.Count);
            foreach (var rendered in _rendered)
            {
                elements.Add(rendered.Element);
            }

            return elements;
        }
    }

    /// <summary>Shows new blocks, re-rendering only what changed.</summary>
    /// <param name="blocks">The blocks to show.</param>
    /// <param name="definitionsChanged">Whether the message's link reference definitions changed (then everything is re-rendered).</param>
    /// <returns>The plan that was applied.</returns>
    public MarkdownRenderPlan Update(IReadOnlyList<MarkdownBlock> blocks, bool definitionsChanged = false)
    {
        var plan = MarkdownRenderPlan.Compare(_blocks, blocks, definitionsChanged);
        if (plan.IsUnchanged)
        {
            return plan;
        }

        var keep = plan.FirstChanged + (plan.UpdateFirstInPlace ? 1 : 0);
        for (var i = _rendered.Count - 1; i >= keep; i--)
        {
            _rendered[i].Dispose();
            _rendered.RemoveAt(i);
            _blocks.RemoveAt(i);
            _panel.Children.RemoveAt(i);
        }

        if (plan.UpdateFirstInPlace)
        {
            _rendered[plan.FirstChanged].Update(blocks[plan.FirstChanged], definitionsChanged);
            _blocks[plan.FirstChanged] = blocks[plan.FirstChanged];
        }

        for (var i = keep; i < blocks.Count; i++)
        {
            var rendered = RenderedBlock.Create(blocks[i], _context);
            _rendered.Add(rendered);
            _blocks.Add(blocks[i]);
            _panel.Children.Add(rendered.Element);
        }

        return plan;
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        foreach (var rendered in _rendered)
        {
            rendered.Dispose();
        }

        _rendered.Clear();
        _blocks.Clear();
        _panel.Children.Clear();
    }
}

/// <summary>The element rendered for one block, which can update itself when <see cref="MarkdownBlock.CanUpdateTo"/> allows.</summary>
internal abstract class RenderedBlock : IDisposable
{
    /// <summary>Gets the element.</summary>
    public abstract FrameworkElement Element { get; }

    /// <summary>Creates the element for a block.</summary>
    /// <param name="block">The block.</param>
    /// <param name="context">The theme and services.</param>
    /// <returns>The rendered block.</returns>
    public static RenderedBlock Create(MarkdownBlock block, MarkdownRenderContext context) => block switch
    {
        MarkdownParagraph or MarkdownHeading => new ProseBlock(block, context),
        MarkdownCodeBlock code => new CodeBlockElement(code, context),
        MarkdownList or MarkdownListItem or MarkdownQuote => new ContainerBlock(block, context),
        MarkdownTable table => new TableBlock(table, context),
        _ => new RuleBlock(context),
    };

    /// <summary>Shows a block the current one can update to.</summary>
    /// <param name="block">The new block.</param>
    /// <param name="definitionsChanged">Whether link reference definitions changed.</param>
    public abstract void Update(MarkdownBlock block, bool definitionsChanged);

    /// <inheritdoc/>
    public virtual void Dispose()
    {
    }

    /// <summary>Sets a brush from a theme key, when there is one.</summary>
    /// <param name="element">The element.</param>
    /// <param name="property">The brush property.</param>
    /// <param name="key">The resource key.</param>
    protected static void SetBrush(FrameworkElement element, DependencyProperty property, object? key)
    {
        if (key is not null)
        {
            element.SetResourceReference(property, key);
        }
    }
}

/// <summary>A paragraph or heading: selectable rich text in a read-only <see cref="RichTextBox"/>.</summary>
internal sealed class ProseBlock : RenderedBlock
{
    private static readonly double[] HeadingScale = [1.6, 1.4, 1.2, 1.1, 1.0, 1.0];

    private readonly RichTextBox _box;
    private readonly Paragraph _paragraph;
    private readonly MarkdownRenderContext _context;

    /// <summary>Initializes a new instance of the <see cref="ProseBlock"/> class.</summary>
    /// <param name="block">A paragraph or heading.</param>
    /// <param name="context">The theme and services.</param>
    public ProseBlock(MarkdownBlock block, MarkdownRenderContext context)
    {
        _context = context;
        _paragraph = new Paragraph { Margin = new Thickness(0) };
        _box = CreateRichText(new FlowDocument(_paragraph), context);
        _box.Margin = new Thickness(0, 2, 0, 4);
        Fill(block);
    }

    /// <inheritdoc/>
    public override FrameworkElement Element => _box;

    /// <summary>Creates the read-only, selectable, borderless rich text box every prose element uses.</summary>
    /// <param name="document">The document.</param>
    /// <param name="context">The theme and services.</param>
    /// <returns>The box.</returns>
    public static RichTextBox CreateRichText(FlowDocument document, MarkdownRenderContext context)
    {
        document.PagePadding = new Thickness(0);
        var box = new RichTextBox(document)
        {
            IsReadOnly = true,
            IsReadOnlyCaretVisible = true, // Keyboard users can move into a link and press Enter.
            IsDocumentEnabled = true, // Hyperlinks are clickable.
            AcceptsTab = false,
            IsUndoEnabled = false,
            BorderThickness = new Thickness(0),
            Padding = new Thickness(0),
            Background = Brushes.Transparent,
            VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
        };
        SetBrush(box, Control.ForegroundProperty, context.Theme.TextBrushKey);
        box.PreviewKeyDown += OnPreviewKeyDown;
        return box;
    }

    /// <inheritdoc/>
    public override void Update(MarkdownBlock block, bool definitionsChanged) => Fill(block);

    /// <summary>Enter on a link opens it (after the host's confirmation), so links work from the keyboard.</summary>
    private static void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || sender is not RichTextBox box)
        {
            return;
        }

        for (var element = box.CaretPosition.Parent as DependencyObject; element is not null; element = (element as TextElement)?.Parent)
        {
            if (element is Hyperlink link)
            {
                e.Handled = true;
                link.RaiseEvent(new RoutedEventArgs(Hyperlink.ClickEvent, link));
                return;
            }
        }
    }

    private void Fill(MarkdownBlock block)
    {
        _paragraph.Inlines.Clear();
        if (block is MarkdownHeading heading)
        {
            _box.FontSize = _context.BaseFontSize() * HeadingScale[heading.Level - 1];
            _box.FontWeight = heading.Level <= 4 ? FontWeights.SemiBold : FontWeights.Bold;
            _box.Margin = new Thickness(0, heading.Level <= 2 ? 8 : 6, 0, 4);
            InlineBuilder.Add(_paragraph.Inlines, heading.Inlines, _context);
            AutomationPropertiesHelper.SetName(_box, ChatStrings.Format(ChatStrings.HeadingNameFormat, heading.Level, heading.PlainText));
        }
        else if (block is MarkdownParagraph paragraph)
        {
            InlineBuilder.Add(_paragraph.Inlines, paragraph.Inlines, _context);
            AutomationPropertiesHelper.SetName(_box, paragraph.PlainText);
        }
    }
}

/// <summary>A list, list item or quote: its children rendered by a nested <see cref="MarkdownBlockRenderer"/>.</summary>
internal sealed class ContainerBlock : RenderedBlock
{
    private readonly FrameworkElement _element;
    private readonly MarkdownBlockRenderer _children;

    /// <summary>Initializes a new instance of the <see cref="ContainerBlock"/> class.</summary>
    /// <param name="block">A list, list item or quote.</param>
    /// <param name="context">The theme and services.</param>
    public ContainerBlock(MarkdownBlock block, MarkdownRenderContext context)
    {
        var panel = new StackPanel();
        switch (block)
        {
            case MarkdownListItem item:
                // A marker column and the item's blocks beside it.
                var marker = new TextBlock
                {
                    Text = item.Marker,
                    Margin = new Thickness(0, 2, 6, 0),
                    MinWidth = 14,
                    TextAlignment = TextAlignment.Right,
                };
                var grid = new Grid();
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                grid.Children.Add(marker);
                Grid.SetColumn(panel, 1);
                grid.Children.Add(panel);
                AutomationPropertiesHelper.SetName(grid, item.Marker);
                _element = grid;
                break;
            case MarkdownQuote:
                var bar = new Border
                {
                    BorderThickness = new Thickness(3, 0, 0, 0),
                    Padding = new Thickness(8, 0, 0, 0),
                    Margin = new Thickness(0, 2, 0, 4),
                    Child = panel,
                };
                SetBrush(bar, Border.BorderBrushProperty, context.Theme.QuoteBarBrushKey ?? context.Theme.BorderBrushKey);
                AutomationPropertiesHelper.SetName(bar, ChatStrings.QuoteName);
                _element = bar;
                break;
            default:
                panel.Margin = new Thickness(4, 0, 0, 4);
                _element = panel;
                break;
        }

        _children = new MarkdownBlockRenderer(panel, context);
        Update(block, definitionsChanged: false);
    }

    /// <inheritdoc/>
    public override FrameworkElement Element => _element;

    /// <inheritdoc/>
    public override void Update(MarkdownBlock block, bool definitionsChanged)
    {
        _children.Update(block.Children, definitionsChanged);
        if (block is MarkdownList list)
        {
            AutomationPropertiesHelper.SetName(_element, ChatStrings.Format(ChatStrings.ListNameFormat, list.Items.Count));
        }
    }

    /// <inheritdoc/>
    public override void Dispose()
    {
        _children.Dispose();
        base.Dispose();
    }
}

/// <summary>A fenced code block: a header with the language, Copy and Insert at cursor, above a code view.</summary>
internal sealed class CodeBlockElement : RenderedBlock
{
    private readonly MarkdownRenderContext _context;
    private readonly ICodeView _view;
    private readonly Border _border;
    private readonly TextBlock _note;
    private string _code;

    /// <summary>Initializes a new instance of the <see cref="CodeBlockElement"/> class.</summary>
    /// <param name="block">The code block.</param>
    /// <param name="context">The theme and services.</param>
    public CodeBlockElement(MarkdownCodeBlock block, MarkdownRenderContext context)
    {
        _context = context;
        _code = block.Code;
        var language = block.Language.NameFor(block.Info);
        var name = ChatStrings.Format(ChatStrings.CodeBlockNameFormat, language);

        var label = new TextBlock { Text = language, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(6, 0, 6, 0) };
        SetBrush(label, TextBlock.ForegroundProperty, context.Theme.SecondaryTextBrushKey);
        _note = new TextBlock { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(6, 0, 6, 0), TextTrimming = TextTrimming.CharacterEllipsis };
        SetBrush(_note, TextBlock.ForegroundProperty, context.Theme.SecondaryTextBrushKey);
        AutomationProperties.SetLiveSetting(_note, AutomationLiveSetting.Polite);

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        CopyButton = CreateButton(ChatStrings.CopyCodeButton, ChatStrings.Format(ChatStrings.CopyCodeNameFormat, language), ChatStrings.CopyCodeToolTip, OnCopy);
        buttons.Children.Add(CopyButton);
        if (context.InsertAtCaret is not null)
        {
            InsertButton = CreateButton(ChatStrings.InsertCodeButton, ChatStrings.Format(ChatStrings.InsertCodeNameFormat, language), ChatStrings.InsertCodeToolTip, OnInsert);
            buttons.Children.Add(InsertButton);
        }

        var header = new DockPanel { LastChildFill = true, Margin = new Thickness(0, 2, 2, 2) };
        DockPanel.SetDock(buttons, Dock.Right);
        header.Children.Add(buttons);
        DockPanel.SetDock(label, Dock.Left);
        header.Children.Add(label);
        header.Children.Add(_note);

        _view = context.CodeViews.Create(block);
        AutomationPropertiesHelper.ForwardMouseWheel(_view.Element);
        _view.Element.ToolTip = name;

        var headerBorder = new Border { BorderThickness = new Thickness(0, 0, 0, 1), Child = header };
        SetBrush(headerBorder, Border.BorderBrushProperty, context.Theme.BorderBrushKey);
        var layout = new DockPanel { LastChildFill = true };
        DockPanel.SetDock(headerBorder, Dock.Top);
        layout.Children.Add(headerBorder);
        layout.Children.Add(_view.Element);

        _border = new Border { BorderThickness = new Thickness(1), Margin = new Thickness(0, 4, 0, 6), Child = layout, ToolTip = name };
        SetBrush(_border, Border.BorderBrushProperty, context.Theme.BorderBrushKey);
        AutomationPropertiesHelper.SetName(_border, name);
        ToolTipService.SetShowOnDisabled(_border, true);
    }

    /// <inheritdoc/>
    public override FrameworkElement Element => _border;

    /// <summary>Gets the Copy button.</summary>
    public Button CopyButton { get; }

    /// <summary>Gets the Insert at cursor button, when the host supports it.</summary>
    public Button? InsertButton { get; }

    /// <summary>Gets the code view.</summary>
    public ICodeView View => _view;

    /// <summary>Gets the note beside the buttons ("Copied.", "Inserted into Program.cs.").</summary>
    public string Note => _note.Text;

    /// <inheritdoc/>
    public override void Update(MarkdownBlock block, bool definitionsChanged)
    {
        var code = (MarkdownCodeBlock)block;
        _code = code.Code;
        _view.Update(code);
    }

    /// <inheritdoc/>
    public override void Dispose()
    {
        _view.Dispose();
        base.Dispose();
    }

    private static Button CreateButton(string text, string name, string toolTip, RoutedEventHandler click)
    {
        var button = new Button
        {
            Content = text,
            Margin = new Thickness(4, 0, 0, 0),
            Padding = new Thickness(6, 1, 6, 1),
            MinWidth = 0,
            ToolTip = toolTip,
        };
        AutomationPropertiesHelper.SetName(button, name);
        button.Click += click;
        return button;
    }

    private void OnCopy(object sender, RoutedEventArgs e) => _note.Text = _context.CopyCode(_code);

    private void OnInsert(object sender, RoutedEventArgs e) => _note.Text = _context.InsertAtCaret?.Invoke(_code) ?? string.Empty;
}

/// <summary>A pipe table: a grid with auto-sized columns and a bold header row. Cells are text blocks, which size to their content.</summary>
internal sealed class TableBlock : RenderedBlock
{
    private readonly Border _border;

    /// <summary>Initializes a new instance of the <see cref="TableBlock"/> class.</summary>
    /// <param name="table">The table.</param>
    /// <param name="context">The theme and services.</param>
    public TableBlock(MarkdownTable table, MarkdownRenderContext context)
    {
        var grid = new Grid();
        for (var c = 0; c < table.ColumnCount; c++)
        {
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        }

        for (var r = 0; r < table.Rows.Count; r++)
        {
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var row = table.Rows[r];
            for (var c = 0; c < table.ColumnCount; c++)
            {
                IReadOnlyList<MarkdownInline> inlines = c < row.Count ? row[c] : [];
                var text = new TextBlock
                {
                    TextAlignment = AlignmentOf(table.AlignmentOf(c)),
                    FontWeight = r < table.HeaderRows ? FontWeights.SemiBold : FontWeights.Normal,
                };
                InlineBuilder.Add(text.Inlines, inlines, context);
                AutomationPropertiesHelper.SetName(text, MarkdownInline.ToPlainText(inlines));
                var cell = new Border
                {
                    BorderThickness = new Thickness(c == 0 ? 1 : 0, r == 0 ? 1 : 0, 1, 1),
                    Padding = new Thickness(6, 2, 6, 2),
                    Child = text,
                };
                SetBrush(cell, Border.BorderBrushProperty, context.Theme.BorderBrushKey);
                Grid.SetRow(cell, r);
                Grid.SetColumn(cell, c);
                grid.Children.Add(cell);
            }
        }

        // Wide tables scroll sideways rather than squeezing every column.
        var scroller = new ScrollViewer
        {
            Content = grid,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Focusable = false,
        };
        AutomationPropertiesHelper.ForwardMouseWheel(scroller);
        _border = new Border { Margin = new Thickness(0, 4, 0, 6), Child = scroller, HorizontalAlignment = HorizontalAlignment.Left };
        AutomationPropertiesHelper.SetName(_border, ChatStrings.Format(ChatStrings.TableNameFormat, table.Rows.Count, table.ColumnCount));
    }

    /// <inheritdoc/>
    public override FrameworkElement Element => _border;

    /// <inheritdoc/>
    public override void Update(MarkdownBlock block, bool definitionsChanged) =>
        throw new InvalidOperationException("A table is replaced, never updated in place.");

    private static TextAlignment AlignmentOf(MarkdownColumnAlignment alignment) => alignment switch
    {
        MarkdownColumnAlignment.Center => TextAlignment.Center,
        MarkdownColumnAlignment.Right => TextAlignment.Right,
        _ => TextAlignment.Left,
    };
}

/// <summary>A thematic break: a one-pixel line.</summary>
internal sealed class RuleBlock : RenderedBlock
{
    private readonly Border _line;

    /// <summary>Initializes a new instance of the <see cref="RuleBlock"/> class.</summary>
    /// <param name="context">The theme and services.</param>
    public RuleBlock(MarkdownRenderContext context)
    {
        _line = new Border { Height = 1, BorderThickness = new Thickness(0, 1, 0, 0), Margin = new Thickness(0, 6, 0, 6) };
        SetBrush(_line, Border.BorderBrushProperty, context.Theme.BorderBrushKey);
        AutomationPropertiesHelper.SetName(_line, ChatStrings.RuleName);
    }

    /// <inheritdoc/>
    public override FrameworkElement Element => _line;

    /// <inheritdoc/>
    public override void Update(MarkdownBlock block, bool definitionsChanged)
    {
    }
}

/// <summary>Turns <see cref="MarkdownInline"/>s into WPF inlines.</summary>
internal static class InlineBuilder
{
    /// <summary>Adds inlines to a collection.</summary>
    /// <param name="target">The paragraph's or span's inlines.</param>
    /// <param name="inlines">The Markdown inlines.</param>
    /// <param name="context">The theme and services.</param>
    public static void Add(InlineCollection target, IReadOnlyList<MarkdownInline> inlines, MarkdownRenderContext context)
    {
        foreach (var inline in inlines)
        {
            target.Add(Create(inline, context));
        }
    }

    private static Inline Create(MarkdownInline inline, MarkdownRenderContext context)
    {
        switch (inline.Kind)
        {
            case MarkdownInlineKind.Code:
                var code = new Run(inline.Text) { FontFamily = context.Theme.CodeFontFamily };
                SetBrush(code, TextElement.BackgroundProperty, context.Theme.CodeBackgroundBrushKey);
                SetBrush(code, TextElement.ForegroundProperty, context.Theme.CodeTextBrushKey);
                return code;
            case MarkdownInlineKind.Emphasis:
                return Fill(new Italic(), inline, context);
            case MarkdownInlineKind.Strong:
                return Fill(new Bold(), inline, context);
            case MarkdownInlineKind.Strikethrough:
                return Fill(new Span { TextDecorations = TextDecorations.Strikethrough }, inline, context);
            case MarkdownInlineKind.Link:
                return CreateLink(inline, context);
            case MarkdownInlineKind.Image:
                // Never fetched: the alt text stands in for the image.
                var alt = inline.Text.Trim().Length == 0 ? ChatStrings.ImageWithoutAlt : ChatStrings.Format(ChatStrings.ImageAltFormat, inline.Text);
                var image = new Run(alt) { FontStyle = FontStyles.Italic, ToolTip = ChatStrings.Format(ChatStrings.ImageToolTipFormat, inline.Url ?? string.Empty) };
                SetBrush(image, TextElement.ForegroundProperty, context.Theme.SecondaryTextBrushKey);
                return image;
            case MarkdownInlineKind.LineBreak:
                return inline.IsHardBreak ? new LineBreak() : new Run(" ");
            default:
                return new Run(inline.Text);
        }
    }

    private static Inline CreateLink(MarkdownInline inline, MarkdownRenderContext context)
    {
        if (inline.SafeUri is not { } uri || context.OpenLink is null)
        {
            // Not openable: the text, with the address it would have gone to in the tooltip.
            var inert = Fill(new Span(), inline, context);
            inert.ToolTip = ChatStrings.Format(ChatStrings.UnsafeLinkToolTipFormat, inline.Url ?? string.Empty);
            return inert;
        }

        var link = (Hyperlink)Fill(new Hyperlink(), inline, context);
        link.ToolTip = ChatStrings.Format(ChatStrings.LinkToolTipFormat, uri.AbsoluteUri);
        SetBrush(link, TextElement.ForegroundProperty, context.Theme.LinkBrushKey);
        AutomationProperties.SetName(link, MarkdownInline.ToPlainText(inline.Children));
        AutomationProperties.SetHelpText(link, uri.AbsoluteUri);
        var open = context.OpenLink;
        link.Click += (_, _) => open(uri);
        return link;
    }

    private static Span Fill(Span span, MarkdownInline inline, MarkdownRenderContext context)
    {
        Add(span.Inlines, inline.Children, context);
        return span;
    }

    private static void SetBrush(FrameworkContentElement element, DependencyProperty property, object? key)
    {
        if (key is not null)
        {
            element.SetResourceReference(property, key);
        }
    }
}

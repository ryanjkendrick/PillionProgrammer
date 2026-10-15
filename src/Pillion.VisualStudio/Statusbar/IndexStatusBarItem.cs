using System;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using Microsoft.VisualStudio.PlatformUI;
using Microsoft.VisualStudio.Shell;
using Pillion.VisualStudio.Core;

namespace Pillion.VisualStudio.Statusbar;

/// <summary>
/// The status bar's indexing indicator as a clickable item beside the host state (work item 2.15). The text-only status bar
/// service (<c>IVsStatusbar.SetText</c>) cannot take a click, so the item is one small WPF button put into the status bar's
/// panel in Visual Studio's main window. That panel is not a public extension point: when it cannot be found (another
/// Visual Studio layout) the item reports <see cref="TryAttach"/> as <see langword="false"/> and the package falls back to
/// adding the indicator's text to the status bar text, without a click.
/// </summary>
/// <remarks>All members must be called on the UI thread. The item never throws: a failure to attach is logged and reported.</remarks>
internal sealed class IndexStatusBarItem
{
    /// <summary>The name of the status bar's panel in the main window's visual tree.</summary>
    private const string PanelName = "StatusBarPanel";

    private readonly Action _open;
    private readonly PillionLog? _log;
    private Button? _button;
    private TextBlock? _text;
    private bool _failed;

    /// <summary>Initializes a new instance of the <see cref="IndexStatusBarItem"/> class.</summary>
    /// <param name="open">What a click does: opens the Index window.</param>
    /// <param name="log">The logger, or <see langword="null"/>.</param>
    public IndexStatusBarItem(Action open, PillionLog? log)
    {
        _open = open ?? throw new ArgumentNullException(nameof(open));
        _log = log;
    }

    /// <summary>Gets a value indicating whether the item is in the status bar.</summary>
    public bool IsAttached => _button is { Parent: not null };

    /// <summary>Gets a value indicating whether finding the status bar's panel was tried and failed (the text fallback is used from then on).</summary>
    public bool HasFailed => _failed;

    /// <summary>Puts the item into the status bar's panel, once. Call only when there is something to show: the visual tree is not walked at start-up.</summary>
    /// <returns><see langword="true"/> when the item is in the status bar.</returns>
    public bool TryAttach()
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        if (IsAttached)
        {
            return true;
        }

        if (_failed)
        {
            return false;
        }

        try
        {
            var window = Application.Current?.MainWindow;
            if (window is not null && Find(window, PanelName) is DockPanel panel)
            {
                _text = new TextBlock { VerticalAlignment = VerticalAlignment.Center };
                _text.SetResourceReference(TextBlock.ForegroundProperty, EnvironmentColors.StatusBarDefaultTextBrushKey);
                _button = new Button
                {
                    Content = _text,
                    Padding = new Thickness(8, 0, 8, 0),
                    Margin = new Thickness(0),
                    BorderThickness = new Thickness(0),
                    Background = Brushes.Transparent,
                    Cursor = System.Windows.Input.Cursors.Hand,
                    Focusable = true,
                    Visibility = Visibility.Collapsed,
                    Template = CreateFlatTemplate(),
                };
                AutomationProperties.SetName(_button, IndexStrings.StatusBarOpenName);
                _button.Click += (_, _) => _open();
                DockPanel.SetDock(_button, Dock.Right);
                panel.Children.Insert(Math.Min(1, panel.Children.Count), _button);
                _log?.Info("index.statusbar.attached", "The indexing indicator is in the status bar.");
                return true;
            }
        }
#pragma warning disable CA1031 // The indicator is a convenience; any failure falls back to the status bar text.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            _log?.Warning("index.statusbar.attach.failed", "Could not put the indexing indicator into the status bar.", ("error", ex.GetType().Name));
        }

        _failed = true;
        _button = null;
        _text = null;
        _log?.Info("index.statusbar.fallback", "The status bar panel was not found: the indexing indicator is added to the status bar text.");
        return false;
    }

    /// <summary>Shows the indicator's text and tooltip, or hides the item when the text is empty. Does nothing when not attached.</summary>
    /// <param name="text">The indicator text, for example "↻ Indexing 42%".</param>
    /// <param name="toolTip">The sentence for the tooltip and screen readers.</param>
    public void Update(string text, string toolTip)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        if (_button is null || _text is null)
        {
            return;
        }

        _text.Text = text;
        _button.ToolTip = toolTip.Length == 0 ? null : toolTip;
        AutomationProperties.SetName(_button, toolTip.Length == 0 ? IndexStrings.StatusBarOpenName : toolTip);
        _button.Visibility = text.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
    }

    /// <remarks>
    /// Children are visited last first: the status bar is docked at the bottom of the main window, the last child at each level
    /// on the way down, so the walk reaches it before the menus, tool bars and document wells. Visiting them first cost 50 to
    /// 80 ms on the UI thread on a CI runner, inside the package's start-up budget (work item 2.17).
    /// </remarks>
    private static DependencyObject? Find(DependencyObject parent, string name)
    {
        var count = VisualTreeHelper.GetChildrenCount(parent);
        for (var i = count - 1; i >= 0; i--)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is FrameworkElement element && string.Equals(element.Name, name, StringComparison.Ordinal))
            {
                return child;
            }

            if (Find(child, name) is { } found)
            {
                return found;
            }
        }

        return null;
    }

    /// <summary>A button without chrome: just its content, with the status bar's text colour inherited from the panel, and a ring while it has keyboard focus.</summary>
    private static ControlTemplate CreateFlatTemplate()
    {
        const string ring = "FocusRing";
        var template = new ControlTemplate(typeof(Button));
        var border = new FrameworkElementFactory(typeof(Border), ring);
        border.SetValue(Border.BackgroundProperty, Brushes.Transparent);
        border.SetValue(Border.BorderThicknessProperty, new Thickness(1));
        border.SetValue(Border.BorderBrushProperty, Brushes.Transparent);
        border.SetValue(Border.PaddingProperty, new TemplateBindingExtension(Control.PaddingProperty));
        var content = new FrameworkElementFactory(typeof(ContentPresenter));
        content.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
        border.AppendChild(content);
        template.VisualTree = border;
        var focused = new Trigger { Property = UIElement.IsKeyboardFocusedProperty, Value = true };
        focused.Setters.Add(new Setter(Border.BorderBrushProperty, SystemColors.HighlightBrush, ring));
        template.Triggers.Add(focused);
        return template;
    }
}

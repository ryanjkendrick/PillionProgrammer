using System;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using Microsoft.VisualStudio.PlatformUI;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Threading;
using Pillion.VisualStudio.Core;

namespace Pillion.VisualStudio.Statusbar;

/// <summary>
/// The status bar's indexing indicator as a clickable item beside the host state (work item 2.15). The text-only status bar
/// service (<c>IVsStatusbar.SetText</c>) cannot take a click, so the item is one small link put into the status bar's panel in
/// Visual Studio's main window. That panel is not a public extension point: when it cannot be found (another Visual Studio
/// layout), or finding it takes longer than <see cref="SearchBudget"/>, the item is never attached and the package keeps
/// adding the indicator's words to the status bar text, without a click.
/// </summary>
/// <remarks>
/// <para>
/// Nothing here runs until there is something to show. Then <see cref="RequestAttach"/> queues the attach once, at the UI
/// thread's idle priority (Visual Studio's background-priority scheduler), so it runs after the start-up and the host's Ready,
/// when the UI thread has nothing more urgent to do; until it has run, the status bar text shows the indicator. The search for the panel is bounded
/// (<see cref="StatusBarPanelFinder"/>), and the item is a link in a text block, with no template to build. Its glyph is drawn
/// in Segoe UI Symbol: the glyphs are not in Segoe UI, and text that needs a fallback font makes WPF parse the system's
/// composite fonts on the UI thread the first time, which took over 3 s in the first layout pass after a host restart on a
/// CI runner.
/// </para>
/// <para>All members must be called on the UI thread. The item never throws: a failure to attach is logged and reported.</para>
/// </remarks>
internal sealed class IndexStatusBarItem
{
    /// <summary>The longest the search for the status bar's panel may take before the item gives up and the text is used.</summary>
    public static readonly TimeSpan SearchBudget = TimeSpan.FromMilliseconds(5);

    /// <summary>The name of the status bar's panel in the main window's visual tree.</summary>
    private const string PanelName = "StatusBarPanel";

    /// <summary>A font that has all three glyphs of <see cref="IndexFormat"/>; Segoe UI, the interface font, lacks ↻ and ⚠.</summary>
    private static readonly FontFamily GlyphFont = new("Segoe UI Symbol");

    private readonly JoinableTaskFactory _joinableTaskFactory;
    private readonly Action _open;
    private readonly Action _attached;
    private readonly PillionLog? _log;
    private TextBlock? _host;
    private Hyperlink? _link;
    private Run? _glyph;
    private Run? _label;
    private bool _requested;
    private bool _failed;

    /// <summary>Initializes a new instance of the <see cref="IndexStatusBarItem"/> class.</summary>
    /// <param name="joinableTaskFactory">The package's factory, which queues the attach on the UI thread at idle priority.</param>
    /// <param name="open">What a click does: opens the Index window.</param>
    /// <param name="attached">Called on the UI thread once the item is in the status bar, so the package can show the current state in it.</param>
    /// <param name="log">The logger, or <see langword="null"/>.</param>
    public IndexStatusBarItem(JoinableTaskFactory joinableTaskFactory, Action open, Action attached, PillionLog? log)
    {
        _joinableTaskFactory = joinableTaskFactory ?? throw new ArgumentNullException(nameof(joinableTaskFactory));
        _open = open ?? throw new ArgumentNullException(nameof(open));
        _attached = attached ?? throw new ArgumentNullException(nameof(attached));
        _log = log;
    }

    /// <summary>Gets a value indicating whether the item is in the status bar.</summary>
    public bool IsAttached => _host is { Parent: not null };

    /// <summary>Gets a value indicating whether attaching was tried and failed (the text fallback is used from then on).</summary>
    public bool HasFailed => _failed;

    /// <summary>
    /// Asks for the item to be put into the status bar. The first call queues the attach at the UI thread's idle priority and
    /// returns at once; later calls do nothing. Call only when there is something to show.
    /// </summary>
    public void RequestAttach()
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        if (_requested)
        {
            return;
        }

        _requested = true;
        _joinableTaskFactory.StartOnIdle(AttachWhenIdle).FileAndForget("pillion/statusbar/attach");
    }

    /// <summary>Shows the indicator's glyph, words and tooltip, or hides the item when there are no words. Does nothing when not attached.</summary>
    /// <param name="glyph">The glyph, for example "↻".</param>
    /// <param name="label">The words, for example "Indexing 42%".</param>
    /// <param name="toolTip">The sentence for the tooltip and screen readers.</param>
    public void Update(string glyph, string label, string toolTip)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        if (_host is null || _link is null || _glyph is null || _label is null)
        {
            return;
        }

        if (!string.Equals(_glyph.Text, glyph, StringComparison.Ordinal))
        {
            _glyph.Text = glyph;
        }

        if (!string.Equals(_label.Text, label, StringComparison.Ordinal))
        {
            _label.Text = label;
        }

        _host.ToolTip = toolTip.Length == 0 ? null : toolTip;
        AutomationProperties.SetName(_link, toolTip.Length == 0 ? IndexStrings.StatusBarOpenName : toolTip);
        _host.Visibility = label.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
    }

    private void AttachWhenIdle()
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        bool attached;
        using (UiThreadTimings.Measure("package.statusbar.attach"))
        {
            attached = TryAttach();
        }

        if (attached)
        {
            _attached();
        }
    }

    /// <summary>Puts the item into the status bar's panel, once.</summary>
    /// <returns><see langword="true"/> when the item is in the status bar.</returns>
    private bool TryAttach()
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

        var why = "the main window is not available";
        try
        {
            if (Application.Current?.MainWindow is { } window)
            {
                var search = StatusBarPanelFinder.Find(window, PanelName, SearchBudget);
                why = FormattableString.Invariant($"{search.How} after {search.Elapsed.TotalMilliseconds:0.0} ms and {search.Visited} elements");
                if (search.Element is DockPanel panel)
                {
                    Attach(panel);
                    _log?.Info("index.statusbar.attached", "The indexing indicator is in the status bar.", ("search", why));
                    return true;
                }
            }
        }
#pragma warning disable CA1031 // The indicator is a convenience; any failure falls back to the status bar text.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            why = ex.GetType().Name;
            _log?.Warning("index.statusbar.attach.failed", "Could not put the indexing indicator into the status bar.", ("error", ex.GetType().Name));
        }

        _failed = true;
        _host = null;
        _link = null;
        _glyph = null;
        _label = null;
        _log?.Info("index.statusbar.fallback", "The status bar panel was not found in time: the indexing indicator is added to the status bar text.", ("search", why));
        return false;
    }

    private void Attach(DockPanel panel)
    {
        _glyph = new Run { FontFamily = GlyphFont };
        _label = new Run();
        _link = new Hyperlink { TextDecorations = null, Focusable = true };
        _link.Inlines.Add(_glyph);
        _link.Inlines.Add(new Run(" "));
        _link.Inlines.Add(_label);
        _link.SetResourceReference(TextElement.ForegroundProperty, EnvironmentColors.StatusBarDefaultTextBrushKey);
        AutomationProperties.SetName(_link, IndexStrings.StatusBarOpenName);
        _link.Click += (_, _) => _open();
        _host = new TextBlock
        {
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(8, 0, 8, 0),
            Visibility = Visibility.Collapsed,
        };
        _host.Inlines.Add(_link);
        DockPanel.SetDock(_host, Dock.Right);
        panel.Children.Insert(Math.Min(1, panel.Children.Count), _host);
    }
}

using System;
using System.Runtime.ExceptionServices;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Pillion.VisualStudio.Statusbar;

namespace Pillion.VisualStudio.Tests;

/// <summary>
/// Finding the status bar's panel for the indexing indicator (work item 2.15) without walking the whole main window: on a CI
/// runner the whole walk cost 150 to 350 ms of UI thread time, inside the package's start-up budget and inside the host-restart
/// responsiveness window.
/// </summary>
public sealed class StatusBarPanelFinderTests
{
    private const string PanelName = "StatusBarPanel";

    /// <summary>Enough to stand for the tool windows and documents of a real main window.</summary>
    private const int DocumentElements = 20_000;

    [Fact]
    public void ThePanel_IsFoundFromTheBottomOfTheWindow_WithoutVisitingTheDocuments() => Sta.Run(() =>
    {
        var window = MainWindow(statusBarAtTheBottom: true, out var panel);

        var search = StatusBarPanelFinder.Find(window, PanelName, TimeSpan.FromSeconds(10));

        Assert.Same(panel, search.Element);
        Assert.True(search.How is "hit test" or "status bar subtree", search.How);
        Assert.True(search.Visited < 50, $"visited {search.Visited} elements ({search.How})");
        Assert.False(search.OverBudget);
    });

    [Fact]
    public void APanelElsewhere_IsFoundInTheWholeTree_WithinTheBudget() => Sta.Run(() =>
    {
        var window = MainWindow(statusBarAtTheBottom: false, out var panel);

        var search = StatusBarPanelFinder.Find(window, PanelName, TimeSpan.FromSeconds(10));

        Assert.Same(panel, search.Element);
        Assert.Equal("whole tree", search.How);
    });

    [Fact]
    public void TheSearch_GivesUpAtItsBudget_SoTheCallerFallsBackToText() => Sta.Run(() =>
    {
        var window = MainWindow(statusBarAtTheBottom: false, out _);

        var search = StatusBarPanelFinder.Find(window, PanelName, TimeSpan.Zero);

        Assert.Null(search.Element);
        Assert.True(search.OverBudget, search.How);
        Assert.True(search.Visited < DocumentElements, $"visited {search.Visited} elements");
    });

    [Fact]
    public void NoPanel_IsNotFound() => Sta.Run(() =>
    {
        var window = Arrange(new Grid { Children = { new TextBlock { Text = "no status bar" } } });

        var search = StatusBarPanelFinder.Find(window, PanelName, TimeSpan.FromSeconds(10));

        Assert.Null(search.Element);
        Assert.Equal("not found", search.How);
    });

    /// <summary>
    /// A stand-in for Visual Studio's main window: a dock panel with a status bar (a border holding the named panel) and a deep
    /// document area. With <paramref name="statusBarAtTheBottom"/> false the status bar is docked at the top, where the finder's
    /// hit tests do not look.
    /// </summary>
    private static FrameworkElement MainWindow(bool statusBarAtTheBottom, out DockPanel panel)
    {
        panel = new DockPanel { Name = PanelName, LastChildFill = false };
        panel.Children.Add(new TextBlock { Text = "Ready" });
        var statusBar = new Border { Height = 24, Background = Brushes.SteelBlue, Child = new Grid { Children = { panel } } };
        DockPanel.SetDock(statusBar, statusBarAtTheBottom ? Dock.Bottom : Dock.Top);

        var documents = new StackPanel { Background = Brushes.White };
        var parent = (Panel)documents;
        for (var i = 0; i < DocumentElements; i++)
        {
            var child = i % 100 == 0 ? new StackPanel() : (Panel)new Grid();
            child.Children.Add(new Border { Width = 1, Height = 1 });
            parent.Children.Add(child);
            parent = i % 100 == 99 ? documents : child;
        }

        var root = new DockPanel { LastChildFill = true, Background = Brushes.Gray };
        root.Children.Add(statusBar);
        root.Children.Add(new ScrollViewer { Content = documents });
        return Arrange(root);
    }

    private static FrameworkElement Arrange(FrameworkElement root)
    {
        root.Measure(new Size(1200, 800));
        root.Arrange(new Rect(0, 0, 1200, 800));
        root.UpdateLayout();
        return root;
    }

    private static class Sta
    {
        public static void Run(Action action)
        {
            ExceptionDispatchInfo? failure = null;
            var thread = new Thread(() =>
            {
                try
                {
                    action();
                }
#pragma warning disable CA1031 // Rethrown on the test thread.
                catch (Exception ex)
#pragma warning restore CA1031
                {
                    failure = ExceptionDispatchInfo.Capture(ex);
                }
            });
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
            thread.Join();
            failure?.Throw();
        }
    }
}

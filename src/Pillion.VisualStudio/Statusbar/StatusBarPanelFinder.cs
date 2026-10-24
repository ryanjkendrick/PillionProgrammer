using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Media;

namespace Pillion.VisualStudio.Statusbar;

/// <summary>What <see cref="StatusBarPanelFinder.Find"/> found, how, and what it cost.</summary>
internal sealed class StatusBarPanelSearch
{
    /// <summary>Initializes a new instance of the <see cref="StatusBarPanelSearch"/> class.</summary>
    /// <param name="element">The element found, or <see langword="null"/>.</param>
    /// <param name="how">How it was found, or why not: <c>hit test</c>, <c>status bar subtree</c>, <c>whole tree</c>, <c>not found</c> or <c>over budget</c>.</param>
    /// <param name="visited">How many visual tree nodes were looked at.</param>
    /// <param name="elapsed">How long the search took.</param>
    public StatusBarPanelSearch(FrameworkElement? element, string how, int visited, TimeSpan elapsed)
    {
        Element = element;
        How = how;
        Visited = visited;
        Elapsed = elapsed;
    }

    /// <summary>Gets the element found, or <see langword="null"/>.</summary>
    public FrameworkElement? Element { get; }

    /// <summary>Gets how the element was found, or why not.</summary>
    public string How { get; }

    /// <summary>Gets how many visual tree nodes were looked at.</summary>
    public int Visited { get; }

    /// <summary>Gets how long the search took.</summary>
    public TimeSpan Elapsed { get; }

    /// <summary>Gets a value indicating whether the search gave up at its time budget.</summary>
    public bool OverBudget => How == StatusBarPanelFinder.OverBudget;
}

/// <summary>
/// Finds a named element in the status bar of Visual Studio's main window (the panel the indexing indicator goes into, work
/// item 2.15) without walking the whole window. The main window's visual tree holds every tool window and document, which
/// on a CI runner with a few windows open cost 150 to 350 ms on the UI thread to walk. The status bar is the strip along the
/// bottom of the window, so the finder hit-tests a few points there, climbs from what it hit to the outermost element that is
/// still no taller than a status bar (the status bar itself), and searches only below that. Only when no point lands in the
/// status bar does it search the whole window, breadth first. Either way it stops at a time budget, and the caller falls back
/// to the plain status bar text.
/// </summary>
/// <remarks>Call on the thread that owns the visuals (the UI thread).</remarks>
internal static class StatusBarPanelFinder
{
    /// <summary>The <see cref="StatusBarPanelSearch.How"/> of a search that gave up at its time budget.</summary>
    public const string OverBudget = "over budget";

    /// <summary>The tallest element (in device-independent pixels) taken for a part of the status bar.</summary>
    public const double MaxStatusBarHeight = 60;

    /// <summary>How many nodes are visited between two looks at the clock.</summary>
    private const int CheckEvery = 16;

    /// <summary>Distances from the bottom edge of the window, in device-independent pixels, where the status bar is looked for.</summary>
    private static readonly double[] BottomOffsets = [8, 14, 4];

    /// <summary>Positions across the window, as fractions of its width, where the status bar is looked for.</summary>
    private static readonly double[] AcrossFractions = [0.5, 0.3, 0.7, 0.1, 0.9];

    /// <summary>Finds the element named <paramref name="name"/> in the status bar of <paramref name="window"/>.</summary>
    /// <param name="window">The window (its root visual).</param>
    /// <param name="name">The element's <see cref="FrameworkElement.Name"/>.</param>
    /// <param name="budget">How long the search may take; it gives up after that.</param>
    /// <returns>What was found, how, and what it cost.</returns>
    public static StatusBarPanelSearch Find(FrameworkElement window, string name, TimeSpan budget)
    {
        if (window is null)
        {
            throw new ArgumentNullException(nameof(window));
        }

        var clock = Stopwatch.StartNew();
        var visited = 0;
        var searched = new HashSet<DependencyObject>();
        if (window.ActualWidth > 0 && window.ActualHeight > MaxStatusBarHeight)
        {
            foreach (var offset in BottomOffsets)
            {
                foreach (var fraction in AcrossFractions)
                {
                    var point = new Point(window.ActualWidth * fraction, window.ActualHeight - offset);
                    if (VisualTreeHelper.HitTest(window, point)?.VisualHit is not { } hit)
                    {
                        continue;
                    }

                    // Climb to the outermost element that is still no taller than a status bar.
                    DependencyObject? scope = null;
                    for (DependencyObject? node = hit; node is not null && !ReferenceEquals(node, window); node = VisualTreeHelper.GetParent(node))
                    {
                        visited++;
                        if (IsNamed(node, name))
                        {
                            return new StatusBarPanelSearch((FrameworkElement)node, "hit test", visited, clock.Elapsed);
                        }

                        if (node is FrameworkElement element)
                        {
                            if (element.ActualHeight > MaxStatusBarHeight)
                            {
                                break;
                            }

                            scope = node;
                        }
                    }

                    if (scope is not null && searched.Add(scope))
                    {
                        var below = Search(scope, name, clock, budget, ref visited);
                        if (below is not null)
                        {
                            return new StatusBarPanelSearch(below, "status bar subtree", visited, clock.Elapsed);
                        }
                    }

                    if (clock.Elapsed > budget)
                    {
                        return new StatusBarPanelSearch(null, OverBudget, visited, clock.Elapsed);
                    }
                }
            }
        }

        // No point landed in the status bar (another layout, a window too small): the whole window, still within the budget.
        var anywhere = Search(window, name, clock, budget, ref visited);
        return anywhere is not null
            ? new StatusBarPanelSearch(anywhere, "whole tree", visited, clock.Elapsed)
            : new StatusBarPanelSearch(null, clock.Elapsed > budget ? OverBudget : "not found", visited, clock.Elapsed);
    }

    /// <summary>Compiles the search ahead of time, on the calling thread, so the UI thread does not have to (2 to 3 ms there).</summary>
    public static void Prepare()
    {
        RuntimeHelpers.PrepareMethod(((Func<FrameworkElement, string, TimeSpan, StatusBarPanelSearch>)Find).Method.MethodHandle);
        RuntimeHelpers.PrepareMethod(typeof(StatusBarPanelFinder).GetMethod(nameof(Search), System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!.MethodHandle);
        RuntimeHelpers.PrepareMethod(((Func<DependencyObject, string, bool>)IsNamed).Method.MethodHandle);
    }

    /// <summary>Breadth first below <paramref name="start"/>, until the element is found or the budget is spent.</summary>
    private static FrameworkElement? Search(DependencyObject start, string name, Stopwatch clock, TimeSpan budget, ref int visited)
    {
        var queue = new Queue<DependencyObject>();
        queue.Enqueue(start);
        while (queue.Count > 0)
        {
            var node = queue.Dequeue();
            if (IsNamed(node, name))
            {
                return (FrameworkElement)node;
            }

            if (++visited % CheckEvery == 0 && clock.Elapsed > budget)
            {
                return null;
            }

            if (node is not (Visual or System.Windows.Media.Media3D.Visual3D))
            {
                continue;
            }

            var count = VisualTreeHelper.GetChildrenCount(node);
            for (var i = 0; i < count; i++)
            {
                queue.Enqueue(VisualTreeHelper.GetChild(node, i));
            }
        }

        return null;
    }

    private static bool IsNamed(DependencyObject node, string name) =>
        node is FrameworkElement element && string.Equals(element.Name, name, StringComparison.Ordinal);
}

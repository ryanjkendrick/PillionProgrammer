#if !IDE_VS2022
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Microsoft.VisualStudio.Shell;
using Pillion.Contracts;
using Pillion.VisualStudio.Core;
using Task = System.Threading.Tasks.Task;

namespace Pillion.VisualStudio.IntegrationTests;

/// <summary>What <see cref="IndexWindowChecks.CheckRowsAndPauseAsync"/> saw.</summary>
/// <param name="Root">The only root row's path.</param>
/// <param name="Role">The row's role text.</param>
/// <param name="Files">The row's files text.</param>
/// <param name="Chunks">The row's chunks text.</param>
/// <param name="PausedText">The header's pause reason text while paused.</param>
/// <param name="ExcludedEntries">The entries on the Excluded files tab.</param>
/// <param name="SecretFirst">Whether the first excluded entry is secret-bearing.</param>
/// <param name="ButtonNames">The automation names of the buttons that were reachable.</param>
public sealed record IndexWindowReport(string Root, string Role, string Files, string Chunks, string PausedText, int ExcludedEntries, bool SecretFirst, string[] ButtonNames);

/// <summary>
/// The Index window checks (work item 2.15) that use Pillion.VisualStudio.Core types: reading the rows the window shows for
/// the fake host's canned index, and clicking Pause and Resume. Call only after the package has loaded and the fake host is
/// behind it, on Visual Studio's UI thread.
/// </summary>
public static class IndexWindowChecks
{
    /// <summary>The variable that makes the fake host start with <c>C:\fake</c> indexed (tests/Pillion.FakeHost).</summary>
    public const string IndexVariable = "PILLION_FAKEHOST_INDEX";

    /// <summary>The root the fake host's canned index covers.</summary>
    public const string CannedRoot = @"C:\fake";

    /// <summary>
    /// In the Index window: waits for the canned root's row and checks it, checks that every button has an automation name,
    /// clicks Pause and waits for the paused header, clicks Resume and waits for idle, then opens the Excluded files tab and
    /// counts its entries.
    /// </summary>
    /// <param name="control">The Index window's content (IndexToolWindowControl).</param>
    /// <param name="log">Writes to the test output.</param>
    /// <param name="cancellationToken">Cancels the waits.</param>
    /// <returns>What was seen.</returns>
    public static async System.Threading.Tasks.Task<IndexWindowReport> CheckRowsAndPauseAsync(FrameworkElement control, Action<string> log, CancellationToken cancellationToken)
    {
        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync(cancellationToken);
        var vm = control.DataContext as IndexWindowViewModel ?? throw new InvalidOperationException("The Index window's DataContext is not an IndexWindowViewModel.");
        Assert.True(vm.IsWindowEnabled, "The Index window is disabled by the indexWindow flag.");
        await UntilAsync(() => vm.Roots.Count == 1 && vm.PhaseText == IndexFormat.Phase(IndexPhase.Idle), TimeSpan.FromSeconds(30), "the canned root to be listed", cancellationToken).ConfigureAwait(true);

        var row = vm.Roots[0];
        log($"Row: {row.RootPath}; {row.RoleText}; files {row.FilesText}; chunks {row.ChunksText}; {row.DiskText}; {row.ModelText}.");
        Assert.Equal(CannedRoot, row.RootPath);
        Assert.Equal(IndexRole.Writer, row.Role);
        Assert.Equal("40 of 40", row.FilesText);
        Assert.Equal("320", row.ChunksText);
        Assert.Equal("bge-small-en-v1.5", row.ModelText);
        Assert.True(row.RebuildCommand.CanExecute(null), "Rebuild is not available for an idle root.");
        Assert.True(row.DeleteCommand.CanExecute(null), "Delete is not available for an idle root.");

        var buttons = Descendants<Button>(control).Where(b => b.IsVisible).ToList();
        var names = buttons.Select(b => System.Windows.Automation.AutomationProperties.GetName(b)).ToArray();
        log("Buttons: " + string.Join("; ", names));
        Assert.All(names, name => Assert.False(string.IsNullOrWhiteSpace(name), "A button has no automation name."));
        Assert.Contains(IndexStrings.PauseName, names);

        var pause = buttons.Single(b => System.Windows.Automation.AutomationProperties.GetName(b) == IndexStrings.PauseName);
        var resume = buttons.Single(b => System.Windows.Automation.AutomationProperties.GetName(b) == IndexStrings.ResumeName);
        Assert.True(pause.IsEnabled, "Pause is disabled.");
        Assert.False(resume.IsEnabled, "Resume is enabled before anything is paused.");

        Click(pause);
        await UntilAsync(() => vm.IsPaused, TimeSpan.FromSeconds(15), "the paused header", cancellationToken).ConfigureAwait(true);
        var pausedText = vm.PauseReasonText;
        log("Paused: " + pausedText);
        Assert.Equal("Paused: " + IndexStrings.PauseUser, pausedText);
        await UntilAsync(() => resume.IsEnabled && !pause.IsEnabled, TimeSpan.FromSeconds(5), "Resume to be enabled", cancellationToken).ConfigureAwait(true);

        Click(resume);
        await UntilAsync(() => !vm.IsPaused && vm.PhaseText == IndexFormat.Phase(IndexPhase.Idle), TimeSpan.FromSeconds(15), "indexing to resume", cancellationToken).ConfigureAwait(true);

        // The Excluded files tab: the fake host lists two secret-bearing files first, then four other exclusions.
        if (control.FindName("ExcludedList") is ListView list && Descendants<TabControl>(control).FirstOrDefault() is { } tabs)
        {
            tabs.SelectedIndex = 1;
            await UntilAsync(() => list.Items.Count == 6, TimeSpan.FromSeconds(15), "the excluded files", cancellationToken).ConfigureAwait(true);
            var first = (ExcludedFileRowViewModel)list.Items[0];
            log($"Excluded: {list.Items.Count} entries; first {first.RelativePath} ({first.KindText}).");
            return new IndexWindowReport(row.RootPath, row.RoleText, row.FilesText, row.ChunksText, pausedText, list.Items.Count, first.IsSecretBearing, names);
        }

        throw new InvalidOperationException("The Index window has no Excluded files list.");
    }

    private static void Click(Button button)
    {
        // A click raises Click on the button, which runs its command (when it can execute), as the mouse or Space would.
        button.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
    }

    private static IEnumerable<T> Descendants<T>(DependencyObject root)
        where T : DependencyObject
    {
        var count = VisualTreeHelper.GetChildrenCount(root);
        for (var i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T match)
            {
                yield return match;
            }

            foreach (var nested in Descendants<T>(child))
            {
                yield return nested;
            }
        }
    }

    private static async Task UntilAsync(Func<bool> condition, TimeSpan timeout, string what, CancellationToken cancellationToken)
    {
        var clock = Stopwatch.StartNew();
        while (!condition())
        {
            if (clock.Elapsed > timeout)
            {
                throw new TimeoutException($"Timed out after {timeout.TotalSeconds:0} s waiting for {what}.");
            }

            await Task.Delay(100, cancellationToken).ConfigureAwait(true);
        }
    }
}
#endif

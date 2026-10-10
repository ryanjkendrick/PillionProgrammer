#if !IDE_VS2022
using System;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Microsoft.VisualStudio.Shell;
using Pillion.Contracts;
using Pillion.VisualStudio.Core;
using Pillion.VisualStudio.Core.Mentions;
using Task = System.Threading.Tasks.Task;

namespace Pillion.VisualStudio.IntegrationTests;

/// <summary>What <see cref="MentionChecks.PickFileAndSendAsync"/> saw.</summary>
/// <param name="PickerEntries">The picker's entries after typing the file name.</param>
/// <param name="Pin">The pin's name and token badge.</param>
/// <param name="InputAfterPick">The input text after the file was picked (the typed mention is removed).</param>
/// <param name="ReplyHeader">The reply's header.</param>
/// <param name="OutboundRows">The Outbound log rows after the reply.</param>
public sealed record MentionRunReport(string[] PickerEntries, string Pin, string InputAfterPick, string ReplyHeader, int OutboundRows);

/// <summary>
/// The mention checks (work item 1.9) that use Pillion.VisualStudio.Core types: swapping the host for the fake host and
/// back, and driving the chat window's input with the keyboard events its code-behind handles. Call only after the package
/// has loaded (see <see cref="PillionIdeTest.EnsurePackageLoadedAsync"/>), on Visual Studio's UI thread.
/// </summary>
public static class MentionChecks
{
    /// <summary>The variable the package reads at each host launch (HostLauncherSelector.OverrideVariable).</summary>
    public const string HostOverrideVariable = "PILLION_TEST_HOST_PATH";

    private static readonly string[] Variables = [HostOverrideVariable, "PILLION_FAKEHOST_MODE", "PILLION_FAKEHOST_RECORD", "PILLION_FAKEHOST_CHUNK_DELAY_MS"];

    /// <summary>Puts the fake host behind the package: sets the variables inside devenv.exe, retries the host and waits for it.</summary>
    /// <param name="fakeHostPath">The fake host executable.</param>
    /// <param name="recordFile">Where the fake host appends each chat request's pins.</param>
    /// <param name="cancellationToken">Cancels the wait.</param>
    /// <returns>The fake host's process id.</returns>
    public static async Task<int> UseFakeHostAsync(string fakeHostPath, string recordFile, CancellationToken cancellationToken)
    {
        Environment.SetEnvironmentVariable(HostOverrideVariable, fakeHostPath);
        Environment.SetEnvironmentVariable("PILLION_FAKEHOST_MODE", "quiet");
        Environment.SetEnvironmentVariable("PILLION_FAKEHOST_RECORD", recordFile);
        Environment.SetEnvironmentVariable("PILLION_FAKEHOST_CHUNK_DELAY_MS", "10");
        return await RestartAsync("Pillion.FakeHost", cancellationToken).ConfigureAwait(true);
    }

    /// <summary>Clears the variables and restarts the shipped host.</summary>
    /// <param name="cancellationToken">Cancels the wait.</param>
    /// <returns>The host's process id.</returns>
    public static async Task<int> RestoreHostAsync(CancellationToken cancellationToken)
    {
        foreach (var name in Variables)
        {
            Environment.SetEnvironmentVariable(name, null);
        }

        return await RestartAsync("Pillion.Host", cancellationToken).ConfigureAwait(true);
    }

    /// <summary>
    /// In the chat window: types <c>@</c> and then <c>@</c> plus the start of a file name into the input, picks the file
    /// with Down and Enter, waits for its pin, types a message and presses Enter, and waits for the reply and the Outbound log.
    /// </summary>
    /// <param name="control">The chat window's content (ChatToolWindowControl).</param>
    /// <param name="fileName">The file to pick, for example <c>Program.cs</c>.</param>
    /// <param name="log">Writes to the test output.</param>
    /// <param name="cancellationToken">Cancels the waits.</param>
    /// <returns>What was seen.</returns>
    public static async Task<MentionRunReport> PickFileAndSendAsync(FrameworkElement control, string fileName, Action<string> log, CancellationToken cancellationToken)
    {
        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync(cancellationToken);
        var vm = control.DataContext as ChatViewModel ?? throw new InvalidOperationException("The chat window's DataContext is not a ChatViewModel.");
        var input = control.FindName("Input") as TextBox ?? throw new InvalidOperationException("The chat window has no Input text box.");
        await UntilAsync(() => vm.ModelPicker.SelectedModel is { IsAvailable: true }, TimeSpan.FromSeconds(30), "a usable model in the picker", cancellationToken).ConfigureAwait(true);
        log("Model: " + vm.ModelPicker.SelectedModel!.DisplayText);
        Assert.True(vm.IsMentionsEnabled, "The mentions feature is off.");
        input.Focus();

        Type(input, "@");
        await UntilAsync(() => vm.Mentions.Picker.IsOpen && vm.Mentions.Picker.Items.Count >= 5, TimeSpan.FromSeconds(10), "the picker to list the kinds", cancellationToken).ConfigureAwait(true);
        log("After @: " + string.Join(", ", vm.Mentions.Picker.Items.Select(i => i.Label)));
        Assert.Equal("@file", vm.Mentions.Picker.Items[0].Label);

        var typed = "@" + fileName.Substring(0, Math.Min(4, fileName.Length));
        Type(input, typed);
        await UntilAsync(
            () => vm.Mentions.Picker.IsOpen && vm.Mentions.Picker.Items.Any(i => i.ItemKind == MentionPickerItemKind.File && i.Label == fileName),
            TimeSpan.FromSeconds(30),
            "the picker to list " + fileName,
            cancellationToken).ConfigureAwait(true);
        var entries = vm.Mentions.Picker.Items.Select(i => i.AutomationName).ToArray();
        log("After " + typed + ": " + string.Join("; ", entries));

        for (var i = 0; i < 25 && !(vm.Mentions.Picker.SelectedItem is { ItemKind: MentionPickerItemKind.File } selected && selected.Label == fileName); i++)
        {
            Press(input, Key.Down);
        }

        Assert.Equal(fileName, vm.Mentions.Picker.SelectedItem?.Label);
        Press(input, Key.Enter);
        await UntilAsync(() => vm.Mentions.Pins.Count == 1, TimeSpan.FromSeconds(30), "the file to be pinned", cancellationToken).ConfigureAwait(true);
        var pin = vm.Mentions.Pins[0];
        var inputAfterPick = input.Text;
        log($"Pinned {pin.DisplayName} ({pin.TokenBadge} tokens); input now \"{inputAfterPick}\".");
        Assert.False(vm.Mentions.Picker.IsOpen, "The picker stayed open after the pick.");

        Type(input, "Explain this file.");
        Press(input, Key.Enter);
        await UntilAsync(
            () => vm.Turns.Count == 2 && !vm.IsSending && vm.Turns[1].State == ChatTurnState.Complete,
            TimeSpan.FromSeconds(60),
            "the reply to complete",
            cancellationToken).ConfigureAwait(true);
        log("Reply: " + vm.Turns[1].Header);
        await UntilAsync(() => vm.OutboundLog.Records.Count > 0, TimeSpan.FromSeconds(15), "the Outbound log to show the request", cancellationToken).ConfigureAwait(true);
        log("Outbound log: " + string.Join(" | ", vm.OutboundLog.Records.Select(r => r.ToText(", "))));

        return new MentionRunReport(entries, pin.DisplayName + " " + pin.TokenBadge, inputAfterPick, vm.Turns[1].Header, vm.OutboundLog.Records.Count);
    }

    /// <summary>Removes every pin and turn the run left in the current conversation.</summary>
    /// <param name="control">The chat window's content.</param>
    public static void Reset(FrameworkElement control)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        if (control.DataContext is ChatViewModel vm)
        {
            vm.Mentions.Pins.Clear();
            vm.Clear();
            vm.InputText = string.Empty;
        }
    }

    /// <summary>Replaces the input text and puts the caret at its end, as typing would (the code-behind follows both).</summary>
    private static void Type(TextBox input, string text)
    {
        input.Text = text;
        input.CaretIndex = text.Length;
    }

    /// <summary>Raises the key on the input the way WPF raises a key press (preview first), without needing the foreground.</summary>
    private static void Press(TextBox input, Key key)
    {
        var source = PresentationSource.FromVisual(input) ?? throw new InvalidOperationException("The chat input is not shown.");
        var preview = new KeyEventArgs(Keyboard.PrimaryDevice, source, Environment.TickCount, key) { RoutedEvent = Keyboard.PreviewKeyDownEvent };
        input.RaiseEvent(preview);
        if (!preview.Handled)
        {
            input.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, source, Environment.TickCount, key) { RoutedEvent = Keyboard.KeyDownEvent });
        }
    }

    private static async Task<int> RestartAsync(string processName, CancellationToken cancellationToken)
    {
        var service = await AsyncServiceProvider.GlobalProvider.GetServiceAsync(typeof(IHostConnection)).ConfigureAwait(true);
        var supervisor = service as HostSupervisor ?? throw new InvalidOperationException("The IHostConnection service is not a HostSupervisor.");
        var before = supervisor.HostProcessId;
        await supervisor.RetryAsync().ConfigureAwait(true);
        var pid = 0;
        await UntilAsync(
            () =>
            {
                if (supervisor.State != HostState.Ready || supervisor.HostProcessId is not { } current || current == before)
                {
                    return false;
                }

                try
                {
                    using var process = Process.GetProcessById(current);
                    pid = current;
                    return string.Equals(process.ProcessName, processName, StringComparison.OrdinalIgnoreCase);
                }
                catch (ArgumentException)
                {
                    return false;
                }
            },
            TimeSpan.FromSeconds(45),
            processName + " to be Ready",
            cancellationToken).ConfigureAwait(true);
        return pid;
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

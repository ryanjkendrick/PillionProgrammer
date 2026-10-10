using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Microsoft.VisualStudio.ComponentModelHost;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Text;
using Microsoft.VisualStudio.Text.Editor;
using Microsoft.VisualStudio.Text.Operations;
using Pillion.Contracts;
using Pillion.VisualStudio.Core.InlineChat;

namespace Pillion.VisualStudio.IntegrationTests;

/// <summary>
/// Inline chat steps that use Pillion.Contracts and Pillion.VisualStudio.Core types (work item 1.7). Kept out of the test
/// class, like <see cref="PillionHostChecks"/>, so test discovery outside Visual Studio never needs those assemblies; call
/// these only after the package has loaded. Every member runs on the UI thread.
/// </summary>
/// <remarks>
/// The tests have no model: <see cref="InstallCannedEdits"/> puts a canned <see cref="IInlineEditService"/> in
/// <see cref="InlineChatOverrides.EditService"/>, which new inline chat sessions use instead of Pillion Host's. Keys are
/// raised as WPF key events on the adornment's prompt, the path keys take when they reach WPF.
/// </remarks>
public static class InlineChatChecks
{
    /// <summary>The comment line the canned service adds above the scope.</summary>
    public const string CannedLine = "// Pillion integration test edit";

    /// <summary>The adornment layer of the inline chat control (InlineChatEditorServices.AdornmentLayer in the extension).</summary>
    private const string AdornmentLayer = "PillionInlineChat";

    /// <summary>Makes new inline chat sessions use a canned inline edit service that adds <see cref="CannedLine"/> above the scope.</summary>
    public static void InstallCannedEdits() => InlineChatOverrides.EditService = new CannedInlineEditService();

    /// <summary>Restores Pillion Host's inline edit service.</summary>
    public static void RemoveCannedEdits() => InlineChatOverrides.EditService = null;

    /// <summary>Gets the text the canned service proposes for a scope.</summary>
    /// <param name="scopeText">The scope's text.</param>
    /// <returns>The proposal.</returns>
    public static string CannedProposal(string scopeText) => CannedInlineEditService.Propose(scopeText);

    /// <summary>Selects whole lines of the view (from the first line's start to the last line's end).</summary>
    /// <param name="view">The view.</param>
    /// <param name="firstLine">The zero-based first line.</param>
    /// <param name="lastLine">The zero-based last line.</param>
    /// <returns>The selected text.</returns>
    public static string SelectLines(IWpfTextView view, int firstLine, int lastLine)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        var snapshot = view.TextSnapshot;
        var span = new SnapshotSpan(snapshot.GetLineFromLineNumber(firstLine).Start, snapshot.GetLineFromLineNumber(lastLine).End);
        view.Selection.Select(span, isReversed: false);
        view.Caret.MoveTo(span.End);
        return span.GetText();
    }

    /// <summary>Waits for the inline chat prompt to appear in the view's adornment layer.</summary>
    /// <param name="view">The view.</param>
    /// <param name="timeout">How long to wait.</param>
    /// <param name="cancellationToken">Cancels the wait.</param>
    /// <returns>The prompt text box.</returns>
    public static async Task<TextBox> WaitForPromptAsync(IWpfTextView view, TimeSpan timeout, CancellationToken cancellationToken)
    {
        TextBox? prompt = null;
        await WaitAsync(() => (prompt = Find<TextBox>(view, "PillionInlineChatPrompt")) is not null, timeout, "the inline chat prompt", cancellationToken).ConfigureAwait(true);
        return prompt!;
    }

    /// <summary>Types an instruction into the prompt and presses Enter.</summary>
    /// <param name="prompt">The prompt.</param>
    /// <param name="instruction">The instruction.</param>
    public static void Send(TextBox prompt, string instruction)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        prompt.Focus();
        prompt.Text = instruction;
        PressKey(prompt, Key.Enter);
    }

    /// <summary>Waits until the proposal is complete and can be applied (the Apply button is shown).</summary>
    /// <param name="view">The view.</param>
    /// <param name="timeout">How long to wait.</param>
    /// <param name="cancellationToken">Cancels the wait.</param>
    /// <returns>A task that completes when the proposal is shown.</returns>
    public static Task WaitForPreviewAsync(IWpfTextView view, TimeSpan timeout, CancellationToken cancellationToken) =>
        WaitAsync(
            () => Find<Button>(view, "PillionInlineChatApply") is { IsVisible: true }
                && Find<FrameworkElement>(view, "PillionInlineChatDiff") is { IsVisible: true },
            timeout,
            "the inline difference view with a complete proposal",
            cancellationToken);

    /// <summary>Gets a value indicating whether the inline chat adornment is open in the view.</summary>
    /// <param name="view">The view.</param>
    /// <returns><see langword="true"/> when it is.</returns>
    public static bool IsOpen(IWpfTextView view)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        return view.GetAdornmentLayer(AdornmentLayer).Elements.Count > 0;
    }

    /// <summary>Presses a key on an element: raises PreviewKeyDown and KeyDown as WPF does for a real key press.</summary>
    /// <param name="target">The element.</param>
    /// <param name="key">The key.</param>
    public static void PressKey(UIElement target, Key key)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        var source = PresentationSource.FromVisual(target) ?? throw new InvalidOperationException("The element is not shown.");
        var preview = new KeyEventArgs(Keyboard.PrimaryDevice, source, Environment.TickCount, key) { RoutedEvent = Keyboard.PreviewKeyDownEvent };
        target.RaiseEvent(preview);
        if (!preview.Handled)
        {
            target.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, source, Environment.TickCount, key) { RoutedEvent = Keyboard.KeyDownEvent });
        }
    }

    /// <summary>Describes the undo history of the view's buffer: the number of units and the newest unit's description.</summary>
    /// <param name="view">The view.</param>
    /// <returns>The count and description, or <see langword="null"/> when the buffer has no editor undo history.</returns>
    public static (int Count, string? Top)? UndoState(IWpfTextView view)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        var registry = (Package.GetGlobalService(typeof(SComponentModel)) as IComponentModel)?.GetService<ITextUndoHistoryRegistry>();
        if (registry is null || !registry.TryGetHistory(view.TextBuffer, out var history))
        {
            return null;
        }

        var stack = history.UndoStack.ToList();
        return (stack.Count, stack.FirstOrDefault()?.Description);
    }

    private static T? Find<T>(IWpfTextView view, string automationId)
        where T : FrameworkElement
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        foreach (var element in view.GetAdornmentLayer(AdornmentLayer).Elements)
        {
            if (Descendants(element.Adornment).OfType<T>().FirstOrDefault(e => AutomationProperties.GetAutomationId(e) == automationId) is { } match)
            {
                return match;
            }
        }

        return null;
    }

    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        yield return root;
        var count = root is Visual ? VisualTreeHelper.GetChildrenCount(root) : 0;
        for (var i = 0; i < count; i++)
        {
            foreach (var child in Descendants(VisualTreeHelper.GetChild(root, i)))
            {
                yield return child;
            }
        }
    }

    private static async Task WaitAsync(Func<bool> condition, TimeSpan timeout, string what, CancellationToken cancellationToken)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (true)
        {
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync(cancellationToken);
            if (condition())
            {
                return;
            }

            if (DateTime.UtcNow > deadline)
            {
                throw new TimeoutException($"Timed out after {timeout.TotalSeconds:0} s waiting for {what}.");
            }

            await Task.Delay(100, cancellationToken).ConfigureAwait(true);
        }
    }

    /// <summary>A canned inline edit service: two text chunks (the comment line, then the scope) and a final chunk.</summary>
    private sealed class CannedInlineEditService : IInlineEditService
    {
        public static string Propose(string scopeText)
        {
            var newline = scopeText.Contains("\r\n") ? "\r\n" : "\n";
            var indent = new string(scopeText.TakeWhile(c => c is ' ' or '\t').ToArray());
            return indent + CannedLine + newline + scopeText;
        }

        public async IAsyncEnumerable<InlineEditChunk> EditAsync(InlineEditRequest request, [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            var proposal = Propose(request.ScopeText);
            var split = proposal.IndexOf('\n') + 1;
            await Task.Delay(100, cancellationToken).ConfigureAwait(false);
            yield return new InlineEditChunk { RequestId = request.RequestId, Text = proposal.Substring(0, split), ModelId = "canned", ProviderId = "test" };
            await Task.Delay(100, cancellationToken).ConfigureAwait(false);
            yield return new InlineEditChunk { RequestId = request.RequestId, Text = proposal.Substring(split), ModelId = "canned", ProviderId = "test" };
            yield return new InlineEditChunk { RequestId = request.RequestId, IsFinal = true, ModelId = "canned", ProviderId = "test" };
        }

        public Task CancelEditAsync(string requestId, CancellationToken cancellationToken) => Task.CompletedTask;
    }
}

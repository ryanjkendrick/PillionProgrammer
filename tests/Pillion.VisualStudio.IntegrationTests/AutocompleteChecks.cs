using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio;
using Microsoft.VisualStudio.ComponentModelHost;
using Microsoft.VisualStudio.Editor;
using Microsoft.VisualStudio.Language.Intellisense.AsyncCompletion;
using Microsoft.VisualStudio.Language.Suggestions;
using Microsoft.VisualStudio.OLE.Interop;
using Microsoft.VisualStudio.Settings;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using Microsoft.VisualStudio.Text.Editor;
using Pillion.Contracts;
using Pillion.VisualStudio.Core.Autocomplete;

namespace Pillion.VisualStudio.IntegrationTests;

/// <summary>
/// Ghost-text steps that use Pillion.Contracts and Pillion.VisualStudio.Core types (work items 1.1 and 1.4). Kept out of
/// the test class, like <see cref="PillionHostChecks"/>, so test discovery outside Visual Studio never needs those
/// assemblies; call these only after the package has loaded. Every member except the canned service runs on the UI thread.
/// </summary>
/// <remarks>
/// The tests have no model: <see cref="InstallCannedCompletions"/> puts a canned <see cref="IModelService"/> in
/// <see cref="AutocompleteOverrides.ModelService"/> that answers like <c>tests/Pillion.FakeHost</c> does. Keys go through
/// the view's command chain (<c>IVsTextView</c> as <see cref="IOleCommandTarget"/>), the path real typing takes, so the
/// broker sees TypeChar, Tab and Esc as it would from the keyboard.
/// </remarks>
public static class AutocompleteChecks
{
    /// <summary>The single-line completion the canned service streams (the fake host's).</summary>
    public const string CannedCompletion = "Calculator.Sum(numbers));";

    /// <summary>Makes autocomplete requests use the canned completion service.</summary>
    public static void InstallCannedCompletions() => AutocompleteOverrides.ModelService = new CannedCompletionService();

    /// <summary>Restores Pillion Host's model service.</summary>
    public static void RemoveCannedCompletions() => AutocompleteOverrides.ModelService = null;

    /// <summary>Gets the id of the proposal displayed in a view, or <see langword="null"/>.</summary>
    /// <param name="view">The view.</param>
    /// <returns>The id.</returns>
    public static string? DisplayedProposalId(ITextView view)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        return Suggestions()?.GetDisplayedProposal(view)?.ProposalId;
    }

    /// <summary>Gets a value indicating whether the displayed proposal is Pillion's.</summary>
    /// <param name="view">The view.</param>
    /// <returns><see langword="true"/> when a Pillion proposal is displayed.</returns>
    public static bool IsPillionDisplayed(ITextView view)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        return PillionProposalId.IsPillion(DisplayedProposalId(view));
    }

    /// <summary>Gets a value indicating whether a completion list is open in a view.</summary>
    /// <param name="view">The view.</param>
    /// <returns><see langword="true"/> while a list is open.</returns>
    public static bool IsCompletionActive(ITextView view)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        var componentModel = (IComponentModel)Package.GetGlobalService(typeof(SComponentModel));
        return componentModel.GetService<IAsyncCompletionBroker>().IsCompletionActive(view);
    }

    /// <summary>Gets the text in the selected item of the open completion list, or <see langword="null"/>.</summary>
    /// <param name="view">The view.</param>
    /// <returns>The item's display text.</returns>
    public static string? SelectedCompletionItem(ITextView view)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        var componentModel = (IComponentModel)Package.GetGlobalService(typeof(SComponentModel));
        var session = componentModel.GetService<IAsyncCompletionBroker>().GetSession(view);
        if (session is null || session.IsDismissed)
        {
            return null;
        }

        var items = session.GetComputedItems(CancellationToken.None);
        return items.SuggestionItemSelected || items.SelectedItem is null ? null : items.SelectedItem.DisplayText;
    }

    /// <summary>Types text one character at a time through the view's command chain.</summary>
    /// <param name="view">The view.</param>
    /// <param name="text">The text.</param>
    public static void Type(IWpfTextView view, string text)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        foreach (var c in text)
        {
            var variant = Marshal.AllocCoTaskMem(32);
            try
            {
                Marshal.GetNativeVariantForObject((ushort)c, variant);
                Exec(view, VSConstants.VSStd2KCmdID.TYPECHAR, variant);
            }
            finally
            {
                Marshal.FreeCoTaskMem(variant);
            }
        }
    }

    /// <summary>Sends an editor command (Tab, Esc, Complete Word ...) through the view's command chain.</summary>
    /// <param name="view">The view.</param>
    /// <param name="command">The command.</param>
    /// <returns>The HRESULT.</returns>
    public static int Exec(IWpfTextView view, VSConstants.VSStd2KCmdID command)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        return Exec(view, command, IntPtr.Zero);
    }

    /// <summary>Runs "Pillion: Suggest" in the view (the text view's command filter handles it).</summary>
    /// <param name="view">The view.</param>
    /// <returns>The HRESULT.</returns>
    public static int Suggest(IWpfTextView view)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        var group = new Guid("444aebc2-1c36-43a9-ab09-2c2d4e04a11d");
        return Target(view).Exec(ref group, 0x0310, 0, IntPtr.Zero, IntPtr.Zero);
    }

    /// <summary>
    /// Sets IntelliCode's "Automatically generate code completions in the Editor" (Visual Studio 2022) through the settings
    /// manager and returns the previous stored value.
    /// </summary>
    /// <param name="value">0 (the default, on), 1 (on) or 2 (off).</param>
    /// <returns>The previous value, or <see langword="null"/> when it was never set.</returns>
    public static async Task<object?> SetIntelliCodeCompletionsAsync(int value)
    {
        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
        var settings = (ISettingsManager)Package.GetGlobalService(typeof(Microsoft.Internal.VisualStudio.Shell.Interop.SVsSettingsPersistenceManager));
        object? previous = settings.TryGetValue(IntelliCodeRequirement.ShowCompletionsSetting, out object stored) == GetValueResult.Success ? stored : null;
        await settings.SetValueAsync(IntelliCodeRequirement.ShowCompletionsSetting, value, isMachineLocal: false).ConfigureAwait(true);
        return previous;
    }

    /// <summary>Gets the status bar text.</summary>
    /// <returns>The text.</returns>
    public static string StatusBarText()
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        var statusBar = (IVsStatusbar)Package.GetGlobalService(typeof(SVsStatusbar));
        return ErrorHandler.Succeeded(statusBar.GetText(out var text)) ? text ?? string.Empty : string.Empty;
    }

    /// <summary>Gets the end of the Pillion Programmer Output pane, through the extension's IDE signals service.</summary>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>The text, or an empty string.</returns>
    public static async Task<string> PillionOutputTextAsync(CancellationToken cancellationToken)
    {
        if (await AsyncServiceProvider.GlobalProvider.GetServiceAsync(typeof(Pillion.VisualStudio.Core.Signals.IIdeSignals)).ConfigureAwait(false) is not Pillion.VisualStudio.Core.Signals.IIdeSignals signals)
        {
            return string.Empty;
        }

        return await signals.GetOutputPaneTextAsync("Pillion Programmer", 20000, cancellationToken).ConfigureAwait(false) ?? string.Empty;
    }

    private static SuggestionServiceBase? Suggestions()
    {
        var componentModel = (IComponentModel)Package.GetGlobalService(typeof(SComponentModel));
        return componentModel.DefaultExportProvider.GetExportedValueOrDefault<SuggestionServiceBase>();
    }

    private static int Exec(IWpfTextView view, VSConstants.VSStd2KCmdID command, IntPtr variant)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        var group = VSConstants.VSStd2K;
        return Target(view).Exec(ref group, (uint)command, 0, variant, IntPtr.Zero);
    }

    private static IOleCommandTarget Target(IWpfTextView view)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        var componentModel = (IComponentModel)Package.GetGlobalService(typeof(SComponentModel));
        var adapters = componentModel.GetService<IVsEditorAdaptersFactoryService>();
        return (IOleCommandTarget)adapters.GetViewAdapter(view)!;
    }

    /// <summary>A canned completion service that answers like the fake host: a single line, streamed in small chunks.</summary>
    private sealed class CannedCompletionService : IModelService
    {
        public async IAsyncEnumerable<CompletionChunk> CompleteAsync(CompletionRequest request, [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            var text = CannedCompletion + "\n        Console.WriteLine(\"more\");";
            for (var i = 0; i < text.Length; i += 8)
            {
                await Task.Delay(10, cancellationToken).ConfigureAwait(false);
                yield return new CompletionChunk { RequestId = request.RequestId, Text = text.Substring(i, Math.Min(8, text.Length - i)), ModelId = "canned", ProviderId = "test" };
            }

            yield return new CompletionChunk { RequestId = request.RequestId, IsFinal = true, ModelId = "canned", ProviderId = "test" };
        }

        public IAsyncEnumerable<ChatChunk> ChatAsync(ChatRequest request, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task CancelAsync(string requestId, CancellationToken cancellationToken) => Task.CompletedTask;

        public Task<ModelDescriptor[]> ListModelsAsync(CancellationToken cancellationToken) => Task.FromResult(Array.Empty<ModelDescriptor>());
    }
}

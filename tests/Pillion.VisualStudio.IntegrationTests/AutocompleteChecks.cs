using System;
using System.Collections.Generic;
using System.Linq;
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

    private static int _cannedRequests;
    private static int _lastCannedRequestTick;

    /// <summary>Gets how many completion requests reached the canned service since <see cref="InstallCannedCompletions"/>: Pillion's source asking the model.</summary>
    public static int CannedRequestCount => Volatile.Read(ref _cannedRequests);

    /// <summary>Makes autocomplete requests use the canned completion service.</summary>
    public static void InstallCannedCompletions()
    {
        Volatile.Write(ref _cannedRequests, 0);
        AutocompleteOverrides.ModelService = new CannedCompletionService();
    }

    /// <summary>Restores Pillion Host's model service.</summary>
    public static void RemoveCannedCompletions() => AutocompleteOverrides.ModelService = null;

    /// <summary>Gets the id of the proposal displayed in a view, or <see langword="null"/>.</summary>
    /// <param name="view">The view.</param>
    /// <returns>The id.</returns>
    public static string? DisplayedProposalId(ITextView view)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        return SuggestionService()?.GetDisplayedProposal(view)?.ProposalId;
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

    /// <summary>
    /// Describes what the editor and Pillion's adapter are doing in a view, for a failure message: whether Pillion's source
    /// reached the model, what is displayed, the completion list and the caret line.
    /// </summary>
    /// <param name="view">The view.</param>
    /// <returns>One line.</returns>
    public static string DescribeState(IWpfTextView view)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        var lastRequest = Volatile.Read(ref _cannedRequests) == 0
            ? "none"
            : (Environment.TickCount - Volatile.Read(ref _lastCannedRequestTick)).ToString(System.Globalization.CultureInfo.InvariantCulture) + " ms ago";
        string? selected = null;
        var active = false;
        try
        {
            active = IsCompletionActive(view);
            if (active)
            {
                var componentModel = (IComponentModel)Package.GetGlobalService(typeof(SComponentModel));
                using var limit = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                var items = componentModel.GetService<IAsyncCompletionBroker>().GetSession(view)?.GetComputedItems(limit.Token);
                selected = items is null ? null : items.SuggestionItemSelected ? "<suggestion mode>" : items.SelectedItem?.DisplayText;
            }
        }
        catch (OperationCanceledException)
        {
            selected = "<computing>";
        }

        var caret = view.Caret.Position.BufferPosition;
        return $"canned requests: {CannedRequestCount} (last {lastRequest}); displayed proposal: {DisplayedProposalId(view) ?? "none"}; "
            + $"completion list: {(active ? "open" : "closed")}, selected {selected ?? "nothing"}; keyboard focus: {view.HasAggregateFocus}; "
            + $"Pillion state on the view: {HasPillionViewState(view)}; model override set: {AutocompleteOverrides.ModelService is not null}; "
            + $"caret line {caret.GetContainingLine().LineNumber}: \"{caret.GetContainingLine().GetText().Trim()}\"";
    }

    /// <summary>
    /// Asks the view's proposal broker directly, for a failure message: whether any source is registered, whether the
    /// proposal managers (Pillion's among them) say a proposal may be generated at the caret, and which source answers an
    /// explicit invocation, and after how long. It makes every source run again, Pillion's included.
    /// </summary>
    /// <param name="view">The view.</param>
    /// <param name="timeout">How long to wait for the sources.</param>
    /// <param name="cancellationToken">Cancels the probe.</param>
    /// <returns>One line.</returns>
    public static async Task<string> ProbeBrokerAsync(IWpfTextView view, TimeSpan timeout, CancellationToken cancellationToken)
    {
        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync(cancellationToken);
        var componentModel = (IComponentModel)Package.GetGlobalService(typeof(SComponentModel));
        var provider = componentModel.DefaultExportProvider.GetExportedValueOrDefault<Microsoft.VisualStudio.Language.Proposals.ProposalBrokerProviderBase>();
        if (provider is null)
        {
            return "broker: no proposal broker provider";
        }

        var broker = provider.GetProposalBroker(view);
        var caret = view.Caret.Position.VirtualBufferPosition;
        var text = new System.Text.StringBuilder("broker: sources registered ")
            .Append(broker.AnyProposalSources)
            .Append(", managers allow a proposal at the caret ")
            .Append(broker.ShouldGenerateProposal(caret, Microsoft.VisualStudio.Language.Proposals.ProposalScenario.TypeChar, ' '));
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        limit.CancelAfter(timeout);
        var clock = System.Diagnostics.Stopwatch.StartNew();
        var requestsBefore = CannedRequestCount;
        try
        {
            var queue = broker.RequestProposals(caret, null!, Microsoft.VisualStudio.Language.Proposals.ProposalScenario.ExplicitInvocation, '\0', TimeSpan.Zero, limit.Token);
            while (true)
            {
                var collection = await queue.DequeueAsync(limit.Token).ConfigureAwait(true);
                _ = text.Append("; ").Append(collection.SourceName).Append(" answered after ").Append(clock.ElapsedMilliseconds).Append(" ms with ").Append(collection.Proposals.Count).Append(" proposal(s)");
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            _ = text.Append("; no further answer within ").Append(timeout.TotalSeconds.ToString("0", System.Globalization.CultureInfo.InvariantCulture)).Append(" s");
        }
        catch (InvalidOperationException)
        {
            _ = text.Append("; every source has answered");
        }

        return text.Append("; canned requests caused by the probe: ").Append(CannedRequestCount - requestsBefore).ToString();
    }

    /// <summary>
    /// Gets a value indicating whether Pillion's adapter has created its state for a view, which it does the first time the
    /// proposal broker asks Pillion's proposal source or manager provider about the view.
    /// </summary>
    /// <param name="view">The view.</param>
    /// <returns><see langword="true"/> when the broker has reached Pillion for the view.</returns>
    public static bool HasPillionViewState(ITextView view) =>
        view.Properties.PropertyList.Any(p => p.Key is Type type && type.Name == "ViewAutocomplete");

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

    /// <summary>Gets Visual Studio's suggestion service (the proposal broker's front end), or <see langword="null"/> when there is none.</summary>
    /// <returns>The service.</returns>
    public static SuggestionServiceBase? SuggestionService()
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
            _ = Interlocked.Increment(ref _cannedRequests);
            Volatile.Write(ref _lastCannedRequestTick, Environment.TickCount);
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

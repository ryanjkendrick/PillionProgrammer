using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Internal.VisualStudio.Shell.Interop;
using Microsoft.VisualStudio.ComponentModelHost;
using Microsoft.VisualStudio.Imaging;
using Microsoft.VisualStudio.Language.Proposals;
using Microsoft.VisualStudio.Settings;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using Microsoft.VisualStudio.Threading;
using Pillion.VisualStudio.Core.Autocomplete;

namespace Pillion.VisualStudio.Autocomplete;

/// <summary>
/// Suggestion coexistence for the process (work item 1.4): detects GitHub Copilot and IntelliCode whole-line
/// completions when the first document view opens, remembers whose proposals have been seen, and shows the
/// once-per-machine info bar that writes <c>autocomplete.ordering</c>. It never changes another product's settings: the
/// bar's settings link opens that product's own Options page.
/// </summary>
internal sealed class CoexistenceFeature
{
    /// <summary>The environment variable that overrides the countermeasure for measurements: <c>off</c>, <c>replace</c> or <c>decline</c>.</summary>
    public const string CountermeasureVariable = "PILLION_AUTOCOMPLETE_COUNTERMEASURE";

    /// <summary>Copilot's completions switch in the settings manager (Visual Studio 18 keeps it as the legacy moniker of its "Inline Suggestions" provider).</summary>
    public const string CopilotCompletionsSetting = "Microsoft.VisualStudio.Conversations.AreCopilotCompletionsEnabled";

    /// <summary>IntelliCode's completions switch on Visual Studio 18 (legacy moniker of its "Inline Suggestions" provider).</summary>
    public const string IntelliCodeCompletionsSetting18 = "Microsoft.VisualStudio.IntelliCode.ShowIntellicodeCompletions";

    /// <summary>Visual Studio 18's list of enabled inline-suggestion providers (Text Editor &gt; Inline Suggestions &gt; General).</summary>
    public const string ProvidersSetting18 = "textEditor.codeCompletions.general.codeCompletionsProviders";

    /// <summary>IntelliCode's General Options page on Visual Studio 2022 (from its pkgdef).</summary>
    private const string IntelliCodeOptionsPage17 = "c6a0ae28-f8eb-330b-8012-35a3022dded0";

    private const string SettingsAction = "settings";

    private readonly AutocompleteFeature _feature;
    private int _started;
    private int _promptShown;
    private int _observed;
    private volatile CompetitorDetection _detection = CompetitorDetection.None;

    /// <summary>Initializes a new instance of the <see cref="CoexistenceFeature"/> class.</summary>
    /// <param name="feature">The package's autocomplete feature.</param>
    public CoexistenceFeature(AutocompleteFeature feature)
    {
        _feature = feature;
        Mode = CoexistencePolicy.ParseMode(Environment.GetEnvironmentVariable(CountermeasureVariable));
    }

    /// <summary>Gets the countermeasure used under <see cref="AutocompleteOrdering.PillionFirst"/>.</summary>
    public CountermeasureMode Mode { get; }

    /// <summary>Gets the start-up detection (nothing found until it has run).</summary>
    public CompetitorDetection Detection => _detection;

    /// <summary>Gets the competitors whose proposals have been displayed in any view of this process.</summary>
    public CompetitorKind Observed => (CompetitorKind)Volatile.Read(ref _observed);

    /// <summary>Gets a value indicating whether another inline-suggestion provider is present (detected and not switched off, or seen).</summary>
    public bool CompetitorPresent => _detection.Present != CompetitorKind.None || Observed != CompetitorKind.None;

    /// <summary>Runs the detection once, when the first document view asks for suggestions.</summary>
    /// <param name="joinableTaskFactory">The joinable task factory.</param>
    public void EnsureStarted(JoinableTaskFactory joinableTaskFactory)
    {
        if (Interlocked.Exchange(ref _started, 1) != 0)
        {
            return;
        }

        joinableTaskFactory.RunAsync(() => StartAsync(joinableTaskFactory)).FileAndForget("pillion/autocomplete/coexistence");
    }

    /// <summary>Records that a competitor's proposal was displayed; the first one may show the info bar.</summary>
    /// <param name="kind">Whose.</param>
    /// <param name="joinableTaskFactory">The joinable task factory.</param>
    public void CompetitorSeen(CompetitorKind kind, JoinableTaskFactory joinableTaskFactory)
    {
        var bit = (int)kind;
        int before;
        do
        {
            before = Volatile.Read(ref _observed);
            if ((before & bit) == bit)
            {
                return;
            }
        }
        while (Interlocked.CompareExchange(ref _observed, before | bit, before) != before);

        _feature.Log.Info("autocomplete.coexistence.seen", "Another provider's inline proposal was displayed.", ("provider", kind.ToString()));
        if (Volatile.Read(ref _started) != 0)
        {
            joinableTaskFactory.RunAsync(() => MaybePromptAsync(joinableTaskFactory)).FileAndForget("pillion/autocomplete/coexistenceprompt");
        }
    }

    private async Task StartAsync(JoinableTaskFactory joinableTaskFactory)
    {
        try
        {
            await joinableTaskFactory.SwitchToMainThreadAsync();
            _detection = Detect();
            _feature.Log.Info("autocomplete.coexistence.detected", "Suggestion coexistence: " + _detection.Describe() + ".", ("mode", Mode.ToString()));
            CoexistenceTrace.Write("detected", _detection.Describe());
            await MaybePromptAsync(joinableTaskFactory).ConfigureAwait(true);
        }
#pragma warning disable CA1031 // Detection must never break autocomplete; ghost text works without it.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            _feature.Log.Warning("autocomplete.coexistence.failed", "Suggestion coexistence detection failed.", ("error", ex.GetType().Name));
        }
    }

    private static CompetitorSetting ReadCopilot(ISettingsManager? settings, int major)
    {
        var legacy = CompetitorDetection.InterpretSetting(TryRead(settings, CopilotCompletionsSetting));
        return legacy != CompetitorSetting.Unknown || major < 18 ? legacy : FromProviders(settings, "copilot-completions");
    }

    private static CompetitorSetting ReadIntelliCode(ISettingsManager? settings, int major)
    {
        if (major is > 0 and < 18)
        {
            return IntelliCodeRequirement.InterpretShowCompletions(TryRead(settings, IntelliCodeRequirement.ShowCompletionsSetting)) switch
            {
                true => CompetitorSetting.On,
                false => CompetitorSetting.Off,
                _ => CompetitorSetting.Unknown,
            };
        }

        var legacy = CompetitorDetection.InterpretSetting(TryRead(settings, IntelliCodeCompletionsSetting18));
        return legacy != CompetitorSetting.Unknown ? legacy : FromProviders(settings, "intellicode");
    }

    /// <summary>Reads Visual Studio 18's provider list, when it has been set (an unset list means each provider's default).</summary>
    private static CompetitorSetting FromProviders(ISettingsManager? settings, string providerId) =>
        TryRead(settings, ProvidersSetting18) is System.Collections.IEnumerable list and not string
            ? list.Cast<object?>().Any(item => string.Equals(item?.ToString(), providerId, StringComparison.OrdinalIgnoreCase)) ? CompetitorSetting.On : CompetitorSetting.Off
            : CompetitorSetting.Unknown;

    private static object? TryRead(ISettingsManager? settings, string key)
    {
        if (settings is null)
        {
            return null;
        }

        try
        {
            return settings.TryGetValue(key, out object value) == GetValueResult.Success ? value : null;
        }
#pragma warning disable CA1031 // An unreadable setting counts as unknown.
        catch (Exception)
#pragma warning restore CA1031
        {
            return null;
        }
    }

    private static CompetitorDetection Detect()
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        var major = IntelliCodeCheck.VisualStudioMajorVersion;
        var names = new List<string?>();
        if (Package.GetGlobalService(typeof(SComponentModel)) is IComponentModel componentModel)
        {
            foreach (var export in componentModel.DefaultExportProvider.GetExports<ProposalSourceProviderBase, IDictionary<string, object>>())
            {
                names.Add(export.Metadata.TryGetValue("Name", out var name) ? name as string : null);
            }
        }

        var settings = Package.GetGlobalService(typeof(SVsSettingsPersistenceManager)) as ISettingsManager;
        return CompetitorDetection.FromSourceNames(major, names, ReadCopilot(settings, major), ReadIntelliCode(settings, major));
    }

    private async Task MaybePromptAsync(JoinableTaskFactory joinableTaskFactory)
    {
        await joinableTaskFactory.SwitchToMainThreadAsync();
        var competitors = CoexistencePrompt.ToPrompt(_feature.Availability.Settings, _detection, Observed, Volatile.Read(ref _promptShown) != 0);
        if (competitors == CompetitorKind.None || Interlocked.Exchange(ref _promptShown, 1) != 0)
        {
            return;
        }

        if (await AsyncServiceProvider.GlobalProvider.GetServiceAsync(typeof(SVsInfoBarUIFactory)).ConfigureAwait(true) is not IVsInfoBarUIFactory factory
            || await AsyncServiceProvider.GlobalProvider.GetServiceAsync(typeof(SVsShell)).ConfigureAwait(true) is not IVsShell shell
            || !Microsoft.VisualStudio.ErrorHandler.Succeeded(shell.GetProperty((int)__VSSPROPID7.VSSPROPID_MainWindowInfoBarHost, out var hostObject))
            || hostObject is not IVsInfoBarHost host)
        {
            _feature.Log.Warning("autocomplete.coexistence.infobar", "Could not show the coexistence info bar.");
            return;
        }

        var actions = new List<IVsInfoBarActionItem>();
        foreach (var choice in new[] { CoexistenceChoice.PillionFirst, CoexistenceChoice.CompetitorFirst, CoexistenceChoice.OnDemand, CoexistenceChoice.KeepBoth })
        {
            actions.Add(new InfoBarHyperlink(CoexistencePrompt.Label(choice, competitors), choice));
        }

        actions.Add(new InfoBarHyperlink(CompetitorProposals.DisplayName(competitors) + " settings", SettingsAction));
        actions.Add(new InfoBarHyperlink(CoexistencePrompt.Label(CoexistenceChoice.DontShowAgain, competitors), CoexistenceChoice.DontShowAgain));
        var model = new InfoBarModel(
            [new InfoBarTextSpan("Pillion Programmer: " + CoexistencePrompt.Message(competitors) + " ")],
            actions,
            KnownMonikers.StatusInformation,
            isCloseButtonVisible: true);
        var element = factory.CreateInfoBar(model);
        var events = new InfoBarEvents(this, competitors, joinableTaskFactory);
        element.Advise(events, out var cookie);
        events.Cookie = cookie;
        host.AddInfoBar(element);
        _feature.Log.Info("autocomplete.coexistence.prompt", "Showed the coexistence info bar.", ("competitors", competitors.ToString()));
        CoexistenceTrace.Write("prompt.shown", competitors.ToString());
    }

    private async Task AnswerAsync(CoexistenceChoice choice, CompetitorKind competitors)
    {
        var answer = CoexistencePrompt.Apply(choice);
        _feature.Log.Info("autocomplete.coexistence.answer", "Coexistence info bar answered.", ("choice", choice.ToString()), ("competitors", competitors.ToString()));
        CoexistenceTrace.Write("prompt.answer", choice.ToString());
        if (!answer.MarkDone && answer.Ordering is null)
        {
            return;
        }

        try
        {
            var service = _feature.Connection.GetProxy<Pillion.Contracts.ISettingsService>();
            if (answer.Ordering is { } ordering)
            {
                await service.SetAsync(
                    AutocompleteSettings.SectionKey + "." + AutocompleteSettings.OrderingProperty,
                    "\"" + AutocompleteOrderingText.ToSettingValue(ordering) + "\"",
                    CancellationToken.None).ConfigureAwait(false);
            }

            if (answer.MarkDone)
            {
                await service.SetAsync(AutocompleteSettings.SectionKey + "." + AutocompleteSettings.CoexistencePromptDoneProperty, "true", CancellationToken.None).ConfigureAwait(false);
            }

            await _feature.Availability.RefreshAsync(CancellationToken.None).ConfigureAwait(false);
        }
#pragma warning disable CA1031 // The host may be away; the answer is logged and the bar shows again next time.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            _feature.Log.Warning("autocomplete.coexistence.save.failed", "Could not save the coexistence choice.", ("error", ex.GetType().Name));
        }
    }

    private void OpenCompetitorSettings(CompetitorKind competitors)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        if (Package.GetGlobalService(typeof(EnvDTE.DTE)) is not EnvDTE.DTE dte)
        {
            return;
        }

        try
        {
            if (IntelliCodeCheck.VisualStudioMajorVersion is > 0 and < 18 && (competitors & CompetitorKind.IntelliCode) != 0)
            {
                dte.ExecuteCommand("Tools.Options", IntelliCodeOptionsPage17);
            }
            else
            {
                dte.ExecuteCommand("Tools.Options");
            }
        }
        catch (System.Runtime.InteropServices.COMException ex)
        {
            _feature.Log.Debug("autocomplete.coexistence.settings", "Could not open the Options page.", ("error", ex.Message));
        }
    }

    /// <summary>The info bar's events: a choice writes the setting and closes the bar.</summary>
    private sealed class InfoBarEvents(CoexistenceFeature owner, CompetitorKind competitors, JoinableTaskFactory joinableTaskFactory) : IVsInfoBarUIEvents
    {
        private bool _answered;

        public uint Cookie { get; set; }

        public void OnActionItemClicked(IVsInfoBarUIElement infoBarUIElement, IVsInfoBarActionItem actionItem)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            if (actionItem.ActionContext is string action && action == SettingsAction)
            {
                owner.OpenCompetitorSettings(competitors);
                return;
            }

            if (actionItem.ActionContext is CoexistenceChoice choice)
            {
                _answered = true;
                joinableTaskFactory.RunAsync(() => owner.AnswerAsync(choice, competitors)).FileAndForget("pillion/autocomplete/coexistenceanswer");
                infoBarUIElement.Close();
            }
        }

        public void OnClosed(IVsInfoBarUIElement infoBarUIElement)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            infoBarUIElement.Unadvise(Cookie);
            if (!_answered)
            {
                joinableTaskFactory.RunAsync(() => owner.AnswerAsync(CoexistenceChoice.Closed, competitors)).FileAndForget("pillion/autocomplete/coexistenceanswer");
            }
        }
    }
}

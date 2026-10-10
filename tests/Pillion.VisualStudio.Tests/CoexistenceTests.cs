using System;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.VisualStudio.Threading;
using Newtonsoft.Json.Linq;
using Pillion.Contracts;
using Pillion.VisualStudio.Core;
using Pillion.VisualStudio.Core.Autocomplete;

namespace Pillion.VisualStudio.Tests;

// The tests await their own view models' tasks; no main thread is involved.
#pragma warning disable VSTHRD003

/// <summary>
/// Suggestion coexistence (work item 1.4): the ordering setting, the proposal manager and source rules per ordering,
/// the countermeasure against a competitor's queued proposal (a fake proposal timeline), "Pillion: Suggest", the
/// once-per-machine info bar, the competitor detection and the <c>displaced</c> outcome.
/// </summary>
public sealed class CoexistenceTests : IDisposable
{
    private static readonly CaretKey At10 = new(5, 10);
    private static readonly CaretKey At11 = new(6, 11);
    private readonly JoinableTaskContext _context = Task.Run(() => new JoinableTaskContext()).Result;
    private DateTimeOffset _now = new(2026, 10, 10, 12, 0, 0, TimeSpan.Zero);

    public void Dispose() => _context.Dispose();

    [Theory]
    [InlineData(null, AutocompleteOrdering.PillionFirst)]
    [InlineData("", AutocompleteOrdering.PillionFirst)]
    [InlineData("pillionFirst", AutocompleteOrdering.PillionFirst)]
    [InlineData("competitorFirst", AutocompleteOrdering.CompetitorFirst)]
    [InlineData(" CompetitorFirst ", AutocompleteOrdering.CompetitorFirst)]
    [InlineData("onDemand", AutocompleteOrdering.OnDemand)]
    [InlineData("somethingElse", AutocompleteOrdering.PillionFirst)]
    public void Ordering_Parses_WithPillionFirstAsTheDefault(string? value, AutocompleteOrdering expected)
    {
        Assert.Equal(expected, AutocompleteOrderingText.Parse(value));
        Assert.Equal(expected, AutocompleteOrderingText.Parse(AutocompleteOrderingText.ToSettingValue(expected)));
    }

    [Fact]
    public void Settings_ReadTheOrderingAndThePromptFlag_FromTheAutocompleteSection()
    {
        var defaults = AutocompleteSettings.FromJson(enabled: true, sectionJson: null);
        Assert.Equal(AutocompleteOrdering.PillionFirst, defaults.Ordering);
        Assert.False(defaults.CoexistencePromptDone);

        var set = AutocompleteSettings.FromJson(true, "{\"ordering\":\"onDemand\",\"coexistencePromptDone\":true,\"disabledLanguages\":[\"python\"]}");
        Assert.Equal(AutocompleteOrdering.OnDemand, set.Ordering);
        Assert.True(set.CoexistencePromptDone);
        Assert.Equal(["python"], set.DisabledLanguages);

        var wrongTypes = AutocompleteSettings.FromJson(true, "{\"ordering\":3,\"coexistencePromptDone\":\"yes\"}");
        Assert.Equal(AutocompleteOrdering.PillionFirst, wrongTypes.Ordering);
        Assert.False(wrongTypes.CoexistencePromptDone);
    }

    [Fact]
    public void Availability_RaisesChanged_WhenTheOrderingChanges()
    {
        using var availability = new AutocompleteAvailability(new FakeHostConnection());
        var changes = 0;
        availability.Changed += (_, _) => changes++;

        availability.Apply(new AutocompleteSettings(true, [], true, AutocompleteOrdering.CompetitorFirst));
        availability.Apply(new AutocompleteSettings(true, [], true, AutocompleteOrdering.CompetitorFirst));
        availability.Apply(new AutocompleteSettings(true, [], true, AutocompleteOrdering.CompetitorFirst, coexistencePromptDone: true));

        Assert.Equal(2, changes);
    }

    [Theory]
    [InlineData(AutocompleteOrdering.PillionFirst, false, false, true, SourceAnswer.Request)]
    [InlineData(AutocompleteOrdering.PillionFirst, true, false, true, SourceAnswer.Request)]
    [InlineData(AutocompleteOrdering.CompetitorFirst, false, false, true, SourceAnswer.Request)]
    [InlineData(AutocompleteOrdering.CompetitorFirst, true, false, false, SourceAnswer.RequestInBackground)]
    [InlineData(AutocompleteOrdering.OnDemand, false, false, false, SourceAnswer.Nothing)]
    [InlineData(AutocompleteOrdering.OnDemand, true, false, false, SourceAnswer.Nothing)]
    [InlineData(AutocompleteOrdering.OnDemand, true, true, true, SourceAnswer.FromKept)]
    [InlineData(AutocompleteOrdering.CompetitorFirst, true, true, true, SourceAnswer.FromKept)]
    public void ManagerAndSource_FollowTheOrdering(AutocompleteOrdering ordering, bool competitorPresent, bool kept, bool managerProposes, SourceAnswer source)
    {
        Assert.Equal(managerProposes, CoexistencePolicy.ManagerProposes(ordering, competitorPresent, kept));
        Assert.Equal(source, CoexistencePolicy.Source(ordering, competitorPresent, kept));
    }

    [Theory]
    [InlineData(null, CountermeasureMode.Replace)]
    [InlineData("off", CountermeasureMode.Off)]
    [InlineData("DECLINE", CountermeasureMode.Decline)]
    [InlineData("replace", CountermeasureMode.Replace)]
    public void CountermeasureOverride_Parses(string? value, CountermeasureMode expected) =>
        Assert.Equal(expected, CoexistencePolicy.ParseMode(value));

    [Fact]
    public void QueuedCompetitor_AfterOursDiverges_IsADisplacement_AndOursIsReRequestedAndPushed()
    {
        var timeline = Timeline();

        timeline.PillionShown(7);
        Advance(150);
        timeline.PillionRejected(7, byUserCycling: false); // The keystroke did not match ours.
        Advance(8); // The broker shows the competitor's queued proposal about 8 ms later.
        var shown = timeline.CompetitorShown(CompetitorKind.Copilot, At11);

        Assert.Equal(7, shown.DisplacedAttempt);
        Assert.True(shown.Has(CoexistenceActions.RequestPillion));
        Assert.False(shown.Has(CoexistenceActions.DismissCompetitor)); // Replace keeps it until ours is ready.
        Assert.True(timeline.CompetitorDisplayed);
        Assert.Equal(CompetitorKind.Copilot, timeline.Observed);

        var ready = timeline.PillionReady(At11, At11, hasProposal: true);
        Assert.True(ready.Has(CoexistenceActions.PushPillion));

        timeline.CompetitorGone(); // The push dismissed it.
        timeline.PillionShown(8);
        Assert.Equal(8, timeline.PillionDisplayed);
        Assert.False(timeline.CompetitorDisplayed);
    }

    [Fact]
    public void CompetitorShownWhileOursIsDisplayed_IsADisplacement_CountedOnce()
    {
        var timeline = Timeline();
        timeline.PillionShown(3);

        var first = timeline.CompetitorShown(CompetitorKind.IntelliCode, At10);
        var again = timeline.CompetitorShown(CompetitorKind.IntelliCode, At10); // The same proposal re-shown.

        Assert.Equal(3, first.DisplacedAttempt);
        Assert.Equal(0, again.DisplacedAttempt);
    }

    [Fact]
    public void CompetitorShownLongAfterOursWasRejected_IsNotADisplacement()
    {
        var timeline = Timeline();
        timeline.PillionShown(4);
        timeline.PillionRejected(4, byUserCycling: false);
        Advance(1000);

        Assert.Equal(0, timeline.CompetitorShown(CompetitorKind.Copilot, At10).DisplacedAttempt);
    }

    [Fact]
    public void AcceptedOrDismissed_IsNotFollowedByADisplacement()
    {
        var timeline = Timeline();
        timeline.PillionShown(5);
        timeline.PillionGone(5);
        Advance(10);

        Assert.Equal(0, timeline.CompetitorShown(CompetitorKind.Copilot, At10).DisplacedAttempt);
    }

    [Fact]
    public void PillionWithNothing_LeavesTheCompetitor_NoStarvation()
    {
        var timeline = Timeline();
        timeline.CompetitorShown(CompetitorKind.Copilot, At10);

        var ready = timeline.PillionReady(At10, At10, hasProposal: false);

        Assert.Equal(CoexistenceActions.None, ready.Actions);
        Assert.True(timeline.CompetitorDisplayed);
    }

    [Fact]
    public void StaleSuggestion_ForAnotherCaret_IsNotPushed()
    {
        var timeline = Timeline();
        timeline.CompetitorShown(CompetitorKind.Copilot, At10);

        Assert.Equal(CoexistenceActions.None, timeline.PillionReady(At10, At11, hasProposal: true).Actions);
    }

    [Fact]
    public void TypingThroughTheCompetitor_ReRequestsPillion_AtMostOnePushPerPosition()
    {
        var timeline = Timeline();
        timeline.CompetitorShown(CompetitorKind.Copilot, At10);

        Assert.True(timeline.TextChanged(At11).Has(CoexistenceActions.RequestPillion)); // The broker asks no source now.
        Assert.True(timeline.PillionReady(At11, At11, true).Has(CoexistenceActions.PushPillion));

        // The competitor came back at the same position (ours was not shown): no second fight.
        timeline.CompetitorShown(CompetitorKind.Copilot, At11);
        Assert.Equal(CoexistenceActions.None, timeline.PillionReady(At11, At11, true).Actions);
        Assert.Equal(CoexistenceActions.None, timeline.TextChanged(At11).Actions);
    }

    [Fact]
    public void UserCyclingToTheCompetitor_IsNotADisplacement_AndIsRespectedUntilTheTextChanges()
    {
        var timeline = Timeline();
        timeline.PillionShown(9);
        timeline.PillionRejected(9, byUserCycling: true); // Alt+. to the competitor's proposal.

        var shown = timeline.CompetitorShown(CompetitorKind.Copilot, At10);

        Assert.Equal(0, shown.DisplacedAttempt);
        Assert.Equal(CoexistenceActions.None, shown.Actions);
        Assert.Equal(CoexistenceActions.None, timeline.PillionReady(At10, At10, true).Actions);

        Assert.True(timeline.TextChanged(At11).Has(CoexistenceActions.RequestPillion));
    }

    [Fact]
    public void CountermeasureOff_RecordsTheDisplacement_ButDoesNothing()
    {
        var timeline = Timeline(mode: CountermeasureMode.Off);
        timeline.PillionShown(1);

        var shown = timeline.CompetitorShown(CompetitorKind.Copilot, At10);

        Assert.Equal(1, shown.DisplacedAttempt);
        Assert.Equal(CoexistenceActions.None, shown.Actions);
        Assert.Equal(CoexistenceActions.None, timeline.PillionReady(At10, At10, true).Actions);
        Assert.Equal(CoexistenceActions.None, timeline.TextChanged(At11).Actions);
    }

    [Fact]
    public void Decline_DismissesAtOnce_AndAsksEverySourceAgainWhenPillionHasNothing()
    {
        var timeline = Timeline(mode: CountermeasureMode.Decline);

        var shown = timeline.CompetitorShown(CompetitorKind.Copilot, At10);
        Assert.True(shown.Has(CoexistenceActions.DismissCompetitor));
        Assert.True(shown.Has(CoexistenceActions.RequestPillion));
        timeline.CompetitorGone();

        Assert.True(timeline.PillionReady(At10, At10, hasProposal: false).Has(CoexistenceActions.RequestAll));
    }

    [Fact]
    public void CompetitorFirst_DefersWhileTheCompetitorShows_AndFillsInOnlyWhenNothingIsDisplayed()
    {
        var timeline = Timeline(AutocompleteOrdering.CompetitorFirst);

        var shown = timeline.CompetitorShown(CompetitorKind.IntelliCode, At10);
        Assert.Equal(CoexistenceActions.None, shown.Actions);
        Assert.Equal(CoexistenceActions.None, timeline.PillionReady(At10, At10, true).Actions);

        timeline.CompetitorGone();
        Assert.True(timeline.PillionReady(At10, At10, true).Has(CoexistenceActions.PushPillion));
        Assert.Equal(CoexistenceActions.None, timeline.PillionReady(At10, At10, true).Actions); // Once per position.
        Assert.Equal(CoexistenceActions.None, timeline.PillionReady(At11, At11, false).Actions);
    }

    [Fact]
    public void OnDemand_HasNoAutomaticPushes()
    {
        var timeline = Timeline(AutocompleteOrdering.OnDemand);

        Assert.Equal(CoexistenceActions.None, timeline.CompetitorShown(CompetitorKind.Copilot, At10).Actions);
        Assert.Equal(CoexistenceActions.None, timeline.PillionReady(At10, At10, true).Actions);
        timeline.CompetitorGone();
        Assert.Equal(CoexistenceActions.None, timeline.PillionReady(At10, At10, true).Actions);
    }

    [Theory]
    [InlineData(AutocompleteOrdering.OnDemand)]
    [InlineData(AutocompleteOrdering.CompetitorFirst)]
    [InlineData(AutocompleteOrdering.PillionFirst)]
    public void SuggestCommand_ArmsTheCaret_AndShowsTheSuggestionWhenReady_WhateverIsDisplayed(AutocompleteOrdering ordering)
    {
        var timeline = Timeline(ordering);
        timeline.CompetitorShown(CompetitorKind.Copilot, At10);

        var requested = timeline.SuggestRequested(At10);
        Assert.True(requested.Has(CoexistenceActions.RequestPillion));
        Assert.Equal(At10, timeline.ArmedAt);

        Assert.True(timeline.PillionReady(At10, At10, hasProposal: true).Has(CoexistenceActions.PushPillion));
        Assert.Null(timeline.ArmedAt);
    }

    [Fact]
    public void SuggestCommand_DoesNothing_WhenTheCaretMovedOrTheModelHadNothing_AndExpires()
    {
        var timeline = Timeline(AutocompleteOrdering.OnDemand);

        timeline.SuggestRequested(At10);
        Assert.Equal(CoexistenceActions.None, timeline.PillionReady(At10, At11, true).Actions);
        Assert.Equal(At10, timeline.ArmedAt);
        Assert.Equal(CoexistenceActions.None, timeline.PillionReady(At10, At10, false).Actions);
        Assert.Null(timeline.ArmedAt);

        timeline.SuggestRequested(At11);
        Advance((int)CoexistenceTimeline.ArmedFor.TotalMilliseconds + 1);
        Assert.Null(timeline.ArmedAt);
        Assert.Equal(CoexistenceActions.None, timeline.PillionReady(At11, At11, true).Actions);
    }

    [Fact]
    public void Prompt_IsShownOncePerMachine_ForIntelliCodeOn2022AtStartUp_AndForCopilotWhenSeen()
    {
        var notDone = new AutocompleteSettings(true, [], true);
        var done = new AutocompleteSettings(true, [], true, coexistencePromptDone: true);
        var vs17 = new CompetitorDetection(17, copilotPresent: false, CompetitorSetting.Unknown, intelliCodePresent: true, CompetitorSetting.On);
        var vs18 = new CompetitorDetection(18, copilotPresent: true, CompetitorSetting.Unknown, intelliCodePresent: true, CompetitorSetting.Unknown);

        Assert.Equal(CompetitorKind.IntelliCode, CoexistencePrompt.ToPrompt(notDone, vs17, CompetitorKind.None, shownThisSession: false));
        Assert.Equal(CompetitorKind.None, CoexistencePrompt.ToPrompt(done, vs17, CompetitorKind.None, shownThisSession: false));
        Assert.Equal(CompetitorKind.None, CoexistencePrompt.ToPrompt(notDone, vs17, CompetitorKind.None, shownThisSession: true));

        // Copilot's sign-in state is not readable: wait for one of its proposals.
        Assert.Equal(CompetitorKind.None, CoexistencePrompt.ToPrompt(notDone, vs18, CompetitorKind.None, shownThisSession: false));
        Assert.Equal(CompetitorKind.Copilot, CoexistencePrompt.ToPrompt(notDone, vs18, CompetitorKind.Copilot, shownThisSession: false));

        // Switched off in its own settings: not named, even if seen earlier.
        var copilotOff = new CompetitorDetection(18, true, CompetitorSetting.Off, false, CompetitorSetting.Unknown);
        Assert.Equal(CompetitorKind.None, CoexistencePrompt.ToPrompt(notDone, copilotOff, CompetitorKind.Copilot, false));

        // Autocomplete switched off by settings or policy: no prompt.
        Assert.Equal(CompetitorKind.None, CoexistencePrompt.ToPrompt(new AutocompleteSettings(false, [], true), vs17, CompetitorKind.None, false));
    }

    [Fact]
    public void Prompt_Choices_WriteTheOrdering_AndTheFlag()
    {
        Assert.Equal("Copilot inline suggestions are also on. Choose:", CoexistencePrompt.Message(CompetitorKind.Copilot));
        Assert.Equal("Copilot first", CoexistencePrompt.Label(CoexistenceChoice.CompetitorFirst, CompetitorKind.Copilot));
        Assert.Equal("IntelliCode first", CoexistencePrompt.Label(CoexistenceChoice.CompetitorFirst, CompetitorKind.IntelliCode));
        Assert.Equal("Copilot and IntelliCode", CompetitorProposals.DisplayName(CompetitorKind.Copilot | CompetitorKind.IntelliCode));

        AssertAnswer(CoexistenceChoice.PillionFirst, AutocompleteOrdering.PillionFirst, markDone: true);
        AssertAnswer(CoexistenceChoice.CompetitorFirst, AutocompleteOrdering.CompetitorFirst, markDone: true);
        AssertAnswer(CoexistenceChoice.OnDemand, AutocompleteOrdering.OnDemand, markDone: true);
        AssertAnswer(CoexistenceChoice.KeepBoth, null, markDone: true);
        AssertAnswer(CoexistenceChoice.DontShowAgain, null, markDone: true);
        AssertAnswer(CoexistenceChoice.Closed, null, markDone: false);
    }

    [Fact]
    public void Detection_UsesTheProposalSourceNames_AndTheSettings()
    {
        var names = new[] { "PillionProgrammer.ProposalSource", "CompletionsProposalSourceProvider", "IntelliCodeVCProposalSource", "DummyInlineProposalSourceProvider", "IntelliCodeCSharpProposalSource" };
        var vs18 = CompetitorDetection.FromSourceNames(18, names, CompetitorSetting.Unknown, CompetitorSetting.Unknown);
        Assert.True(vs18.CopilotPresent);
        Assert.True(vs18.IntelliCodePresent);
        Assert.Equal(CompetitorKind.Copilot | CompetitorKind.IntelliCode, vs18.Present);
        Assert.Equal(CompetitorKind.None, vs18.ActiveAtStartup);

        var vs17 = CompetitorDetection.FromSourceNames(17, ["PillionProgrammer.ProposalSource", "IntelliCodeCSharpProposalSource"], CompetitorSetting.Unknown, CompetitorSetting.On);
        Assert.Equal(CompetitorKind.IntelliCode, vs17.Present);
        Assert.Equal(CompetitorKind.IntelliCode, vs17.ActiveAtStartup);
        Assert.Contains("IntelliCode source present", vs17.Describe(), StringComparison.Ordinal);

        var off = CompetitorDetection.FromSourceNames(17, ["IntelliCodeCSharpProposalSource"], CompetitorSetting.Unknown, CompetitorSetting.Off);
        Assert.Equal(CompetitorKind.None, off.Present);
        Assert.Equal(CompetitorKind.None, off.ActiveAtStartup);
        Assert.Equal(CompetitorKind.None, CompetitorDetection.FromSourceNames(18, [null, "Something"], CompetitorSetting.On, CompetitorSetting.On).Present);
    }

    [Theory]
    [InlineData(null, CompetitorSetting.Unknown)]
    [InlineData(true, CompetitorSetting.On)]
    [InlineData(false, CompetitorSetting.Off)]
    [InlineData(0, CompetitorSetting.Unknown)]
    [InlineData(1, CompetitorSetting.On)]
    [InlineData(2, CompetitorSetting.Off)]
    [InlineData("true", CompetitorSetting.On)]
    [InlineData("2", CompetitorSetting.Off)]
    [InlineData(1.5, CompetitorSetting.Unknown)]
    public void Detection_InterpretsStoredSettings(object? value, CompetitorSetting expected) =>
        Assert.Equal(expected, CompetitorDetection.InterpretSetting(value));

    [Theory]
    [InlineData("cptac999017-2bd5-416b-ba45-4d3b407169e7", CompetitorKind.Copilot)]
    [InlineData("nes1234", CompetitorKind.Copilot)]
    [InlineData("5631a880-e03b-46c7-b5ae-76bfbe7615c5:0", CompetitorKind.IntelliCode)]
    [InlineData("pillion:12:0", CompetitorKind.None)]
    [InlineData("", CompetitorKind.None)]
    [InlineData(null, CompetitorKind.None)]
    [InlineData("someone-else", CompetitorKind.Other)]
    public void ProposalIds_AreClassified(string? id, CompetitorKind expected) =>
        Assert.Equal(expected, CompetitorProposals.Classify(id));

    [Fact]
    public void DisplacedOutcome_IsCounted_ReportedAndLogged_AndAnAcceptLaterStillWins()
    {
        var clock = new ManualClock();
        var metrics = new AutocompleteMetrics(clock, new LatencyWindow(() => clock.UtcNow));
        foreach (var id in new long[] { 1, 2, 3 })
        {
            metrics.RequestStarted(id, "csharp");
            metrics.RequestSent(id, multiLine: false, degraded: false);
            metrics.SuggestionReady(id, TimeSpan.FromMilliseconds(300));
            metrics.RequestEnded(id);
            metrics.SuggestionShown(id);
        }

        metrics.SuggestionRejected(1);
        metrics.SuggestionDisplaced(1);
        metrics.SuggestionDisplaced(2);
        metrics.SuggestionAccepted(2); // Typed through the competitor's and then accepted ours (a push).
        metrics.SuggestionRejected(3);

        var recent = metrics.Snapshot().Recent;
        Assert.Equal(3, recent.Shown);
        Assert.Equal(1, recent.Displaced);
        Assert.Equal(1, recent.Accepted);
        Assert.Equal(1, recent.Rejected);
        Assert.Equal(AutocompleteOutcome.Displaced, metrics.Records().Single(r => r.Attempt == 1).Outcome);

        Assert.Contains("1 displaced by another provider", AutocompleteMetricsText.Describe(metrics.Snapshot(), host: null), StringComparison.Ordinal);
        var json = JObject.Parse(AutocompleteMetricsJson.Create(clock.UtcNow, clock.UtcNow, recent, degradation: null, host: null));
        Assert.Equal(1, (long)json["displaced"]!);
    }

    [Fact]
    public async Task OptionsPage_RoundTrip_KeepsUnknownProperties()
    {
        var settings = new InMemorySettingsService();
        settings.Values["autocomplete"] = "{\"disabledLanguages\":[\"python\"],\"announceSuggestions\":false,\"futureKey\":1}";
        using var vm = new AutocompleteOptionsViewModel(new FakeHostConnection().With<ISettingsService>(settings), _context.Factory);

        await vm.LoadAsync();

        Assert.Equal(OptionsLoadState.Loaded, vm.LoadState);
        Assert.Equal(AutocompleteOrdering.PillionFirst, vm.Ordering.Ordering);
        Assert.Equal("python", vm.DisabledLanguages);
        Assert.False(vm.AnnounceSuggestions);
        Assert.True(vm.AskAgain);
        Assert.Equal(3, vm.Orderings.Count);
        Assert.False(vm.IsDirty);

        vm.Ordering = vm.Orderings.Single(o => o.Ordering == AutocompleteOrdering.OnDemand);
        vm.DisabledLanguages = "python; xaml, python";
        vm.AskAgain = false;
        Assert.True(vm.IsDirty);
        await vm.SaveAsync();

        Assert.Equal("Saved.", vm.StatusMessage);
        var saved = JObject.Parse(settings.Values["autocomplete"]);
        Assert.Equal("onDemand", (string?)saved["ordering"]);
        Assert.Equal(["python", "xaml"], saved["disabledLanguages"]!.Select(t => (string?)t));
        Assert.False((bool)saved["announceSuggestions"]!);
        Assert.True((bool)saved["coexistencePromptDone"]!);
        Assert.Equal(1, (int)saved["futureKey"]!);

        var read = AutocompleteSettings.FromJson(true, settings.Values["autocomplete"]);
        Assert.Equal(AutocompleteOrdering.OnDemand, read.Ordering);
        Assert.True(read.CoexistencePromptDone);
    }

    private static void AssertAnswer(CoexistenceChoice choice, AutocompleteOrdering? ordering, bool markDone)
    {
        var answer = CoexistencePrompt.Apply(choice);
        Assert.Equal(ordering, answer.Ordering);
        Assert.Equal(markDone, answer.MarkDone);
    }

    private CoexistenceTimeline Timeline(AutocompleteOrdering ordering = AutocompleteOrdering.PillionFirst, CountermeasureMode mode = CountermeasureMode.Replace) =>
        new(() => _now) { Ordering = ordering, Mode = mode };

    private void Advance(int milliseconds) => _now += TimeSpan.FromMilliseconds(milliseconds);
}

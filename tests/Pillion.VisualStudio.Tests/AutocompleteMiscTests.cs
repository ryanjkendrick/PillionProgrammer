using System;
using System.Threading.Tasks;
using Pillion.Contracts;
using Pillion.VisualStudio.Core.Autocomplete;

namespace Pillion.VisualStudio.Tests;

public sealed class AutocompleteMiscTests
{
    [Fact]
    public void ProposalIds_RoundTripAndIdentifyPillion()
    {
        var id = PillionProposalId.Create(42, 1);

        Assert.Equal("pillion:42:1", id);
        Assert.True(PillionProposalId.IsPillion(id));
        Assert.True(PillionProposalId.TryParse(id, out var attempt, out var variant));
        Assert.Equal(42, attempt);
        Assert.Equal(1, variant);
        Assert.False(PillionProposalId.IsPillion("cpt-123"));
        Assert.False(PillionProposalId.IsPillion(null));
        Assert.False(PillionProposalId.TryParse("pillion:x", out _, out _));
        Assert.False(PillionProposalId.TryParse("4b6d1f2e:0", out _, out _));
    }

    [Theory]
    [InlineData("Calculator.Sum(numbers));", 10)]
    [InlineData(".Sum(numbers));", 1)]
    [InlineData("  total += n;", 7)]
    [InlineData("));", 2)]
    [InlineData("\n        return x;", 15)]
    [InlineData("\r\n    {", 7)]
    [InlineData("   ", 3)]
    [InlineData("", 0)]
    public void NextWord_TakesWhitespaceThenOneWordOrPunctuationRun(string suggestion, int expected)
    {
        Assert.Equal(expected, WordBoundary.NextWordLength(suggestion));
    }

    [Fact]
    public void ContinuationCache_OffersTheRestWhileTheTypedTextMatches()
    {
        var cache = new ContinuationCache();
        const string Before = "    var x = ";
        cache.Remember("a.cs", new StringDocumentText(Before + "\n"), Before.Length, "Math.Min(a, b);", attempt: 7);

        // Typed "Math." since.
        Assert.True(cache.TryGetRemainder("a.cs", new StringDocumentText(Before + "Math.\n"), Before.Length + 5, out var rest, out var attempt));
        Assert.Equal("Min(a, b);", rest);
        Assert.Equal(7, attempt);

        // Typed something else.
        Assert.False(cache.TryGetRemainder("a.cs", new StringDocumentText(Before + "Mx\n"), Before.Length + 2, out _, out _));

        // Another document, or the caret before the anchor.
        Assert.False(cache.TryGetRemainder("b.cs", new StringDocumentText(Before + "Math.\n"), Before.Length + 5, out _, out _));
        Assert.False(cache.TryGetRemainder("a.cs", new StringDocumentText(Before + "\n"), Before.Length - 1, out _, out _));

        // Typed it all: nothing left.
        Assert.False(cache.TryGetRemainder("a.cs", new StringDocumentText(Before + "Math.Min(a, b);\n"), Before.Length + 15, out _, out _));

        cache.Clear();
        Assert.False(cache.TryGetRemainder("a.cs", new StringDocumentText(Before + "Math.\n"), Before.Length + 5, out _, out _));
    }

    [Fact]
    public void ContinuationCache_RejectsAnEditBeforeTheAnchor()
    {
        var cache = new ContinuationCache();
        const string Before = "    var x = ";
        cache.Remember("a.cs", new StringDocumentText(Before), Before.Length, "1;", attempt: 1);

        Assert.False(cache.TryGetRemainder("a.cs", new StringDocumentText("    var y = "), Before.Length, out _, out _));
    }

    [Fact]
    public void ContextCapture_IncludesVirtualSpaceAndNeighbouringLines()
    {
        const string Code = "class C\n{\n    void M()\n    {\n\n    }\n}\n";
        var caret = Code.IndexOf("\n\n", StringComparison.Ordinal) + 1;

        var context = ContextCapture.Capture(new StringDocumentText(Code), caret, virtualSpaces: 8);

        Assert.Equal(4, context.CaretLine);
        Assert.Equal(8, context.CaretColumn);
        Assert.EndsWith("    {\n        ", context.Prefix, StringComparison.Ordinal);
        Assert.Equal("        ", context.LineBeforeCaret);
        Assert.Equal(string.Empty, context.LineAfterCaret);
        Assert.Equal("    {", context.PreviousNonBlankLine);
        Assert.Equal(["    }", "}", string.Empty], context.LinesBelow);
    }

    [Fact]
    public void StringDocumentText_HandlesAllLineBreaks()
    {
        var text = new StringDocumentText("a\r\nbb\ncc\rd");

        Assert.Equal(4, text.LineCount);
        Assert.Equal("bb", text.GetLineText(1));
        Assert.Equal("cc", text.GetLineText(2));
        Assert.Equal(2, text.GetLineNumber(text.GetLineStart(2)));
        Assert.Equal(1, text.GetLineNumber(4));
    }

    [Theory]
    [InlineData(null, true)]
    [InlineData("{}", true)]
    [InlineData("{\"chat\":false}", true)]
    [InlineData("{\"autocomplete\":true}", true)]
    [InlineData("{\"autocomplete\":false}", false)]
    [InlineData("{\"autocomplete\":\"no\"}", true)]
    public void FeatureFlag_IsOnUnlessExplicitlyOff(string? json, bool expected)
    {
        Assert.Equal(expected, AutocompleteSettings.ReadFeatureFlag(json));
    }

    [Fact]
    public void Settings_ReadTheSection()
    {
        var settings = AutocompleteSettings.FromJson(enabled: true, "{\"disabledLanguages\":[\"python\",\" PYTHON \",\"typescript\",3,\"\"],\"announceSuggestions\":false}");

        Assert.Equal(["python", "typescript"], settings.DisabledLanguages);
        Assert.False(settings.AnnounceSuggestions);
        Assert.True(AutocompleteSettings.FromJson(enabled: false, null).AnnounceSuggestions);
        Assert.Empty(AutocompleteSettings.FromJson(enabled: true, "not json").DisabledLanguages);
    }

    [Fact]
    public async Task Availability_ReadsTheFlagAndSectionFromTheHost()
    {
        var settingsService = new InMemorySettingsService();
        settingsService.Values["effective.features"] = "{\"chat\":true,\"autocomplete\":false}";
        settingsService.Values["autocomplete"] = "{\"disabledLanguages\":[\"vb\"]}";
        var connection = new FakeHostConnection(HostState.Ready).With<ISettingsService>(settingsService);
        using var availability = new AutocompleteAvailability(connection);
        var changed = 0;
        availability.Changed += (_, _) => changed++;

        await availability.RefreshAsync();

        Assert.False(availability.Settings.Enabled);
        Assert.Equal(["vb"], availability.Settings.DisabledLanguages);
        Assert.Equal(1, changed);
    }

    [Fact]
    public async Task Availability_KeepsTheDefaultsWhenTheHostIsAway()
    {
        var connection = new FakeHostConnection(HostState.Restarting);
        using var availability = new AutocompleteAvailability(connection);

        await availability.RefreshAsync();

        Assert.True(availability.Settings.Enabled);
    }

    [Theory]
    [InlineData(18, false, false, IntelliCodeStatus.NotRequired)]
    [InlineData(17, false, true, IntelliCodeStatus.Missing)]
    [InlineData(17, true, false, IntelliCodeStatus.CompletionsOff)]
    [InlineData(17, true, true, IntelliCodeStatus.Satisfied)]
    public void IntelliCode_IsRequiredOnVisualStudio2022Only(int major, bool present, bool on, IntelliCodeStatus expected)
    {
        Assert.Equal(expected, IntelliCodeRequirement.Evaluate(major, present, on));
    }

    [Fact]
    public void IntelliCode_UnreadableOptionCountsAsOn()
    {
        Assert.Equal(IntelliCodeStatus.Satisfied, IntelliCodeRequirement.Evaluate(17, intelliCodePresent: true, completionsOn: null));
    }

    [Theory]
    [InlineData(null, true)]
    [InlineData(0, true)]
    [InlineData(1, true)]
    [InlineData(2, false)]
    [InlineData(true, true)]
    [InlineData(false, false)]
    [InlineData("2", false)]
    [InlineData("True", true)]
    [InlineData(2.5, null)]
    public void IntelliCode_StoredOptionValues(object? stored, bool? expected)
    {
        Assert.Equal(expected, IntelliCodeRequirement.InterpretShowCompletions(stored));
    }

    [Fact]
    public void IntelliCode_MessagesNameTheFix()
    {
        Assert.StartsWith(IntelliCodeRequirement.Message, IntelliCodeRequirement.Describe(IntelliCodeStatus.CompletionsOff), StringComparison.Ordinal);
        Assert.Contains("Automatically generate code completions in the Editor", IntelliCodeRequirement.Describe(IntelliCodeStatus.CompletionsOff), StringComparison.Ordinal);
        Assert.Contains("not installed", IntelliCodeRequirement.Describe(IntelliCodeStatus.Missing), StringComparison.Ordinal);
        Assert.Null(IntelliCodeRequirement.Describe(IntelliCodeStatus.Satisfied));
        Assert.Null(IntelliCodeRequirement.Describe(IntelliCodeStatus.NotRequired));
    }

    [Fact]
    public void ThrottledLog_WritesOncePerInterval()
    {
        var now = new DateTimeOffset(2026, 10, 9, 9, 0, 0, TimeSpan.Zero);
        var log = new ThrottledLog(null, () => now);

        Assert.True(log.Warning("e", "m"));
        Assert.False(log.Warning("e", "m"));
        now += TimeSpan.FromSeconds(59);
        Assert.False(log.Warning("e", "m"));
        now += TimeSpan.FromSeconds(1);
        Assert.True(log.Warning("e", "m"));
        Assert.Equal(2, log.Written);
    }
}

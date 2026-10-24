using System;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.VisualStudio.Threading;
using Pillion.Contracts;
using Pillion.VisualStudio.Core;

namespace Pillion.VisualStudio.Tests;

// The tests await the view models' tasks; no main thread is involved.
#pragma warning disable VSTHRD003

/// <summary>The text the Index window, the status bar indicator and screen readers get from the host's index status (work item 2.15).</summary>
public sealed class IndexFormatTests : IDisposable
{
    private readonly JoinableTaskContext _context = Task.Run(() => new JoinableTaskContext()).Result;
    private readonly InMemorySettingsService _settings = TestModels.Settings();
    private readonly FakeHostConnection _connection;
    private readonly ScriptedContextService _host = new();

    public IndexFormatTests()
    {
        _connection = new FakeHostConnection().With<IContextService>(_host).With<ISettingsService>(_settings);
    }

    public void Dispose() => _context.Dispose();

    [Theory]
    [InlineData(0L, "0 B")]
    [InlineData(1023L, "1,023 B")]
    [InlineData(1024L, "1.0 KB")]
    [InlineData(480_000L, "469 KB")]
    [InlineData(5_500_000L, "5.2 MB")]
    [InlineData(1_610_612_736L, "1.5 GB")]
    public void Bytes_UseBinaryUnitsWithOneDecimal(long bytes, string expected)
    {
        var previous = CultureInfo.CurrentCulture;
        CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("en-US");
        try
        {
            Assert.Equal(expected, IndexFormat.Bytes(bytes));
        }
        finally
        {
            CultureInfo.CurrentCulture = previous;
        }
    }

    [Theory]
    [InlineData(-1, "")]
    [InlineData(0, "Less than a minute remaining")]
    [InlineData(59, "Less than a minute remaining")]
    [InlineData(60, "About 1 min remaining")]
    [InlineData(150, "About 3 min remaining")]
    [InlineData(3600, "About 1 h 0 min remaining")]
    [InlineData(5400, "About 1 h 30 min remaining")]
    public void Remaining_IsRoundedToMinutes_AndEmptyWithoutAnEstimate(int seconds, string expected) =>
        Assert.Equal(expected, IndexFormat.Remaining(seconds));

    [Theory]
    [InlineData(IndexPhase.Idle, "Idle (up to date)")]
    [InlineData(IndexPhase.Discovering, "Discovering files")]
    [InlineData(IndexPhase.Hashing, "Hashing files")]
    [InlineData(IndexPhase.Chunking, "Parsing and chunking")]
    [InlineData(IndexPhase.Embedding, "Embedding chunks")]
    [InlineData(IndexPhase.Storing, "Storing")]
    [InlineData(IndexPhase.Paused, "Paused")]
    [InlineData(IndexPhase.Failed, "Failed")]
    public void EveryPhase_HasAName(IndexPhase phase, string expected) => Assert.Equal(expected, IndexFormat.Phase(phase));

    [Fact]
    public void EveryPauseReason_HasALongAndAShortName()
    {
        foreach (var reason in Enum.GetValues(typeof(IndexPauseReason)).Cast<IndexPauseReason>().Where(r => r != IndexPauseReason.None))
        {
            Assert.False(string.IsNullOrWhiteSpace(IndexFormat.PauseReason(reason)), reason.ToString());
            Assert.False(string.IsNullOrWhiteSpace(IndexFormat.PauseReasonShort(reason)), reason.ToString());
        }

        Assert.Equal(string.Empty, IndexFormat.PauseReason(IndexPauseReason.None));
    }

    [Fact]
    public void TheStatusBarText_IsEmptyWhenIdle_AndAGlyphWithThePercentWhileIndexing()
    {
        Assert.Equal(string.Empty, IndexFormat.StatusBarText(null));
        Assert.Equal(string.Empty, IndexFormat.StatusBarText(IndexData.Idle(IndexData.Root(IndexData.RootA))));
        Assert.Equal(string.Empty, IndexFormat.StatusBarText(new IndexStatus()));

        Assert.Equal(IndexFormat.IndexingGlyph + " Indexing 75%", IndexFormat.StatusBarText(IndexData.Running(IndexPhase.Embedding, 30, 40, 160, 1)));
        Assert.Equal(IndexFormat.IndexingGlyph + " Indexing 0%", IndexFormat.StatusBarText(IndexData.Running(IndexPhase.Discovering, 0, 40, 0, -1)));
        Assert.Equal(IndexFormat.IndexingGlyph + " Indexing", IndexFormat.StatusBarText(new IndexStatus { IsIndexing = true, Phase = IndexPhase.Discovering }));
        Assert.Equal(IndexFormat.IndexingGlyph + " Indexing 99%", IndexFormat.StatusBarText(IndexData.Running(IndexPhase.Storing, 999, 1000, 1, 0)));
    }

    [Fact]
    public void ThePausedStatusBarText_NamesTheReason_AndAFailureShowsAWarning()
    {
        Assert.Equal(
            IndexFormat.PausedGlyph + " Index paused: build",
            IndexFormat.StatusBarText(new IndexStatus { Phase = IndexPhase.Paused, IsPaused = true, PauseReason = IndexPauseReason.Build }));
        Assert.Equal(
            IndexFormat.PausedGlyph + " Index paused: you",
            IndexFormat.StatusBarText(new IndexStatus { IsPaused = true, PauseReason = IndexPauseReason.User }));
        Assert.Equal(
            IndexFormat.PausedGlyph + " Index paused",
            IndexFormat.StatusBarText(new IndexStatus { Phase = IndexPhase.Paused }));
        Assert.Equal(
            IndexFormat.FailedGlyph + " Index error",
            IndexFormat.StatusBarText(new IndexStatus { Phase = IndexPhase.Failed, LastError = "boom" }));
    }

    /// <summary>
    /// The glyph and the words come apart: the plain status bar text shows the words only, because the glyphs are not in Segoe
    /// UI, and text that needs a fallback font makes WPF parse the system's composite fonts on the UI thread (over 3 s on a CI
    /// runner, after a host restart).
    /// </summary>
    [Fact]
    public void TheStatusBarParts_AreTheGlyphAndTheWords_AndTheWordsHaveNoGlyph()
    {
        Assert.Equal((string.Empty, string.Empty), IndexFormat.StatusBarParts(null));
        Assert.Equal((string.Empty, string.Empty), IndexFormat.StatusBarParts(IndexData.Idle(IndexData.Root(IndexData.RootA))));
        Assert.Equal((IndexFormat.IndexingGlyph, "Indexing 75%"), IndexFormat.StatusBarParts(IndexData.Running(IndexPhase.Embedding, 30, 40, 160, 1)));
        Assert.Equal((IndexFormat.PausedGlyph, "Index paused: build"), IndexFormat.StatusBarParts(new IndexStatus { Phase = IndexPhase.Paused, IsPaused = true, PauseReason = IndexPauseReason.Build }));
        Assert.Equal((IndexFormat.FailedGlyph, "Index error"), IndexFormat.StatusBarParts(new IndexStatus { Phase = IndexPhase.Failed, LastError = "boom" }));

        foreach (var status in new[]
        {
            IndexData.Running(IndexPhase.Storing, 999, 1000, 1, 0),
            new IndexStatus { IsIndexing = true, Phase = IndexPhase.Discovering },
            new IndexStatus { IsPaused = true, PauseReason = IndexPauseReason.User },
            new IndexStatus { Phase = IndexPhase.Failed },
        })
        {
            var (glyph, label) = IndexFormat.StatusBarParts(status);
            Assert.Equal(glyph + " " + label, IndexFormat.StatusBarText(status));
            Assert.All(label, c => Assert.True(c < 0x2000, $"'{c}' (U+{(int)c:X4}) in \"{label}\" may need a fallback font"));
        }
    }

    [Fact]
    public void TheFraction_IsClampedForOddNumbers_AndUnknownWithoutATotal()
    {
        Assert.Equal(1.0, IndexFormat.Fraction(IndexData.Running(IndexPhase.Storing, 50, 40, 0, 0)));
        Assert.Equal(0.0, IndexFormat.Fraction(IndexData.Running(IndexPhase.Storing, -5, 40, 0, 0)));
        Assert.Null(IndexFormat.Fraction(IndexData.Running(IndexPhase.Discovering, 0, 0, 0, -1)));
        Assert.Equal(0.5, IndexFormat.Fraction(new IndexStatus { IsIndexing = true, IndexedFileCount = 5, TotalFileCount = 10 }));
    }

    [Fact]
    public void TheSummary_IsOneSentenceForEveryState()
    {
        Assert.Equal("The index status is not known yet", IndexFormat.Summary(null));
        Assert.Equal("There is no index to build", IndexFormat.Summary(new IndexStatus()));
        Assert.Equal("The index is up to date", IndexFormat.Summary(IndexData.Idle(IndexData.Root(IndexData.RootA))));
        Assert.Equal("Indexing is paused: a build is running", IndexFormat.Summary(new IndexStatus { IsPaused = true, PauseReason = IndexPauseReason.Build }));
        Assert.Equal("Indexing failed: boom", IndexFormat.Summary(new IndexStatus { Phase = IndexPhase.Failed, LastError = "boom" }));
        Assert.Equal(
            "Indexing: Embedding chunks, 30 of 40 files, 160 chunks (75%), less than a minute remaining",
            IndexFormat.Summary(IndexData.Running(IndexPhase.Embedding, 30, 40, 160, 0)));
    }

    [Theory]
    [InlineData("SecretBearing", "Secret-bearing file")]
    [InlineData("GitIgnore", "Ignored by .gitignore")]
    [InlineData("PillionIgnore", "Ignored by .pillionignore")]
    [InlineData("UserExclude", "Matches indexing.exclude")]
    [InlineData("TooLarge", "Too large")]
    [InlineData("SomeFutureRule", "SomeFutureRule")]
    [InlineData("", "Excluded")]
    [InlineData(null, "Excluded")]
    public void ExcludedReasons_AreNamed_AndAnUnknownRuleIsShownAsTheHostNamedIt(string? reason, string expected) =>
        Assert.Equal(expected, IndexFormat.ExcludedReason(reason));

    [Fact]
    public void IndexStrings_EveryStringResolvesFromTheResourceFile()
    {
        var properties = typeof(IndexStrings).GetProperties().Where(p => p.PropertyType == typeof(string)).ToList();

        Assert.True(properties.Count > 150);
        foreach (var property in properties)
        {
            var value = (string)property.GetValue(null)!;
            Assert.False(string.IsNullOrWhiteSpace(value), property.Name);
            Assert.NotEqual(property.Name, value);
            if (property.Name.EndsWith("Format", StringComparison.Ordinal))
            {
                Assert.Contains("{0}", value, StringComparison.Ordinal);
            }
        }
    }

    [Fact]
    public async Task TheIndicator_ShowsThePercentWhileIndexing_ThePausedReason_AndNothingWhenIdle()
    {
        _host.Status = IndexData.Idle(IndexData.Root(IndexData.RootA));
        using var indicator = new IndexStatusIndicator(_connection, availability: null);
        var changes = 0;
        indicator.Changed += (_, _) => changes++;

        indicator.Start();
        await Task.Delay(100);
        Assert.Equal(string.Empty, indicator.Text);

        await _connection.RaiseIndexStatusChangedAsync(IndexData.Running(IndexPhase.Embedding, 10, 40, 80, 3));
        await Wait.UntilAsync(() => indicator.Text.EndsWith("Indexing 25%", StringComparison.Ordinal), "the percentage");
        Assert.StartsWith(IndexFormat.IndexingGlyph, indicator.Text, StringComparison.Ordinal);
        Assert.Equal((IndexFormat.IndexingGlyph, "Indexing 25%"), (indicator.Glyph, indicator.Label));
        Assert.Contains("Open the Pillion Index window", indicator.ToolTip, StringComparison.Ordinal);
        Assert.Contains("Embedding chunks", indicator.ToolTip, StringComparison.Ordinal);

        await _connection.RaiseIndexStatusChangedAsync(new IndexStatus { Phase = IndexPhase.Paused, IsPaused = true, PauseReason = IndexPauseReason.Debug });
        await Wait.UntilAsync(() => indicator.Text.Contains("Index paused: debugger", StringComparison.Ordinal), "the paused text");

        await _connection.RaiseIndexStatusChangedAsync(IndexData.Idle(IndexData.Root(IndexData.RootA)));
        await Wait.UntilAsync(() => indicator.Text.Length == 0, "an empty indicator");
        Assert.Equal(string.Empty, indicator.ToolTip);
        Assert.Equal((string.Empty, string.Empty), (indicator.Glyph, indicator.Label));
        Assert.True(changes >= 3);
    }

    [Fact]
    public async Task TheIndicator_StartsFromOneReadOfTheStatus_GoesQuietWhenTheHostLeaves_AndWhenTheFlagIsOff()
    {
        _host.Status = IndexData.Running(IndexPhase.Hashing, 4, 40, 0, 9);
        _settings.Values[ChatAvailability.EffectiveFeaturesKey] = "{\"indexWindow\":true}";
        using var availability = new ChatAvailability(_connection);
        await availability.RefreshAsync();
        using var indicator = new IndexStatusIndicator(_connection, availability);

        indicator.Start();
        await Wait.UntilAsync(() => indicator.Text.EndsWith("Indexing 10%", StringComparison.Ordinal), "the opening read");
        Assert.Equal(1, _host.Count("GetIndexStatusAsync"));

        _settings.Values[ChatAvailability.EffectiveFeaturesKey] = "{\"indexWindow\":false}";
        await availability.RefreshAsync();
        await Wait.UntilAsync(() => indicator.Text.Length == 0, "an empty indicator while the flag is off");

        _settings.Values[ChatAvailability.EffectiveFeaturesKey] = "{\"indexWindow\":true}";
        await availability.RefreshAsync();
        await Wait.UntilAsync(() => indicator.Text.Length > 0, "the indicator back");

        await _connection.SetStateAsync(HostState.Restarting);
        await Wait.UntilAsync(() => indicator.Text.Length == 0, "an empty indicator while the host is away");
        Assert.Null(indicator.Status);
    }

    [Fact]
    public async Task TheIndicator_FoldsABurstOfNotifications_SoTheStatusBarIsNotFlooded()
    {
        using var indicator = new IndexStatusIndicator(_connection, availability: null);
        var changes = 0;
        indicator.Changed += (_, _) => System.Threading.Interlocked.Increment(ref changes);
        indicator.Start();

        var burst = System.Diagnostics.Stopwatch.StartNew();
        for (var i = 1; i <= 40; i++)
        {
            await _connection.RaiseIndexStatusChangedAsync(IndexData.Running(IndexPhase.Embedding, i, 40, i, 1));
        }

        burst.Stop();
        await Wait.UntilAsync(() => indicator.Text.EndsWith("Indexing 100%", StringComparison.Ordinal), "the last percentage");
        await Task.Delay(700);

        // At most one change per 500 ms interval (plus the first and the last), so the bound follows the time the burst took
        // to raise: a few milliseconds on an idle machine, longer on a loaded one.
        var bound = Math.Max(6, 2 + (int)Math.Ceiling(burst.Elapsed.TotalMilliseconds / 500));
        Assert.InRange(System.Threading.Volatile.Read(ref changes), 1, Math.Min(bound, 39));
    }

    [Fact]
    public async Task ChatAvailability_ReadsTheIndexWindowFlag_AndAnAbsentFlagMeansOn()
    {
        using var availability = new ChatAvailability(_connection);
        var raised = 0;
        availability.Changed += (_, _) => raised++;
        Assert.True(availability.IsIndexWindowEnabled);

        _settings.Values[ChatAvailability.EffectiveFeaturesKey] = "{\"chat\":true}";
        await availability.RefreshAsync();
        Assert.True(availability.IsIndexWindowEnabled);
        Assert.Equal(0, raised);

        _settings.Values[ChatAvailability.EffectiveFeaturesKey] = "{\"chat\":true,\"indexWindow\":false}";
        await availability.RefreshAsync();
        Assert.False(availability.IsIndexWindowEnabled);
        Assert.True(availability.IsEnabled);
        Assert.Equal(1, raised);
    }
}

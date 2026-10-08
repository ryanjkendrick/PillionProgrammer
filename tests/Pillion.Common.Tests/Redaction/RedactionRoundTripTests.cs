using System.Text.RegularExpressions;
using Pillion.Common.Redaction;

namespace Pillion.Common.Tests.Redaction;

/// <summary>
/// Property-style tests over 60 generated files (6 formats x 10 seeds), each with fabricated secrets of every kind
/// planted in format-appropriate syntax.
/// </summary>
public partial class RedactionRoundTripTests
{
    private const int SeedsPerFormat = 10;

    public static IEnumerable<object[]> Files() =>
        Enum.GetValues<FileFormat>().SelectMany(format => Enumerable.Range(1, SeedsPerFormat).Select(seed => new object[] { format, seed }));

    [Fact]
    public void Generator_ProducesAtLeastFiftyFiles_AndEveryKindIsPlanted()
    {
        Assert.True(Files().Count() >= 50);
        Assert.Equal(16, Enum.GetValues<SecretKind>().Length);
    }

    [Theory]
    [MemberData(nameof(Files))]
    public void RestoreOfRedact_IsTheOriginal_AndNoOriginalSurvives(FileFormat format, int seed)
    {
        var file = PlantedFileGenerator.Generate(format, seed);
        using var session = new RedactionSession();

        var redacted = session.Redact(file.Text);

        Assert.NotEqual(file.Text, redacted);
        foreach (var needle in file.Needles)
        {
            Assert.DoesNotContain(needle, redacted, StringComparison.Ordinal);
        }

        Assert.Equal(file.Text, session.Restore(redacted));
    }

    [Theory]
    [MemberData(nameof(Files))]
    public void Redaction_IsStableWithinASession_AndDeterministicAcrossFreshOnes(FileFormat format, int seed)
    {
        var file = PlantedFileGenerator.Generate(format, seed);
        using var session = new RedactionSession();

        var first = session.Redact(file.Text);
        var second = session.Redact(file.Text);
        using var fresh = new RedactionSession();

        Assert.Equal(first, second);
        Assert.Equal(first, fresh.Redact(file.Text));
        Assert.Equal(session.Redactions.DistinctCount, fresh.Redactions.DistinctCount);
        Assert.Equal(session.Redactions.Count, fresh.Redactions.Count * 2);
    }

    [Theory]
    [MemberData(nameof(Files))]
    public void SamePlaceholderAlwaysMapsToTheSameOriginal(FileFormat format, int seed)
    {
        var file = PlantedFileGenerator.Generate(format, seed);
        using var session = new RedactionSession();
        var redacted = session.Redact(file.Text);

        // Every distinct placeholder in the output must restore to exactly one original value.
        foreach (var placeholder in Placeholder().Matches(redacted).Select(m => m.Value).Where(p => p != PlantedFileGenerator.Bogus).Distinct())
        {
            var restored = session.Restore(placeholder);
            Assert.NotEqual(placeholder, restored);
            Assert.Equal(restored, session.Restore(placeholder));
        }

        // Fewer distinct placeholders than occurrences proves reuse (every planted secret appears at least twice).
        var occurrences = Placeholder().Count(redacted) - 1; // minus the bogus token
        Assert.True(Placeholder().Matches(redacted).Select(m => m.Value).Distinct().Count() - 1 < occurrences);
    }

    [Theory]
    [MemberData(nameof(Files))]
    public void FindUnresolved_ReportsThePlantedBogusPlaceholder_AndNothingElse(FileFormat format, int seed)
    {
        var file = PlantedFileGenerator.Generate(format, seed);
        using var session = new RedactionSession();
        var redacted = session.Redact(file.Text);

        Assert.Equal([PlantedFileGenerator.Bogus], session.FindUnresolved(redacted));
        Assert.Empty(session.FindUnresolved(session.Restore(redacted).Replace(PlantedFileGenerator.Bogus, "ok", StringComparison.Ordinal)));
    }

    [Theory]
    [MemberData(nameof(Files))]
    public void PlaceholdersFromOneSession_DoNotRestoreTheOriginalsInAnother(FileFormat format, int seed)
    {
        var file = PlantedFileGenerator.Generate(format, seed);
        var other = PlantedFileGenerator.Generate(format, seed + 1000, variantBase: 100);
        using var owner = new RedactionSession();
        var redacted = owner.Redact(file.Text);

        // A session that has seen nothing leaves every placeholder unresolved.
        using var empty = new RedactionSession();
        Assert.Equal(redacted, empty.Restore(redacted));
        Assert.Contains(empty.FindUnresolved(redacted), p => p.StartsWith("<redacted:", StringComparison.Ordinal));

        // A session with other secrets maps the same placeholder text to its own originals, never the owner's.
        using var stranger = new RedactionSession();
        stranger.Redact(other.Text);
        var misrestored = stranger.Restore(redacted);
        foreach (var needle in file.Needles)
        {
            Assert.DoesNotContain(needle, misrestored, StringComparison.Ordinal);
        }
    }

    [Theory]
    [MemberData(nameof(Files))]
    public void BenignLookalikes_SurviveUntouched_AndRedactedOutputScansClean(FileFormat format, int seed)
    {
        var file = PlantedFileGenerator.Generate(format, seed);
        using var session = new RedactionSession();

        var redacted = session.Redact(file.Text);

        foreach (var filler in file.FillerLines)
        {
            Assert.Contains(filler, redacted, StringComparison.Ordinal);
        }

        Assert.Empty(SecretScanner.Scan(redacted));
        Assert.Equal(redacted, LogRedactor.Redact(redacted));
    }

    [Theory]
    [MemberData(nameof(Files))]
    public void LogRedactor_RemovesEveryPlantedSecret(FileFormat format, int seed)
    {
        var file = PlantedFileGenerator.Generate(format, seed);

        var logged = LogRedactor.Redact(file.Text);

        foreach (var needle in file.Needles)
        {
            Assert.DoesNotContain(needle, logged, StringComparison.Ordinal);
        }
    }

    [Theory]
    [MemberData(nameof(Files))]
    public void SummaryNeverContainsOriginals_AndNamesTheKindsFound(FileFormat format, int seed)
    {
        var file = PlantedFileGenerator.Generate(format, seed);
        using var session = new RedactionSession();
        session.Redact(file.Text);

        var description = session.Redactions.Describe();

        foreach (var needle in file.Needles)
        {
            Assert.DoesNotContain(needle, description, StringComparison.Ordinal);
        }

        Assert.StartsWith($"{session.Redactions.Count} redactions: ", description, StringComparison.Ordinal);
        Assert.Contains("private-key-block", session.Redactions.Kinds);
    }

    [GeneratedRegex(@"<redacted:[^<>\r\n]{0,80}>")]
    private static partial Regex Placeholder();
}

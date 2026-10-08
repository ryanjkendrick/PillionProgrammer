using Pillion.Common.Redaction;

namespace Pillion.Common.Tests.Redaction;

public class SecretScannerCorpusTests
{
    public static IEnumerable<object[]> Positives() => Corpus.PositiveNames();

    public static IEnumerable<object[]> Negatives() => Corpus.NegativeNames();

    [Theory]
    [MemberData(nameof(Positives))]
    public void PositiveSample_ReportsExactlyTheExpectedKindsAndValues(string name)
    {
        var sample = Corpus.LoadPositive(name);

        var matches = SecretScanner.Scan(sample.Text);

        Assert.Equal(sample.ExpectedKinds, matches.Select(m => m.KindId).ToList());
        Assert.Equal(sample.ExpectedValues, matches.Select(m => sample.Text.Substring(m.Start, m.Length)).ToList());
    }

    [Theory]
    [MemberData(nameof(Negatives))]
    public void NegativeSample_ReportsNothing(string name)
    {
        var text = Corpus.LoadNegative(name);

        var matches = SecretScanner.Scan(text);

        Assert.True(
            matches.Count == 0,
            $"{name}: unexpected {string.Join(", ", matches.Select(m => $"{m.KindId}@{m.Start}+{m.Length}"))}");
    }

    [Fact]
    public void Corpus_CoversEveryKindWithAPositiveSample()
    {
        var covered = Corpus.PositiveNames()
            .SelectMany(o => Corpus.LoadPositive((string)o[0]).ExpectedKinds)
            .ToHashSet();

        foreach (var kind in Enum.GetValues<SecretKind>())
        {
            Assert.Contains(kind.ToId(), covered);
        }
    }

    [Fact]
    public void Matches_AreSortedAndNeverOverlap_AcrossTheWholeCorpus()
    {
        var texts = Corpus.PositiveNames().Select(o => Corpus.LoadPositive((string)o[0]).Text)
            .Concat(Corpus.NegativeNames().Select(o => Corpus.LoadNegative((string)o[0])));

        foreach (var text in texts)
        {
            var matches = SecretScanner.Scan(text);
            for (var i = 0; i < matches.Count; i++)
            {
                Assert.True(matches[i].Length > 0);
                Assert.True(matches[i].End <= text.Length);
                if (i > 0)
                {
                    Assert.True(matches[i - 1].End <= matches[i].Start, "matches overlap or are out of order");
                }
            }
        }
    }

    [Fact]
    public void NegativeCorpus_FalsePositiveRate_IsZero()
    {
        var lines = 0;
        var flaggedLines = 0;
        foreach (var name in Corpus.NegativeNames().Select(o => (string)o[0]))
        {
            var text = Corpus.LoadNegative(name);
            var matches = SecretScanner.Scan(text);
            var split = text.Split('\n');
            lines += split.Length;
            var offset = 0;
            foreach (var line in split)
            {
                if (matches.Any(m => m.Start >= offset && m.Start < offset + line.Length + 1))
                {
                    flaggedLines++;
                }

                offset += line.Length + 1;
            }
        }

        Assert.True(lines > 50, "the negative corpus should contain a meaningful number of lines");
        Assert.Equal(0, flaggedLines);
    }
}

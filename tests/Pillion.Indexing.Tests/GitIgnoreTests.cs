using System.Diagnostics;
using Pillion.Indexing.Discovery.Ignore;

namespace Pillion.Indexing.Tests;

public sealed class GitIgnoreTests
{
    private static IgnoreVerdict Verdict(string rules, string path, bool isDirectory = false, string baseDirectory = "", bool ignoreCase = true) =>
        IgnoreRules.Parse(rules, baseDirectory, ".gitignore", ignoreCase).Match(path, isDirectory).Verdict;

    [Theory]
    [InlineData("*.log", "debug.log", true)]
    [InlineData("*.log", "a/b/debug.log", true)]
    [InlineData("*.log", "debug.logx", false)]
    [InlineData("*.log", "log", false)]
    [InlineData("debug.*", "src/debug.txt", true)]
    [InlineData("debug.*", "src/xdebug.txt", false)]
    [InlineData("foo.bar", "foo.bar", true)]
    [InlineData("foo.bar", "fooxbar", false)]
    [InlineData("file(1)+.txt", "file(1)+.txt", true)]
    [InlineData("a*b", "axxb", true)]
    [InlineData("a*b", "a/b", false)]
    [InlineData("?.txt", "a.txt", true)]
    [InlineData("?.txt", "ab.txt", false)]
    [InlineData("tmp", "tmp", true)]
    [InlineData("tmp", "a/tmp", true)]
    [InlineData("tmp", "a/tmpx", false)]
    public void UnanchoredPatterns_MatchTheNameAtAnyDepth(string rules, string path, bool ignored)
    {
        Assert.Equal(ignored ? IgnoreVerdict.Ignored : IgnoreVerdict.None, Verdict(rules, path));
    }

    [Theory]
    [InlineData("/build", "build", true)]
    [InlineData("/build", "src/build", false)]
    [InlineData("build", "src/build", true)]
    [InlineData("docs/*.md", "docs/readme.md", true)]
    [InlineData("docs/*.md", "docs/sub/readme.md", false)]
    [InlineData("docs/*.md", "x/docs/readme.md", false)]
    [InlineData("src/gen", "src/gen", true)]
    [InlineData("src/gen", "other/src/gen", false)]
    [InlineData("/*.json", "package.json", true)]
    [InlineData("/*.json", "sub/package.json", false)]
    public void PatternsWithALeadingOrInnerSlash_AreAnchoredToTheRulesDirectory(string rules, string path, bool ignored)
    {
        Assert.Equal(ignored ? IgnoreVerdict.Ignored : IgnoreVerdict.None, Verdict(rules, path, isDirectory: true));
    }

    [Fact]
    public void ATrailingSlash_LimitsTheRuleToDirectories()
    {
        Assert.Equal(IgnoreVerdict.Ignored, Verdict("logs/", "logs", isDirectory: true));
        Assert.Equal(IgnoreVerdict.Ignored, Verdict("logs/", "a/b/logs", isDirectory: true));
        Assert.Equal(IgnoreVerdict.None, Verdict("logs/", "logs", isDirectory: false));
        Assert.Equal(IgnoreVerdict.None, Verdict("logs/", "logs/today.txt", isDirectory: false));
    }

    [Fact]
    public void ANegation_ReIncludesWhatAnEarlierRuleIgnored_AndOnlyWhenItComesLater()
    {
        const string After = "*.log\n!important.log\n";
        Assert.Equal(IgnoreVerdict.Ignored, Verdict(After, "debug.log"));
        Assert.Equal(IgnoreVerdict.Included, Verdict(After, "important.log"));
        Assert.Equal(IgnoreVerdict.Included, Verdict(After, "sub/important.log"));

        // The last matching rule wins, so a negation before the rule it targets does nothing.
        const string Before = "!important.log\n*.log\n";
        Assert.Equal(IgnoreVerdict.Ignored, Verdict(Before, "important.log"));
    }

    [Fact]
    public void ANegationWithoutASlashCanReIncludeContentsOfAnUnanchoredFolderPattern()
    {
        // "build/*" ignores the entries of build, not build itself, so a later negation can rescue one of them.
        const string Rules = "build/*\n!build/keep.txt\n";
        Assert.Equal(IgnoreVerdict.None, Verdict(Rules, "build", isDirectory: true));
        Assert.Equal(IgnoreVerdict.Ignored, Verdict(Rules, "build/other.txt"));
        Assert.Equal(IgnoreVerdict.Included, Verdict(Rules, "build/keep.txt"));
    }

    [Theory]
    [InlineData("**/foo", "foo", true)]
    [InlineData("**/foo", "a/foo", true)]
    [InlineData("**/foo", "a/b/foo", true)]
    [InlineData("**/foo", "a/b/foobar", false)]
    [InlineData("foo/**", "foo/a", true)]
    [InlineData("foo/**", "foo/a/b/c", true)]
    [InlineData("foo/**", "foo", false)]
    [InlineData("a/**/b", "a/b", true)]
    [InlineData("a/**/b", "a/x/b", true)]
    [InlineData("a/**/b", "a/x/y/b", true)]
    [InlineData("a/**/b", "a/x/y/c", false)]
    [InlineData("a/**/b", "x/a/b", false)]
    [InlineData("**", "anything/at/all", true)]
    [InlineData("a**b", "axxb", true)]
    [InlineData("a**b", "a/b", false)]
    public void DoubleStars_AreWholeSegmentWildcards(string rules, string path, bool ignored)
    {
        Assert.Equal(ignored ? IgnoreVerdict.Ignored : IgnoreVerdict.None, Verdict(rules, path));
    }

    [Theory]
    [InlineData("[ab].txt", "a.txt", true)]
    [InlineData("[ab].txt", "c.txt", false)]
    [InlineData("[a-c].txt", "b.txt", true)]
    [InlineData("[a-c].txt", "d.txt", false)]
    [InlineData("[!a-c].txt", "d.txt", true)]
    [InlineData("[!a-c].txt", "b.txt", false)]
    [InlineData("[^a].txt", "b.txt", true)]
    [InlineData("[[:digit:]].txt", "5.txt", true)]
    [InlineData("[[:digit:]].txt", "x.txt", false)]
    [InlineData("[[:upper:][:digit:]]x", "Ax", true)]
    [InlineData("[]].txt", "].txt", true)]
    [InlineData("[a-].txt", "-.txt", true)]
    [InlineData("a[.txt", "a[.txt", true)]
    public void CharacterClasses_SupportRangesNegationAndPosixNames(string rules, string path, bool ignored)
    {
        Assert.Equal(ignored ? IgnoreVerdict.Ignored : IgnoreVerdict.None, Verdict(rules, path));
    }

    [Fact]
    public void ANegatedClass_NeverMatchesAPathSeparator()
    {
        Assert.Equal(IgnoreVerdict.None, Verdict("/a[!x]b", "a/b"));
        Assert.Equal(IgnoreVerdict.Ignored, Verdict("/a[!x]b", "ayb"));
        Assert.Equal(IgnoreVerdict.None, Verdict("/a?b", "a/b"));
    }

    [Fact]
    public void BlankLinesCommentsAndEscapes_AreHandledAsGitDoes()
    {
        const string Rules = "\n# a comment\n\\#notacomment\n\\!bang\n   \n";
        Assert.Equal(IgnoreVerdict.Ignored, Verdict(Rules, "#notacomment"));
        Assert.Equal(IgnoreVerdict.Ignored, Verdict(Rules, "!bang"));
        Assert.Equal(IgnoreVerdict.None, Verdict(Rules, "a comment"));
        Assert.Equal(2, IgnoreRules.Parse(Rules).Count);
    }

    [Fact]
    public void TrailingSpaces_AreDroppedUnlessEscaped_AndWindowsLineEndingsAndABomAreTolerated()
    {
        Assert.Equal(IgnoreVerdict.Ignored, Verdict("secret.txt   \r\n", "secret.txt"));
        Assert.Equal(IgnoreVerdict.Ignored, Verdict("keep\\ \n", "keep "));
        Assert.Equal(IgnoreVerdict.None, Verdict("keep\\ \n", "keep"));
        Assert.Equal(IgnoreVerdict.Ignored, Verdict("﻿*.tmp\r\n", "a.tmp"));
    }

    [Fact]
    public void Matching_IgnoresCaseByDefault_AndCanBeCaseSensitive()
    {
        Assert.Equal(IgnoreVerdict.Ignored, Verdict("*.LOG", "debug.log"));
        Assert.Equal(IgnoreVerdict.Ignored, Verdict("Build/", "build", isDirectory: true));
        Assert.Equal(IgnoreVerdict.Ignored, Verdict("[A-C].txt", "b.txt"));
        Assert.Equal(IgnoreVerdict.None, Verdict("*.LOG", "debug.log", ignoreCase: false));
        Assert.Equal(IgnoreVerdict.Ignored, Verdict("*.LOG", "debug.LOG", ignoreCase: false));
    }

    [Fact]
    public void ARulesFileInASubfolder_OnlyAppliesBelowIt_AndAnchorsToIt()
    {
        const string Rules = "*.generated\n/local.cfg\n";
        Assert.Equal(IgnoreVerdict.Ignored, Verdict(Rules, "src/app/a.generated", baseDirectory: "src"));
        Assert.Equal(IgnoreVerdict.None, Verdict(Rules, "other/a.generated", baseDirectory: "src"));
        Assert.Equal(IgnoreVerdict.None, Verdict(Rules, "src", baseDirectory: "src"));
        Assert.Equal(IgnoreVerdict.Ignored, Verdict(Rules, "src/local.cfg", baseDirectory: "src"));
        Assert.Equal(IgnoreVerdict.None, Verdict(Rules, "src/deeper/local.cfg", baseDirectory: "src"));
        Assert.Equal(IgnoreVerdict.None, Verdict(Rules, "srcx/a.generated", baseDirectory: "src"));
    }

    [Fact]
    public void ADeeperRulesFile_OverridesAShallowerOne()
    {
        var chain = IgnoreChain.Empty
            .Push(IgnoreRules.Parse("*.log\n", "", ".gitignore"))
            .Push(IgnoreRules.Parse("!keep.log\n", "src", "src/.gitignore"));

        Assert.True(chain.Match("a.log", isDirectory: false).IsIgnored);
        Assert.True(chain.Match("src/a.log", isDirectory: false).IsIgnored);
        var rescued = chain.Match("src/keep.log", isDirectory: false);
        Assert.Equal(IgnoreVerdict.Included, rescued.Verdict);
        Assert.Equal("src/.gitignore:1", rescued.Source);
        Assert.Equal("!keep.log", rescued.Rule);
        Assert.True(chain.Match("keep.log", isDirectory: false).IsIgnored);
    }

    [Fact]
    public void AnIgnoredParentIgnoresEverythingBelow_EvenWhenANegationNamesTheFile()
    {
        var chain = IgnoreChain.Empty.Push(IgnoreRules.Parse("build/\n!build/keep.txt\n"));

        Assert.True(chain.MatchWithParents("build/keep.txt", isDirectory: false).IsIgnored);
        Assert.True(chain.MatchWithParents("a/build/x/y.txt", isDirectory: false).IsIgnored);
        Assert.False(chain.MatchWithParents("src/x.txt", isDirectory: false).IsIgnored);
    }

    [Fact]
    public void FromPatterns_BuildsRulesFromSettingsEntries()
    {
        var rules = IgnoreRules.FromPatterns(["**/generated/**", "*.snap", "!keep.snap"], "indexing.exclude");

        Assert.True(rules.Match("src/generated/a.cs", isDirectory: false).IsIgnored);
        Assert.True(rules.Match("x.snap", isDirectory: false).IsIgnored);
        Assert.Equal(IgnoreVerdict.Included, rules.Match("keep.snap", isDirectory: false).Verdict);
        Assert.Equal("indexing.exclude:1", rules.Match("src/generated/a.cs", isDirectory: false).Source);
    }

    [Fact]
    public void PathologicalPatterns_StayFast()
    {
        // Patterns come from repositories; matching must be linear in the path length whatever they contain.
        var rules = IgnoreRules.Parse("a/**/**/**/**/**/**/b\n*a*a*a*a*a*a*a*a*a*b\n");
        var path = string.Join('/', Enumerable.Repeat("aaaaaaaaaaaaaaaaaaaa", 40)) + "/c";

        var stopwatch = Stopwatch.StartNew();
        for (var i = 0; i < 200; i++)
        {
            rules.Match(path, isDirectory: false);
        }

        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(10), $"took {stopwatch.Elapsed}");
    }
}

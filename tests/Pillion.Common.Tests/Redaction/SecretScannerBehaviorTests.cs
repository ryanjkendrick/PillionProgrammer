using Pillion.Common.Redaction;

namespace Pillion.Common.Tests.Redaction;

public class SecretScannerBehaviorTests
{
    [Fact]
    public void Scan_Null_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => SecretScanner.Scan(null!));
    }

    [Fact]
    public void Scan_EmptyAndPlainText_ReturnsNothing()
    {
        Assert.Empty(SecretScanner.Scan(string.Empty));
        Assert.Empty(SecretScanner.Scan("Hello, world. Nothing to see here."));
    }

    [Fact]
    public void KindIds_AreStableAndRoundTrip()
    {
        string[] expected =
        [
            "aws-access-key", "aws-secret-key", "azure-storage-key", "azure-connection-string", "gcp-api-key",
            "gcp-service-account", "github-token", "slack-token", "slack-webhook", "stripe-key", "openai-key",
            "anthropic-key", "jwt", "connection-string-password", "private-key-block", "generic-high-entropy",
        ];

        Assert.Equal(expected.Order(StringComparer.Ordinal), Enum.GetValues<SecretKind>().Select(k => k.ToId()).Order(StringComparer.Ordinal));
        foreach (var kind in Enum.GetValues<SecretKind>())
        {
            Assert.True(SecretKindExtensions.TryParseId(kind.ToId(), out var parsed));
            Assert.Equal(kind, parsed);
        }

        Assert.False(SecretKindExtensions.TryParseId("nope", out _));
    }

    [Theory]
    [InlineData(0, SecretSeverity.High)]
    [InlineData(1, SecretSeverity.Medium)]
    [InlineData(2, SecretSeverity.High)]
    [InlineData(3, SecretSeverity.Low)]
    public void StripeKeys_HaveSeverityByPrefix(int variant, SecretSeverity expected)
    {
        var key = SecretFactory.Create(SecretKind.StripeKey, variant);

        var match = Assert.Single(SecretScanner.Scan($"stripe = '{key}'"));

        Assert.Equal(SecretKind.StripeKey, match.Kind);
        Assert.Equal(expected, match.Severity);
    }

    [Fact]
    public void Severity_IsHighForPrivateKeysAndLiveCredentials()
    {
        Assert.Equal(SecretSeverity.High, Assert.Single(SecretScanner.Scan(SecretFactory.Create(SecretKind.PrivateKeyBlock, 0))).Severity);
        Assert.Equal(SecretSeverity.High, Assert.Single(SecretScanner.Scan("id=" + SecretFactory.Create(SecretKind.AwsAccessKey, 0))).Severity);
        Assert.Equal(SecretSeverity.Medium, Assert.Single(SecretScanner.Scan("x " + SecretFactory.Create(SecretKind.Jwt, 0))).Severity);
    }

    [Fact]
    public void MoreSpecificKind_WinsOverGeneric_WhenSpansCoincide()
    {
        var jwt = SecretFactory.Create(SecretKind.Jwt, 0);
        var token = SecretFactory.Create(SecretKind.GitHubToken, 0);
        var awsSecret = SecretFactory.Create(SecretKind.AwsSecretKey, 0);

        Assert.Equal(SecretKind.Jwt, Assert.Single(SecretScanner.Scan($"var apiToken = \"{jwt}\";")).Kind);
        Assert.Equal(SecretKind.GitHubToken, Assert.Single(SecretScanner.Scan($"GITHUB_TOKEN={token}")).Kind);
        Assert.Equal(SecretKind.AwsSecretKey, Assert.Single(SecretScanner.Scan($"aws_secret_access_key = {awsSecret}")).Kind);
        Assert.Equal(SecretKind.ConnectionStringPassword, Assert.Single(SecretScanner.Scan("Password=" + SecretFactory.Pick(new Random(1), "abcdefABCDEF0123456789", 40))).Kind);
    }

    [Fact]
    public void AnthropicKeys_AreNotReportedAsOpenAi()
    {
        var match = Assert.Single(SecretScanner.Scan("key: " + SecretFactory.Create(SecretKind.AnthropicKey, 0)));

        Assert.Equal(SecretKind.AnthropicKey, match.Kind);
    }

    [Fact]
    public void ReportedSpan_IsTheSecretValueNotItsContext()
    {
        var password = SecretFactory.Create(SecretKind.ConnectionStringPassword, 0);
        var text = $"Server=db;Password={password};Encrypt=True";

        var match = Assert.Single(SecretScanner.Scan(text));

        Assert.Equal(password, text.Substring(match.Start, match.Length));
    }

    [Fact]
    public void GenericEntropy_RequiresLettersAndDigitsMixedAndHighEntropy()
    {
        var random = SecretFactory.Create(SecretKind.GenericHighEntropy, 0);

        Assert.Single(SecretScanner.Scan($"apiKey = \"{random}\""));

        // No digits.
        Assert.Empty(SecretScanner.Scan($"apiKey = \"{new string('a', 20)}{new string('b', 20)}\""));
        Assert.Empty(SecretScanner.Scan("apiKey = \"" + SecretFactory.Pick(new Random(3), "abcdefghijklmnopqrstuvwxyz", 40) + "\""));

        // Digits and letters but low entropy (two symbols repeated).
        var lowEntropy = string.Concat(Enumerable.Repeat("ab1", 14));
        Assert.True(Entropy(lowEntropy) < SecretScanner.GenericEntropyThreshold);
        Assert.Empty(SecretScanner.Scan($"apiKey = \"{lowEntropy}\""));

        // Shorter than 32 characters.
        Assert.Empty(SecretScanner.Scan($"apiKey = \"{random[..31]}\""));
        Assert.Single(SecretScanner.Scan($"apiKey = \"{random[..32]}\""));
    }

    [Fact]
    public void GenericEntropy_CatchesRandomHexAtTheMinimumLength()
    {
        // 32 hex characters is the shortest and lowest-entropy credential shape we mean to catch (an MD5-sized key).
        var rng = new Random(99);
        var misses = 0;
        for (var i = 0; i < 2000; i++)
        {
            var hex = SecretFactory.Pick(rng, "0123456789abcdef", 32);
            if (SecretScanner.Scan($"api_secret: {hex}").Count == 0)
            {
                misses++;
            }
        }

        // Entropy of a 32-character random hex string averages ~3.66 bits; the threshold is 3.0.
        Assert.True(misses <= 2, $"{misses} of 2000 random 32-character hex secrets were missed");
    }

    [Theory]
    [InlineData("api_key = \"your-api-key-here-0123456789abcdef0123456789\"")]
    [InlineData("Password=changeme;")]
    [InlineData("AKIAXXXXXXXXXXXXXXXX")]
    [InlineData("AKIAIOSFODNN7EXAMPLE")]
    [InlineData("SECRET_TOKEN=00000000000000000000000000000000")]
    [InlineData("auth_token = \"<redacted:generic-high-entropy>\"")]
    [InlineData("x-api-key: 3f2504e0-4f89-11d3-9a0c-0305e82c3301")]
    public void AllowListedValues_AreNotReported(string text)
    {
        Assert.Empty(SecretScanner.Scan(text));
    }

    [Fact]
    public void MatchesUnderUnicodeAndOtherCultures_BehaveTheSame()
    {
        var original = Thread.CurrentThread.CurrentCulture;
        try
        {
            var key = SecretFactory.Create(SecretKind.AwsAccessKey, 2);
            foreach (var name in new[] { "tr-TR", "en-US", "de-DE" })
            {
                Thread.CurrentThread.CurrentCulture = new System.Globalization.CultureInfo(name);

                // With Turkish culture rules, "KEY" and "key" would not compare equal without CultureInvariant.
                Assert.Equal(2, SecretScanner.Scan($"AWS_ACCESS_KEY={key} SECRET_KEY={SecretFactory.Create(SecretKind.GenericHighEntropy, 1)}").Count);
            }
        }
        finally
        {
            Thread.CurrentThread.CurrentCulture = original;
        }
    }

    private static double Entropy(string value)
    {
        var entropy = 0.0;
        foreach (var group in value.GroupBy(c => c))
        {
            var p = (double)group.Count() / value.Length;
            entropy -= p * Math.Log2(p);
        }

        return entropy;
    }
}

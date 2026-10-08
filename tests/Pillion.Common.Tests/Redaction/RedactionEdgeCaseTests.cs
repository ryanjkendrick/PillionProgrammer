using System.Text;
using Pillion.Common.Redaction;

namespace Pillion.Common.Tests.Redaction;

public class RedactionEdgeCaseTests
{
    public static IEnumerable<object[]> Surroundings() =>
    [
        ["(", ")"], ["[", "]"], ["{", "}"], ["\"", "\""], ["'", "'"], ["`", "`"], ["<", ">"], ["=", ";"], [":", ","],
        ["", "."], [".", ""], ["", ""], [" ", " "], ["\n", "\n"], ["\r\n", "\r\n"], ["\t", "\t"], ["/", "/"],
        ["key=", "&other=1"], ["<id>", "</id>"],
    ];

    public static IEnumerable<object[]> SecretKindsWithSelfIdentifyingShape() =>
        new[]
        {
            SecretKind.AwsAccessKey, SecretKind.GitHubToken, SecretKind.Jwt, SecretKind.GcpApiKey, SecretKind.StripeKey,
            SecretKind.AnthropicKey, SecretKind.OpenAiKey, SecretKind.SlackToken, SecretKind.SlackWebhook,
        }.Select(k => new object[] { k });

    [Theory]
    [MemberData(nameof(Surroundings))]
    public void SecretAdjacentToPunctuation_IsFoundExactly(string before, string after)
    {
        foreach (var kind in new[] { SecretKind.AwsAccessKey, SecretKind.GitHubToken, SecretKind.Jwt })
        {
            var secret = SecretFactory.Create(kind, 1);
            var text = $"x{(before.Length == 0 ? " " : string.Empty)}{before}{secret}{after}{(after.Length == 0 ? " " : string.Empty)}y";

            var match = Assert.Single(SecretScanner.Scan(text));

            Assert.Equal(kind, match.Kind);
            Assert.Equal(secret, text.Substring(match.Start, match.Length));
            using var session = new RedactionSession();
            var redacted = session.Redact(text);
            Assert.DoesNotContain(secret, redacted, StringComparison.Ordinal);
            Assert.Equal(text, session.Restore(redacted));
        }
    }

    [Theory]
    [MemberData(nameof(SecretKindsWithSelfIdentifyingShape))]
    public void SecretIsTheWholeText_OrAtEitherEnd(SecretKind kind)
    {
        var secret = SecretFactory.Create(kind, 0);

        foreach (var text in new[] { secret, secret + " tail", "head " + secret, "head\n" + secret + "\ntail" })
        {
            var match = Assert.Single(SecretScanner.Scan(text));
            Assert.Equal(kind, match.Kind);
            Assert.Equal(secret, text.Substring(match.Start, match.Length));
        }

        using var session = new RedactionSession();
        Assert.Equal($"<redacted:{kind.ToId()}>", session.Redact(secret));
    }

    [Fact]
    public void SecretStartingAtIndexZero_AndEndingAtTheLastCharacter_AreReplaced()
    {
        using var session = new RedactionSession();
        var key = SecretFactory.Create(SecretKind.AwsAccessKey, 3);

        Assert.Equal("<redacted:aws-access-key>", session.Redact(key));
        Assert.Equal("a <redacted:aws-access-key>", session.Redact("a " + key));
        Assert.Equal("<redacted:aws-access-key> a", session.Redact(key + " a"));
    }

    [Fact]
    public void SecretsInsideMultiLineStrings_AreRedacted()
    {
        var key = SecretFactory.Create(SecretKind.AwsAccessKey, 0);
        var token = SecretFactory.Create(SecretKind.GitHubToken, 0);
        var pem = SecretFactory.Create(SecretKind.PrivateKeyBlock, 0);
        var text = $"var s = @\"line one\n{key}\nline three {token}\n\";\nvar raw = \"\"\"\n    {pem.Replace("\n", "\n    ", StringComparison.Ordinal)}\n    \"\"\";\n";

        using var session = new RedactionSession();
        var redacted = session.Redact(text);

        Assert.DoesNotContain(key, redacted, StringComparison.Ordinal);
        Assert.DoesNotContain(token, redacted, StringComparison.Ordinal);
        Assert.DoesNotContain(SecretFactory.Needle(SecretKind.PrivateKeyBlock, pem), redacted, StringComparison.Ordinal);
        Assert.Contains("line one\n<redacted:aws-access-key>\nline three <redacted:github-token>\n", redacted, StringComparison.Ordinal);
        Assert.Equal(text, session.Restore(redacted));
    }

    [Theory]
    [InlineData("\n")]
    [InlineData("\r\n")]
    public void PrivateKeyBlock_IsRedactedWhole_WithEitherLineEnding(string newline)
    {
        var pem = SecretFactory.Create(SecretKind.PrivateKeyBlock, 0).Replace("\n", newline, StringComparison.Ordinal);
        var text = $"before{newline}{pem}{newline}after{newline}";

        using var session = new RedactionSession();
        var redacted = session.Redact(text);

        Assert.Equal($"before{newline}<redacted:private-key-block>{newline}after{newline}", redacted);
        Assert.Equal(text, session.Restore(redacted));
    }

    [Fact]
    public void LfAndCrlfVersionsOfTheSameText_RedactToTheSamePlaceholdersAndRoundTrip()
    {
        var lf = $"a={SecretFactory.Create(SecretKind.AwsAccessKey, 0)}\nb: \"{SecretFactory.Create(SecretKind.Jwt, 0)}\"\nc=1\n";
        var crlf = lf.Replace("\n", "\r\n", StringComparison.Ordinal);

        using var session = new RedactionSession();
        var redactedLf = session.Redact(lf);
        var redactedCrlf = session.Redact(crlf);

        Assert.Equal(redactedLf.Replace("\n", "\r\n", StringComparison.Ordinal), redactedCrlf);
        Assert.Equal(lf, session.Restore(redactedLf));
        Assert.Equal(crlf, session.Restore(redactedCrlf));
    }

    [Fact]
    public void TruncatedPrivateKey_WithoutEndLine_IsStillRedacted()
    {
        var pem = SecretFactory.Create(SecretKind.PrivateKeyBlock, 0);
        var truncated = pem[..pem.LastIndexOf("-----END", StringComparison.Ordinal)];

        using var session = new RedactionSession();
        var redacted = session.Redact("key:\n" + truncated);

        Assert.DoesNotContain(SecretFactory.Needle(SecretKind.PrivateKeyBlock, pem), redacted, StringComparison.Ordinal);
        Assert.Contains("<redacted:private-key-block>", redacted, StringComparison.Ordinal);
    }

    [Fact]
    public void EmptyString_IsHandledEverywhere()
    {
        using var session = new RedactionSession();

        Assert.Empty(SecretScanner.Scan(string.Empty));
        Assert.Equal(string.Empty, session.Redact(string.Empty));
        Assert.Equal(string.Empty, session.Restore(string.Empty));
        Assert.Empty(session.FindUnresolved(string.Empty));
        Assert.Equal(string.Empty, LogRedactor.Redact(string.Empty));
    }

    [Fact]
    public void VeryLongSingleLine_IsRedactedAndRestored()
    {
        var key = SecretFactory.Create(SecretKind.AwsAccessKey, 0);
        var jwt = SecretFactory.Create(SecretKind.Jwt, 0);
        var filler = new StringBuilder();
        var rng = new Random(5);
        while (filler.Length < 2_000_000)
        {
            filler.Append(SecretFactory.Pick(rng, "abcdefghijklmnopqrstuvwxyz", 8)).Append(' ');
        }

        var half = filler.Length / 2;
        var text = filler.ToString(0, half) + " " + key + " " + filler.ToString(half, filler.Length - half) + " Bearer " + jwt;
        Assert.DoesNotContain('\n', text);

        using var session = new RedactionSession();
        var redacted = session.Redact(text);

        Assert.DoesNotContain(key, redacted, StringComparison.Ordinal);
        Assert.DoesNotContain(jwt, redacted, StringComparison.Ordinal);
        Assert.Equal(text.Length - key.Length - jwt.Length + "<redacted:aws-access-key>".Length + "<redacted:jwt>".Length, redacted.Length);
        Assert.Equal(text, session.Restore(redacted));
    }

    [Fact]
    public void LongSingleLineOfBase64_WithNoSecrets_HasNoMatches()
    {
        var line = SecretFactory.Pick(new Random(8), "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789+/", 1_000_000);

        Assert.Empty(SecretScanner.Scan(line));
        Assert.Empty(SecretScanner.Scan("\"image\": \"data:image/png;base64," + line + "\""));
    }

    [Fact]
    public void ManySecrets_AreAllReplaced()
    {
        var builder = new StringBuilder();
        var expected = new List<string>();
        for (var i = 0; i < 500; i++)
        {
            var key = SecretFactory.Create(SecretKind.AwsAccessKey, i);
            expected.Add(key);
            builder.Append("id").Append(i).Append('=').Append(key).Append('\n');
        }

        using var session = new RedactionSession();
        var redacted = session.Redact(builder.ToString());

        Assert.All(expected, key => Assert.DoesNotContain(key, redacted, StringComparison.Ordinal));
        Assert.Equal(500, session.Redactions.Count);
        Assert.Equal(builder.ToString(), session.Restore(redacted));
        Assert.Contains("<redacted:aws-access-key#500>", redacted, StringComparison.Ordinal);
    }
}

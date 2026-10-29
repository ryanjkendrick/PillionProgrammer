using Pillion.Common.Redaction;

namespace Pillion.Common.Tests.Redaction;

/// <summary>The index's one-way redaction (work item 3.2, issue #103): its placeholder is marked as the index's, never restored, never counted twice.</summary>
public class IndexRedactorTests
{
    [Fact]
    public void Redact_ReplacesSecretsWithIndexPlaceholders_AndCountsThem()
    {
        var key = SecretFactory.Create(SecretKind.AwsAccessKey, 0);
        var jwt = SecretFactory.Create(SecretKind.Jwt, 0);

        var result = IndexRedactor.Redact($"var key = \"{key}\";\nvar token = \"{jwt}\";\n");

        Assert.True(result.IsRedacted);
        Assert.Equal(2, result.Count);
        Assert.Equal("var key = \"<redacted-index:aws-access-key>\";\nvar token = \"<redacted-index:jwt>\";\n", result.Text);
    }

    [Fact]
    public void Redact_TextWithoutSecrets_IsReturnedAsTheSameInstance_WithZero()
    {
        const string text = "public int Add(int a, int b) => a + b;";

        var result = IndexRedactor.Redact(text);

        Assert.False(result.IsRedacted);
        Assert.Equal(0, result.Count);
        Assert.Same(text, result.Text);
        Assert.Same(string.Empty, IndexRedactor.Redact(string.Empty).Text);
    }

    [Fact]
    public void AnIndexPlaceholder_IsNotASecretToTheScanner_SoARetrievedChunkIsNotRedactedAgain()
    {
        var stored = IndexRedactor.Redact($"apiKey = \"{SecretFactory.Create(SecretKind.GenericHighEntropy, 0)}\"; aws = \"{SecretFactory.Create(SecretKind.AwsAccessKey, 1)}\"").Text;
        Assert.Contains("<redacted-index:", stored, StringComparison.Ordinal);

        Assert.Empty(SecretScanner.Scan(stored));
        Assert.Equal(stored, IndexRedactor.Redact(stored).Text);
        Assert.Same(stored, LogRedactor.Redact(stored));

        using var session = new RedactionSession();
        Assert.Same(stored, session.Redact(stored));
        Assert.Equal(0, session.Redactions.Count);
    }

    [Fact]
    public void ASession_NeverRestoresAnIndexPlaceholder_AndDoesNotReportItAsUnresolved()
    {
        using var session = new RedactionSession();
        var redacted = session.Redact($"key {SecretFactory.Create(SecretKind.AwsAccessKey, 0)}");
        Assert.StartsWith("key <redacted:aws-access-key>", redacted, StringComparison.Ordinal);

        var output = redacted + " and from the index <redacted-index:aws-access-key> <redacted-index:aws-access-key#2>";
        var restored = session.Restore(output);

        Assert.DoesNotContain("<redacted:", restored, StringComparison.Ordinal);
        Assert.Contains("<redacted-index:aws-access-key> <redacted-index:aws-access-key#2>", restored, StringComparison.Ordinal);
        Assert.Empty(session.FindUnresolved(output));
        Assert.Equal(["<redacted-index:aws-access-key>", "<redacted-index:aws-access-key#2>"], IndexRedactor.FindPlaceholders(output));
    }

    [Theory]
    [InlineData("<redacted-index:aws-access-key>", true)]
    [InlineData("text <redacted-index:scan-failed> text", true)]
    [InlineData("<redacted:aws-access-key>", false)]
    [InlineData("<redacted-index:>", true)]
    [InlineData("<redacted-index:no closing", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void ContainsPlaceholder_RecognisesTheIndexFormOnly(string? text, bool expected)
    {
        Assert.Equal(expected, IndexRedactor.ContainsPlaceholder(text));
    }

    [Fact]
    public void Redact_IsIdempotent_AndKeepsLineStructure()
    {
        var text = $"line1\nPassword={SecretFactory.Create(SecretKind.ConnectionStringPassword, 0)};\nline3";

        var once = IndexRedactor.Redact(text);
        var twice = IndexRedactor.Redact(once.Text);

        Assert.Equal(once.Text, twice.Text);
        Assert.Equal(0, twice.Count);
        Assert.Equal(3, once.Text.Split('\n').Length);
    }
}

using Pillion.Common.Redaction;

namespace Pillion.Common.Tests.Redaction;

public class LogRedactorTests
{
    [Fact]
    public void Redact_ReplacesSecretsWithKindPlaceholders_NumberingNothing()
    {
        var one = SecretFactory.Create(SecretKind.AwsAccessKey, 0);
        var two = SecretFactory.Create(SecretKind.AwsAccessKey, 1);
        var jwt = SecretFactory.Create(SecretKind.Jwt, 0);

        var redacted = LogRedactor.Redact($"keys {one} {two} jwt {jwt} end");

        Assert.Equal("keys <redacted:aws-access-key> <redacted:aws-access-key> jwt <redacted:jwt> end", redacted);
    }

    [Fact]
    public void Redact_TextWithoutSecrets_IsReturnedAsTheSameInstance()
    {
        const string text = "Indexed 120 files in 3.2 s";

        Assert.Same(text, LogRedactor.Redact(text));
    }

    [Fact]
    public void Redact_NullAndEmpty_PassThrough()
    {
        Assert.Null(LogRedactor.Redact(null));
        Assert.Equal(string.Empty, LogRedactor.Redact(string.Empty));
    }

    [Fact]
    public void Redact_IsIdempotent()
    {
        var once = LogRedactor.Redact($"Authorization: Bearer {SecretFactory.Create(SecretKind.Jwt, 2)}");

        Assert.Equal(once, LogRedactor.Redact(once));
    }

    [Fact]
    public void Redact_HandlesPrivateKeysAndConnectionStrings()
    {
        var pem = SecretFactory.Create(SecretKind.PrivateKeyBlock, 1);
        var password = SecretFactory.Create(SecretKind.ConnectionStringPassword, 0);

        var redacted = LogRedactor.Redact($"loaded\n{pem}\nServer=db;Password={password};");

        Assert.Equal("loaded\n<redacted:private-key-block>\nServer=db;Password=<redacted:connection-string-password>;", redacted);
    }
}

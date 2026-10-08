namespace Pillion.Common.Redaction;

/// <summary>The kinds of secret that <see cref="SecretScanner"/> recognises.</summary>
/// <remarks>
/// Use <see cref="SecretKindExtensions.ToId(SecretKind)"/> for the stable string id that appears in placeholders
/// (<c>&lt;redacted:aws-access-key&gt;</c>), logs and the Context Inspector.
/// </remarks>
public enum SecretKind
{
    /// <summary>AWS access key id (<c>AKIA</c>, <c>ASIA</c>, <c>ABIA</c>, <c>ACCA</c> prefixes).</summary>
    AwsAccessKey,

    /// <summary>AWS secret access key (40 characters) assigned to an AWS secret-key style name.</summary>
    AwsSecretKey,

    /// <summary>Azure storage account key (88 characters ending in <c>==</c>) assigned to a storage or account key name.</summary>
    AzureStorageKey,

    /// <summary>The key material in an Azure connection string (<c>AccountKey=</c>, <c>SharedAccessKey=</c>).</summary>
    AzureConnectionString,

    /// <summary>Google API key (<c>AIza</c> followed by 35 characters).</summary>
    GcpApiKey,

    /// <summary>Google service account JSON key: the <c>private_key</c> and <c>private_key_id</c> values.</summary>
    GcpServiceAccount,

    /// <summary>GitHub token (<c>ghp_</c>, <c>gho_</c>, <c>ghu_</c>, <c>ghs_</c>, <c>ghr_</c>, <c>github_pat_</c>).</summary>
    GitHubToken,

    /// <summary>Slack API token (<c>xoxb-</c>, <c>xoxp-</c>, <c>xoxa-</c>, <c>xoxr-</c>, <c>xoxs-</c>).</summary>
    SlackToken,

    /// <summary>Slack incoming webhook URL.</summary>
    SlackWebhook,

    /// <summary>Stripe key (<c>sk_live_</c>, <c>sk_test_</c>, <c>rk_live_</c>, <c>pk_live_</c> and the other live/test pairs).</summary>
    StripeKey,

    /// <summary>OpenAI API key (<c>sk-...</c> or <c>sk-proj-...</c>).</summary>
    OpenAiKey,

    /// <summary>Anthropic API key (<c>sk-ant-...</c>).</summary>
    AnthropicKey,

    /// <summary>JSON Web Token: three base64url segments starting with an <c>eyJ</c> header.</summary>
    Jwt,

    /// <summary>A password in a connection string (<c>Password=</c>, <c>Pwd=</c>) or in a URL (<c>://user:password@host</c>).</summary>
    ConnectionStringPassword,

    /// <summary>A PEM private key block (RSA, EC, DSA, OPENSSH, PGP, encrypted or plain PKCS#8).</summary>
    PrivateKeyBlock,

    /// <summary>A 32+ character high-entropy value assigned to a name that suggests a credential.</summary>
    GenericHighEntropy,
}

/// <summary>Helpers for <see cref="SecretKind"/>.</summary>
public static class SecretKindExtensions
{
    /// <summary>Gets the stable kebab-case id of <paramref name="kind"/>, for example <c>aws-access-key</c>.</summary>
    /// <param name="kind">The kind.</param>
    /// <returns>The id used in placeholders, logs and the Context Inspector.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="kind"/> is not a defined value.</exception>
    public static string ToId(this SecretKind kind) => kind switch
    {
        SecretKind.AwsAccessKey => "aws-access-key",
        SecretKind.AwsSecretKey => "aws-secret-key",
        SecretKind.AzureStorageKey => "azure-storage-key",
        SecretKind.AzureConnectionString => "azure-connection-string",
        SecretKind.GcpApiKey => "gcp-api-key",
        SecretKind.GcpServiceAccount => "gcp-service-account",
        SecretKind.GitHubToken => "github-token",
        SecretKind.SlackToken => "slack-token",
        SecretKind.SlackWebhook => "slack-webhook",
        SecretKind.StripeKey => "stripe-key",
        SecretKind.OpenAiKey => "openai-key",
        SecretKind.AnthropicKey => "anthropic-key",
        SecretKind.Jwt => "jwt",
        SecretKind.ConnectionStringPassword => "connection-string-password",
        SecretKind.PrivateKeyBlock => "private-key-block",
        SecretKind.GenericHighEntropy => "generic-high-entropy",
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unknown secret kind."),
    };

    /// <summary>Parses an id produced by <see cref="ToId(SecretKind)"/>.</summary>
    /// <param name="id">The kebab-case id.</param>
    /// <param name="kind">The parsed kind when the method returns <see langword="true"/>.</param>
    /// <returns><see langword="true"/> when <paramref name="id"/> names a known kind.</returns>
    public static bool TryParseId(string? id, out SecretKind kind)
    {
        foreach (var candidate in Enum.GetValues<SecretKind>())
        {
            if (string.Equals(candidate.ToId(), id, StringComparison.Ordinal))
            {
                kind = candidate;
                return true;
            }
        }

        kind = default;
        return false;
    }
}

using System.Text;
using Pillion.Common.Redaction;

namespace Pillion.Common.Tests.Redaction;

/// <summary>
/// Builds fabricated secrets of every kind, deterministically. Nothing here is a real credential; the values only
/// have the right shape. They are assembled at run time so that no complete secret-shaped literal is committed to
/// the repository (which would trip push protection and secret scanners).
/// </summary>
internal static class SecretFactory
{
    private const string Upper = "ABCDEFGHIJKLMNOPQRSTUVWXYZ";
    private const string Lower = "abcdefghijklmnopqrstuvwxyz";
    private const string Digits = "0123456789";
    private const string Alnum = Upper + Lower + Digits;
    private const string Base64 = Alnum + "+/";
    private const string Base64Url = Alnum + "-_";
    private const string Base32 = Upper + "234567";
    private const string Hex = "0123456789abcdef";
    private const string PasswordChars = Alnum + "_-!^";

    private static readonly string[] PemLabels =
    [
        "RSA PRIVATE KEY", "EC PRIVATE KEY", "OPENSSH PRIVATE KEY", "PRIVATE KEY", "PGP PRIVATE KEY BLOCK", "ENCRYPTED PRIVATE KEY",
    ];

    /// <summary>Gets a deterministic generator for <paramref name="kind"/> and <paramref name="variant"/>.</summary>
    public static Random Rng(SecretKind kind, int variant) => new(unchecked(((int)kind * 7919) + (variant * 104729) + 17));

    /// <summary>
    /// Creates a fabricated secret value of <paramref name="kind"/>. Different variants give different values.
    /// For <see cref="SecretKind.GcpServiceAccount"/> this is the JSON-escaped <c>private_key</c> string content
    /// (use <see cref="GcpKeyId"/> for the key id). For <see cref="SecretKind.PrivateKeyBlock"/> it is a multi-line
    /// PEM block with LF newlines.
    /// </summary>
    public static string Create(SecretKind kind, int variant)
    {
        var rng = Rng(kind, variant);
        switch (kind)
        {
            case SecretKind.AwsAccessKey:
                return "AKIA" + Pick(rng, Base32, 16);
            case SecretKind.AwsSecretKey:
                return Pick(rng, Alnum + "/+", 40);
            case SecretKind.AzureStorageKey:
                return Pick(rng, Base64, 86) + "==";
            case SecretKind.AzureConnectionString:
                return Pick(rng, Base64, 43) + "=";
            case SecretKind.GcpApiKey:
                return "AIza" + Pick(rng, Base64Url, 35);
            case SecretKind.GcpServiceAccount:
                return (Pem(rng, "PRIVATE KEY", 12) + "\n").Replace("\n", "\\n", StringComparison.Ordinal);
            case SecretKind.GitHubToken:
                string[] prefixes = ["ghp_", "gho_", "ghu_", "ghs_", "ghr_"];
                var which = variant % 6;
                return which < 5
                    ? prefixes[which] + Pick(rng, Alnum, 36)
                    : "github_pat_" + Pick(rng, Alnum, 22) + "_" + Pick(rng, Alnum, 59);
            case SecretKind.SlackToken:
                return "xox" + "baprs"[variant % 5] + "-" + Pick(rng, Digits, 12) + "-" + Pick(rng, Digits, 13) + "-" + Pick(rng, Alnum, 24);
            case SecretKind.SlackWebhook:
                return "https://hooks.slack.com/services/T" + Pick(rng, Upper + Digits, 8) + "/B" + Pick(rng, Upper + Digits, 8) + "/" + Pick(rng, Alnum, 24);
            case SecretKind.StripeKey:
                string[] stripe = ["sk_live_", "sk_test_", "rk_live_", "pk_live_"];
                return stripe[variant % 4] + Pick(rng, Alnum, 24);
            case SecretKind.OpenAiKey:
                return variant % 2 == 0 ? "sk-" + Pick(rng, Alnum, 48) : "sk-proj-" + Pick(rng, Base64Url, 74);
            case SecretKind.AnthropicKey:
                return "sk-ant-api03-" + Pick(rng, Base64Url, 90) + "AA";
            case SecretKind.Jwt:
                return Jwt(rng);
            case SecretKind.ConnectionStringPassword:
                return Pick(rng, Alnum, 1) + Pick(rng, PasswordChars, 11);
            case SecretKind.PrivateKeyBlock:
                return Pem(rng, PemLabels[variant % PemLabels.Length], 10);
            case SecretKind.GenericHighEntropy:
                var chars = Pick(rng, Alnum, 40).ToCharArray();
                chars[3] = Digits[rng.Next(Digits.Length)];
                chars[4] = Upper[rng.Next(Upper.Length)];
                return new string(chars);
            default:
                throw new ArgumentOutOfRangeException(nameof(kind));
        }
    }

    /// <summary>Creates a fabricated <c>private_key_id</c> (40 hex characters).</summary>
    public static string GcpKeyId(int variant) => Pick(Rng(SecretKind.GcpServiceAccount, 1000 + variant), Hex, 40);

    /// <summary>Gets a substring of the secret that must not survive redaction (the whole value, or one PEM body line).</summary>
    public static string Needle(SecretKind kind, string value)
    {
        if (kind is SecretKind.PrivateKeyBlock or SecretKind.GcpServiceAccount)
        {
            var separator = kind == SecretKind.GcpServiceAccount ? "\\n" : "\n";
            return value.Split(separator)[1];
        }

        return value;
    }

    /// <summary>Picks <paramref name="count"/> characters from <paramref name="alphabet"/>.</summary>
    public static string Pick(Random rng, string alphabet, int count)
    {
        var builder = new StringBuilder(count);
        for (var i = 0; i < count; i++)
        {
            builder.Append(alphabet[rng.Next(alphabet.Length)]);
        }

        return builder.ToString();
    }

    /// <summary>Builds a PEM block with 64-character base64 body lines and LF newlines.</summary>
    public static string Pem(Random rng, string label, int bodyLines)
    {
        var builder = new StringBuilder();
        builder.Append("-----BEGIN ").Append(label).Append("-----\n");
        for (var i = 0; i < bodyLines; i++)
        {
            builder.Append(Pick(rng, Base64, i == bodyLines - 1 ? 44 : 64)).Append('\n');
        }

        builder.Append("-----END ").Append(label).Append("-----");
        return builder.ToString();
    }

    private static string Jwt(Random rng)
    {
        var header = Base64UrlEncode("{\"alg\":\"HS256\",\"typ\":\"JWT\"}");
        var payload = Base64UrlEncode($"{{\"sub\":\"{Pick(rng, Alnum, 12)}\",\"name\":\"Test User\",\"iat\":{1700000000 + rng.Next(1000000)}}}");
        return header + "." + payload + "." + Pick(rng, Base64Url, 43);
    }

    private static string Base64UrlEncode(string json) =>
        Convert.ToBase64String(Encoding.UTF8.GetBytes(json)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}

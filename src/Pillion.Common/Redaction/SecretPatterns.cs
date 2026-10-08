using System.Text.RegularExpressions;

namespace Pillion.Common.Redaction;

/// <summary>One detection rule: a compiled regex, a priority for overlap resolution and optional validation.</summary>
/// <param name="Kind">The kind reported for matches.</param>
/// <param name="Priority">
/// Lower wins when two matches overlap. 0 is whole-block secrets, 1 is self-identifying token formats, 2 is
/// formats that need a name or key context, 3 is the heuristic catch-all.
/// </param>
/// <param name="Regex">
/// The compiled regex. Its secret value is always in the named group <c>v</c>; only that span is reported.
/// </param>
/// <param name="Severity">The severity of matches (see <paramref name="SeverityOf"/> to compute it per match).</param>
/// <param name="Accept">Optional extra check on the value; return <see langword="false"/> to discard the match.</param>
/// <param name="SeverityOf">Optional per-match severity, for kinds whose severity depends on the value.</param>
internal sealed record SecretPattern(
    SecretKind Kind,
    int Priority,
    Regex Regex,
    SecretSeverity Severity,
    Func<string, bool>? Accept = null,
    Func<string, SecretSeverity>? SeverityOf = null);

/// <summary>The pattern table. Keep README.md in this folder in step with it.</summary>
internal static class SecretPatterns
{
    /// <summary>Shannon entropy (bits per character) a generic value must exceed.</summary>
    public const double GenericEntropyThreshold = 3.0;

    // Fragments shared by the generic pattern. "Kw" is the name keyword; the value classes are the characters
    // that can appear in base64, base64url and hex secrets.
    private const string Kw = "(?:key|secret|token|passw(?:or)?d|pwd|credential)";
    private const string ValueClass = @"[A-Za-z0-9+/=_\-]";
    private const string Quoted = "(?>" + ValueClass + "{32,8192})";
    private const string Unquoted = @"(?>[A-Za-z0-9+/_\-]" + ValueClass + "{31,8191})";

    private static readonly SecretPattern[] AllPatterns =
    [
        // Tier 0: whole-block secrets.
        new(
            SecretKind.PrivateKeyBlock,
            0,
            Make(
                @"(?<v>-----BEGIN (?<t>[A-Z0-9 ]{0,30}?PRIVATE KEY(?: BLOCK)?)-----(?:(?:(?!-----BEGIN )[\s\S]){0,65536}?-----END \k<t>-----|(?:[ \t]*(?:\r?\n|\\n)[ \t]*[A-Za-z0-9+/=]{16,}[ \t]*)+))"),
            SecretSeverity.High),

        // The id and the private key of a service account JSON file. The key is a single-line JSON string with
        // escaped newlines; unescaped PEM blocks in other formats are caught by private-key-block.
        new(
            SecretKind.GcpServiceAccount,
            0,
            Make(
                @"[""']?private_key_id[""']?\s*[:=]\s*[""']?(?<v>[0-9a-f]{40})(?![0-9A-Za-z])"
                + @"|[""']?private_key[""']?\s*:\s*""(?<v>-----BEGIN [A-Z0-9 ]{0,30}?PRIVATE KEY-----(?:(?!-----BEGIN )[^""]){20,16384}?-----END [A-Z0-9 ]{0,30}?PRIVATE KEY-----(?:\\n)?)""",
                ignoreCase: true),
            SecretSeverity.High),

        // Tier 1: formats that identify themselves.
        new(
            SecretKind.AwsAccessKey,
            1,
            Make(@"(?<![A-Za-z0-9])(?<v>(?:AKIA|ASIA|ABIA|ACCA)[A-Z2-7]{16})(?![A-Za-z0-9])"),
            SecretSeverity.High),
        new(
            SecretKind.GitHubToken,
            1,
            Make(@"(?<![A-Za-z0-9_])(?<v>gh[pousr]_[A-Za-z0-9]{36,255}|github_pat_[A-Za-z0-9]{20,40}_[A-Za-z0-9]{40,100})(?![A-Za-z0-9_])"),
            SecretSeverity.High),
        new(
            SecretKind.SlackToken,
            1,
            Make(@"(?<![A-Za-z0-9])(?<v>xox[baprs]-(?=[A-Za-z0-9-]{20,})[A-Za-z0-9]+(?:-[A-Za-z0-9]+){1,6})(?![A-Za-z0-9-])"),
            SecretSeverity.High),
        new(
            SecretKind.SlackWebhook,
            1,
            Make(@"(?<v>https://hooks\.slack\.com/(?:services|workflows|triggers)/[A-Za-z0-9/_\-]{20,})", ignoreCase: true),
            SecretSeverity.Medium),
        new(
            SecretKind.StripeKey,
            1,
            Make(@"(?<![A-Za-z0-9_])(?<v>(?:sk|rk|pk)_(?:live|test)_[A-Za-z0-9]{16,99})(?![A-Za-z0-9_])"),
            SecretSeverity.High,
            SeverityOf: StripeSeverity),
        new(
            SecretKind.AnthropicKey,
            1,
            Make(@"(?<![A-Za-z0-9_\-])(?<v>sk-ant-[A-Za-z0-9_\-]{32,300})(?![A-Za-z0-9_\-])"),
            SecretSeverity.High),
        new(
            SecretKind.OpenAiKey,
            1,
            Make(@"(?<![A-Za-z0-9_\-])(?<v>sk-(?!ant-)(?:(?:proj|svcacct|admin)-[A-Za-z0-9_\-]{32,300}|[A-Za-z0-9]{32,300}))(?![A-Za-z0-9_\-])"),
            SecretSeverity.High),
        new(
            SecretKind.GcpApiKey,
            1,
            Make(@"(?<![A-Za-z0-9_\-])(?<v>AIza[0-9A-Za-z_\-]{35})(?![A-Za-z0-9_\-])"),
            SecretSeverity.Medium),
        new(
            SecretKind.Jwt,
            1,
            Make(@"(?<![A-Za-z0-9_\-])(?<v>eyJ[A-Za-z0-9_\-]{8,}\.[A-Za-z0-9_\-]{8,}\.[A-Za-z0-9_\-]{8,})(?![A-Za-z0-9_\-])"),
            SecretSeverity.Medium),

        // Tier 2: formats that need a name or key context.
        new(
            SecretKind.AzureConnectionString,
            2,
            Make(
                @"(?<![A-Za-z0-9_.])(?:AccountKey|SharedAccessKey)[ \t]*=[ \t]*(?<v>[A-Za-z0-9+/]{20,128}={0,2})(?![A-Za-z0-9+/=])",
                ignoreCase: true),
            SecretSeverity.High),
        new(
            SecretKind.AzureStorageKey,
            2,
            Make(
                @"(?:storage|account)[A-Za-z0-9_.\-]{0,20}key[""']?\s*(?:=|:)\s*[""']?(?<v>[A-Za-z0-9+/]{86}==)(?![A-Za-z0-9+/=])",
                ignoreCase: true),
            SecretSeverity.High),
        new(
            SecretKind.AwsSecretKey,
            2,
            Make(
                @"(?:aws[_\-. ]?secret[_\-. ]?(?:access[_\-. ]?)?key|secret[_\-. ]?access[_\-. ]?key)[""']?\s*(?:=|:)\s*[""']?(?<v>[A-Za-z0-9/+]{40})(?![A-Za-z0-9/+=])",
                ignoreCase: true),
            SecretSeverity.High),
        new(
            SecretKind.ConnectionStringPassword,
            2,
            Make(
                @"(?<![A-Za-z0-9.])(?:password|pwd)=(?:'(?<v>[^'\r\n]{3,128})'|(?<v>[^\s;""'<>{}$%@*()\[\],][^\s;""'<>()\[\],]{2,255}))"
                + @"|://[^\s:/?#@""'<>]{1,64}:(?<v>[^\s@/?#""'<>{}$]{3,128})@",
                ignoreCase: true),
            SecretSeverity.High,
            Accept: AcceptPassword),

        // Tier 3: heuristic catch-all for credential-named assignments.
        new(
            SecretKind.GenericHighEntropy,
            3,
            Make(
                Kw + @"[A-Za-z0-9_.\-]{0,40}[""']?(?:(?:[ \t]*:[ \t]*[A-Za-z_][A-Za-z0-9_<>\[\]]{0,30})?\s*(?:=>|:=|=)|\s*:)\s*"
                + @"(?:""(?<v>" + Quoted + @")""|'(?<v>" + Quoted + @")'|`(?<v>" + Quoted + @")`"
                + @"|(?<v>" + Unquoted + @")(?![(\[<]|\.\w))"
                + @"|<add\s+key\s*=\s*""[^""]{0,80}?" + Kw + @"[^""]{0,80}""\s+value\s*=\s*""(?<v>" + Quoted + @")"""
                + @"|<add\s+value\s*=\s*""(?<v>" + Quoted + @")""\s+key\s*=\s*""[^""]{0,80}?" + Kw + @"[^""]{0,80}"""
                + @"|<(?<n>[A-Za-z0-9_.\-]{0,40}" + Kw + @"[A-Za-z0-9_.\-]{0,40})>\s*(?<v>" + Quoted + @")\s*</\k<n>>",
                ignoreCase: true),
            SecretSeverity.Medium,
            Accept: AcceptGeneric),
    ];

    /// <summary>Gets all patterns, ordered by priority.</summary>
    public static IReadOnlyList<SecretPattern> All => AllPatterns;

    /// <summary>Shannon entropy of <paramref name="value"/> in bits per character.</summary>
    /// <param name="value">An ASCII string.</param>
    /// <returns>The entropy, 0 for empty or single-symbol strings.</returns>
    public static double ShannonEntropy(string value)
    {
        if (value.Length == 0)
        {
            return 0;
        }

        var counts = new Dictionary<char, int>();
        foreach (var c in value)
        {
            counts.TryGetValue(c, out var n);
            counts[c] = n + 1;
        }

        var entropy = 0.0;
        foreach (var n in counts.Values)
        {
            var p = (double)n / value.Length;
            entropy -= p * Math.Log2(p);
        }

        return entropy;
    }

    private static Regex Make(string pattern, bool ignoreCase = false)
    {
        var options = RegexOptions.Compiled | RegexOptions.CultureInvariant;
        if (ignoreCase)
        {
            options |= RegexOptions.IgnoreCase;
        }

        return new Regex(pattern, options, SecretScanner.MatchTimeout);
    }

    private static SecretSeverity StripeSeverity(string value) =>
        value.StartsWith("pk_", StringComparison.Ordinal)
            ? SecretSeverity.Low
            : value.Contains("_test_", StringComparison.Ordinal) ? SecretSeverity.Medium : SecretSeverity.High;

    /// <summary>
    /// Extra checks for the generic pattern: letters and digits mixed, entropy above the threshold, and not a GUID
    /// or a hyphen/underscore separated phrase (<c>my-service-secret-store-v2</c>).
    /// </summary>
    private static bool AcceptGeneric(string value)
    {
        var hasLetter = false;
        var hasDigit = false;
        foreach (var c in value)
        {
            if (char.IsAsciiDigit(c))
            {
                hasDigit = true;
            }
            else if (char.IsAsciiLetter(c))
            {
                hasLetter = true;
            }
        }

        if (!hasLetter || !hasDigit)
        {
            return false;
        }

        return ShannonEntropy(value) > GenericEntropyThreshold && !IsGuid(value) && !LooksLikeWords(value);
    }

    /// <summary>
    /// Rejects values that are clearly code rather than a password: a dotted member access such as
    /// <c>settings.DbPassword</c> or <c>Environment.GetEnvironmentVariable</c>.
    /// </summary>
    private static bool AcceptPassword(string value)
    {
        if (!value.Contains('.', StringComparison.Ordinal))
        {
            return true;
        }

        foreach (var part in value.Split('.'))
        {
            if (part.Length == 0 || !(char.IsAsciiLetter(part[0]) || part[0] == '_'))
            {
                return true;
            }

            foreach (var c in part)
            {
                if (!(char.IsAsciiLetterOrDigit(c) || c == '_'))
                {
                    return true;
                }
            }
        }

        return false;
    }

    private static bool IsGuid(string value) => value.Length == 36 && Guid.TryParseExact(value, "D", out _);

    /// <summary>
    /// True when the value splits on <c>-</c> and <c>_</c> into at least three segments of which 75% or more are
    /// plain words (all lower, Capitalised, all upper) or plain numbers. Random base64url segments essentially
    /// never look like that.
    /// </summary>
    private static bool LooksLikeWords(string value)
    {
        var segments = value.Split(['-', '_'], StringSplitOptions.RemoveEmptyEntries);
        var total = 0;
        var words = 0;
        foreach (var segment in segments)
        {
            if (segment.Length < 2)
            {
                continue;
            }

            total++;
            if (IsWordLike(segment))
            {
                words++;
            }
        }

        return total >= 3 && words * 4 >= total * 3;
    }

    private static bool IsWordLike(string segment)
    {
        var allDigits = true;
        var allLetters = true;
        var upper = 0;
        var lower = 0;
        foreach (var c in segment)
        {
            if (char.IsAsciiDigit(c))
            {
                allLetters = false;
            }
            else if (char.IsAsciiLetterUpper(c))
            {
                allDigits = false;
                upper++;
            }
            else if (char.IsAsciiLetterLower(c))
            {
                allDigits = false;
                lower++;
            }
            else
            {
                return false;
            }
        }

        if (allDigits)
        {
            return true;
        }

        return allLetters && (upper == 0 || lower == 0 || (upper == 1 && char.IsAsciiLetterUpper(segment[0])));
    }
}

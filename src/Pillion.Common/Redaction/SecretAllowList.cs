using System.Text.RegularExpressions;

namespace Pillion.Common.Redaction;

/// <summary>
/// Well-known values that look like secrets but are not, so they are left alone. The list is deliberately small;
/// every rule is documented in the Redaction README.
/// </summary>
internal static class SecretAllowList
{
    /// <summary>Exact public documentation / emulator values.</summary>
    private static readonly HashSet<string> WellKnownValues = new(StringComparer.Ordinal)
    {
        // AWS documentation examples.
        "AKIAIOSFODNN7EXAMPLE",
        "wJalrXUtnFEMI/K7MDENG/bPxRfiCYEXAMPLEKEY",

        // The publicly documented Azurite / storage emulator account key (devstoreaccount1).
        "Eby8vdM02xNOcqFlqUwJPLlmEtlCDXJ1OUzFT50uSRZ6IFsuFq2UVErCz4I6tq/K1SZFPTOtr/KBHBeksoGMGw==",

        // The key shown throughout Stripe's documentation.
        "sk_test_4eC39HqLyjWDarjtT1zdp7dc",
    };

    /// <summary>Whole-value placeholder words that are common as passwords in documentation and templates.</summary>
    private static readonly HashSet<string> PlaceholderWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "password", "passwd", "pass", "pwd", "secret", "changeme", "change-me", "redacted", "null", "none",
    };

    /// <summary>A placeholder word appearing as a whole word (not flanked by letters) anywhere in the value.</summary>
    private static readonly Regex PlaceholderShape = new(
        @"(?<![A-Za-z])(?:your|insert|replace|placeholder|changeme|dummy|fake|redacted|example|sample|x{3,})(?![A-Za-z])",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
        SecretScanner.MatchTimeout);

    /// <summary>Returns <see langword="true"/> when <paramref name="value"/> is a known non-secret.</summary>
    /// <param name="value">The candidate secret value (not the surrounding context).</param>
    /// <returns>Whether the value is allow-listed.</returns>
    public static bool IsAllowed(string value)
    {
        if (value.Length == 0)
        {
            return true;
        }

        if (WellKnownValues.Contains(value))
        {
            return true;
        }

        // AWS documentation convention: example credentials end in EXAMPLE or EXAMPLEKEY.
        if (value.EndsWith("EXAMPLE", StringComparison.Ordinal) || value.EndsWith("EXAMPLEKEY", StringComparison.Ordinal))
        {
            return true;
        }

        // Anything that already looks like one of our placeholders.
        if (value.StartsWith("<redacted:", StringComparison.Ordinal))
        {
            return true;
        }

        return IsFiller(value) || PlaceholderWords.Contains(value) || PlaceholderShape.IsMatch(value);
    }

    /// <summary>
    /// True for values that are one repeated character (<c>0000</c>, <c>xxxx</c>, <c>****</c>), or, for values of 16
    /// or more characters, dominated by one character (60% or more), which catches <c>AKIAXXXXXXXXXXXXXXXX</c> and
    /// <c>ghp_xxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxx</c>.
    /// </summary>
    private static bool IsFiller(string value)
    {
        var first = value[0];
        var allSame = true;
        foreach (var c in value)
        {
            if (c != first)
            {
                allSame = false;
                break;
            }
        }

        if (allSame)
        {
            return true;
        }

        if (value.Length < 16)
        {
            return false;
        }

        var counts = new Dictionary<char, int>();
        var max = 0;
        foreach (var c in value)
        {
            counts.TryGetValue(c, out var n);
            n++;
            counts[c] = n;
            if (n > max)
            {
                max = n;
            }
        }

        return max * 10 >= value.Length * 6;
    }
}

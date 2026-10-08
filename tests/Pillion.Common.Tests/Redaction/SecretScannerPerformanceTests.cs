using System.Diagnostics;
using System.Text;
using Pillion.Common.Redaction;

namespace Pillion.Common.Tests.Redaction;

public class SecretScannerPerformanceTests
{
    /// <summary>
    /// Smoke test, not a benchmark: a 1 MB file of ordinary code, hashes and base64 (and no secrets) scans in under
    /// 200 ms. The bound is generous for a development machine; the first call is excluded because it compiles the
    /// regexes. If this fails on a loaded CI agent, re-run before suspecting the scanner.
    /// </summary>
    [Fact]
    [Trait("Category", "Smoke")]
    public void OneMegabyteWithNoSecrets_ScansInUnder200Milliseconds()
    {
        var text = BuildRealisticFile(1_048_576);
        _ = SecretScanner.Scan(text); // warm-up: compiles the regexes and lets the JIT tier up

        var best = TimeSpan.MaxValue;
        IReadOnlyList<SecretMatch> matches = [];
        for (var run = 0; run < 3; run++)
        {
            var stopwatch = Stopwatch.StartNew();
            matches = SecretScanner.Scan(text);
            stopwatch.Stop();
            if (stopwatch.Elapsed < best)
            {
                best = stopwatch.Elapsed;
            }
        }

        Assert.Empty(matches);
        Assert.True(best < TimeSpan.FromMilliseconds(200), $"best of 3 runs: {best.TotalMilliseconds:F1} ms for {text.Length} characters");
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public void OneMegabyteWithManySecrets_StillScansQuickly()
    {
        var text = BuildRealisticFile(1_048_576);
        var builder = new StringBuilder(text);
        for (var i = 0; i < 200; i++)
        {
            builder.Append("\nid").Append(i).Append(" = ").Append(SecretFactory.Create(SecretKind.AwsAccessKey, i));
        }

        _ = SecretScanner.Scan("warm up");
        var stopwatch = Stopwatch.StartNew();
        var matches = SecretScanner.Scan(builder.ToString());
        stopwatch.Stop();

        Assert.Equal(200, matches.Count);
        Assert.True(stopwatch.Elapsed < TimeSpan.FromMilliseconds(400), $"{stopwatch.Elapsed.TotalMilliseconds:F1} ms");
    }

    private static string BuildRealisticFile(int length)
    {
        var rng = new Random(12345);
        string[] templates =
        [
            "public async Task<IActionResult> Get(int id, CancellationToken cancellationToken)",
            "    var token = await _tokens.AcquireAsync(scope, cancellationToken).ConfigureAwait(false);",
            "    // The password policy and key rotation rules are documented in docs/security.md",
            "    _logger.LogInformation(\"Fetched {Count} items for key {Key}\", items.Count, key);",
            "    return Ok(new { id, name = \"Quarterly report\", secretCount = 3, tokenType = \"Bearer\" });",
            "<PackageReference Include=\"Microsoft.Extensions.Hosting\" Version=\"10.0.12\" />",
            "    private readonly string _apiKeyHeaderName = \"X-Api-Key\";",
            "https://example.com/api/v1/items?page=2&pageSize=50&sort=name",
        ];

        var builder = new StringBuilder(length + 1024);
        var n = 0;
        while (builder.Length < length)
        {
            switch (n++ % 6)
            {
                case 3:
                    builder.Append("    \"integrity\": \"sha512-").Append(SecretFactory.Pick(rng, "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789+/", 86)).Append("==\",\n");
                    break;
                case 4:
                    builder.Append("    commit ").Append(SecretFactory.Pick(rng, "0123456789abcdef", 40)).Append('\n');
                    break;
                case 5:
                    builder.Append("    <img src=\"data:image/png;base64,").Append(SecretFactory.Pick(rng, "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789+/", 400)).Append("\">\n");
                    break;
                default:
                    builder.Append(templates[rng.Next(templates.Length)]).Append('\n');
                    break;
            }
        }

        return builder.ToString();
    }
}

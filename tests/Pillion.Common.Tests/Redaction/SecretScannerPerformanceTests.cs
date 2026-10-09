using System.Text;
using Pillion.Common.Redaction;

namespace Pillion.Common.Tests.Redaction;

/// <summary>
/// Smoke tests, not benchmarks: each takes the best of three runs after a warm-up (the first call compiles the regexes),
/// runs alone in the test process (see <see cref="PerformanceTestGroup"/>) and has its bound scaled by
/// <see cref="PerformanceBounds"/> on CI. The scanner's own 2 s regex timeout is fail-closed, so a scan that hits it
/// counts as a slow run, not a crash.
/// </summary>
[Collection(PerformanceTestGroup.Name)]
[Trait("Category", "Performance")]
public class SecretScannerPerformanceTests
{
    /// <summary>A 1 MB file of ordinary code, hashes and base64 (and no secrets) scans in under 200 ms on a development machine.</summary>
    [Fact]
    public void OneMegabyteWithNoSecrets_ScansInUnder200Milliseconds()
    {
        var text = BuildRealisticFile(1_048_576);

        var best = PerformanceBounds.BestOf(text, 3, out var matches);

        Assert.Empty(matches);
        Assert.True(best < PerformanceBounds.For(TimeSpan.FromMilliseconds(200)), $"best of 3 runs: {best.TotalMilliseconds:F1} ms for {text.Length} characters");
    }

    [Fact]
    public void OneMegabyteWithManySecrets_StillScansQuickly()
    {
        var text = BuildRealisticFile(1_048_576);
        var builder = new StringBuilder(text);
        for (var i = 0; i < 200; i++)
        {
            builder.Append("\nid").Append(i).Append(" = ").Append(SecretFactory.Create(SecretKind.AwsAccessKey, i));
        }

        var best = PerformanceBounds.BestOf(builder.ToString(), 3, out var matches);

        Assert.Equal(200, matches.Count);
        Assert.True(best < PerformanceBounds.For(TimeSpan.FromMilliseconds(400)), $"best of 3 runs: {best.TotalMilliseconds:F1} ms");
    }

    [Theory]
    [InlineData("keykeykey")]
    [InlineData("token=")]
    [InlineData("password=")]
    [InlineData("://a:")]
    [InlineData("-----BEGIN PRIVATE KEY-----\n")]
    [InlineData("eyJabcdefghij")]
    [InlineData("sk-")]
    [InlineData("AccountKey=")]
    [InlineData("secret: \"")]
    [InlineData("<add key=\"apiKey\" value=\"")]
    public void PathologicalInput_DoesNotTimeOutOrBlowUp(string unit)
    {
        var builder = new StringBuilder();
        while (builder.Length < 1_000_000)
        {
            builder.Append(unit);
        }

        var best = PerformanceBounds.BestOf(builder.ToString(), 3, out _);

        Assert.True(best < PerformanceBounds.For(TimeSpan.FromSeconds(1.5)), $"best of 3 runs: {best.TotalMilliseconds:F0} ms");
    }

    [Fact]
    public void PathologicalSingleRun_OfValueCharacters_IsLinear()
    {
        // One enormous run of base64 characters preceded by a credential-looking name.
        var text = "secret=" + new string('A', 1_000_000);

        var best = PerformanceBounds.BestOf(text, 3, out _);

        Assert.True(best < PerformanceBounds.For(TimeSpan.FromSeconds(1.5)), $"best of 3 runs: {best.TotalMilliseconds:F0} ms");
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

using System.Diagnostics;
using System.Text.RegularExpressions;
using Pillion.Common.Redaction;

namespace Pillion.Common.Tests.Redaction;

/// <summary>
/// Runs the scanner's performance tests one at a time, alone in the test process: xUnit runs a collection that disables
/// parallelization after every parallel test has finished, so the timings are not skewed by the rest of the suite.
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class PerformanceTestGroup
{
    /// <summary>The collection's name.</summary>
    public const string Name = "Performance";
}

/// <summary>
/// Timing helpers for the scanner's performance tests. Shared CI runners are slow and noisy, so the bounds are five times
/// looser there (GitHub Actions sets the <c>CI</c> environment variable); locally they stay tight enough to catch a real
/// regression.
/// </summary>
internal static class PerformanceBounds
{
    private static readonly double Scale = string.IsNullOrEmpty(Environment.GetEnvironmentVariable("CI")) ? 1 : 5;

    /// <summary>Scales a bound for the machine the tests run on.</summary>
    /// <param name="bound">The bound for a development machine.</param>
    /// <returns>The same bound, or five times as long on CI.</returns>
    public static TimeSpan For(TimeSpan bound) => bound * Scale;

    /// <summary>
    /// Scans <paramref name="text"/> once to warm up (the regexes are compiled and the JIT tiers up on first use), then
    /// scans it <paramref name="runs"/> more times and returns the fastest. A run that hits the scanner's own regex timeout
    /// counts as taking that long, so noise on a loaded machine costs one run, while a scanner that really backtracks
    /// exhaustively fails every run.
    /// </summary>
    /// <param name="text">The text to scan.</param>
    /// <param name="runs">How many timed runs.</param>
    /// <param name="matches">The matches of the fastest completed run (empty when every run timed out).</param>
    /// <returns>The fastest time.</returns>
    public static TimeSpan BestOf(string text, int runs, out IReadOnlyList<SecretMatch> matches)
    {
        _ = SecretScanner.Scan("warm up");
        var best = TimeSpan.MaxValue;
        matches = [];
        for (var run = 0; run < runs; run++)
        {
            var stopwatch = Stopwatch.StartNew();
            IReadOnlyList<SecretMatch> result;
            try
            {
                result = SecretScanner.Scan(text);
            }
            catch (RegexMatchTimeoutException)
            {
                best = best < SecretScanner.MatchTimeout ? best : SecretScanner.MatchTimeout;
                continue;
            }

            stopwatch.Stop();
            if (stopwatch.Elapsed < best)
            {
                best = stopwatch.Elapsed;
                matches = result;
            }
        }

        return best;
    }
}

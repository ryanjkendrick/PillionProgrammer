using System.Diagnostics;
using System.Runtime.InteropServices;
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
/// <remarks>
/// A run is timed by the CPU time of the thread that scans (the scanner runs its regexes on the calling thread), not by the
/// wall clock: the other test projects run in their own processes at the same time, and on a loaded machine a scan that
/// costs 300 ms of CPU could take well over a second of wall time (the "pathological input" test failed that way in full
/// parallel runs). The thread's CPU time is counted in clock ticks of about 15.6 ms, which is small beside these bounds.
/// </remarks>
internal static class PerformanceBounds
{
    private static readonly double Scale = string.IsNullOrEmpty(Environment.GetEnvironmentVariable("CI")) ? 1 : 5;

    /// <summary>Scales a bound for the machine the tests run on.</summary>
    /// <param name="bound">The bound for a development machine.</param>
    /// <returns>The same bound, or five times as long on CI.</returns>
    public static TimeSpan For(TimeSpan bound) => bound * Scale;

    /// <summary>
    /// Scans <paramref name="text"/> once to warm up (the regexes are compiled and the JIT tiers up on first use), then
    /// scans it <paramref name="runs"/> more times and returns the fastest, in CPU time of the scanning thread. The
    /// scanner's own regex timeout runs on the wall clock, so a loaded machine can make a run hit it: such a run counts as
    /// taking the timeout and earns one more attempt (at most <paramref name="runs"/> extra), while a scanner that really
    /// backtracks exhaustively times out on every attempt and fails.
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
        var completed = 0;
        for (var attempt = 0; completed < runs && attempt < runs * 2; attempt++)
        {
            var cpuBefore = CurrentThreadCpuTime();
            var wall = Stopwatch.StartNew();
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

            wall.Stop();
            completed++;
            var cpuAfter = CurrentThreadCpuTime();
            var cpu = cpuBefore == TimeSpan.MinValue || cpuAfter == TimeSpan.MinValue ? wall.Elapsed : cpuAfter - cpuBefore;
            if (cpu < TimeSpan.Zero || cpu > wall.Elapsed + TimeSpan.FromMilliseconds(32))
            {
                cpu = wall.Elapsed; // The counter is not usable here: fall back to the wall clock.
            }

            if (cpu < best)
            {
                best = cpu;
                matches = result;
            }
        }

        return best;
    }

    private static TimeSpan CurrentThreadCpuTime()
    {
        if (!OperatingSystem.IsWindows() || !NativeMethods.GetThreadTimes(NativeMethods.GetCurrentThread(), out _, out _, out var kernel, out var user))
        {
            return TimeSpan.MinValue;
        }

        return TimeSpan.FromTicks(kernel + user);
    }

    private static class NativeMethods
    {
        [DllImport("kernel32.dll")]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        public static extern IntPtr GetCurrentThread();

        [DllImport("kernel32.dll", SetLastError = true)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool GetThreadTimes(IntPtr thread, out long creationTime, out long exitTime, out long kernelTime, out long userTime);
    }
}

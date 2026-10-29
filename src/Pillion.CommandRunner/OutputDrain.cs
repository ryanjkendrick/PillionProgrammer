using Pillion.CommandRunner.Native;
using Pillion.CommandRunner.Terminal;

namespace Pillion.CommandRunner;

/// <summary>
/// Ends a pseudo-console's output after its process exited. conhost renders asynchronously and, on the Windows builds
/// Pillion supports, keeps the output pipe open after the last client exits; closing the pseudo-console flushes its last
/// frame but anything still queued behind a large backlog can be lost. So the close waits until the output has been
/// quiet for a moment (or, for a backlog, until it settles, within a bound), then closes and drains to the end of stream.
/// </summary>
internal static class OutputDrain
{
    /// <summary>How long the output must be quiet after the exit before the pseudo-console is closed.</summary>
    internal static readonly TimeSpan QuietPeriod = TimeSpan.FromMilliseconds(200);

    /// <summary>The most time spent waiting for a backlog to settle, and again for the end of stream after the close.</summary>
    internal static readonly TimeSpan Bound = TimeSpan.FromSeconds(5);

    /// <summary>Waits for the output to settle, closes the pseudo-console, drains the pump and disposes the console.</summary>
    /// <param name="console">The pseudo-console whose process has exited.</param>
    /// <param name="terminal">The buffer the pump writes into (its generation shows arrivals).</param>
    /// <param name="pump">The pump task; completes at the end of stream.</param>
    /// <returns>A task that completes when the console is disposed.</returns>
    public static async Task FinishAsync(PseudoConsole console, TerminalBuffer terminal, Task pump)
    {
        var deadline = DateTime.UtcNow + Bound;
        var seen = terminal.Generation;
        var quietSince = DateTime.UtcNow;
        while (!pump.IsCompleted && DateTime.UtcNow < deadline)
        {
            await Task.Delay(25).ConfigureAwait(false);
            var now = terminal.Generation;
            if (now != seen)
            {
                seen = now;
                quietSince = DateTime.UtcNow;
            }
            else if (DateTime.UtcNow - quietSince >= QuietPeriod)
            {
                break;
            }
        }

        console.Close();
        try
        {
            await pump.WaitAsync(Bound).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            // Disposing the output stream unblocks the reader.
        }

        console.Dispose();
    }
}

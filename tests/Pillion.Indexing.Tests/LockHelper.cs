using Pillion.Indexing.Ownership;

namespace Pillion.Indexing.Tests;

/// <summary>
/// The other process of the cross-process lock tests: <c>dotnet Pillion.Indexing.Tests.dll lock-helper acquire &lt;lockPath&gt;
/// &lt;pipeName&gt; &lt;startFile&gt;</c>. It waits for the start file to exist (so several helpers can be released at the same
/// moment), tries to take the lock, reports <c>OWNER &lt;pid&gt;</c> or <c>FOLLOWER &lt;ownerPid&gt; &lt;ownerPipe&gt;</c> on standard
/// output, and, as the owner, keeps the lock until a line arrives on standard input (or the process is killed).
/// </summary>
internal static class LockHelper
{
    public const string Command = "lock-helper";

    public static int Run(string[] args)
    {
        if (args.Length < 4 || args[0] != "acquire")
        {
            Console.Error.WriteLine("usage: lock-helper acquire <lockPath> <pipeName> <startFile>");
            return 2;
        }

        var lockPath = args[1];
        var pipeName = args[2];
        var startFile = args[3];

        var deadline = DateTime.UtcNow.AddSeconds(60);
        while (!File.Exists(startFile))
        {
            if (DateTime.UtcNow > deadline)
            {
                Console.Error.WriteLine("timed out waiting for the start file");
                return 3;
            }

            Thread.Sleep(2);
        }

        var attempt = WriterLock.TryAcquire(lockPath, pipeName);
        if (attempt.Lock is not { } held)
        {
            Console.WriteLine($"FOLLOWER {attempt.Owner!.ProcessId} {attempt.Owner.PipeName}");
            return 0;
        }

        Console.WriteLine($"OWNER {Environment.ProcessId}");
        Console.Out.Flush();
        Console.In.ReadLine();
        held.Dispose();
        Console.WriteLine("RELEASED");
        return 0;
    }
}

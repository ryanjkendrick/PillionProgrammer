namespace Pillion.Indexing.Tests;

/// <summary>
/// Entry point of the test assembly. A normal test run never calls it (the test host loads the assembly); the
/// cross-process lock tests start this assembly with <c>dotnet Pillion.Indexing.Tests.dll lock-helper ...</c>.
/// </summary>
internal static class Program
{
    public static int Main(string[] args) =>
        args.Length > 0 && args[0] == LockHelper.Command ? LockHelper.Run(args[1..]) : 0;
}

using System.Diagnostics;
using System.IO.Pipes;
using System.Text;
using Pillion.Contracts;
using Pillion.FakeHost;
using StreamJsonRpc;

// Usage (the launch protocol): Pillion.FakeHost --pipe <name> --parent-pid <pid> --token-handle <handle> [--log-dir <dir>]
// PILLION_FAKEHOST_MODE selects the behaviour:
//   normal (default)  accept the handshake, report Ready, call the VSIX's services and print the results to stderr
//   reject            answer the handshake with Accepted = false
//   exit              exit with code 3 right after reading the token, before opening the pipe
//   fail              accept the handshake, then report HostState.Failed
string? pipeName = null;
string? tokenHandle = null;
var parentPid = 0;
for (var i = 0; i + 1 < args.Length; i += 2)
{
    switch (args[i])
    {
        case "--pipe":
            pipeName = args[i + 1];
            break;
        case "--parent-pid":
            parentPid = int.Parse(args[i + 1], System.Globalization.CultureInfo.InvariantCulture);
            break;
        case "--token-handle":
            tokenHandle = args[i + 1];
            break;
        case "--log-dir":
            break;
        default:
            Console.Error.WriteLine("fakehost: unknown argument " + args[i]);
            return 2;
    }
}

if (pipeName is null || tokenHandle is null || parentPid == 0)
{
    Console.Error.WriteLine("fakehost: missing --pipe, --parent-pid or --token-handle");
    return 2;
}

string expectedToken;
using (var tokenPipe = new AnonymousPipeClientStream(PipeDirection.In, tokenHandle))
using (var reader = new StreamReader(tokenPipe, new UTF8Encoding(false)))
{
    expectedToken = await reader.ReadLineAsync().ConfigureAwait(false) ?? string.Empty;
}

var mode = Environment.GetEnvironmentVariable("PILLION_FAKEHOST_MODE") ?? "normal";
Console.Error.WriteLine("fakehost: started in mode " + mode + ", token length " + expectedToken.Length);
if (mode == "exit")
{
    return 3;
}

using var parent = Process.GetProcessById(parentPid);
var parentExited = parent.WaitForExitAsync();

await using var server = new NamedPipeServerStream(
    pipeName,
    PipeDirection.InOut,
    1,
    PipeTransmissionMode.Byte,
    PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);

var connected = server.WaitForConnectionAsync();
if (await Task.WhenAny(connected, parentExited).ConfigureAwait(false) != connected)
{
    return 0;
}

var lifecycle = new FakeLifecycle(expectedToken, mode);
using var rpc = new FakeHostJsonRpc(new LengthHeaderMessageHandler(server, server, new MessagePackFormatter()));
rpc.AddLocalRpcTarget<IHostLifecycleService>(lifecycle, null);
lifecycle.Rpc = rpc;
rpc.StartListening();

await Task.WhenAny(rpc.Completion, lifecycle.ShutdownRequested, parentExited).ConfigureAwait(false);
Console.Error.WriteLine("fakehost: exiting");
return 0;

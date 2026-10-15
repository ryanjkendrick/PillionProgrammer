using System.Diagnostics;
using System.IO.Pipes;
using System.Text;
using Pillion.Contracts;
using Pillion.FakeHost;
using StreamJsonRpc;

// Usage (the launch protocol): Pillion.FakeHost --pipe <name> --parent-pid <pid> --token-handle <handle> [--log-dir <dir>]
// Serves IHostLifecycleService, IModelService (FakeModelService: two canned models, a streamed reply naming the model,
// cancellation), IInlineEditService (FakeInlineEditService: canned streamed replacements, ResetText, /explain, an
// error and a slow case), IContextService (FakeContextService: canned mention resolutions), IDiagnosticsService
// (FakeDiagnosticsService: the Outbound records of the chats it served and a canned Context Inspector preview; with
// PILLION_FAKEHOST_RECORD set, each chat request's pins are appended to that file as a JSON line) and ISettingsService (FakeSettingsService: in-memory settings JSON; every save is followed by a\n// settingsChanged notification naming the section). After the handshake it sends
// connectivityChanged (PILLION_FAKEHOST_CONNECTIVITY: Online by default, or Degraded, Offline, LocalOnly).
// Contract 1.3: IConversationService (FakeConversationService: two canned conversations in memory; every chat request
// with a conversation id is appended to it), the index methods of IContextService (PILLION_FAKEHOST_INDEX=ready starts
// with an indexed root; pause, rebuild and delete change the status), and the indexStatusChanged notification, sent after
// every such change and never more than twice a second; SetWorkspaceAsync and the ideActivityChanged, documentSaved and
// openDocumentsChanged notifications are recorded and logged. A chat message containing /cite ends with canned citations.
// PILLION_FAKEHOST_MODE selects the behaviour:
//   normal (default)  accept the handshake, report Ready, call the VSIX's services and print the results to stderr
//   quiet             like normal, but without calling the VSIX's services
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
var recorder = new FakeRecorder();
var conversations = new FakeConversationService();
rpc.AddLocalRpcTarget<IModelService>(new FakeModelService(recorder, conversations), null);
rpc.AddLocalRpcTarget<IInlineEditService>(new FakeInlineEditService(), null);
using var context = new FakeContextService();
rpc.AddLocalRpcTarget<IContextService>(context, null);
rpc.AddLocalRpcTarget<IConversationService>(conversations, null);

// Like Pillion Host, push the index status to the client (indexStatusChanged, one IndexStatus argument).
context.StatusChanged += status => _ = rpc.NotifyAsync("indexStatusChanged", status);

// Like Pillion Host, take the IDE activity the client reports (ideActivityChanged, one IdeActivity argument, contract 1.3).
rpc.AddLocalRpcMethod("ideActivityChanged", new Action<IdeActivity>(context.OnIdeActivityChanged));

// Like Pillion Host, take the documents the client saved (documentSaved, one string argument, contract 1.3, work item 2.10).
rpc.AddLocalRpcMethod("documentSaved", new Action<string>(context.OnDocumentSaved));
rpc.AddLocalRpcMethod("openDocumentsChanged", new Action<string[]>(context.OnOpenDocumentsChanged));
rpc.AddLocalRpcTarget<IDiagnosticsService>(new FakeDiagnosticsService(recorder), null);
var settings = new FakeSettingsService();
rpc.AddLocalRpcTarget<ISettingsService>(settings, null);

// Like Pillion Host, tell the client which top-level section a save changed (settingsChanged, one string-array argument).
settings.SectionChanged += section => _ = rpc.NotifyAsync("settingsChanged", (object)new[] { section });
lifecycle.Rpc = rpc;
rpc.StartListening();

await Task.WhenAny(rpc.Completion, lifecycle.ShutdownRequested, parentExited).ConfigureAwait(false);
Console.Error.WriteLine("fakehost: exiting");
return 0;

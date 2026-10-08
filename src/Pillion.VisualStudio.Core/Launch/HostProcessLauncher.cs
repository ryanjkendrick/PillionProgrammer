using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Security.Principal;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Pillion.VisualStudio.Core;

/// <summary>Settings for <see cref="HostProcessLauncher"/>.</summary>
public sealed class HostLauncherOptions
{
    /// <summary>Initializes a new instance of the <see cref="HostLauncherOptions"/> class.</summary>
    /// <param name="hostExecutablePath">The host executable, normally <c>&lt;extension dir&gt;\Host\Pillion.Host.exe</c>.</param>
    /// <param name="extensionAssemblyPath">The extension assembly whose signer the host must match.</param>
    /// <param name="localTargets">The services exposed to the host.</param>
    public HostLauncherOptions(string hostExecutablePath, string extensionAssemblyPath, HostLocalTargets localTargets)
    {
        HostExecutablePath = hostExecutablePath ?? throw new ArgumentNullException(nameof(hostExecutablePath));
        ExtensionAssemblyPath = extensionAssemblyPath ?? throw new ArgumentNullException(nameof(extensionAssemblyPath));
        LocalTargets = localTargets ?? throw new ArgumentNullException(nameof(localTargets));
    }

    /// <summary>Gets the host executable path.</summary>
    public string HostExecutablePath { get; }

    /// <summary>Gets the extension assembly path, whose Authenticode signer the host must match.</summary>
    public string ExtensionAssemblyPath { get; }

    /// <summary>Gets the services exposed to the host.</summary>
    public HostLocalTargets LocalTargets { get; }

    /// <summary>Gets or sets the directory passed to the host as <c>--log-dir</c>.</summary>
    public string? LogDirectory { get; set; }

    /// <summary>Gets or sets the client (devenv.exe) process id; defaults to the current process.</summary>
    public int ClientProcessId { get; set; } = CurrentProcessId();

    /// <summary>Gets or sets how long to keep trying to connect to the host's pipe (protocol: 10 s).</summary>
    public TimeSpan ConnectTimeout { get; set; } = TimeSpan.FromSeconds(10);

    /// <summary>Gets or sets the delay between connection attempts (protocol: 100 ms).</summary>
    public TimeSpan ConnectRetryInterval { get; set; } = TimeSpan.FromMilliseconds(100);

    /// <summary>Gets or sets how long to wait for the handshake answer.</summary>
    public TimeSpan HandshakeTimeout { get; set; } = TimeSpan.FromSeconds(10);

    /// <summary>
    /// Gets the extension's host location: <c>&lt;directory of the extension assembly&gt;\Host\Pillion.Host.exe</c>.
    /// </summary>
    /// <param name="extensionAssemblyPath">The extension assembly path.</param>
    /// <returns>The host executable path.</returns>
    public static string DefaultHostPath(string extensionAssemblyPath) => Path.Combine(
        Path.GetDirectoryName(extensionAssemblyPath) ?? string.Empty,
        LaunchProtocol.HostFolderName,
        LaunchProtocol.HostExecutableName);

    private static int CurrentProcessId()
    {
        using var current = Process.GetCurrentProcess();
        return current.Id;
    }
}

/// <summary>
/// Launches Pillion Host following the launch protocol: verify the signature, start the process with the token pipe
/// handle inherited, put it in a kill-on-close job, send the token, connect to the pipe (every 100 ms for up to 10 s),
/// verify the pipe server, then handshake.
/// </summary>
public sealed class HostProcessLauncher : IHostLauncher
{
    private const int MaxStandardErrorLines = 200;

    private readonly HostLauncherOptions _options;
    private readonly PillionLog _log;
    private readonly Lazy<SignerInfo?> _extensionSigner;
    private int _launches;
    private volatile bool _useBreakaway;
    private volatile bool _breakawayUnavailable;

    /// <summary>Initializes a new instance of the <see cref="HostProcessLauncher"/> class.</summary>
    /// <param name="options">The settings.</param>
    /// <param name="log">The logger.</param>
    public HostProcessLauncher(HostLauncherOptions options, PillionLog log)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _log = log ?? throw new ArgumentNullException(nameof(log));
        _extensionSigner = new Lazy<SignerInfo?>(() => AuthenticodeInspector.GetSigner(_options.ExtensionAssemblyPath), LazyThreadSafetyMode.ExecutionAndPublication);
    }

    /// <inheritdoc/>
    public async Task<IHostSession> LaunchAsync(CancellationToken cancellationToken)
    {
        return await LaunchCoreAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Launches the host and returns the concrete session (used by tests).</summary>
    /// <param name="cancellationToken">A token that cancels the launch.</param>
    /// <returns>The session.</returns>
    internal async Task<HostRpcSession> LaunchCoreAsync(CancellationToken cancellationToken)
    {
        var launch = Interlocked.Increment(ref _launches);
        var hostPath = _options.HostExecutablePath;
        _log.Info("host.launch.begin", "Launching Pillion Host.", ("launch", launch), ("path", hostPath));

        if (!File.Exists(hostPath))
        {
            throw new HostLaunchException("Pillion Host was not found at " + hostPath + ". Reinstall the extension.", isRetryable: false);
        }

        VerifySignature(hostPath);
        cancellationToken.ThrowIfCancellationRequested();

        var token = LaunchProtocol.GenerateToken();
        _log.AddSecret(token);
        var pipeName = LaunchProtocol.CreatePipeName(_options.ClientProcessId);

        HostProcess? process = null;
        HostJob? job = null;
        NamedPipeClientStream? pipe = null;
        HostRpcSession? session = null;
        try
        {
            using (var tokenPipe = new AnonymousPipeServerStream(PipeDirection.Out, HandleInheritability.Inheritable))
            {
                try
                {
                    var arguments = LaunchProtocol.BuildArguments(pipeName, _options.ClientProcessId, tokenPipe.GetClientHandleAsString(), _options.LogDirectory);
                    (process, job) = StartProcess(hostPath, arguments, launch);

                    // The token: UTF-8, no BOM, terminated by "\n". It never appears on the command line or in a log.
                    var bytes = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false).GetBytes(token + "\n");
                    await tokenPipe.WriteAsync(bytes, 0, bytes.Length, CancellationToken.None).ConfigureAwait(false);
                    await tokenPipe.FlushAsync(CancellationToken.None).ConfigureAwait(false);
                }
                finally
                {
                    // Once exposed with GetClientHandleAsString, the client handle is not closed by Dispose.
                    tokenPipe.DisposeLocalCopyOfClientHandle();
                }
            }

            _log.Debug("host.token.sent", "Sent the protocol token over the inherited pipe.", ("pid", process.Id));

            pipe = await ConnectAsync(pipeName, process, cancellationToken).ConfigureAwait(false);
            session = new HostRpcSession(process, job, pipe, _options.LocalTargets, _log, token);

            var response = await session.HandshakeAsync(_options.ClientProcessId, _options.HandshakeTimeout, cancellationToken).ConfigureAwait(false);
            _log.Info(
                "host.handshake.accepted",
                "Pillion Host accepted the handshake.",
                ("pid", process.Id),
                ("hostVersion", response.HostVersion),
                ("contract", response.ContractMajor + "." + response.ContractMinor),
                ("state", session.LatestHostState));
            return session;
        }
        catch (Exception ex)
        {
            if (session is not null)
            {
                session.Dispose();
            }
            else
            {
                pipe?.Dispose();
                if (process is not null && !process.HasExited)
                {
                    if (job is not null)
                    {
                        job.Terminate();
                    }
                    else
                    {
                        process.KillTree();
                    }
                }

                job?.Dispose();
                process?.Dispose();
                _log.RemoveSecret(token);
            }

            if (ex is HostLaunchException or OperationCanceledException)
            {
                throw;
            }

            throw new HostLaunchException("Pillion Host could not be launched: " + ex.Message, ex);
        }
    }

    private void VerifySignature(string hostPath)
    {
        var decision = HostSignaturePolicy.Evaluate(_extensionSigner.Value, AuthenticodeInspector.GetSigner(hostPath));
        if (!decision.Allowed)
        {
            _log.Error("host.signature.refused", decision.Message, ("path", hostPath));
            throw new HostLaunchException(decision.Message, isRetryable: false);
        }

        _log.Info(decision.CheckSkipped ? "host.signature.skipped" : "host.signature.verified", decision.Message, ("path", hostPath));
    }

    private (HostProcess Process, HostJob? Job) StartProcess(string hostPath, System.Collections.Generic.IReadOnlyList<string> arguments, int launch)
    {
        HostJob? job;
        try
        {
            job = HostJob.Create();
        }
        catch (Win32Exception ex)
        {
            _log.Warning("host.job.create_failed", "Could not create a job object; relying on --parent-pid.", ("error", ex.NativeErrorCode));
            job = null;
        }

        HostProcess process;
        if (_useBreakaway && !_breakawayUnavailable)
        {
            try
            {
                process = HostProcess.StartWithBreakaway(hostPath, arguments);
            }
            catch (Win32Exception ex) when (ex.NativeErrorCode == NativeMethods.ERROR_ACCESS_DENIED)
            {
                _breakawayUnavailable = true;
                _log.Warning("host.job.breakaway_denied", "devenv.exe's job does not allow breakaway; starting the host without a job of its own.", ("error", ex.NativeErrorCode));
                process = HostProcess.Start(hostPath, arguments, CreateStandardErrorLogger());
            }
        }
        else
        {
            process = HostProcess.Start(hostPath, arguments, CreateStandardErrorLogger());
        }

        _log.Info(
            "host.process.started",
            "Started Pillion Host.",
            ("launch", launch),
            ("pid", process.Id),
            ("pipe", arguments[1]),
            ("breakaway", process.StartedWithBreakaway));

        if (job is not null)
        {
            if (job.TryAssign(process, out var error))
            {
                _log.Debug("host.job.assigned", "Assigned the host to a kill-on-close job.", ("pid", process.Id));
            }
            else
            {
                if (error == NativeMethods.ERROR_ACCESS_DENIED && !process.StartedWithBreakaway)
                {
                    _useBreakaway = true;
                }

                _log.Warning(
                    "host.job.assign_failed",
                    _useBreakaway && !_breakawayUnavailable
                        ? "Could not assign the host to a job (nested jobs unavailable); the next launch uses CREATE_BREAKAWAY_FROM_JOB. Relying on --parent-pid for this one."
                        : "Could not assign the host to a job; relying on --parent-pid.",
                    ("pid", process.Id),
                    ("error", error));
                job.Dispose();
                job = null;
            }
        }

        return (process, job);
    }

    private Action<string> CreateStandardErrorLogger()
    {
        var lines = 0;
        return line =>
        {
            var count = Interlocked.Increment(ref lines);
            if (count <= MaxStandardErrorLines)
            {
                _log.Warning("host.stderr", line);
            }
            else if (count == MaxStandardErrorLines + 1)
            {
                _log.Warning("host.stderr", "Further standard error output from this host process is not logged.");
            }
        };
    }

    private async Task<NamedPipeClientStream> ConnectAsync(string pipeName, HostProcess process, CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();
        var attempts = 0;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (process.HasExited)
            {
                throw new HostLaunchException("Pillion Host exited with code " + process.ExitCode + " before accepting a connection.");
            }

            attempts++;

            // PipeOptions.CurrentUserOnly does not exist on .NET Framework; VerifyServer performs the equivalent
            // owner check and also pins the server to the process we started.
            var client = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
            try
            {
                client.Connect(0);

                // Before anything (and above all the token) is sent: is this pipe really served by our host?
                VerifyServer(client, process.Id);
                _log.Info("host.connected", "Connected to Pillion Host's pipe.", ("pid", process.Id), ("attempts", attempts), ("elapsedMs", stopwatch.ElapsedMilliseconds));
                return client;
            }
            catch (TimeoutException)
            {
                client.Dispose();
            }
            catch (IOException)
            {
                // The pipe exists but all instances are busy, or it closed while connecting; try again.
                client.Dispose();
            }
            catch (HostLaunchException ex)
            {
                // Pipe squatting: disconnect without sending anything; the supervisor treats it as a crash.
                client.Dispose();
                _log.Error("host.pipe.untrusted", ex.Message, ("pid", process.Id), ("pipe", pipeName));
                throw;
            }
            catch
            {
                client.Dispose();
                throw;
            }

            if (attempts == 1 || attempts % 10 == 0)
            {
                _log.Debug("host.connect.attempt", "Waiting for Pillion Host's pipe.", ("pid", process.Id), ("attempts", attempts), ("elapsedMs", stopwatch.ElapsedMilliseconds));
            }

            if (stopwatch.Elapsed >= _options.ConnectTimeout)
            {
                throw new HostLaunchException(
                    $"Pillion Host did not open its pipe within {_options.ConnectTimeout.TotalSeconds:0} s ({attempts} attempts).");
            }

            await Task.Delay(_options.ConnectRetryInterval, cancellationToken).ConfigureAwait(false);
        }
    }

    internal static void VerifyServer(NamedPipeClientStream client, int expectedProcessId) =>
        VerifyServer(client, expectedProcessId, PipeIdentity.Current, ReadPipeOwner);

    /// <summary>
    /// Checks that the pipe is served by the process that was started and is owned by this user. The owner is
    /// accepted when it is the user SID or the token owner SID: an elevated token (Visual Studio run as administrator,
    /// or an elevated build agent) owns the objects it creates as <c>BUILTIN\Administrators</c>, not as the user.
    /// Any other owner is refused.
    /// </summary>
    /// <param name="client">The connected client end of the pipe.</param>
    /// <param name="expectedProcessId">The id of the process that was started.</param>
    /// <param name="identity">The identity of the current process (tests pass a fake).</param>
    /// <param name="readOwner">Reads the owner SID of the pipe (tests pass a fake).</param>
    internal static void VerifyServer(
        NamedPipeClientStream client,
        int expectedProcessId,
        IPipeIdentity identity,
        Func<NamedPipeClientStream, SecurityIdentifier?> readOwner)
    {
        if (!NativeMethods.GetNamedPipeServerProcessId(client.SafePipeHandle, out var serverProcessId))
        {
            throw new HostLaunchException("Could not identify the process serving Pillion Host's pipe.");
        }

        if (serverProcessId != (uint)expectedProcessId)
        {
            throw new HostLaunchException(
                "Pillion Host's pipe is served by process " + serverProcessId + ", not by the host that was started (" + expectedProcessId + "). Disconnected without sending the handshake.");
        }

        var owner = readOwner(client);
        if (!PipeIdentity.IsTrustedOwner(owner, identity))
        {
            throw new HostLaunchException(
                "Pillion Host's pipe is owned by another user (" + (owner?.Value ?? "unknown") + "). Disconnected without sending the handshake.");
        }
    }

    private static SecurityIdentifier? ReadPipeOwner(NamedPipeClientStream client) =>
        client.GetAccessControl().GetOwner(typeof(SecurityIdentifier)) as SecurityIdentifier;
}

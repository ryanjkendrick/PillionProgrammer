using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Pillion.Host;
using Pillion.Host.Lifecycle;
using Pillion.Host.Logging;
using Pillion.Host.Rpc;

var commandLine = HostCommandLine.Parse(args);

switch (commandLine.Mode)
{
    case HostMode.Version:
        Console.WriteLine(HostInfo.VersionText);
        return 0;

    case HostMode.Invalid:
        Console.Error.WriteLine(commandLine.Error);
        Console.Error.WriteLine(HostCommandLine.Usage);
        return 2;
}

// Arguments are parsed above, so the generic host is not given them (it would treat them as configuration).
var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings
{
    Args = [],
    ContentRootPath = AppContext.BaseDirectory,
});

builder.AddPillionHost(
    new PillionHostOptions(
        commandLine.PipeName!,
        commandLine.ParentProcessId,
        commandLine.LogDirectory ?? RollingFileLoggerOptions.DefaultDirectory),
    new AnonymousPipeLaunchTokenSource(commandLine.TokenHandle!));

using var host = builder.Build();
var shutdown = host.Services.GetRequiredService<HostShutdown>();

// Stops on ShutdownAsync, parent exit, a start-up failure, Ctrl+C or SIGTERM. RunAsync disposes the host.
await host.RunAsync().ConfigureAwait(false);
return shutdown.ExitCode;

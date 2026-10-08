using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Pillion.Host;

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

builder.Logging.ClearProviders();
builder.Logging.AddSimpleConsole(options =>
{
    options.SingleLine = true;
    options.TimestampFormat = "HH:mm:ss.fff ";
});

builder.Services.AddSingleton(new PillionHostOptions(commandLine.PipeName!));
builder.Services.AddHostedService<PillionHostService>();

using var host = builder.Build();

// ConsoleLifetime (the default) stops the host on Ctrl+C or SIGTERM.
await host.RunAsync().ConfigureAwait(false);
return 0;

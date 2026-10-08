using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Console;

namespace Pillion.Host.Logging;

/// <summary>Configures the host's logging.</summary>
internal static class LoggingBuilderExtensions
{
    /// <summary>
    /// Replaces the default providers with single-line console output and the rolling JSON file sink in
    /// <paramref name="logDirectory"/>. Minimum level Information (Microsoft.* at Warning); override with the usual
    /// configuration keys, for example the environment variable <c>Logging__LogLevel__Default=Debug</c>.
    /// </summary>
    /// <param name="builder">The logging builder.</param>
    /// <param name="logDirectory">The log folder.</param>
    /// <returns>The same logging builder.</returns>
    public static ILoggingBuilder AddPillionHostLogging(this ILoggingBuilder builder, string logDirectory)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrEmpty(logDirectory);

        builder.ClearProviders();
        builder.SetMinimumLevel(LogLevel.Information);
        builder.AddFilter("Microsoft", LogLevel.Warning);
        builder.AddFilter("Microsoft.Hosting.Lifetime", LogLevel.Information);

        builder.AddSimpleConsole(options =>
        {
            options.SingleLine = true;
            options.TimestampFormat = "HH:mm:ss.fff ";
        });

        // If the VSIX redirects stdout and stops reading it, drop console lines instead of blocking the host.
        builder.Services.Configure<ConsoleLoggerOptions>(options => options.QueueFullMode = ConsoleLoggerQueueFullMode.DropWrite);

        builder.Services.AddSingleton<ILoggerProvider>(_ => new RollingFileLoggerProvider(
            new RollingFileLoggerOptions { Directory = logDirectory },
            TimeProvider.System));
        return builder;
    }
}

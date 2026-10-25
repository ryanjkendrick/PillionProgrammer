using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Pillion.CommandRunner.Shells;
using Pillion.Common.Settings;
using Pillion.Contracts;

namespace Pillion.CommandRunner;

/// <summary>
/// The <see cref="ICommandRunner"/>: creates <see cref="CommandSession"/>s behind <c>features.commandRunner</c>, with
/// the shell resolved and the Developer PowerShell environment loaded through a shared <see cref="DeveloperEnvironmentCache"/>.
/// </summary>
public sealed partial class CommandRunner : ICommandRunner
{
    /// <summary>The message of the <c>PolicyDenied</c> error when the flag is off.</summary>
    public const string DisabledMessage = "The Command Runner is disabled by settings or policy (feature commandRunner); turn features.commandRunner on in settings.json to run commands.";

    private readonly IFeatureFlags? _features;
    private readonly DeveloperEnvironmentCache _environments;
    private readonly ILoggerFactory? _loggerFactory;
    private readonly ILogger _logger;
    private readonly TimeProvider? _time;

    /// <summary>Initializes a new instance of the <see cref="CommandRunner"/> class.</summary>
    /// <param name="features">The feature flags; <see langword="null"/> means always on (tests and the evaluation harness).</param>
    /// <param name="environments">The shared developer-environment cache; <see langword="null"/> for a private one.</param>
    /// <param name="loggerFactory">Where to log; may be <see langword="null"/>.</param>
    /// <param name="time">The clock; <see langword="null"/> for the system clock.</param>
    public CommandRunner(IFeatureFlags? features = null, DeveloperEnvironmentCache? environments = null, ILoggerFactory? loggerFactory = null, TimeProvider? time = null)
    {
        _features = features;
        _environments = environments ?? new DeveloperEnvironmentCache(loggerFactory);
        _loggerFactory = loggerFactory;
        _logger = loggerFactory?.CreateLogger<CommandRunner>() ?? Microsoft.Extensions.Logging.Abstractions.NullLogger<CommandRunner>.Instance;
        _time = time;
    }

    /// <inheritdoc/>
    public async Task<ICommandSession> CreateSessionAsync(CommandSessionOptions options, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (_features is not null && !_features.IsEnabled(FeatureNames.CommandRunner))
        {
            throw new PillionRpcException(new PillionError { Code = PillionErrorCode.PolicyDenied, Message = DisabledMessage });
        }

        if (!Directory.Exists(options.WorkingDirectory))
        {
            throw new DirectoryNotFoundException($"The working directory does not exist: {options.WorkingDirectory}");
        }

        var warnings = new List<string>();
        var shell = ShellLocator.Resolve(options.Shell);
        if (shell.Warning is not null)
        {
            warnings.Add(shell.Warning);
        }

        IReadOnlyDictionary<string, string>? environment = null;
        if (options.LoadDeveloperEnvironment)
        {
            var result = await _environments.GetAsync(options.VisualStudioInstallationPath, cancellationToken).ConfigureAwait(false);
            environment = result.Environment;
            if (result.Warning is not null)
            {
                warnings.Add(result.Warning);
            }
        }

        var session = new CommandSession(options, shell, environment, warnings, _loggerFactory, _time);
        LogSessionCreated(_logger, session.Id, options.WorkingDirectory, shell.Kind, environment is not null, warnings.Count == 0 ? "none" : string.Join(" | ", warnings));
        return session;
    }

    [LoggerMessage(EventId = 3100, Level = LogLevel.Information, Message = "Command session {Id} created in {Directory} with {Shell}; developer environment loaded: {DeveloperEnvironment}; warnings: {Warnings}.")]
    private static partial void LogSessionCreated(ILogger logger, string id, string directory, ShellKind shell, bool developerEnvironment, string warnings);
}

/// <summary>Dependency injection registration for the Command Runner.</summary>
public static class CommandRunnerServiceCollectionExtensions
{
    /// <summary>
    /// Registers <see cref="ICommandRunner"/> (behind <c>features.commandRunner</c>) and the shared
    /// <see cref="DeveloperEnvironmentCache"/>. Needs <see cref="IFeatureFlags"/>; an <see cref="ILoggerFactory"/> and a
    /// <see cref="TimeProvider"/> are used if registered. Existing registrations are kept.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <returns>The service collection.</returns>
    public static IServiceCollection AddPillionCommandRunner(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.TryAddSingleton(provider => new DeveloperEnvironmentCache(provider.GetService<ILoggerFactory>()));
        services.TryAddSingleton<ICommandRunner>(provider => new CommandRunner(
            provider.GetRequiredService<IFeatureFlags>(),
            provider.GetRequiredService<DeveloperEnvironmentCache>(),
            provider.GetService<ILoggerFactory>(),
            provider.GetService<TimeProvider>()));
        return services;
    }
}

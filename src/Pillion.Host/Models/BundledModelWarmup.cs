using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Pillion.Common.Settings;
using Pillion.ModelGateway;
using Pillion.ModelGateway.Providers.Bundled;

namespace Pillion.Host.Models;

/// <summary>
/// Verifies the bundled embedding models once when the host starts (work item 2.7, design section 11): every file found in the
/// install folder or the user's models folder is hashed against the model manifest in the background, so the first embedding
/// request does not pay for it, and the log says which models are ready or why one is refused. The verdicts are kept for the
/// rest of the process. Nothing is loaded into ONNX Runtime until a model is used.
/// </summary>
internal sealed partial class BundledModelWarmup : BackgroundService
{
    private readonly BundledModelRuntime _runtime;
    private readonly IGatewaySettingsSource _settings;
    private readonly ILogger<BundledModelWarmup> _logger;

    /// <summary>Initializes a new instance of the <see cref="BundledModelWarmup"/> class.</summary>
    /// <param name="runtime">The bundled model runtime.</param>
    /// <param name="settings">The effective settings (the feature flags).</param>
    /// <param name="logger">The logger.</param>
    public BundledModelWarmup(BundledModelRuntime runtime, IGatewaySettingsSource settings, ILogger<BundledModelWarmup> logger)
    {
        _runtime = runtime;
        _settings = settings;
        _logger = logger;
    }

    /// <summary>Gets a task that completes when the start-up verification has run (for tests).</summary>
    internal Task Completion => ExecuteTask ?? Task.CompletedTask;

    /// <inheritdoc/>
    protected override Task ExecuteAsync(CancellationToken stoppingToken) => Task.Run(() => Verify(stoppingToken), stoppingToken);

    private void Verify(CancellationToken cancellationToken)
    {
        var effective = _settings.GetCurrent();
        if (!effective.IsFeatureEnabled(FeatureNames.ModelGateway) ||
            !effective.IsFeatureEnabled(FeatureNames.Embeddings) ||
            !effective.IsFeatureEnabled(FeatureNames.BundledEmbeddings))
        {
            LogSkipped(_logger);
            return;
        }

        try
        {
            var available = _runtime.GetAvailableModels(cancellationToken);
            foreach (var located in available)
            {
                LogReady(_logger, located.Model.Id, located.Directory, located.HasFp16);
            }

            foreach (var model in _runtime.Manifest.Models.Where(m => m.Bundled && m.Available && !available.Any(a => a.Model == m)))
            {
                LogMissing(_logger, model.Id, string.Join(", ", _runtime.Locator.Roots));
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // The host is stopping.
        }
    }

    [LoggerMessage(EventId = 480, Level = LogLevel.Information, Message = "Bundled embedding model {Model} verified against the model manifest in {Directory} (FP16 graph for DirectML: {HasFp16}).")]
    private static partial void LogReady(ILogger logger, string model, string directory, bool hasFp16);

    [LoggerMessage(EventId = 481, Level = LogLevel.Warning, Message = "The bundled embedding model {Model} is missing or does not match the model manifest (searched {Roots}); the Embedding task class falls back to another local model.")]
    private static partial void LogMissing(ILogger logger, string model, string roots);

    [LoggerMessage(EventId = 482, Level = LogLevel.Debug, Message = "Bundled embedding models are turned off by settings or policy; their files are not verified.")]
    private static partial void LogSkipped(ILogger logger);
}

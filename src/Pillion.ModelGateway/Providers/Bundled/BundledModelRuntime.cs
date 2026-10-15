using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.ML.OnnxRuntime;
using Pillion.Common.Hardware;
using Pillion.Contracts;

namespace Pillion.ModelGateway.Providers.Bundled;

/// <summary>
/// The bundled embedding models in this host process (work item 2.7): finds and verifies their files, loads them into ONNX
/// Runtime on first use (DirectML when a GPU is present, the CPU otherwise or when DirectML fails), embeds, exposes each
/// model's token counter for the chunker, and releases a session that has been idle. One instance per host, shared by every
/// provider of kind <c>bundled</c>. Thread-safe.
/// </summary>
public sealed partial class BundledModelRuntime : IDisposable
{
    private readonly object _gate = new();
    private readonly Dictionary<string, OnnxEmbeddingModel> _loaded = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, EmbeddingTokenizer> _tokenizers = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, long> _lastUsed = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, int> _inFlight = new(StringComparer.OrdinalIgnoreCase);
    private readonly Func<GpuTier> _gpuTier;
    private readonly TimeProvider _time;
    private readonly ILogger _logger;
    private readonly ITimer? _idleTimer;
    private bool _directMLFailed;
    private bool? _directMLBuilt;
    private bool _disposed;

    /// <summary>Initializes a new instance of the <see cref="BundledModelRuntime"/> class.</summary>
    /// <param name="manifest">The model manifest (normally <see cref="ModelManifest.Default"/>).</param>
    /// <param name="options">The tunables.</param>
    /// <param name="gpuTier">Gets the effective GPU tier; DirectML is only tried when it is not <see cref="GpuTier.CpuOnly"/>. <see langword="null"/> detects it.</param>
    /// <param name="logger">The logger.</param>
    /// <param name="time">The clock of the idle release.</param>
    public BundledModelRuntime(ModelManifest manifest, BundledModelOptions options, Func<GpuTier>? gpuTier = null, ILogger<BundledModelRuntime>? logger = null, TimeProvider? time = null)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        ArgumentNullException.ThrowIfNull(options);

        Manifest = manifest;
        Options = options;
        _gpuTier = gpuTier ?? (() => GpuTierDetector.Shared.Detect().Tier);
        _logger = (ILogger?)logger ?? NullLogger.Instance;
        _time = time ?? TimeProvider.System;
        Locator = new BundledModelLocator(new[] { options.InstallModelsDirectory, options.UserModelsDirectory ?? string.Empty }, _logger);
        if (options.IdleUnloadAfter > TimeSpan.Zero)
        {
            _idleTimer = _time.CreateTimer(_ => ReleaseIdle(), null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        }
    }

    /// <summary>Gets the model manifest.</summary>
    public ModelManifest Manifest { get; }

    /// <summary>Gets the tunables.</summary>
    public BundledModelOptions Options { get; }

    /// <summary>Gets the locator that finds and verifies model files.</summary>
    public BundledModelLocator Locator { get; }

    /// <summary>Gets the models whose files are present and verified, in manifest order. The first call hashes the files.</summary>
    /// <param name="cancellationToken">A token that cancels the hashing.</param>
    /// <returns>The located models.</returns>
    public IReadOnlyList<LocatedModel> GetAvailableModels(CancellationToken cancellationToken = default)
    {
        var result = new List<LocatedModel>();
        foreach (var model in Manifest.Models.Where(m => m.Available))
        {
            if (Locator.Locate(model, cancellationToken) is { } located)
            {
                result.Add(located);
            }
        }

        return result;
    }

    /// <summary>Finds one model whose files are present and verified.</summary>
    /// <param name="modelId">The model id.</param>
    /// <param name="cancellationToken">A token that cancels the hashing.</param>
    /// <returns>Where it is, or <see langword="null"/> when the manifest does not offer it or its files are missing or refused.</returns>
    public LocatedModel? Locate(string modelId, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(modelId);
        return Manifest.Find(modelId) is { Available: true } model ? Locator.Locate(model, cancellationToken) : null;
    }

    /// <summary>Gets the most tokens a model reads from one text here: its own limit capped by <see cref="BundledModelOptions.MaxSequenceTokens"/>.</summary>
    /// <param name="model">The model.</param>
    /// <returns>The limit, special tokens included.</returns>
    public int EffectiveMaxTokens(ManifestModel model)
    {
        ArgumentNullException.ThrowIfNull(model);
        return Math.Max(3, Math.Min(model.MaxTokens, Options.MaxSequenceTokens));
    }

    /// <summary>
    /// Gets a model's token counter, for chunk caps in the model's own tokenizer (work item 2.4). Only the tokenizer files are
    /// read, not the graph.
    /// </summary>
    /// <param name="modelId">The model id.</param>
    /// <returns>The counter, or <see langword="null"/> when the model is not available.</returns>
    public IEmbeddingTokenCounter? GetTokenCounter(string modelId) => Locate(modelId) is { } located ? GetTokenizer(located) : null;

    /// <summary>Describes a model for the index <c>meta</c>.</summary>
    /// <param name="providerId">The provider id.</param>
    /// <param name="modelId">The model id.</param>
    /// <returns>The description, or <see langword="null"/> when the manifest does not list the model.</returns>
    public EmbeddingModelInfo? Describe(string providerId, string modelId) =>
        Manifest.Find(modelId) is { } model ? new EmbeddingModelInfo(providerId, model.Id, model.Dimensions, model.Tokenizer, EffectiveMaxTokens(model)) : null;

    /// <summary>Gets the execution provider a model runs on now, or <see langword="null"/> when it is not loaded.</summary>
    /// <param name="modelId">The model id.</param>
    /// <returns>The execution provider.</returns>
    public LoadedExecutionProvider? ExecutionProviderOf(string modelId)
    {
        lock (_gate)
        {
            return _loaded.TryGetValue(modelId, out var model) ? model.ExecutionProvider : null;
        }
    }

    /// <summary>
    /// Embeds texts with a model, loading it first if needed. A DirectML failure (at load or while running) is logged once and
    /// the model is loaded again on the CPU for the rest of the process.
    /// </summary>
    /// <param name="modelId">The model id.</param>
    /// <param name="texts">The texts.</param>
    /// <param name="cancellationToken">A token that cancels the call.</param>
    /// <returns>One vector per text, in order, and the tokens read.</returns>
    /// <exception cref="ProviderException">The model is not available or could not be run.</exception>
    public (float[][] Vectors, int Tokens) Embed(string modelId, IReadOnlyList<string> texts, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(modelId);
        ArgumentNullException.ThrowIfNull(texts);

        Enter(modelId);
        try
        {
            return EmbedCore(modelId, texts, cancellationToken);
        }
        finally
        {
            Leave(modelId);
        }
    }

    /// <summary>Forgets every verification verdict, so that files downloaded or replaced since are hashed again on the next lookup.</summary>
    public void Invalidate() => Locator.Invalidate();

    private (float[][] Vectors, int Tokens) EmbedCore(string modelId, IReadOnlyList<string> texts, CancellationToken cancellationToken)
    {
        var model = GetOrLoad(modelId, cancellationToken);
        try
        {
            return model.Embed(texts, cancellationToken);
        }
        catch (ObjectDisposedException) when (!cancellationToken.IsCancellationRequested)
        {
            // Released as idle between the lookup and the run: load it again.
            return GetOrLoad(modelId, cancellationToken).Embed(texts, cancellationToken);
        }
        catch (OnnxRuntimeException ex) when (model.ExecutionProvider == LoadedExecutionProvider.DirectML && !cancellationToken.IsCancellationRequested)
        {
            DirectMLFailed(model, ex);
            return GetOrLoad(modelId, cancellationToken).Embed(texts, cancellationToken);
        }
        catch (OnnxRuntimeException ex) when (!cancellationToken.IsCancellationRequested)
        {
            throw new ProviderException(PillionErrorCode.ProviderUnavailable, $"The bundled model {modelId} failed: {ex.Message}", innerException: ex);
        }
        catch (OnnxRuntimeException ex)
        {
            throw new OperationCanceledException("The embedding run was cancelled.", ex, cancellationToken);
        }
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        List<OnnxEmbeddingModel> models;
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            models = [.. _loaded.Values];
            _loaded.Clear();
        }

        _idleTimer?.Dispose();
        foreach (var model in models)
        {
            model.Dispose();
        }
    }

    private EmbeddingTokenizer GetTokenizer(LocatedModel located)
    {
        lock (_gate)
        {
            if (_tokenizers.TryGetValue(located.Model.Id, out var cached))
            {
                return cached;
            }
        }

        var tokenizer = EmbeddingTokenizer.Create(
            located.Model,
            role => located.Model.Find(role) is { } file && (!file.Optional || File.Exists(located.PathOf(file))) ? ModelFiles.ReadVerified(located.PathOf(file), file) : null,
            EffectiveMaxTokens(located.Model));
        lock (_gate)
        {
            return _tokenizers.TryAdd(located.Model.Id, tokenizer) ? tokenizer : _tokenizers[located.Model.Id];
        }
    }

    private OnnxEmbeddingModel GetOrLoad(string modelId, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_loaded.TryGetValue(modelId, out var loaded))
            {
                return loaded;
            }
        }

        var located = Locate(modelId, cancellationToken) ?? throw new ProviderException(
            PillionErrorCode.ModelNotInstalled,
            Manifest.Find(modelId) is { Available: true }
                ? $"The bundled model {modelId} is not installed or its files do not match the model manifest. Searched: {string.Join(", ", Locator.Roots)}."
                : $"The bundled provider has no model '{modelId}'.",
            isProviderFault: false);

        OnnxEmbeddingModel model;
        try
        {
            var tokenizer = GetTokenizer(located);
            model = Load(located, tokenizer);
        }
        catch (ModelVerificationException ex)
        {
            Locator.Invalidate();
            throw new ProviderException(PillionErrorCode.ModelNotInstalled, ex.Message, innerException: ex, isProviderFault: false);
        }
        catch (Exception ex) when (ex is OnnxRuntimeException or InvalidOperationException or IOException or UnauthorizedAccessException)
        {
            throw new ProviderException(PillionErrorCode.ProviderUnavailable, $"The bundled model {modelId} could not be loaded: {ex.Message}", innerException: ex);
        }

        lock (_gate)
        {
            if (_disposed || _loaded.TryGetValue(modelId, out var raced))
            {
                model.Dispose();
                ObjectDisposedException.ThrowIf(_disposed, this);
                return _loaded[modelId];
            }

            _loaded[modelId] = model;
            return model;
        }
    }

    private OnnxEmbeddingModel Load(LocatedModel located, EmbeddingTokenizer tokenizer)
    {
        var started = Stopwatch.GetTimestamp();
        if (WantsDirectML())
        {
            try
            {
                var gpu = OnnxEmbeddingModel.Load(located, tokenizer, useDirectML: true, Options);
                var gpuMilliseconds = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
                LogLoaded(_logger, located.Model.Id, gpu.Graph, "DirectML", located.Directory, gpuMilliseconds);
                return gpu;
            }
            catch (Exception ex) when (ex is OnnxRuntimeException or EntryPointNotFoundException or DllNotFoundException)
            {
                lock (_gate)
                {
                    _directMLFailed = true;
                }

                LogDirectMLUnavailable(_logger, located.Model.Id, ex.Message);
            }
        }

        var cpu = OnnxEmbeddingModel.Load(located, tokenizer, useDirectML: false, Options);
        var cpuMilliseconds = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        LogLoaded(_logger, located.Model.Id, cpu.Graph, "CPU", located.Directory, cpuMilliseconds);
        return cpu;
    }

    private bool WantsDirectML()
    {
        lock (_gate)
        {
            if (_directMLFailed || Options.ExecutionProvider == BundledExecutionProvider.Cpu)
            {
                return false;
            }
        }

        _directMLBuilt ??= DirectMLInThisBuild();
        if (_directMLBuilt != true)
        {
            return false;
        }

        if (Options.ExecutionProvider == BundledExecutionProvider.DirectML)
        {
            return true;
        }

        try
        {
            return _gpuTier() != GpuTier.CpuOnly;
        }
        catch (Exception ex) when (ex is InvalidOperationException or PlatformNotSupportedException)
        {
            return false;
        }
    }

    private static bool DirectMLInThisBuild()
    {
        try
        {
            return OrtEnv.Instance().GetAvailableProviders().Contains("DmlExecutionProvider", StringComparer.Ordinal);
        }
        catch (Exception ex) when (ex is OnnxRuntimeException or DllNotFoundException or EntryPointNotFoundException)
        {
            return false;
        }
    }

    private void DirectMLFailed(OnnxEmbeddingModel model, OnnxRuntimeException ex)
    {
        lock (_gate)
        {
            _directMLFailed = true;
            if (_loaded.TryGetValue(model.Model.Id, out var current) && ReferenceEquals(current, model))
            {
                _loaded.Remove(model.Model.Id);
            }
        }

        LogDirectMLUnavailable(_logger, model.Model.Id, ex.Message);
        model.Dispose();
    }

    private void Enter(string modelId)
    {
        lock (_gate)
        {
            _inFlight[modelId] = _inFlight.GetValueOrDefault(modelId) + 1;
        }
    }

    /// <summary>Ends a call: the model's idle time starts now, and the idle timer is armed.</summary>
    private void Leave(string modelId)
    {
        lock (_gate)
        {
            _inFlight[modelId] = Math.Max(0, _inFlight.GetValueOrDefault(modelId) - 1);
            if (_disposed)
            {
                return;
            }

            _lastUsed[modelId] = _time.GetTimestamp();
        }

        _idleTimer?.Change(Options.IdleUnloadAfter, Timeout.InfiniteTimeSpan);
    }

    /// <summary>
    /// Releases every loaded model that is not running now, with its ONNX Runtime session and memory arena (the indexing
    /// pipeline's first step under memory pressure); the next request loads it again.
    /// </summary>
    /// <returns>The number of models released.</returns>
    public int ReleaseUnused() => Release(force: true);

    private void ReleaseIdle() => Release(force: false);

    private int Release(bool force)
    {
        var released = new List<OnnxEmbeddingModel>();
        lock (_gate)
        {
            foreach (var (id, model) in _loaded.ToList())
            {
                if (_inFlight.GetValueOrDefault(id) == 0 &&
                    (force || !_lastUsed.TryGetValue(id, out var last) || _time.GetElapsedTime(last) >= Options.IdleUnloadAfter))
                {
                    _loaded.Remove(id);
                    released.Add(model);
                }
            }
        }

        foreach (var model in released)
        {
            // Dispose waits for a run in progress to finish.
            model.Dispose();
            LogReleased(_logger, model.Model.Id, Options.IdleUnloadAfter.TotalMinutes);
        }

        return released.Count;
    }

    [LoggerMessage(EventId = 473, Level = LogLevel.Information, Message = "Bundled embedding model {Model} loaded from {Graph} on {ExecutionProvider} ({Directory}) in {Milliseconds:F0} ms.")]
    private static partial void LogLoaded(ILogger logger, string model, string graph, string executionProvider, string directory, double milliseconds);

    [LoggerMessage(EventId = 474, Level = LogLevel.Warning, Message = "DirectML could not run the bundled embedding model {Model} ({Reason}); the bundled models run on the CPU for the rest of this session.")]
    private static partial void LogDirectMLUnavailable(ILogger logger, string model, string reason);

    [LoggerMessage(EventId = 475, Level = LogLevel.Information, Message = "Bundled embedding model {Model} released after {Minutes} minutes unused.")]
    private static partial void LogReleased(ILogger logger, string model, double minutes);
}

using System.Globalization;
using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Pillion.Common.Hardware;
using Pillion.Contracts;

namespace Pillion.ModelGateway.Providers.Ollama;

/// <summary>What a request asks the residency manager for.</summary>
/// <param name="ModelId">The model.</param>
/// <param name="IsFim">Whether the request is an autocomplete completion (the model is then the completion model and stays resident).</param>
/// <param name="Profile">The profile the gateway routed the request under.</param>
/// <param name="ExplicitContext">A context size the caller asked for, or <see langword="null"/>.</param>
/// <param name="NeedTokens">The tokens the request needs: its prompt plus the output it reserves.</param>
/// <param name="KeepAliveOverride">A <c>keep_alive</c> the caller asked for, or <see langword="null"/>.</param>
/// <param name="RequestId">The gateway request id, for progress notices.</param>
/// <param name="IsEmbedding">Whether the request embeds text (<c>/api/embed</c>); the model is then never preloaded or unloaded through <c>/api/generate</c>, which embedding models do not serve.</param>
internal sealed record ResidencyRequest(string ModelId, bool IsFim, Profile? Profile, int? ExplicitContext, int NeedTokens, string? KeepAliveOverride, string? RequestId, bool IsEmbedding = false);

/// <summary>The <c>num_ctx</c> and <c>keep_alive</c> a request is sent with.</summary>
/// <param name="NumCtx">The context size.</param>
/// <param name="KeepAlive">The keep-alive.</param>
/// <param name="CpuOnly">Whether the model is to run on the CPU (<c>num_gpu</c> 0) because the GPU has no room for it beside the resident models.</param>
internal sealed record ResidencyPlan(int NumCtx, string KeepAlive, bool CpuOnly = false);

/// <summary>
/// Keeps one resident configuration per model (design section 6, Local model residency). Ollama keeps a separate runner for
/// every context size, and on a small GPU it cannot start a second one beside a pinned one: the request then waits for
/// memory that never comes. The manager reads which models are loaded and with what context (<c>/api/ps</c>, at most every
/// 30 s), sends a request at the context the model already has when that is enough, and otherwise either lets Ollama load a
/// second instance (when the catalog's memory estimate says it fits the tier) or unloads the resident one and loads it once
/// at the larger size. Every unload and load runs under a timeout and reports progress, so nothing waits forever.
/// </summary>
internal sealed partial class OllamaResidency : ILocalResidencySource, IDisposable
{
    /// <summary>How long an <c>/api/ps</c> answer is trusted.</summary>
    internal static readonly TimeSpan CacheDuration = TimeSpan.FromSeconds(30);

    /// <summary>The resident size of a model shared between autocomplete and chat when the catalog names none for the tier.</summary>
    internal const int DefaultSharedContext = 4096;

    /// <summary>The output tokens a request reserves when it does not say how many it wants.</summary>
    internal const int DefaultOutputReserve = 512;

    private const int MinimumContext = 2048;
    private static readonly TimeSpan PsTimeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(200);

    private readonly OllamaProvider _provider;
    private readonly ProviderRuntime _runtime;
    private readonly ILogger _logger;
    private readonly object _lock = new();
    private readonly Dictionary<string, (string Name, List<Instance> Instances)> _resident = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, HashSet<int>> _wanted = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, SemaphoreSlim> _gates = new(StringComparer.OrdinalIgnoreCase);
    private readonly SemaphoreSlim _psGate = new(1, 1);
    private DateTimeOffset? _observedAt;
    private bool _psUnsupported;
    private GpuTier? _tier;

    public OllamaResidency(OllamaProvider provider, ProviderRuntime runtime, ILogger logger)
    {
        _provider = provider;
        _runtime = runtime;
        _logger = logger;
        _runtime.Residency?.Register(this);
    }

    /// <inheritdoc/>
    public string ProviderId => _provider.Id;

    /// <summary>
    /// Gets the memory the tier can give a local model, in MiB: the nominal size minus 1 GiB for the display, the desktop and the CUDA
    /// context. <see langword="null"/> for the CPU tier, where the models live in system memory.
    /// </summary>
    /// <param name="tier">The tier.</param>
    /// <returns>The budget, or <see langword="null"/>.</returns>
    internal static int? TierBudgetMiB(GpuTier tier) => tier switch
    {
        GpuTier.Gpu8GB => 7 * 1024,
        GpuTier.Gpu12GB => 11 * 1024,
        GpuTier.Gpu16GB => 15 * 1024,
        GpuTier.Gpu24GBPlus => 23 * 1024,
        _ => null,
    };

    /// <summary>Rounds a token count up to the next power of two of at least 2,048, the sizes Ollama contexts come in.</summary>
    /// <param name="tokens">The tokens.</param>
    /// <returns>The size.</returns>
    internal static int RoundUpContext(int tokens)
    {
        var size = MinimumContext;
        while (size < tokens && size < (1 << 30))
        {
            size <<= 1;
        }

        return size;
    }

    /// <inheritdoc/>
    public (DateTimeOffset? ObservedAt, GpuTier? Tier, IReadOnlyList<ResidentModel> Models) GetLastKnown()
    {
        GpuTier? tier = null;
        try
        {
            tier = _runtime.Tier();
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or System.ComponentModel.Win32Exception)
        {
            // The tier is a courtesy in the snapshot; the models are what matters.
        }

        lock (_lock)
        {
            _tier = tier ?? _tier;
            return (_observedAt, _tier, Snapshot());
        }
    }

    /// <inheritdoc/>
    public async Task<IReadOnlyList<ResidentModel>> GetResidentAsync(bool force, CancellationToken cancellationToken)
    {
        await RefreshAsync(force, cancellationToken).ConfigureAwait(false);
        lock (_lock)
        {
            return Snapshot();
        }
    }

    /// <summary>
    /// Works out the context size and keep-alive a request is sent with, and makes the model resident at that size (unloading
    /// the resident instance first when the tier cannot hold two). Returns once the model is loaded.
    /// </summary>
    /// <param name="request">The request.</param>
    /// <param name="cancellationToken">A token that cancels the wait.</param>
    /// <returns>The plan to send the request with.</returns>
    /// <exception cref="ProviderException">The request needs more than the tier can hold (<see cref="PillionErrorCode.ContextOverflow"/>), or a load or unload did not finish in time (<see cref="PillionErrorCode.ProviderUnavailable"/>).</exception>
    public async Task<ResidencyPlan> PrepareAsync(ResidencyRequest request, CancellationToken cancellationToken)
    {
        var key = KeyOf(request.ModelId);
        var keepAlive = request.KeepAliveOverride ?? _provider.KeepAliveFor(request.ModelId, request.IsFim);
        var wanted = request.ExplicitContext ?? _provider.Options.ContextSizeFor(request.Profile ?? (request.IsFim ? Profile.LocalFast : null));
        NoteWanted(key, wanted);
        if (request.IsFim || _provider.IsCompletionModel(request.ModelId))
        {
            NoteWanted(key, _provider.Options.ContextSizeFor(Profile.LocalFast));
        }

        SemaphoreSlim gate;
        lock (_lock)
        {
            if (!_gates.TryGetValue(key, out gate!))
            {
                _gates[key] = gate = new SemaphoreSlim(1, 1);
            }
        }

        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await DecideAsync(request, key, wanted, keepAlive, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>
    /// Works out how an embedding request is sent: the context size (small, because each text is read on its own and Ollama would
    /// otherwise load the whole window), the keep-alive, and whether the model runs on the CPU. An embedding model is never allowed to
    /// push a resident model out of the GPU: when the tier's memory is not enough for it beside what is loaded (or the catalog cannot
    /// say how much it needs and something is loaded), it runs on the CPU instead. A model that is already resident keeps the
    /// configuration it has when that serves the request, so there is one resident configuration per model.
    /// </summary>
    /// <param name="request">The request (<see cref="ResidencyRequest.IsEmbedding"/> set).</param>
    /// <param name="cancellationToken">A token that cancels the wait.</param>
    /// <returns>The plan to send the request with.</returns>
    /// <exception cref="ProviderException">An unload did not finish in time (<see cref="PillionErrorCode.ProviderUnavailable"/>).</exception>
    public async Task<ResidencyPlan> PrepareEmbeddingAsync(ResidencyRequest request, CancellationToken cancellationToken)
    {
        var key = KeyOf(request.ModelId);
        var keepAlive = request.KeepAliveOverride ?? _provider.Options.EmbeddingKeepAlive;

        SemaphoreSlim gate;
        lock (_lock)
        {
            if (!_gates.TryGetValue(key, out gate!))
            {
                _gates[key] = gate = new SemaphoreSlim(1, 1);
            }
        }

        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await DecideEmbeddingAsync(request, key, keepAlive, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>Estimates the tokens a chat request needs: its text plus the output it reserves.</summary>
    /// <param name="texts">The message texts.</param>
    /// <param name="maxOutputTokens">The output limit of the request, if it sets one.</param>
    /// <returns>The estimate.</returns>
    internal static int EstimateNeed(IEnumerable<string> texts, int? maxOutputTokens) =>
        TokenEstimator.Estimate(texts) + (maxOutputTokens is > 0 ? maxOutputTokens.Value : DefaultOutputReserve);

    /// <inheritdoc/>
    public void Dispose()
    {
        _runtime.Residency?.Unregister(ProviderId, this);
        _psGate.Dispose();
        lock (_lock)
        {
            foreach (var gate in _gates.Values)
            {
                gate.Dispose();
            }

            _gates.Clear();
        }
    }

    private static string KeyOf(string modelId)
    {
        var trimmed = modelId.Trim();
        return trimmed.EndsWith(":latest", StringComparison.OrdinalIgnoreCase) ? trimmed[..^":latest".Length] : trimmed;
    }

    private static string DeriveKeepAlive(DateTimeOffset? expiresAt, DateTimeOffset now)
    {
        if (expiresAt is null || expiresAt.Value.Year >= 2100)
        {
            return "-1";
        }

        var remaining = expiresAt.Value - now;
        if (remaining <= TimeSpan.Zero)
        {
            return "0";
        }

        return remaining.TotalMinutes >= 1
            ? string.Create(CultureInfo.InvariantCulture, $"{(int)Math.Ceiling(remaining.TotalMinutes)}m")
            : string.Create(CultureInfo.InvariantCulture, $"{(int)Math.Ceiling(remaining.TotalSeconds)}s");
    }

    private static DateTimeOffset? ExpiryOf(string keepAlive, DateTimeOffset now)
    {
        if (keepAlive is "-1" || keepAlive.StartsWith('-'))
        {
            return null;
        }

        if (int.TryParse(keepAlive, NumberStyles.Integer, CultureInfo.InvariantCulture, out var seconds))
        {
            return now.AddSeconds(seconds);
        }

        var match = DurationPattern().Match(keepAlive);
        if (match.Success && double.TryParse(match.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var amount))
        {
            return now + match.Groups[2].Value switch
            {
                "h" => TimeSpan.FromHours(amount),
                "m" => TimeSpan.FromMinutes(amount),
                _ => TimeSpan.FromSeconds(amount),
            };
        }

        return null;
    }

    private static DateTimeOffset? ParseTimestamp(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        // Ollama writes nanoseconds; DateTimeOffset reads at most seven fractional digits.
        var trimmed = FractionPattern().Replace(text, m => m.Value[..8]);
        return DateTimeOffset.TryParse(trimmed, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var parsed) ? parsed : null;
    }

    private static string DescribeTier(GpuTier tier) => tier switch
    {
        GpuTier.CpuOnly => "CPU-only",
        GpuTier.Gpu8GB => "8 GB",
        GpuTier.Gpu12GB => "12 GB",
        GpuTier.Gpu16GB => "16 GB",
        _ => "24 GB",
    };

    private static ResidentModel ToResident(string providerId, string name, Instance instance) => new(
        providerId,
        name,
        instance.Context,
        instance.SizeBytes,
        instance.VramBytes,
        instance.KeepAlive,
        instance.ExpiresAt,
        IsPinned: instance.KeepAlive == "-1");

    private void NoteWanted(string key, int tokens)
    {
        lock (_lock)
        {
            if (!_wanted.TryGetValue(key, out var set))
            {
                _wanted[key] = set = [];
            }

            set.Add(tokens);
        }
    }

    private List<ResidentModel> Snapshot()
    {
        var result = new List<ResidentModel>();
        foreach (var (name, instances) in _resident.Values)
        {
            result.AddRange(instances.Select(i => ToResident(ProviderId, name, i)));
        }

        return result;
    }

    private List<Instance> InstancesOf(string key)
    {
        lock (_lock)
        {
            return _resident.TryGetValue(key, out var entry) ? [.. entry.Instances] : [];
        }
    }

    private double ResidentMiB()
    {
        lock (_lock)
        {
            return _resident.Values.Sum(e => e.Instances.Sum(i => i.VramBytes)) / (1024.0 * 1024.0);
        }
    }

    private double ResidentMiBExcept(string key)
    {
        lock (_lock)
        {
            return _resident.Where(e => !string.Equals(e.Key, key, StringComparison.OrdinalIgnoreCase)).Sum(e => e.Value.Instances.Sum(i => i.VramBytes)) / (1024.0 * 1024.0);
        }
    }

    private void Remember(string key, string name, int context, string keepAlive, bool onCpu = false)
    {
        // Until /api/ps is read again the instance takes what the catalog says a model of this kind takes at this context.
        var estimated = _provider.Catalog.GetResidency(name) is { } estimate ? estimate.EstimateMiB(context) * 1024L * 1024L : 0L;
        var now = _runtime.Time.GetUtcNow();
        lock (_lock)
        {
            if (!_resident.TryGetValue(key, out var entry))
            {
                _resident[key] = entry = (name, []);
            }

            var existing = entry.Instances.FindIndex(i => i.Context == context);
            var size = existing >= 0 ? entry.Instances[existing].SizeBytes : estimated;
            var instance = new Instance(context, size, onCpu ? 0 : existing >= 0 ? entry.Instances[existing].VramBytes : estimated, ExpiryOf(keepAlive, now), keepAlive);
            if (existing >= 0)
            {
                entry.Instances[existing] = instance;
            }
            else
            {
                entry.Instances.Add(instance);
            }
        }
    }

    private void Forget(string key)
    {
        lock (_lock)
        {
            _resident.Remove(key);
        }
    }

    /// <summary>Reads <c>/api/ps</c> unless the last answer is younger than the cache duration. Returns whether the answer is trustworthy.</summary>
    private async Task<bool> RefreshAsync(bool force, CancellationToken cancellationToken)
    {
        if (_psUnsupported)
        {
            return false;
        }

        if (!force && IsFresh())
        {
            return true;
        }

        await _psGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!force && IsFresh())
            {
                return true;
            }

            JsonDocument document;
            using var timeout = new CancellationTokenSource(PsTimeout, _runtime.Time);
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);
            try
            {
                document = await _provider.Http.GetJsonAsync("api/ps", modelId: null, linked.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (timeout.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
            {
                throw new ProviderException(
                    PillionErrorCode.ProviderUnavailable,
                    string.Create(CultureInfo.InvariantCulture, $"Ollama did not answer /api/ps within {PsTimeout.TotalSeconds:0} s; it may be stuck loading another model. Check what is resident with 'ollama ps'."),
                    isProviderFault: false);
            }
            catch (ProviderException ex) when (ex.StatusCode == HttpStatusCode.NotFound)
            {
                // A server without /api/ps (or a stand-in for one): there is nothing to learn, so requests go out as they used to.
                _psUnsupported = true;
                return false;
            }

            using (document)
            {
                ReadPs(document.RootElement);
            }

            return true;
        }
        finally
        {
            _psGate.Release();
        }
    }

    private bool IsFresh()
    {
        lock (_lock)
        {
            return _observedAt is { } at && _runtime.Time.GetUtcNow() - at < CacheDuration;
        }
    }

    private void ReadPs(JsonElement root)
    {
        var now = _runtime.Time.GetUtcNow();
        var fresh = new Dictionary<string, (string Name, List<Instance> Instances)>(StringComparer.OrdinalIgnoreCase);
        if (root.TryGetProperty("models", out var models) && models.ValueKind == JsonValueKind.Array)
        {
            foreach (var model in models.EnumerateArray())
            {
                var name = model.TryGetProperty("name", out var n) ? n.GetString() : model.TryGetProperty("model", out var m) ? m.GetString() : null;
                if (string.IsNullOrWhiteSpace(name))
                {
                    continue;
                }

                var expires = model.TryGetProperty("expires_at", out var e) ? ParseTimestamp(e.GetString()) : null;
                var context = model.TryGetProperty("context_length", out var c) && c.TryGetInt32(out var length) && length > 0 ? length : (int?)null;
                var instance = new Instance(
                    context,
                    model.TryGetProperty("size", out var s) && s.TryGetInt64(out var size) ? size : 0,
                    model.TryGetProperty("size_vram", out var v) && v.TryGetInt64(out var vram) ? vram : 0,
                    expires is { Year: >= 2100 } ? null : expires,
                    DeriveKeepAlive(expires, now));

                var key = KeyOf(name);
                if (!fresh.TryGetValue(key, out var entry))
                {
                    fresh[key] = entry = (name, []);
                }

                entry.Instances.Add(instance);
            }
        }

        lock (_lock)
        {
            _resident.Clear();
            foreach (var (key, entry) in fresh)
            {
                _resident[key] = entry;
            }

            _observedAt = now;
        }
    }

    private async Task<ResidencyPlan> DecideEmbeddingAsync(ResidencyRequest request, string key, string keepAlive, CancellationToken cancellationToken)
    {
        var verifiable = await RefreshAsync(force: false, cancellationToken).ConfigureAwait(false);
        var instances = InstancesOf(key);

        // The smallest resident instance that is big enough serves the request, on whatever device it runs.
        var fit = instances.Where(i => i.Context is { } c && c >= request.NeedTokens).OrderBy(i => i.Context).FirstOrDefault();
        if (fit is { Context: { } resident })
        {
            return new ResidencyPlan(resident, keepAlive, CpuOnly: fit.SizeBytes > 0 && fit.VramBytes == 0);
        }

        var model = request.ModelId;
        var tier = CurrentTier();
        var estimate = _provider.Catalog.GetResidency(model);
        var window = _provider.ContextWindowOf(model);
        var wanted = request.ExplicitContext ?? estimate?.RecommendedContextFor(tier, _provider.Options.EmbeddingContextSize) ?? _provider.Options.EmbeddingContextSize;
        var target = Math.Min(Math.Max(wanted, RoundUpContext(request.NeedTokens)), Math.Max(window, MinimumContext));
        if (!verifiable || instances.Any(i => i.Context is null))
        {
            // Nothing can be verified without /api/ps (or the server did not say what the resident instance has): send the request.
            Remember(key, model, target, keepAlive);
            return new ResidencyPlan(target, keepAlive);
        }

        // A resident instance that is too small is unloaded first (through /api/embed: embedding models have no /api/generate).
        if (instances.Count > 0)
        {
            await ReleaseAsync(request, key, instances.Max(i => i.Context ?? 0), target, cancellationToken).ConfigureAwait(false);
        }

        var others = ResidentMiBExcept(key);

        var cpuOnly = false;
        if (TierBudgetMiB(tier) is { } budget)
        {
            var needed = estimate?.EstimateMiB(target);
            cpuOnly = needed is null ? others > 0 : needed.Value > budget - others;
            if (cpuOnly)
            {
                LogOnCpu(_logger, ProviderId, model, others, budget);
                Report(
                    request,
                    ModelProgressKind.WaitingForLocalModel,
                    TimeSpan.Zero,
                    string.Create(CultureInfo.InvariantCulture, $"{model} runs on the CPU: the resident models leave {Math.Max(0, budget - others):N0} MiB of GPU memory and it needs {(needed is { } n ? n.ToString("N0", CultureInfo.InvariantCulture) : "an unknown amount")} MiB."));
            }
        }

        Remember(key, model, target, keepAlive, onCpu: cpuOnly);
        return new ResidencyPlan(target, keepAlive, cpuOnly);
    }

    private async Task<ResidencyPlan> DecideAsync(ResidencyRequest request, string key, int wanted, string keepAlive, CancellationToken cancellationToken)
    {
        var verifiable = await RefreshAsync(force: false, cancellationToken).ConfigureAwait(false);
        var instances = InstancesOf(key);

        // The smallest resident instance that is big enough serves the request, whatever its profile default says.
        var fit = instances.Where(i => i.Context is { } c && c >= request.NeedTokens).OrderBy(i => i.Context).FirstOrDefault();
        if (fit is { Context: { } resident })
        {
            return new ResidencyPlan(resident, keepAlive);
        }

        if (instances.Any(i => i.Context is null))
        {
            // The server did not say what the resident instance was loaded with, so the profile default is the best guess.
            return new ResidencyPlan(wanted, keepAlive);
        }

        var model = request.ModelId;
        var tier = CurrentTier();
        var estimate = _provider.Catalog.GetResidency(model);
        var window = _provider.ContextWindowOf(model);
        var target = ChooseTarget(request, key, wanted, tier, estimate, window);
        if (!verifiable)
        {
            // Nothing can be verified without /api/ps: send the request and let Ollama decide, as before.
            Remember(key, model, target, keepAlive);
            return new ResidencyPlan(target, keepAlive);
        }

        if (instances.Count > 0)
        {
            var current = instances.Max(i => i.Context ?? 0);
            if (CanLoadSecondInstance(estimate, target, tier))
            {
                LogSecondInstance(_logger, ProviderId, model, target, current);
            }
            else
            {
                await ReleaseAsync(request, key, current, target, cancellationToken).ConfigureAwait(false);
            }
        }

        await LoadAsync(request, key, target, keepAlive, cancellationToken).ConfigureAwait(false);
        return new ResidencyPlan(target, keepAlive);
    }

    private GpuTier CurrentTier()
    {
        var tier = _runtime.Tier();
        lock (_lock)
        {
            _tier = tier;
        }

        return tier;
    }

    /// <summary>
    /// The context to load a model at: the resident size (the user's override, else the largest size any routed task class
    /// asks for, capped for a model shared between autocomplete and chat on a tier that cannot hold two instances), raised to
    /// what the request needs and limited by the model's window and the tier's memory.
    /// </summary>
    private int ChooseTarget(ResidencyRequest request, string key, int wanted, GpuTier tier, ModelResidencyEstimate? estimate, int window)
    {
        int wantedMax;
        lock (_lock)
        {
            wantedMax = _wanted.TryGetValue(key, out var set) && set.Count > 0 ? Math.Max(wanted, set.Max()) : wanted;
        }

        int resident;
        if (_runtime.ResidentContextOverride(request.ModelId) is { } overridden && overridden > 0)
        {
            resident = overridden;
        }
        else if ((request.IsFim || _provider.IsCompletionModel(request.ModelId)) && !FitsTwice(estimate, wantedMax, tier))
        {
            resident = Math.Min(wantedMax, estimate?.RecommendedContextFor(tier, DefaultSharedContext) ?? DefaultSharedContext);
        }
        else
        {
            resident = wantedMax;
        }

        // Memory limit for one instance on its own: the weights plus the cache must fit the tier (a model that does not fit
        // at all runs partly on the CPU, which is Ollama's decision, not a limit on the context).
        // The window the catalog guesses for an unknown model is only a placeholder, so it never cuts below what Pillion sent before.
        var maxContext = Math.Max(window, resident);
        if (estimate is not null && TierBudgetMiB(tier) is { } budget && estimate.WeightsMiB < budget)
        {
            maxContext = Math.Min(maxContext, estimate.MaxContextWithin(budget) / 1024 * 1024);
        }

        if (request.NeedTokens > maxContext)
        {
            throw new ProviderException(
                PillionErrorCode.ContextOverflow,
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"The request needs about {request.NeedTokens:N0} tokens of context, but {request.ModelId} holds at most {maxContext:N0} on this machine ({DescribeTier(tier)} GPU tier). Remove some context from the request, or use a larger model or a cloud model."),
                isProviderFault: false);
        }

        return Math.Min(Math.Max(resident, RoundUpContext(request.NeedTokens)), maxContext);
    }

    private static bool FitsTwice(ModelResidencyEstimate? estimate, int context, GpuTier tier) =>
        estimate is not null && TierBudgetMiB(tier) is { } budget && 2L * estimate.EstimateMiB(context) <= budget;

    /// <summary>Whether Ollama can load a second instance of the model beside the resident one: the free memory covers the estimate.</summary>
    private bool CanLoadSecondInstance(ModelResidencyEstimate? estimate, int target, GpuTier tier)
    {
        if (estimate is null || TierBudgetMiB(tier) is not { } budget)
        {
            return false;
        }

        return estimate.EstimateMiB(target) <= budget - ResidentMiB();
    }

    private async Task ReleaseAsync(ResidencyRequest request, string key, int currentContext, int target, CancellationToken cancellationToken)
    {
        var model = request.ModelId;
        var limit = _provider.Options.ReleaseTimeout;
        var started = _runtime.Time.GetTimestamp();
        Report(request, ModelProgressKind.WaitingForLocalModel, TimeSpan.Zero, string.Create(CultureInfo.InvariantCulture, $"Unloading {model} ({currentContext:N0}-token context) so it can be reloaded with {target:N0} tokens; this GPU cannot hold both."));
        LogReleasing(_logger, ProviderId, model, currentContext, target);

        using var timeout = new CancellationTokenSource(limit, _runtime.Time);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);
        try
        {
            var unload = request.IsEmbedding
                ? new JsonObject { ["model"] = model, ["input"] = new JsonArray(), ["keep_alive"] = 0 }
                : new JsonObject { ["model"] = model, ["prompt"] = string.Empty, ["stream"] = false, ["keep_alive"] = 0 };
            using (await _provider.Http.PostJsonAsync(request.IsEmbedding ? "api/embed" : "api/generate", unload, model, linked.Token).ConfigureAwait(false))
            {
            }

            // Ollama unloads when the runner has no request in flight, so wait until /api/ps no longer lists the model.
            while (true)
            {
                await RefreshAsync(force: true, linked.Token).ConfigureAwait(false);
                if (InstancesOf(key).Count == 0)
                {
                    return;
                }

                await Task.Delay(PollInterval, _runtime.Time, linked.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            Forget(key);
            throw new ProviderException(
                PillionErrorCode.ProviderUnavailable,
                string.Create(CultureInfo.InvariantCulture, $"Ollama did not unload {model} within {limit.TotalSeconds:0.#} s (waited {_runtime.Time.GetElapsedTime(started).TotalSeconds:0.#} s); it may still be answering another request. Try again in a moment."),
                isProviderFault: false);
        }
    }

    private async Task LoadAsync(ResidencyRequest request, string key, int context, string keepAlive, CancellationToken cancellationToken)
    {
        var model = request.ModelId;
        var limit = _provider.Options.LoadTimeout;
        var started = _runtime.Time.GetTimestamp();
        Report(request, ModelProgressKind.WaitingForLocalModel, TimeSpan.Zero, string.Create(CultureInfo.InvariantCulture, $"Loading {model} with {context:N0} tokens of context..."));

        var body = new JsonObject
        {
            ["model"] = model,
            ["prompt"] = string.Empty,
            ["stream"] = false,
            ["options"] = new JsonObject { ["num_ctx"] = context },
        };
        OllamaProvider.SetKeepAlive(body, keepAlive);

        using var timeout = new CancellationTokenSource(limit, _runtime.Time);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);
        using var stopBeat = CancellationTokenSource.CreateLinkedTokenSource(linked.Token);
        var beat = HeartbeatAsync(request, context, started, limit, stopBeat.Token);
        try
        {
            using (await _provider.Http.PostJsonAsync("api/generate", body, model, linked.Token).ConfigureAwait(false))
            {
            }

            Remember(key, model, context, keepAlive);
        }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            throw new ProviderException(
                PillionErrorCode.ProviderUnavailable,
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"Ollama did not finish loading {model} with {context:N0} tokens of context within {limit.TotalSeconds:0.#} s.{DescribeOthers(key)} Unload a model you do not need ('ollama stop <model>'), lower models.residentContext for {model}, or set OLLAMA_MAX_LOADED_MODELS."),
                isProviderFault: false);
        }
        finally
        {
            await stopBeat.CancelAsync().ConfigureAwait(false);
            await beat.ConfigureAwait(false);
        }
    }

    private async Task HeartbeatAsync(ResidencyRequest request, int context, long started, TimeSpan limit, CancellationToken stop)
    {
        try
        {
            while (true)
            {
                await Task.Delay(_provider.Options.ProgressInterval, _runtime.Time, stop).ConfigureAwait(false);
                var elapsed = _runtime.Time.GetElapsedTime(started);
                Report(
                    request,
                    ModelProgressKind.SlowerThanExpected,
                    elapsed,
                    string.Create(CultureInfo.InvariantCulture, $"Still loading {request.ModelId} with {context:N0} tokens of context ({elapsed.TotalSeconds:0} s of {limit.TotalSeconds:0} s)."));
            }
        }
        catch (OperationCanceledException)
        {
            // The load finished or was cancelled.
        }
    }

    private string DescribeOthers(string key)
    {
        List<ResidentModel> others;
        lock (_lock)
        {
            others = [.. Snapshot().Where(m => !string.Equals(KeyOf(m.ModelId), key, StringComparison.OrdinalIgnoreCase))];
        }

        if (others.Count == 0)
        {
            return string.Empty;
        }

        var names = string.Join(", ", others.Select(m => string.Create(CultureInfo.InvariantCulture, $"{m.ModelId} ({m.VramBytes / (1024.0 * 1024 * 1024):0.0} GiB of GPU memory)")));
        return $" These models are resident: {names}.";
    }

    private void Report(ResidencyRequest request, ModelProgressKind kind, TimeSpan elapsed, string message) =>
        _runtime.Progress.Report(new ModelProgress(request.RequestId ?? string.Empty, ProviderId, request.ModelId, kind, elapsed, message));

    [GeneratedRegex(@"^(\d+(?:\.\d+)?)(h|m|s)$", RegexOptions.CultureInvariant)]
    private static partial Regex DurationPattern();

    [GeneratedRegex(@"\.\d{8,}", RegexOptions.CultureInvariant)]
    private static partial Regex FractionPattern();

    [LoggerMessage(EventId = 441, Level = LogLevel.Information, Message = "Ollama provider '{ProviderId}' unloads {Model} ({Current} tokens of context) to reload it at {Target}.")]
    private static partial void LogReleasing(ILogger logger, string providerId, string model, int current, int target);

    [LoggerMessage(EventId = 443, Level = LogLevel.Information, Message = "Ollama provider '{ProviderId}' runs embedding model {Model} on the CPU: the other resident models take {ResidentMiB:N0} MiB of the {BudgetMiB:N0} MiB the GPU tier offers.")]
    private static partial void LogOnCpu(ILogger logger, string providerId, string model, double residentMiB, int budgetMiB);

    [LoggerMessage(EventId = 442, Level = LogLevel.Information, Message = "Ollama provider '{ProviderId}' loads a second instance of {Model} at {Target} tokens beside the resident {Current}.")]
    private static partial void LogSecondInstance(ILogger logger, string providerId, string model, int target, int current);

    private sealed record Instance(int? Context, long SizeBytes, long VramBytes, DateTimeOffset? ExpiresAt, string KeepAlive);
}

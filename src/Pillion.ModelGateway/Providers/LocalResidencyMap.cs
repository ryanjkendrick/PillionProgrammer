using System.Collections.Concurrent;
using Pillion.Common.Hardware;

namespace Pillion.ModelGateway;

/// <summary>A local model that is loaded in a runtime now (design section 6, Local model residency).</summary>
/// <param name="ProviderId">The provider that serves it.</param>
/// <param name="ModelId">The provider's model id.</param>
/// <param name="ContextLength">The context size (<c>num_ctx</c>) it is loaded with, or <see langword="null"/> when the runtime does not report one and Pillion has not loaded it.</param>
/// <param name="SizeBytes">The memory the instance takes in total, in bytes.</param>
/// <param name="VramBytes">The part of it that sits in GPU memory, in bytes (0 when the model runs on the CPU).</param>
/// <param name="KeepAlive">How long it stays loaded when idle: <c>-1</c> (forever), or the remaining time such as <c>29m</c>.</param>
/// <param name="ExpiresAt">When the runtime unloads it if it stays idle, or <see langword="null"/> when it stays loaded forever.</param>
/// <param name="IsPinned">Whether it stays loaded forever (<c>keep_alive</c> -1, the autocomplete model).</param>
public sealed record ResidentModel(
    string ProviderId,
    string ModelId,
    int? ContextLength,
    long SizeBytes,
    long VramBytes,
    string KeepAlive,
    DateTimeOffset? ExpiresAt,
    bool IsPinned);

/// <summary>Which local models are resident, for the status bar and diagnostics.</summary>
/// <param name="ObservedAt">When the runtimes were last asked.</param>
/// <param name="Tier">The GPU tier the residency rules assumed, or <see langword="null"/> when no request has needed it yet.</param>
/// <param name="Models">The resident models, largest first.</param>
public sealed record ResidencySnapshot(DateTimeOffset? ObservedAt, GpuTier? Tier, IReadOnlyList<ResidentModel> Models)
{
    /// <summary>Gets the total GPU memory the resident models use, in bytes.</summary>
    public long TotalVramBytes => Models.Sum(m => m.VramBytes);
}

/// <summary>A local runtime that can say which models it has loaded.</summary>
public interface ILocalResidencySource
{
    /// <summary>Gets the provider id.</summary>
    string ProviderId { get; }

    /// <summary>Gets what was observed last, without contacting the runtime.</summary>
    /// <returns>The resident models and when they were observed.</returns>
    (DateTimeOffset? ObservedAt, GpuTier? Tier, IReadOnlyList<ResidentModel> Models) GetLastKnown();

    /// <summary>Asks the runtime which models are loaded, unless the answer is fresh enough.</summary>
    /// <param name="force"><see langword="true"/> to ask even when the cached answer is younger than the cache duration (30 s).</param>
    /// <param name="cancellationToken">A token that cancels the call.</param>
    /// <returns>The resident models.</returns>
    Task<IReadOnlyList<ResidentModel>> GetResidentAsync(bool force, CancellationToken cancellationToken);
}

/// <summary>
/// The residency map of every local provider: which models are loaded, with their context size, GPU memory and
/// keep-alive. Local providers register themselves; the host reads <see cref="GetSnapshot"/> (no I/O) for the status bar
/// and <see cref="RefreshAsync"/> on demand. A refresh asks a runtime at most once every 30 s unless forced, and an
/// unreachable runtime keeps its last known answer. Thread-safe.
/// </summary>
public sealed class LocalResidencyMap
{
    private readonly ConcurrentDictionary<string, ILocalResidencySource> _sources = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Registers a source, replacing one with the same provider id.</summary>
    /// <param name="source">The source.</param>
    public void Register(ILocalResidencySource source)
    {
        ArgumentNullException.ThrowIfNull(source);
        _sources[source.ProviderId] = source;
    }

    /// <summary>Removes the source of a provider that left the settings, but only when it is still the registered one.</summary>
    /// <param name="providerId">The provider id.</param>
    /// <param name="source">The source to remove, or <see langword="null"/> to remove whatever is registered.</param>
    public void Unregister(string providerId, ILocalResidencySource? source = null)
    {
        ArgumentNullException.ThrowIfNull(providerId);
        if (source is null)
        {
            _sources.TryRemove(providerId, out _);
        }
        else
        {
            _sources.TryRemove(new KeyValuePair<string, ILocalResidencySource>(providerId, source));
        }
    }

    /// <summary>Gets what the runtimes reported last, without contacting them.</summary>
    /// <returns>The snapshot.</returns>
    public ResidencySnapshot GetSnapshot()
    {
        var models = new List<ResidentModel>();
        DateTimeOffset? observed = null;
        GpuTier? tier = null;
        foreach (var source in _sources.Values)
        {
            var known = source.GetLastKnown();
            models.AddRange(known.Models);
            observed = Latest(observed, known.ObservedAt);
            tier ??= known.Tier;
        }

        return new ResidencySnapshot(observed, tier, Order(models));
    }

    /// <summary>Asks the runtimes which models are loaded (at most once per 30 s each, unless forced).</summary>
    /// <param name="force"><see langword="true"/> to bypass the 30 s cache (an explicit refresh by the user).</param>
    /// <param name="cancellationToken">A token that cancels the call.</param>
    /// <returns>The snapshot.</returns>
    public async Task<ResidencySnapshot> RefreshAsync(bool force, CancellationToken cancellationToken)
    {
        foreach (var source in _sources.Values)
        {
            try
            {
                await source.GetResidentAsync(force, cancellationToken).ConfigureAwait(false);
            }
            catch (ProviderException)
            {
                // An unreachable runtime keeps its last known answer.
            }
        }

        return GetSnapshot();
    }

    private static DateTimeOffset? Latest(DateTimeOffset? a, DateTimeOffset? b) =>
        a is null ? b : b is null ? a : a > b ? a : b;

    private static List<ResidentModel> Order(List<ResidentModel> models) =>
        [.. models.OrderByDescending(m => m.SizeBytes).ThenBy(m => m.ModelId, StringComparer.OrdinalIgnoreCase)];
}

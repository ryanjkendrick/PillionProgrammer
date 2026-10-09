using Pillion.Common.Hardware;

namespace Pillion.ModelGateway;

/// <summary>
/// Gateway services that local providers use to manage what is resident (design section 6, Local model residency): the
/// progress sink that slow loads report to, the GPU tier, the user's resident-context overrides, the clock and the map
/// that the host reads for the status bar. Cloud providers ignore it.
/// </summary>
/// <param name="Progress">Receives the progress notices of loads and unloads.</param>
/// <param name="Tier">Gets the effective GPU tier now (the <c>models.gpuTier</c> setting, else the detected tier). Called lazily, when a decision needs it.</param>
/// <param name="ResidentContextOverride">Gets the <c>models.residentContext</c> entry for a model id, or <see langword="null"/> when the user set none.</param>
/// <param name="Time">The clock.</param>
/// <param name="Residency">The map the provider registers with, or <see langword="null"/>.</param>
public sealed record ProviderRuntime(
    IProgressSink Progress,
    Func<GpuTier> Tier,
    Func<string, int?> ResidentContextOverride,
    TimeProvider Time,
    LocalResidencyMap? Residency = null)
{
    /// <summary>Gets a runtime that reports nothing, assumes the tier the detector finds on this machine and has no overrides.</summary>
    public static ProviderRuntime Default { get; } = new(
        new NoProgressSink(),
        () => GpuTierDetector.Shared.Detect().Tier,
        _ => null,
        TimeProvider.System);
}

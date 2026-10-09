using System.Globalization;
using System.Text.Json.Serialization;

namespace Pillion.Common.Hardware;

/// <summary>
/// The GPU tiers of design section 6 (Local model residency). The tier decides which local models stay resident and
/// the autocomplete prompt budget (1,024 tokens on the CPU, 2,048 on a GPU).
/// </summary>
public enum GpuTier
{
    /// <summary>No usable GPU (none, a software adapter, or less than 6 GB of dedicated memory): a 1.5B FIM coder on the CPU.</summary>
    CpuOnly,

    /// <summary>About 8 GB of dedicated memory (6 to 11 GB): one 7B FIM coder. The reference machine.</summary>
    Gpu8GB,

    /// <summary>About 12 GB (11 to 15 GB): a FIM coder plus a chat model, tight.</summary>
    Gpu12GB,

    /// <summary>About 16 GB (15 to 22 GB): a 7B FIM coder plus a 14B chat model.</summary>
    Gpu16GB,

    /// <summary>24 GB or more (22 GB and up): a 7B FIM coder plus a 32B chat model.</summary>
    Gpu24GBPlus,
}

/// <summary>The <c>models.gpuTier</c> setting: <c>auto</c> detects the tier, any other value overrides detection.</summary>
public enum GpuTierSetting
{
    /// <summary>Detect the tier from the adapter's dedicated memory.</summary>
    [JsonStringEnumMemberName("auto")]
    Auto,

    /// <summary>Behave as if there were no GPU.</summary>
    [JsonStringEnumMemberName("cpu")]
    Cpu,

    /// <summary>Behave as an 8 GB GPU.</summary>
    [JsonStringEnumMemberName("8gb")]
    Gpu8GB,

    /// <summary>Behave as a 12 GB GPU.</summary>
    [JsonStringEnumMemberName("12gb")]
    Gpu12GB,

    /// <summary>Behave as a 16 GB GPU.</summary>
    [JsonStringEnumMemberName("16gb")]
    Gpu16GB,

    /// <summary>Behave as a GPU with 24 GB or more.</summary>
    [JsonStringEnumMemberName("24gb")]
    Gpu24GBPlus,
}

/// <summary>What the detector found.</summary>
/// <param name="Tier">The tier.</param>
/// <param name="DedicatedVideoMemoryBytes">The dedicated memory of the largest hardware adapter, or <see langword="null"/> when none was found.</param>
/// <param name="AdapterName">The adapter's name, when known.</param>
/// <param name="Source">Where the answer came from: <c>nvidia-smi</c>, <c>dxgi</c>, <c>registry</c>, <c>setting</c> or <c>none</c>.</param>
public sealed record GpuInfo(GpuTier Tier, long? DedicatedVideoMemoryBytes, string? AdapterName, string Source)
{
    /// <summary>Gets the result when nothing could be detected: CPU only.</summary>
    public static GpuInfo None { get; } = new(GpuTier.CpuOnly, null, null, "none");

    /// <inheritdoc/>
    public override string ToString() => DedicatedVideoMemoryBytes is { } bytes
        ? string.Create(CultureInfo.InvariantCulture, $"{Tier} ({AdapterName ?? "unknown adapter"}, {bytes / (1024 * 1024)} MB, from {Source})")
        : string.Create(CultureInfo.InvariantCulture, $"{Tier} (from {Source})");
}

/// <summary>Maps adapter memory and the setting to a <see cref="GpuTier"/>.</summary>
public static class GpuTiers
{
    private const long GiB = 1024L * 1024 * 1024;

    /// <summary>
    /// Maps dedicated video memory to a tier. The boundaries sit between the nominal sizes so that the memory an
    /// adapter reports (an "8 GB" card reports 7.8 to 8 GiB through DXGI) lands in its nominal tier: below 6 GiB is CPU
    /// only, below 11 GiB is 8 GB, below 15 GiB is 12 GB, below 22 GiB is 16 GB, anything larger is 24 GB or more.
    /// </summary>
    /// <param name="dedicatedVideoMemoryBytes">The dedicated memory, or <see langword="null"/> when there is no hardware adapter.</param>
    /// <returns>The tier.</returns>
    public static GpuTier FromVideoMemory(long? dedicatedVideoMemoryBytes) => dedicatedVideoMemoryBytes switch
    {
        null => GpuTier.CpuOnly,
        < 6 * GiB => GpuTier.CpuOnly,
        < 11 * GiB => GpuTier.Gpu8GB,
        < 15 * GiB => GpuTier.Gpu12GB,
        < 22 * GiB => GpuTier.Gpu16GB,
        _ => GpuTier.Gpu24GBPlus,
    };

    /// <summary>Applies the <c>models.gpuTier</c> setting: <see cref="GpuTierSetting.Auto"/> asks the detector, anything else wins.</summary>
    /// <param name="setting">The setting.</param>
    /// <param name="detector">The detector (only called for <see cref="GpuTierSetting.Auto"/>).</param>
    /// <returns>The effective GPU information.</returns>
    public static GpuInfo Resolve(GpuTierSetting setting, IGpuTierDetector detector)
    {
        ArgumentNullException.ThrowIfNull(detector);
        return setting switch
        {
            GpuTierSetting.Cpu => new GpuInfo(GpuTier.CpuOnly, null, null, "setting"),
            GpuTierSetting.Gpu8GB => new GpuInfo(GpuTier.Gpu8GB, null, null, "setting"),
            GpuTierSetting.Gpu12GB => new GpuInfo(GpuTier.Gpu12GB, null, null, "setting"),
            GpuTierSetting.Gpu16GB => new GpuInfo(GpuTier.Gpu16GB, null, null, "setting"),
            GpuTierSetting.Gpu24GBPlus => new GpuInfo(GpuTier.Gpu24GBPlus, null, null, "setting"),
            _ => detector.Detect(),
        };
    }
}

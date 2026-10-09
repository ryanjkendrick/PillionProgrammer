using System.Text.Json;
using Pillion.Common.Hardware;
using Pillion.Common.Settings;
using Xunit.Abstractions;

namespace Pillion.Common.Tests;

public sealed class GpuTierTests(ITestOutputHelper output)
{
    private const long MiB = 1024L * 1024;
    private static readonly string[] Sources = ["nvidia-smi", "dxgi", "registry", "none"];

    [Theory]
    [InlineData(null, GpuTier.CpuOnly)]
    [InlineData(0L, GpuTier.CpuOnly)]
    [InlineData(128L, GpuTier.CpuOnly)]          // integrated graphics' dedicated slice
    [InlineData(4096L, GpuTier.CpuOnly)]
    [InlineData(6143L, GpuTier.CpuOnly)]
    [InlineData(6144L, GpuTier.Gpu8GB)]
    [InlineData(7979L, GpuTier.Gpu8GB)]           // an 8 GB card as DXGI reports it
    [InlineData(8192L, GpuTier.Gpu8GB)]           // RTX 2080 SUPER through nvidia-smi
    [InlineData(12282L, GpuTier.Gpu12GB)]
    [InlineData(16376L, GpuTier.Gpu16GB)]
    [InlineData(20480L, GpuTier.Gpu16GB)]
    [InlineData(24564L, GpuTier.Gpu24GBPlus)]
    [InlineData(49152L, GpuTier.Gpu24GBPlus)]
    public void FromVideoMemory_MapsToTheNominalTier(long? mib, GpuTier expected) =>
        Assert.Equal(expected, GpuTiers.FromVideoMemory(mib is { } m ? m * MiB : null));

    [Theory]
    [InlineData("8192 MiB, NVIDIA GeForce RTX 2080 SUPER\r\n", 8192, "NVIDIA GeForce RTX 2080 SUPER")]
    [InlineData("8192, NVIDIA GeForce RTX 2080 SUPER\n", 8192, "NVIDIA GeForce RTX 2080 SUPER")]
    [InlineData("24576 MiB, NVIDIA GeForce RTX 4090\n8192 MiB, NVIDIA GeForce RTX 3070\n", 24576, "NVIDIA GeForce RTX 4090")]
    public void NvidiaSmi_OutputIsParsed(string output, long largestMib, string name)
    {
        var adapters = NvidiaSmiProbe.Parse(output);

        Assert.NotNull(adapters);
        var largest = adapters.MaxBy(a => a.DedicatedVideoMemoryBytes)!;
        Assert.Equal(largestMib * MiB, largest.DedicatedVideoMemoryBytes);
        Assert.Equal(name, largest.Name);
    }

    [Fact]
    public void NvidiaSmi_GarbageIsNotAnAnswer() => Assert.Null(NvidiaSmiProbe.Parse("NVIDIA-SMI has failed because it couldn't communicate with the NVIDIA driver."));

    [Fact]
    public void Detector_UsesTheFirstProbeThatAnswers_TheLargestAdapter_AndCaches()
    {
        var missing = new FakeProbe("nvidia-smi", null);
        var dxgi = new FakeProbe("dxgi", [new GpuAdapter("Intel UHD", 128 * MiB), new GpuAdapter("RTX 2080 SUPER", 8031 * MiB)]);
        var registry = new FakeProbe("registry", [new GpuAdapter("other", 24 * 1024 * MiB)]);
        var detector = new GpuTierDetector([missing, dxgi, registry]);

        var first = detector.Detect();
        var second = detector.Detect();

        Assert.Equal(new GpuInfo(GpuTier.Gpu8GB, 8031 * MiB, "RTX 2080 SUPER", "dxgi"), first);
        Assert.Same(first, second);
        Assert.Equal(1, missing.Calls);
        Assert.Equal(1, dxgi.Calls);
        Assert.Equal(0, registry.Calls);
    }

    [Fact]
    public void Detector_AProbeThatFindsNoAdapter_MeansCpuOnly()
    {
        var detector = new GpuTierDetector([new FakeProbe("dxgi", [])]);

        Assert.Equal(new GpuInfo(GpuTier.CpuOnly, null, null, "dxgi"), detector.Detect());
    }

    [Fact]
    public void Detector_FailingProbesAreSkipped_AndNothingMeansCpuOnly()
    {
        var detector = new GpuTierDetector([new FakeProbe("nvidia-smi", null, new InvalidOperationException("boom")), new FakeProbe("dxgi", null)]);

        Assert.Equal(GpuInfo.None, detector.Detect());
    }

    [Fact]
    public void RealDetector_AnswersOnThisMachine()
    {
        // The tier depends on the machine (the reference machine reports Gpu8GB from nvidia-smi; CI reports what its
        // runner has); this only proves the probes run without throwing and agree with the mapping.
        var info = new GpuTierDetector().Detect();
        output.WriteLine($"Detected: {info}");
        foreach (var probe in new IGpuMemoryProbe[] { new NvidiaSmiProbe(), new DxgiProbe(), new RegistryVideoMemoryProbe() })
        {
            var adapters = probe.Probe();
            output.WriteLine($"{probe.Name}: {(adapters is null ? "not available" : string.Join("; ", adapters.Select(a => $"{a.Name} {a.DedicatedVideoMemoryBytes / MiB} MiB")))}");
        }

        Assert.Equal(info.Tier, info.DedicatedVideoMemoryBytes is null ? GpuTier.CpuOnly : GpuTiers.FromVideoMemory(info.DedicatedVideoMemoryBytes));
        Assert.Contains(info.Source, Sources);
    }

    [Fact]
    public void DxgiProbe_ReturnsHardwareAdaptersOnly()
    {
        var adapters = new DxgiProbe().Probe();

        if (!OperatingSystem.IsWindows())
        {
            Assert.Null(adapters);
            return;
        }

        Assert.NotNull(adapters);
        Assert.All(adapters, a => Assert.DoesNotContain("Basic Render", a.Name, StringComparison.OrdinalIgnoreCase));
        Assert.All(adapters, a => Assert.True(a.DedicatedVideoMemoryBytes >= 0));
    }

    [Theory]
    [InlineData(GpuTierSetting.Cpu, GpuTier.CpuOnly)]
    [InlineData(GpuTierSetting.Gpu8GB, GpuTier.Gpu8GB)]
    [InlineData(GpuTierSetting.Gpu12GB, GpuTier.Gpu12GB)]
    [InlineData(GpuTierSetting.Gpu16GB, GpuTier.Gpu16GB)]
    [InlineData(GpuTierSetting.Gpu24GBPlus, GpuTier.Gpu24GBPlus)]
    public void Setting_OverridesDetection(GpuTierSetting setting, GpuTier expected)
    {
        var probe = new FakeProbe("dxgi", [new GpuAdapter("x", 8192 * MiB)]);

        var info = GpuTiers.Resolve(setting, new GpuTierDetector([probe]));

        Assert.Equal(expected, info.Tier);
        Assert.Equal("setting", info.Source);
        Assert.Equal(0, probe.Calls);
    }

    [Fact]
    public void Setting_Auto_Detects()
    {
        var info = GpuTiers.Resolve(GpuTierSetting.Auto, new GpuTierDetector([new FakeProbe("dxgi", [new GpuAdapter("x", 16 * 1024 * MiB)])]));

        Assert.Equal(GpuTier.Gpu16GB, info.Tier);
    }

    [Fact]
    public void ModelsSection_IsWrittenWithAutoAndReadsTheOverride()
    {
        var json = JsonSerializer.SerializeToNode(new PillionSettings(), SettingsJsonForTests())!;
        Assert.Equal("auto", json["models"]!["gpuTier"]!.GetValue<string>());

        var settings = JsonSerializer.Deserialize<PillionSettings>("""{ "models": { "gpuTier": "12gb" } }""", SettingsJsonForTests())!;
        Assert.Equal(GpuTierSetting.Gpu12GB, settings.Models.GpuTier);
    }

    private static JsonSerializerOptions SettingsJsonForTests() => new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    private sealed class FakeProbe(string name, IReadOnlyList<GpuAdapter>? adapters, Exception? failure = null) : IGpuMemoryProbe
    {
        public int Calls;

        public string Name => name;

        public IReadOnlyList<GpuAdapter>? Probe()
        {
            Calls++;
            return failure is not null ? throw failure : adapters;
        }
    }
}

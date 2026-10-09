using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Pillion.Common.Hardware;

/// <summary>Detects the GPU tier of this machine (design section 6, Local model residency). Implementations cache the answer.</summary>
public interface IGpuTierDetector
{
    /// <summary>Gets the GPU tier of this machine. The first call probes the hardware; later calls return the cached answer.</summary>
    /// <returns>The tier and what it was based on.</returns>
    GpuInfo Detect();
}

/// <summary>One display adapter a probe found.</summary>
/// <param name="Name">The adapter's name.</param>
/// <param name="DedicatedVideoMemoryBytes">Its dedicated video memory.</param>
public sealed record GpuAdapter(string Name, long DedicatedVideoMemoryBytes);

/// <summary>One way of reading the adapters' video memory.</summary>
public interface IGpuMemoryProbe
{
    /// <summary>Gets the probe's name, reported as <see cref="GpuInfo.Source"/>.</summary>
    string Name { get; }

    /// <summary>Reads the hardware adapters.</summary>
    /// <returns>The adapters (an empty list when the probe works but finds none), or <see langword="null"/> when the probe is not available here.</returns>
    IReadOnlyList<GpuAdapter>? Probe();
}

/// <summary>
/// The default <see cref="IGpuTierDetector"/>: <c>nvidia-smi</c> when it is installed, else DXGI
/// (<c>IDXGIFactory1::EnumAdapters1</c> and <c>DXGI_ADAPTER_DESC1.DedicatedVideoMemory</c>, software adapters skipped),
/// else the display-class registry value <c>HardwareInformation.qwMemorySize</c>. The first probe that answers wins; the
/// largest adapter decides the tier; the result is cached for the life of the process.
/// </summary>
/// <remarks>
/// The last resort reads the registry rather than WMI's <c>Win32_VideoController.AdapterRAM</c>: that property is a 32-bit
/// value that stops at 4 GB, so it cannot tell a 4 GB card from a 24 GB one, and the registry value is the 64-bit source it
/// is truncated from (and needs no System.Management dependency).
/// </remarks>
public sealed partial class GpuTierDetector : IGpuTierDetector
{
    private readonly IReadOnlyList<IGpuMemoryProbe> _probes;
    private readonly ILogger _logger;
    private readonly Lazy<GpuInfo> _result;

    /// <summary>Initializes a new instance of the <see cref="GpuTierDetector"/> class with the default probes.</summary>
    /// <param name="logger">The logger.</param>
    public GpuTierDetector(ILogger<GpuTierDetector>? logger = null)
        : this([new NvidiaSmiProbe(), new DxgiProbe(), new RegistryVideoMemoryProbe()], logger)
    {
    }

    /// <summary>Initializes a new instance of the <see cref="GpuTierDetector"/> class.</summary>
    /// <param name="probes">The probes, tried in order.</param>
    /// <param name="logger">The logger.</param>
    public GpuTierDetector(IEnumerable<IGpuMemoryProbe> probes, ILogger<GpuTierDetector>? logger = null)
    {
        ArgumentNullException.ThrowIfNull(probes);
        _probes = probes.ToList();
        _logger = logger ?? NullLogger<GpuTierDetector>.Instance;
        _result = new Lazy<GpuInfo>(Run, LazyThreadSafetyMode.ExecutionAndPublication);
    }

    /// <summary>Gets a detector shared by everything in the process that has no detector of its own.</summary>
    public static GpuTierDetector Shared { get; } = new();

    /// <inheritdoc/>
    public GpuInfo Detect() => _result.Value;

    private GpuInfo Run()
    {
        foreach (var probe in _probes)
        {
            IReadOnlyList<GpuAdapter>? adapters;
            try
            {
                adapters = probe.Probe();
            }
            catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or IOException or UnauthorizedAccessException or System.Security.SecurityException or FormatException or EntryPointNotFoundException or DllNotFoundException)
            {
                LogProbeFailed(_logger, probe.Name, ex.Message);
                continue;
            }

            if (adapters is null)
            {
                continue;
            }

            var largest = adapters.OrderByDescending(a => a.DedicatedVideoMemoryBytes).FirstOrDefault();
            var info = largest is null
                ? GpuInfo.None with { Source = probe.Name }
                : new GpuInfo(GpuTiers.FromVideoMemory(largest.DedicatedVideoMemoryBytes), largest.DedicatedVideoMemoryBytes, largest.Name, probe.Name);
            LogDetected(_logger, info);
            return info;
        }

        LogDetected(_logger, GpuInfo.None);
        return GpuInfo.None;
    }

    [LoggerMessage(EventId = 160, Level = LogLevel.Information, Message = "GPU tier: {Info}")]
    private static partial void LogDetected(ILogger logger, GpuInfo info);

    [LoggerMessage(EventId = 161, Level = LogLevel.Debug, Message = "GPU probe {Probe} failed: {Reason}")]
    private static partial void LogProbeFailed(ILogger logger, string probe, string reason);
}

/// <summary>Reads <c>nvidia-smi --query-gpu=memory.total,name --format=csv,noheader</c>.</summary>
public sealed class NvidiaSmiProbe : IGpuMemoryProbe
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);

    /// <inheritdoc/>
    public string Name => "nvidia-smi";

    /// <inheritdoc/>
    public IReadOnlyList<GpuAdapter>? Probe()
    {
        var start = new ProcessStartInfo("nvidia-smi", "--query-gpu=memory.total,name --format=csv,noheader")
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };

        Process? process;
        try
        {
            process = Process.Start(start);
        }
        catch (Win32Exception)
        {
            // Not installed (no NVIDIA driver, or not on the PATH).
            return null;
        }

        if (process is null)
        {
            return null;
        }

        using (process)
        {
            var output = process.StandardOutput.ReadToEndAsync();
            _ = process.StandardError.ReadToEndAsync();
            if (!process.WaitForExit(Timeout))
            {
                process.Kill(entireProcessTree: true);
                return null;
            }

            return process.ExitCode == 0 ? Parse(output.GetAwaiter().GetResult()) : null;
        }
    }

    /// <summary>Parses the CSV output: one line per GPU, <c>8192 MiB, NVIDIA GeForce RTX 2080 SUPER</c> (or without the unit).</summary>
    /// <param name="output">The standard output.</param>
    /// <returns>The adapters, or <see langword="null"/> when no line could be read.</returns>
    public static IReadOnlyList<GpuAdapter>? Parse(string output)
    {
        ArgumentNullException.ThrowIfNull(output);
        var adapters = new List<GpuAdapter>();
        foreach (var raw in output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var comma = raw.IndexOf(',', StringComparison.Ordinal);
            var memory = (comma >= 0 ? raw[..comma] : raw).Trim();
            var name = comma >= 0 ? raw[(comma + 1)..].Trim() : "NVIDIA GPU";
            var number = memory.Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
            if (long.TryParse(number, NumberStyles.Integer, CultureInfo.InvariantCulture, out var mib) && mib > 0)
            {
                adapters.Add(new GpuAdapter(name, mib * 1024 * 1024));
            }
        }

        return adapters.Count > 0 ? adapters : null;
    }
}

/// <summary>Reads the dedicated memory of every hardware display adapter from the display-class registry keys.</summary>
public sealed class RegistryVideoMemoryProbe : IGpuMemoryProbe
{
    private const string DisplayClassKey = @"SYSTEM\CurrentControlSet\Control\Class\{4d36e968-e325-11ce-bfc1-08002be10318}";

    /// <inheritdoc/>
    public string Name => "registry";

    /// <inheritdoc/>
    public IReadOnlyList<GpuAdapter>? Probe()
    {
        if (!OperatingSystem.IsWindows())
        {
            return null;
        }

        using var root = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(DisplayClassKey);
        if (root is null)
        {
            return null;
        }

        var adapters = new List<GpuAdapter>();
        foreach (var name in root.GetSubKeyNames().Where(n => n.All(char.IsAsciiDigit)))
        {
            using var key = root.OpenSubKey(name);
            var bytes = key?.GetValue("HardwareInformation.qwMemorySize") switch
            {
                long value => value,
                byte[] { Length: 8 } raw => BitConverter.ToInt64(raw),
                _ => key?.GetValue("HardwareInformation.MemorySize") switch
                {
                    int value => (long)(uint)value,
                    byte[] { Length: 4 } raw => BitConverter.ToUInt32(raw),
                    _ => 0L,
                },
            };

            if (bytes > 0)
            {
                adapters.Add(new GpuAdapter(key?.GetValue("DriverDesc") as string ?? "display adapter", bytes));
            }
        }

        return adapters;
    }
}

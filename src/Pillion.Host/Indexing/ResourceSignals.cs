using System.Runtime.InteropServices;
using Pillion.Contracts;

namespace Pillion.Host.Indexing;

/// <summary>Reads whether Windows battery saver is on (design section 13: indexing pauses on battery saver).</summary>
internal interface IPowerStatus
{
    /// <summary>Gets a value indicating whether battery saver is on now.</summary>
    /// <returns><see langword="true"/> when Windows reports battery saver on; <see langword="false"/> when it is off or cannot be read.</returns>
    bool IsBatterySaverOn();
}

/// <summary>Reads battery saver through the Win32 <c>GetSystemPowerStatus</c> function (no Windows Forms).</summary>
internal sealed class Win32PowerStatus : IPowerStatus
{
    /// <inheritdoc/>
    public bool IsBatterySaverOn()
    {
        if (!OperatingSystem.IsWindows())
        {
            return false;
        }

        // SYSTEM_POWER_STATUS.SystemStatusFlag: 1 while battery saver is on (Windows 10 and later).
        return NativeMethods.GetSystemPowerStatus(out var status) && status.SystemStatusFlag == 1;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SystemPowerStatus
    {
        public byte AcLineStatus;
        public byte BatteryFlag;
        public byte BatteryLifePercent;
        public byte SystemStatusFlag;
        public int BatteryLifeTime;
        public int BatteryFullLifeTime;
    }

    private static class NativeMethods
    {
        [DllImport("kernel32.dll", SetLastError = true)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool GetSystemPowerStatus(out SystemPowerStatus status);
    }
}

/// <summary>Samples the host's memory use for the indexing memory cap (design section 13: 1.5 GB while indexing).</summary>
internal interface IMemorySampler
{
    /// <summary>Gets the host process's working set now, in bytes.</summary>
    /// <returns>The working set.</returns>
    long WorkingSetBytes();
}

/// <summary>Samples <see cref="Environment.WorkingSet"/> (managed and native memory alike, ONNX Runtime included).</summary>
internal sealed class ProcessMemorySampler : IMemorySampler
{
    /// <inheritdoc/>
    public long WorkingSetBytes() => Environment.WorkingSet;
}

/// <summary>
/// What the IDE is busy with, as the VSIX reports it in the <c>ideActivityChanged</c> notification (contract 1.3). Reset to
/// <see cref="IdeActivity.None"/> when the extension disconnects, so a Visual Studio that crashed during a build does not
/// leave indexing paused.
/// </summary>
internal sealed class IdeActivityMonitor
{
    /// <summary>The notification the VSIX sends (one positional argument, an <see cref="IdeActivity"/>).</summary>
    public const string NotificationName = "ideActivityChanged";

    private int _current;

    /// <summary>Raised after <see cref="Current"/> changes, on the caller's thread.</summary>
    public event EventHandler? Changed;

    /// <summary>Gets the activities in progress.</summary>
    public IdeActivity Current => (IdeActivity)Volatile.Read(ref _current);

    /// <summary>Records the activities in progress (the notification handler).</summary>
    /// <param name="activity">Every activity in progress now.</param>
    public void Set(IdeActivity activity)
    {
        var known = activity & (IdeActivity.Build | IdeActivity.Debug | IdeActivity.Test);
        if (Interlocked.Exchange(ref _current, (int)known) != (int)known)
        {
            Changed?.Invoke(this, EventArgs.Empty);
        }
    }
}

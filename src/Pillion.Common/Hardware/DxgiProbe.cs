using System.Runtime.InteropServices;

namespace Pillion.Common.Hardware;

/// <summary>
/// Reads the hardware adapters through DXGI: <c>CreateDXGIFactory1</c>, then <c>IDXGIFactory1::EnumAdapters1</c> and
/// <c>IDXGIAdapter1::GetDesc1</c> for each adapter, keeping <c>DXGI_ADAPTER_DESC1.DedicatedVideoMemory</c> and skipping
/// software adapters (the Microsoft Basic Render Driver). The COM calls go through the vtable with function pointers.
/// </summary>
public sealed unsafe partial class DxgiProbe : IGpuMemoryProbe
{
    private const int DxgiErrorNotFound = unchecked((int)0x887A0002);
    private const uint DxgiAdapterFlagSoftware = 2;

    // IUnknown (3) + IDXGIObject (4) + IDXGIFactory (5) precede EnumAdapters1 in IDXGIFactory1's vtable.
    private const int EnumAdapters1Slot = 12;

    // IUnknown (3) + IDXGIObject (4) + IDXGIAdapter (3) precede GetDesc1 in IDXGIAdapter1's vtable.
    private const int GetDesc1Slot = 10;
    private const int ReleaseSlot = 2;
    private const int MaxAdapters = 16;

    private static readonly Guid IidIdxgiFactory1 = new("770aae78-f26f-4dba-a829-253c83d1b387");

    /// <inheritdoc/>
    public string Name => "dxgi";

    /// <inheritdoc/>
    public IReadOnlyList<GpuAdapter>? Probe()
    {
        if (!OperatingSystem.IsWindows())
        {
            return null;
        }

        var iid = IidIdxgiFactory1;
        if (CreateDXGIFactory1(in iid, out var factory) < 0 || factory == IntPtr.Zero)
        {
            return null;
        }

        var adapters = new List<GpuAdapter>();
        try
        {
            var factoryVtable = *(IntPtr**)factory;
            var enumAdapters1 = (delegate* unmanaged[Stdcall]<IntPtr, uint, IntPtr*, int>)factoryVtable[EnumAdapters1Slot];
            for (uint index = 0; index < MaxAdapters; index++)
            {
                IntPtr adapter;
                var hr = enumAdapters1(factory, index, &adapter);
                if (hr == DxgiErrorNotFound || hr < 0 || adapter == IntPtr.Zero)
                {
                    break;
                }

                try
                {
                    var adapterVtable = *(IntPtr**)adapter;
                    var getDesc1 = (delegate* unmanaged[Stdcall]<IntPtr, AdapterDesc1*, int>)adapterVtable[GetDesc1Slot];
                    AdapterDesc1 desc;
                    if (getDesc1(adapter, &desc) >= 0 && (desc.Flags & DxgiAdapterFlagSoftware) == 0)
                    {
                        var name = new string(desc.Description, 0, 128).TrimEnd('\0');
                        adapters.Add(new GpuAdapter(name, (long)desc.DedicatedVideoMemory));
                    }
                }
                finally
                {
                    Release(adapter);
                }
            }
        }
        finally
        {
            Release(factory);
        }

        return adapters;
    }

    private static void Release(IntPtr unknown)
    {
        var vtable = *(IntPtr**)unknown;
        _ = ((delegate* unmanaged[Stdcall]<IntPtr, uint>)vtable[ReleaseSlot])(unknown);
    }

    [LibraryImport("dxgi.dll")]
    private static partial int CreateDXGIFactory1(in Guid riid, out IntPtr factory);

    /// <summary>DXGI_ADAPTER_DESC1.</summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct AdapterDesc1
    {
        public fixed char Description[128];
        public uint VendorId;
        public uint DeviceId;
        public uint SubSysId;
        public uint Revision;
        public nuint DedicatedVideoMemory;
        public nuint DedicatedSystemMemory;
        public nuint SharedSystemMemory;
        public uint AdapterLuidLowPart;
        public int AdapterLuidHighPart;
        public uint Flags;
    }
}

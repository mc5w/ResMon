using System.Runtime.InteropServices;

namespace ResMon.Core.Native;

/// <summary>Rohwerte aus <c>GlobalMemoryStatusEx</c>.</summary>
public readonly record struct PhysicalMemoryStatus(long TotalBytes, long AvailableBytes)
{
    public long UsedBytes => TotalBytes - AvailableBytes;

    public double UsedPercent => TotalBytes > 0 ? UsedBytes * 100.0 / TotalBytes : 0;
}

/// <summary>
/// Gesamter und freier physischer Speicher. Günstiger als ein PDH-Zähler und
/// jederzeit gültig, weil es kein ratenbasierter Wert ist (DESIGN.md §8.2).
/// </summary>
public static class SystemMemory
{
    [StructLayout(LayoutKind.Sequential)]
    private struct MEMORYSTATUSEX
    {
        public uint dwLength;
        public uint dwMemoryLoad;
        public ulong ullTotalPhys;
        public ulong ullAvailPhys;
        public ulong ullTotalPageFile;
        public ulong ullAvailPageFile;
        public ulong ullTotalVirtual;
        public ulong ullAvailVirtual;
        public ulong ullAvailExtendedVirtual;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX lpBuffer);

    public static PhysicalMemoryStatus Read()
    {
        var status = new MEMORYSTATUSEX { dwLength = (uint)Marshal.SizeOf<MEMORYSTATUSEX>() };
        if (!GlobalMemoryStatusEx(ref status))
            return new PhysicalMemoryStatus(0, 0);

        return new PhysicalMemoryStatus((long)status.ullTotalPhys, (long)status.ullAvailPhys);
    }
}

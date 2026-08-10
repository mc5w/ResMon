using System.Runtime.InteropServices;

namespace ResMon.Core.Native;

/// <summary>Ein Eintrag aus dem Toolhelp-Snapshot.</summary>
public readonly record struct ProcessTreeEntry(int Pid, int ParentPid, string ExeName, int ThreadCount);

/// <summary>
/// Prozessbaum über <c>CreateToolhelp32Snapshot</c>. Deutlich schneller als
/// <c>Win32_Process</c> über WMI und ohne Sonderrechte nutzbar (DESIGN.md §8.5).
/// </summary>
public static class Toolhelp
{
    private const uint TH32CS_SNAPPROCESS = 0x00000002;
    private static readonly IntPtr InvalidHandle = new(-1);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr CreateToolhelp32Snapshot(uint dwFlags, uint th32ProcessID);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool Process32FirstW(IntPtr hSnapshot, ref PROCESSENTRY32W lppe);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool Process32NextW(IntPtr hSnapshot, ref PROCESSENTRY32W lppe);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr hObject);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct PROCESSENTRY32W
    {
        public uint dwSize;
        public uint cntUsage;
        public uint th32ProcessID;
        public IntPtr th32DefaultHeapID;
        public uint th32ModuleID;
        public uint cntThreads;
        public uint th32ParentProcessID;
        public int pcPriClassBase;
        public uint dwFlags;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
        public string szExeFile;
    }

    /// <summary>Liest alle laufenden Prozesse samt Eltern-PID, indiziert nach PID.</summary>
    public static Dictionary<int, ProcessTreeEntry> Snapshot()
    {
        var result = new Dictionary<int, ProcessTreeEntry>(512);
        IntPtr snapshot = CreateToolhelp32Snapshot(TH32CS_SNAPPROCESS, 0);
        if (snapshot == InvalidHandle)
            return result;

        try
        {
            var entry = new PROCESSENTRY32W { dwSize = (uint)Marshal.SizeOf<PROCESSENTRY32W>() };
            if (!Process32FirstW(snapshot, ref entry))
                return result;

            do
            {
                int pid = (int)entry.th32ProcessID;
                result[pid] = new ProcessTreeEntry(
                    pid,
                    (int)entry.th32ParentProcessID,
                    entry.szExeFile ?? string.Empty,
                    (int)entry.cntThreads);
            }
            while (Process32NextW(snapshot, ref entry));
        }
        finally
        {
            CloseHandle(snapshot);
        }

        return result;
    }
}

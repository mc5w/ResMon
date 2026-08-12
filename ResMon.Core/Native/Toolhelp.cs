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
    private const uint TH32CS_SNAPTHREAD = 0x00000004;
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

    /// <summary>
    /// Die Thread-Kennungen eines Prozesses. Grundlage der Wartekettenanalyse:
    /// <c>GetThreadWaitChain</c> fragt je Thread, nicht je Prozess.
    /// </summary>
    /// <remarks>
    /// Der Schnappschuss umfasst systemweit <b>alle</b> Threads — der Parameter
    /// von <c>CreateToolhelp32Snapshot</c> wird bei <c>TH32CS_SNAPTHREAD</c>
    /// ignoriert, gefiltert werden muss also hier. Das sind auf einem laufenden
    /// System einige tausend Einträge, aber der Aufruf kostet trotzdem nur
    /// Bruchteile einer Millisekunde und läuft ohnehin nur auf Anforderung.
    /// </remarks>
    public static List<int> ThreadsOf(int pid)
    {
        var threads = new List<int>();
        IntPtr snapshot = CreateToolhelp32Snapshot(TH32CS_SNAPTHREAD, 0);
        if (snapshot == InvalidHandle)
            return threads;

        try
        {
            var entry = new THREADENTRY32 { dwSize = (uint)Marshal.SizeOf<THREADENTRY32>() };
            if (!Thread32First(snapshot, ref entry))
                return threads;

            do
            {
                if (entry.th32OwnerProcessID == (uint)pid)
                    threads.Add((int)entry.th32ThreadID);
            }
            while (Thread32Next(snapshot, ref entry));
        }
        finally
        {
            CloseHandle(snapshot);
        }

        return threads;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool Thread32First(IntPtr hSnapshot, ref THREADENTRY32 lpte);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool Thread32Next(IntPtr hSnapshot, ref THREADENTRY32 lpte);

    [StructLayout(LayoutKind.Sequential)]
    private struct THREADENTRY32
    {
        public uint dwSize;
        public uint cntUsage;
        public uint th32ThreadID;
        public uint th32OwnerProcessID;
        public int tpBasePri;
        public int tpDeltaPri;
        public uint dwFlags;
    }
}

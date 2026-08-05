using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using ResMon.Core.Model;
using ResMon.Core.Native;
using ResMon.Core.Sensors;

namespace ResMon.Core.Processes;

/// <summary>
/// Führt die Prozessliste aus <c>Process V2</c>, GPU-Zählern, Toolhelp-Prozessbaum
/// und Dienstauflösung zusammen (DESIGN.md §6, §8.2).
/// </summary>
/// <remarks>
/// Der teuerste Teil der Erfassung — läuft nur, wenn das Detailfenster offen ist.
/// Nicht threadsicher; alle Aufrufe gehören auf denselben Takt.
/// </remarks>
public sealed class ProcessSampler : IDisposable
{
    private const string CpuPath = @"\Process V2(*)\% Processor Time";
    private const string WorkingSetPath = @"\Process V2(*)\Working Set - Private";
    private const string PrivateBytesPath = @"\Process V2(*)\Private Bytes";

    // "Process V2" heißt der PID-Zähler "Process ID"; im älteren Satz "ID Process".
    // Beide Schreibweisen probieren, weil sich das zwischen Windows-Builds unterscheidet.
    private static readonly string[] V2PidPaths =
    [
        @"\Process V2(*)\Process ID",
        @"\Process V2(*)\ID Process",
    ];

    // Fallback für den Fall, dass "Process V2" fehlt (vor Windows 11).
    private const string LegacyCpuPath = @"\Process(*)\% Processor Time";
    private const string LegacyPidPath = @"\Process(*)\ID Process";
    private const string LegacyWorkingSetPath = @"\Process(*)\Working Set - Private";
    private const string LegacyPrivateBytesPath = @"\Process(*)\Private Bytes";

    private static readonly IReadOnlyDictionary<string, double> NoEngines =
        new Dictionary<string, double>(StringComparer.Ordinal);

    private readonly PdhQuery _query = new();
    private readonly ServiceResolver _services;
    private readonly int _processorCount = Environment.ProcessorCount;
    private readonly DescriptionCache _descriptions = new();

    private readonly PdhCounter? _cpu;
    private readonly PdhCounter? _pid;
    private readonly PdhCounter? _workingSet;
    private readonly PdhCounter? _privateBytes;

    public ProcessSampler(ServiceResolver services)
    {
        _services = services;

        _cpu = _query.TryAddCounter(CpuPath);
        if (_cpu is not null)
        {
            foreach (string path in V2PidPaths)
            {
                _pid = _query.TryAddCounter(path);
                if (_pid is not null)
                    break;
            }

            _workingSet = _query.TryAddCounter(WorkingSetPath);
            _privateBytes = _query.TryAddCounter(PrivateBytesPath);
        }

        if (_cpu is null || _pid is null)
        {
            UsesLegacyCounterSet = true;
            _cpu = _query.TryAddCounter(LegacyCpuPath);
            _pid = _query.TryAddCounter(LegacyPidPath);
            _workingSet = _query.TryAddCounter(LegacyWorkingSetPath);
            _privateBytes = _query.TryAddCounter(LegacyPrivateBytesPath);
        }
    }

    /// <summary>True, wenn <c>Process V2</c> fehlt und der ältere Zählersatz verwendet wird.</summary>
    public bool UsesLegacyCounterSet { get; }

    public bool Available => _cpu is not null && _pid is not null;

    /// <summary>
    /// Verwirft das Aufwärm-Sample. Nach einer Pause (Detailfenster geschlossen)
    /// wären die CPU-Deltas sonst über die gesamte Pause gemittelt.
    /// </summary>
    public void Restart() => _query.ResetPriming();

    /// <summary>
    /// Holt ein Sample. Liefert beim ersten Aufruf nach <see cref="Restart"/> eine
    /// leere Liste, weil ratenbasierte Zähler ein Vorgänger-Sample brauchen.
    /// </summary>
    public IReadOnlyList<ProcessSample> Sample(IReadOnlyDictionary<int, GpuProcessUsage> gpuByPid)
    {
        if (!Available || !_query.Collect())
            return [];

        // Instanzname -> PID. Process V2 vergibt eindeutige Instanzen pro Prozess
        // ("code:11508"), beim Legacy-Zählersatz teilen sich gleichnamige Prozesse
        // eine Instanz und werden nur durch "#n" unterschieden.
        var pidByInstance = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (PdhInstanceValueL entry in _pid!.ReadArrayInt64())
        {
            if (entry.Value is > 0 and <= int.MaxValue)
                pidByInstance[entry.Instance] = (int)entry.Value;
        }

        if (pidByInstance.Count == 0)
            return [];

        var workingSet = ReadByInstance(_workingSet);
        var privateBytes = ReadByInstance(_privateBytes);
        Dictionary<int, ProcessTreeEntry> tree = Toolhelp.Snapshot();
        _descriptions.Prune(tree.Keys);

        var samples = new List<ProcessSample>(pidByInstance.Count);
        foreach (PdhInstanceValue cpu in _cpu!.ReadArrayDouble(noCap100: true))
        {
            if (!pidByInstance.TryGetValue(cpu.Instance, out int pid) || pid == 0)
                continue;

            // "% Processor Time" summiert über alle Kerne; der Task-Manager zeigt
            // den Anteil an der Gesamtkapazität.
            double cpuPercent = Math.Clamp(cpu.Value / _processorCount, 0, 100);

            string name = ResolveName(cpu.Instance, pid, tree);
            GpuProcessUsage? gpu = gpuByPid.TryGetValue(pid, out GpuProcessUsage? usage) ? usage : null;
            int? parentPid = tree.TryGetValue(pid, out ProcessTreeEntry entry) && entry.ParentPid != 0
                ? entry.ParentPid
                : null;

            samples.Add(new ProcessSample(
                pid,
                parentPid,
                name,
                _descriptions.Get(pid),
                cpuPercent,
                workingSet.GetValueOrDefault(cpu.Instance),
                privateBytes.GetValueOrDefault(cpu.Instance),
                gpu?.TotalPercent ?? 0,
                gpu?.ByEngineType ?? NoEngines,
                gpu?.MemBytes ?? 0,
                _services.ForPid(pid)));
        }

        return samples;
    }

    private static Dictionary<string, long> ReadByInstance(PdhCounter? counter)
    {
        var result = new Dictionary<string, long>(StringComparer.Ordinal);
        if (counter is null)
            return result;

        foreach (PdhInstanceValueL entry in counter.ReadArrayInt64())
            result[entry.Instance] = entry.Value;
        return result;
    }

    /// <summary>
    /// Der Prozessname aus dem Toolhelp-Snapshot ist am verlässlichsten (er enthält
    /// die Dateiendung); die PDH-Instanz (<c>chrome:1234</c> bzw. <c>chrome#3</c>)
    /// dient als Rückfallebene.
    /// </summary>
    private static string ResolveName(string instance, int pid, Dictionary<int, ProcessTreeEntry> tree)
    {
        if (tree.TryGetValue(pid, out ProcessTreeEntry entry) && entry.ExeName.Length > 0)
            return entry.ExeName;

        string name = instance;
        int cut = name.IndexOfAny([':', '#']);
        if (cut > 0)
            name = name[..cut];

        return name;
    }

    public void Dispose() => _query.Dispose();

    /// <summary>
    /// Dateibeschreibungen sind teuer zu ermitteln (Handle öffnen, Versionsressource
    /// lesen) und ändern sich nie — deshalb pro PID einmalig ermitteln und halten.
    /// </summary>
    private sealed class DescriptionCache
    {
        private readonly Dictionary<int, string?> _byPid = [];
        private readonly Dictionary<string, string?> _byPath = new(StringComparer.OrdinalIgnoreCase);

        public string? Get(int pid)
        {
            if (_byPid.TryGetValue(pid, out string? cached))
                return cached;

            string? description = null;
            string? path = TryGetImagePath(pid);
            if (path is not null)
            {
                if (!_byPath.TryGetValue(path, out description))
                {
                    try
                    {
                        description = FileVersionInfo.GetVersionInfo(path).FileDescription;
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                    {
                        description = null;
                    }

                    _byPath[path] = description;
                }
            }

            _byPid[pid] = description;
            return description;
        }

        /// <summary>Entfernt Einträge beendeter Prozesse, damit der Cache nicht unbegrenzt wächst.</summary>
        public void Prune(IEnumerable<int> livePids)
        {
            var live = livePids as HashSet<int> ?? [.. livePids];
            if (live.Count == 0)
                return;

            foreach (int pid in _byPid.Keys.Where(pid => !live.Contains(pid)).ToList())
                _byPid.Remove(pid);
        }

        private static string? TryGetImagePath(int pid)
        {
            IntPtr handle = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
            if (handle == IntPtr.Zero)
                return null;

            try
            {
                var buffer = new StringBuilder(1024);
                int size = buffer.Capacity;
                return QueryFullProcessImageNameW(handle, 0, buffer, ref size) ? buffer.ToString() : null;
            }
            finally
            {
                CloseHandle(handle);
            }
        }

        private const int PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr OpenProcess(int dwDesiredAccess, [MarshalAs(UnmanagedType.Bool)] bool bInheritHandle, int dwProcessId);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool QueryFullProcessImageNameW(IntPtr hProcess, int dwFlags, StringBuilder lpExeName, ref int lpdwSize);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool CloseHandle(IntPtr hObject);
    }
}

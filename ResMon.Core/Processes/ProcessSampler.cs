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
    private const string IoReadPath = @"\Process V2(*)\IO Read Bytes/sec";
    private const string IoWritePath = @"\Process V2(*)\IO Write Bytes/sec";

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
    private const string LegacyIoReadPath = @"\Process(*)\IO Read Bytes/sec";
    private const string LegacyIoWritePath = @"\Process(*)\IO Write Bytes/sec";

    private static readonly IReadOnlyDictionary<string, double> NoEngines =
        new Dictionary<string, double>(StringComparer.Ordinal);

    private readonly PdhQuery _query = new();
    private readonly ServiceResolver _services;
    private readonly AppErrorLog? _faults;
    private readonly int _processorCount = Environment.ProcessorCount;
    private readonly ProcessInfoCache _info = new();

    private readonly PdhCounter? _cpu;
    private readonly PdhCounter? _pid;
    private readonly PdhCounter? _workingSet;
    private readonly PdhCounter? _privateBytes;
    private readonly PdhCounter? _ioRead;
    private readonly PdhCounter? _ioWrite;

    public ProcessSampler(ServiceResolver services, AppErrorLog? faults = null)
    {
        _services = services;
        _faults = faults;

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
            _ioRead = _query.TryAddCounter(IoReadPath);
            _ioWrite = _query.TryAddCounter(IoWritePath);
        }

        if (_cpu is null || _pid is null)
        {
            UsesLegacyCounterSet = true;
            _cpu = _query.TryAddCounter(LegacyCpuPath);
            _pid = _query.TryAddCounter(LegacyPidPath);
            _workingSet = _query.TryAddCounter(LegacyWorkingSetPath);
            _privateBytes = _query.TryAddCounter(LegacyPrivateBytesPath);
            _ioRead = _query.TryAddCounter(LegacyIoReadPath);
            _ioWrite = _query.TryAddCounter(LegacyIoWritePath);
        }
    }

    /// <summary>True, wenn <c>Process V2</c> fehlt und der ältere Zählersatz verwendet wird.</summary>
    public bool UsesLegacyCounterSet { get; }

    public bool Available => _cpu is not null && _pid is not null;

    /// <summary>
    /// PIDs des letzten Toolhelp-Snapshots. Der Netz-Tracer räumt damit die
    /// Zähler beendeter Prozesse ab.
    /// </summary>
    public IReadOnlySet<int> LivePids { get; private set; } = new HashSet<int>();

    /// <summary>
    /// Prozesse und Threads des letzten Toolhelp-Snapshots. Sie zählen alle
    /// laufenden Prozesse, nicht nur die mit lesbaren Zählern — die Summe über die
    /// Tabellenzeilen fiele deshalb kleiner aus.
    /// </summary>
    public int ProcessCount { get; private set; }

    public int ThreadCount { get; private set; }

    /// <summary>
    /// Verwirft das Aufwärm-Sample. Nach einer Pause (Detailfenster geschlossen)
    /// wären die CPU-Deltas sonst über die gesamte Pause gemittelt.
    /// </summary>
    public void Restart() => _query.ResetPriming();

    /// <summary>
    /// Holt ein Sample. Liefert beim ersten Aufruf nach <see cref="Restart"/> eine
    /// leere Liste, weil ratenbasierte Zähler ein Vorgänger-Sample brauchen.
    /// </summary>
    public IReadOnlyList<ProcessSample> Sample(
        IReadOnlyDictionary<int, GpuProcessUsage> gpuByPid,
        IReadOnlyDictionary<int, NetworkUsage> networkByPid,
        IReadOnlyDictionary<int, ProcessPorts> portsByPid)
    {
        if (!Available)
            return [];

        // Der Prozessbaum steht vor der Zählerabfrage: er liefert die Summen für
        // Prozesse und Threads, und die sollen auch beim verworfenen Aufwärm-Sample
        // schon stimmen.
        Dictionary<int, ProcessTreeEntry> tree = Toolhelp.Snapshot();
        LivePids = tree.Keys.ToHashSet();
        ProcessCount = tree.Count;

        int threads = 0;
        foreach (ProcessTreeEntry process in tree.Values)
            threads += process.ThreadCount;
        ThreadCount = threads;

        // Fenster ändern sich, während der Prozess lebt — anders als Konto und
        // Pfad lässt sich das nicht einmalig ermitteln und wegcachen.
        Dictionary<int, WindowOwners.WindowState> windows = WindowOwners.Snapshot();

        _info.Prune(LivePids);

        if (!_query.Collect())
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
        var ioRead = ReadRateByInstance(_ioRead);
        var ioWrite = ReadRateByInstance(_ioWrite);

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
            NetworkUsage network = networkByPid.GetValueOrDefault(pid);
            ProcessPorts ports = portsByPid.GetValueOrDefault(pid, ProcessPorts.Empty);
            bool known = tree.TryGetValue(pid, out ProcessTreeEntry entry);
            int? parentPid = known && entry.ParentPid != 0 ? entry.ParentPid : null;

            ProcessOwner owner = _info.GetOwner(pid);
            bool hasWindow = windows.TryGetValue(pid, out WindowOwners.WindowState window);

            samples.Add(new ProcessSample(
                pid,
                parentPid,
                name,
                _info.GetDescription(pid),
                cpuPercent,
                workingSet.GetValueOrDefault(cpu.Instance),
                privateBytes.GetValueOrDefault(cpu.Instance),
                gpu?.TotalPercent ?? 0,
                gpu?.ByEngineType ?? NoEngines,
                gpu?.MemBytes ?? 0,
                _services.ForPid(pid),
                network.ReceivedBytesPerSec,
                network.SentBytesPerSec,
                ioRead.GetValueOrDefault(cpu.Instance),
                ioWrite.GetValueOrDefault(cpu.Instance),
                _info.GetPath(pid),
                known ? entry.ThreadCount : 0)
            {
                UserName = owner.Account,
                Category = Categorize(owner.Kind, hasWindow),
                WindowTitle = hasWindow ? window.Title : null,
                NotResponding = hasWindow && window.Hung,
                FaultNote = _faults?.ForName(name)?.Summary,
                ListeningTcpPorts = ports.ListeningTcp,
                ListeningUdpPorts = ports.ListeningUdp,
                ConnectionCount = ports.EstablishedCount,
            });
        }

        return samples;
    }

    /// <summary>
    /// Die Einteilung des Task-Managers: Systemkonten sind Windows-Prozesse, ein
    /// eigenes Fenster macht eine App daraus, alles Übrige läuft im Hintergrund.
    /// Ein Prozess, dessen Token sich nicht öffnen lässt, ist geschützt und
    /// gehört damit ebenfalls zu Windows.
    /// </summary>
    private static ProcessCategory Categorize(AccountKind kind, bool hasWindow) => kind switch
    {
        AccountKind.User when hasWindow => ProcessCategory.App,
        AccountKind.User => ProcessCategory.Background,
        _ => ProcessCategory.Windows,
    };

    /// <summary>Wie <see cref="ReadByInstance"/>, aber für ratenbasierte Zähler.</summary>
    private static Dictionary<string, double> ReadRateByInstance(PdhCounter? counter)
    {
        var result = new Dictionary<string, double>(StringComparer.Ordinal);
        if (counter is null)
            return result;

        foreach (PdhInstanceValue entry in counter.ReadArrayDouble(noCap100: true))
            result[entry.Instance] = Math.Max(0, entry.Value);
        return result;
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
    /// Pfad, Dateibeschreibung und Konto sind teuer zu ermitteln (Handle öffnen,
    /// Versionsressource lesen, SID auflösen) und ändern sich zu Lebzeiten eines
    /// Prozesses nicht — deshalb pro PID einmalig ermitteln und halten.
    /// </summary>
    private sealed class ProcessInfoCache
    {
        private readonly Dictionary<int, Entry> _byPid = [];
        private readonly Dictionary<string, string?> _descriptionByPath = new(StringComparer.OrdinalIgnoreCase);

        private readonly record struct Entry(string? Path, string? Description, ProcessOwner Owner);

        public string? GetDescription(int pid) => Resolve(pid).Description;

        public string? GetPath(int pid) => Resolve(pid).Path;

        public ProcessOwner GetOwner(int pid) => Resolve(pid).Owner;

        private Entry Resolve(int pid)
        {
            if (_byPid.TryGetValue(pid, out Entry cached))
                return cached;

            // Ein Handle für beides: Pfad und Token hängen am selben Prozess, und
            // OpenProcess ist der teure Teil.
            IntPtr handle = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
            string? path = null;
            ProcessOwner owner = ProcessOwner.Unknown;
            try
            {
                if (handle != IntPtr.Zero)
                {
                    path = TryGetImagePath(handle);
                    owner = ProcessIdentity.Read(handle);
                }
            }
            finally
            {
                if (handle != IntPtr.Zero)
                    CloseHandle(handle);
            }

            string? description = null;
            if (path is not null && !_descriptionByPath.TryGetValue(path, out description))
            {
                try
                {
                    description = FileVersionInfo.GetVersionInfo(path).FileDescription;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    description = null;
                }

                _descriptionByPath[path] = description;
            }

            var entry = new Entry(path, description, owner);
            _byPid[pid] = entry;
            return entry;
        }

        /// <summary>Entfernt Einträge beendeter Prozesse, damit der Cache nicht unbegrenzt wächst.</summary>
        public void Prune(IReadOnlySet<int> live)
        {
            if (live.Count == 0)
                return;

            foreach (int pid in _byPid.Keys.Where(pid => !live.Contains(pid)).ToList())
                _byPid.Remove(pid);
        }

        private static string? TryGetImagePath(IntPtr handle)
        {
            var buffer = new StringBuilder(1024);
            int size = buffer.Capacity;
            return QueryFullProcessImageNameW(handle, 0, buffer, ref size) ? buffer.ToString() : null;
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

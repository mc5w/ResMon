using ResMon.Core.Native;

namespace ResMon.Core.Sensors;

/// <summary>GPU-Last eines einzelnen Prozesses, aufgeschlüsselt nach Engine-Typ.</summary>
public sealed record GpuProcessUsage(
    double TotalPercent,
    IReadOnlyDictionary<string, double> ByEngineType,
    long MemBytes);

/// <summary>Ergebnis eines GPU-Takts.</summary>
public sealed record GpuEngineReading(
    double TotalPercent,
    IReadOnlyDictionary<string, double> ByEngineType,
    IReadOnlyDictionary<int, GpuProcessUsage> ByProcess);

/// <summary>
/// <c>\GPU Engine</c> und <c>\GPU Process Memory</c> aus PDH. Die Instanznamen
/// tragen PID und Engine-Typ, siehe DESIGN.md §8.3.
/// </summary>
public sealed class GpuEngineSource
{
    private const string EngineUtilization = @"\GPU Engine(*)\Utilization Percentage";
    private const string ProcessLocalUsage = @"\GPU Process Memory(*)\Local Usage";

    private static readonly GpuEngineReading Empty =
        new(0, new Dictionary<string, double>(), new Dictionary<int, GpuProcessUsage>());

    private readonly PdhCounter? _engine;
    private readonly PdhCounter? _processMemory;

    public GpuEngineSource(PdhQuery query)
    {
        _engine = query.TryAddCounter(EngineUtilization);
        _processMemory = query.TryAddCounter(ProcessLocalUsage);
    }

    /// <summary>False, wenn der Zählersatz auf diesem System fehlt (bestimmte Treiberkonstellationen).</summary>
    public bool Available => _engine is not null;

    public GpuEngineReading Read()
    {
        if (_engine is null)
            return Empty;

        var byEngineType = new Dictionary<string, double>(StringComparer.Ordinal);
        var perProcessEngines = new Dictionary<int, Dictionary<string, double>>();

        foreach (PdhInstanceValue sample in _engine.ReadArrayDouble())
        {
            if (sample.Value <= 0 || !TryParseEngineInstance(sample.Instance, out int pid, out string engineType))
                continue;

            byEngineType[engineType] = byEngineType.GetValueOrDefault(engineType) + sample.Value;

            if (!perProcessEngines.TryGetValue(pid, out Dictionary<string, double>? engines))
                perProcessEngines[pid] = engines = new Dictionary<string, double>(StringComparer.Ordinal);
            engines[engineType] = engines.GetValueOrDefault(engineType) + sample.Value;
        }

        var memoryByPid = new Dictionary<int, long>();
        if (_processMemory is not null)
        {
            foreach (PdhInstanceValueL sample in _processMemory.ReadArrayInt64())
            {
                if (sample.Value <= 0 || !TryParsePid(sample.Instance, out int pid))
                    continue;
                memoryByPid[pid] = memoryByPid.GetValueOrDefault(pid) + sample.Value;
            }
        }

        var byProcess = new Dictionary<int, GpuProcessUsage>(perProcessEngines.Count + memoryByPid.Count);
        foreach ((int pid, Dictionary<string, double> engines) in perProcessEngines)
        {
            var clamped = engines.ToDictionary(e => e.Key, e => Math.Clamp(e.Value, 0, 100), StringComparer.Ordinal);
            byProcess[pid] = new GpuProcessUsage(MaxOverEngines(clamped), clamped, memoryByPid.GetValueOrDefault(pid));
        }

        // Prozesse, die VRAM halten, aber gerade keine Engine belasten.
        foreach ((int pid, long bytes) in memoryByPid)
        {
            if (!byProcess.ContainsKey(pid))
                byProcess[pid] = new GpuProcessUsage(0, new Dictionary<string, double>(StringComparer.Ordinal), bytes);
        }

        var totalByEngine = byEngineType.ToDictionary(e => e.Key, e => Math.Clamp(e.Value, 0, 100), StringComparer.Ordinal);
        return new GpuEngineReading(MaxOverEngines(totalByEngine), totalByEngine, byProcess);
    }

    /// <summary>
    /// Gesamtlast ist das Maximum über die Engine-Typen, nicht deren Summe —
    /// sonst zeigt die Anzeige bei Videowiedergabe Werte über 100 % (DESIGN.md §8.3).
    /// </summary>
    private static double MaxOverEngines(IReadOnlyDictionary<string, double> byEngineType)
        => byEngineType.Count == 0 ? 0 : byEngineType.Values.Max();

    /// <summary>
    /// Zerlegt <c>pid_1234_luid_0x0_0xD3F5_phys_0_eng_0_engtype_3D</c> in PID und Engine-Typ.
    /// </summary>
    public static bool TryParseEngineInstance(string instance, out int pid, out string engineType)
    {
        engineType = string.Empty;
        if (!TryParsePid(instance, out pid))
            return false;

        const string marker = "_engtype_";
        int index = instance.IndexOf(marker, StringComparison.Ordinal);
        if (index < 0)
            return false;

        engineType = instance[(index + marker.Length)..];
        return engineType.Length > 0;
    }

    /// <summary>Liest den <c>pid_&lt;n&gt;</c>-Präfix eines GPU-Instanznamens.</summary>
    public static bool TryParsePid(string instance, out int pid)
    {
        pid = 0;
        const string marker = "pid_";
        if (!instance.StartsWith(marker, StringComparison.Ordinal))
            return false;

        int start = marker.Length;
        int end = instance.IndexOf('_', start);
        ReadOnlySpan<char> digits = end < 0 ? instance.AsSpan(start) : instance.AsSpan(start, end - start);
        return int.TryParse(digits, out pid);
    }
}

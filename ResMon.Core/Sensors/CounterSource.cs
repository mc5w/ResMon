using Microsoft.Win32;
using ResMon.Core.Native;

namespace ResMon.Core.Sensors;

/// <summary>Ergebnis eines Aggregat-Takts aus PDH plus <c>GlobalMemoryStatusEx</c>.</summary>
public sealed record CounterReading(
    double CpuTotalPercent,
    double[] CpuPerCorePercent,
    long MemoryUsedBytes,
    long MemoryTotalBytes,
    long CommittedBytes,
    double MemoryPercent)
{
    /// <summary>
    /// Der aus Basistakt und <c>% Processor Performance</c> gerechnete Takt, oder
    /// <c>null</c>, wenn eines von beidem fehlt. Nur als Ersatz gedacht: der
    /// Sensortreiber liest den Takt direkt aus dem Prozessor.
    /// </summary>
    public double? ClockMhz { get; init; }
}

/// <summary>
/// CPU- und RAM-Aggregate aus PDH (DESIGN.md §8.2). Die Zähler hängen an einer
/// von außen bereitgestellten Abfrage, damit alle Aggregatwerte im selben
/// <c>PdhCollectQueryData</c>-Takt konsistent gelesen werden.
/// </summary>
public sealed class CounterSource
{
    private const string CpuTotalUtility = @"\Processor Information(_Total)\% Processor Utility";
    private const string CpuTotalTime = @"\Processor Information(_Total)\% Processor Time";
    private const string CpuPerCoreUtility = @"\Processor Information(*)\% Processor Utility";
    private const string CpuPerCoreTime = @"\Processor Information(*)\% Processor Time";
    private const string CpuPerformance = @"\Processor Information(_Total)\% Processor Performance";
    private const string CommittedBytes = @"\Memory\Committed Bytes";

    /// <summary>
    /// Obergrenze für den Turbo-Faktor. Kein Prozessor legt das Vierfache seines
    /// Basistakts auf; ein solcher Wert wäre ein Zählerfehler.
    /// </summary>
    private const double MaximumPerformancePercent = 400;

    private readonly PdhCounter? _cpuTotal;
    private readonly PdhCounter? _cpuPerCore;
    private readonly PdhCounter? _cpuPerformance;
    private readonly PdhCounter? _committed;
    private readonly double _baseClockMhz = ReadBaseClockMhz();

    public CounterSource(PdhQuery query)
    {
        // "% Processor Utility" fehlt auf manchen Systemen — dann auf die ältere
        // Metrik zurückfallen (DESIGN.md §16).
        _cpuTotal = query.TryAddCounter(CpuTotalUtility);
        if (_cpuTotal is null)
        {
            _cpuTotal = query.TryAddCounter(CpuTotalTime);
            UsesUtilityFallback = true;
        }

        _cpuPerCore = query.TryAddCounter(CpuPerCoreUtility) ?? query.TryAddCounter(CpuPerCoreTime);
        _cpuPerformance = query.TryAddCounter(CpuPerformance);
        _committed = query.TryAddCounter(CommittedBytes);
    }

    /// <summary>True, wenn statt <c>% Processor Utility</c> nur <c>% Processor Time</c> verfügbar war.</summary>
    public bool UsesUtilityFallback { get; }

    /// <summary>
    /// True, wenn sich der Takt ohne Sensortreiber schätzen lässt — Basistakt und
    /// Leistungszähler sind beide vorhanden.
    /// </summary>
    public bool ClockEstimateAvailable => _cpuPerformance is not null && _baseClockMhz > 0;

    public CounterReading Read()
    {
        double cpuTotal = 0;
        if (_cpuTotal?.TryGetDouble(out double raw) == true)
            cpuTotal = Math.Clamp(raw, 0, 100);

        double[] perCore = ReadPerCore();

        long committed = 0;
        _committed?.TryGetInt64(out committed);

        PhysicalMemoryStatus mem = SystemMemory.Read();

        return new CounterReading(
            cpuTotal,
            perCore,
            mem.UsedBytes,
            mem.TotalBytes,
            committed,
            Math.Clamp(mem.UsedPercent, 0, 100))
        {
            ClockMhz = ReadClock(),
        };
    }

    /// <summary>
    /// Der Takt ohne Sensortreiber: <c>% Processor Performance</c> ist das
    /// Verhältnis zum Basistakt, nicht selbst eine Frequenz — erst beide zusammen
    /// ergeben MHz. Denselben Weg geht der Task-Manager, und wie dort darf der
    /// Wert über 100 % liegen, sonst wäre der Turbo unsichtbar.
    /// </summary>
    private double? ReadClock()
    {
        if (_baseClockMhz <= 0 || _cpuPerformance is null)
            return null;

        if (!_cpuPerformance.TryGetDouble(out double percent, noCap100: true))
            return null;

        return percent is > 0 and <= MaximumPerformancePercent ? _baseClockMhz * percent / 100.0 : null;
    }

    /// <summary>
    /// Der Basistakt, wie ihn der Kernel beim Start hinterlegt — dieselbe Zahl,
    /// die die Systemübersicht als „Basistakt" zeigt.
    /// </summary>
    private static double ReadBaseClockMhz()
    {
        try
        {
            using RegistryKey? key = Registry.LocalMachine.OpenSubKey(
                @"HARDWARE\DESCRIPTION\System\CentralProcessor\0");
            return key?.GetValue("~MHz") is int mhz && mhz > 0 ? mhz : 0;
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException)
        {
            // Ohne Basistakt entfällt nur die Schätzung; alles andere läuft weiter.
            return 0;
        }
    }

    /// <summary>
    /// Liest die Kerninstanzen und bringt sie in Hardwarereihenfolge. Instanznamen
    /// haben die Form <c>"&lt;Gruppe&gt;,&lt;Kern&gt;"</c>; Summenzeilen wie
    /// <c>_Total</c> und <c>0,_Total</c> fallen heraus.
    /// </summary>
    private double[] ReadPerCore()
    {
        if (_cpuPerCore is null)
            return [];

        IReadOnlyList<PdhInstanceValue> values = _cpuPerCore.ReadArrayDouble();
        if (values.Count == 0)
            return [];

        return values
            .Where(v => !v.Instance.Contains("_Total", StringComparison.OrdinalIgnoreCase))
            .OrderBy(v => SortKey(v.Instance))
            .Select(v => Math.Clamp(v.Value, 0, 100))
            .ToArray();
    }

    private static (int Group, int Core) SortKey(string instance)
    {
        int comma = instance.IndexOf(',');
        if (comma < 0)
            return (0, int.TryParse(instance, out int only) ? only : int.MaxValue);

        _ = int.TryParse(instance.AsSpan(0, comma), out int group);
        _ = int.TryParse(instance.AsSpan(comma + 1), out int core);
        return (group, core);
    }
}

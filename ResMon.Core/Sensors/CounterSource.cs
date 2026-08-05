using ResMon.Core.Native;

namespace ResMon.Core.Sensors;

/// <summary>Ergebnis eines Aggregat-Takts aus PDH plus <c>GlobalMemoryStatusEx</c>.</summary>
public sealed record CounterReading(
    double CpuTotalPercent,
    double[] CpuPerCorePercent,
    long MemoryUsedBytes,
    long MemoryTotalBytes,
    long CommittedBytes,
    double MemoryPercent);

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
    private const string CommittedBytes = @"\Memory\Committed Bytes";

    private readonly PdhCounter? _cpuTotal;
    private readonly PdhCounter? _cpuPerCore;
    private readonly PdhCounter? _committed;

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
        _committed = query.TryAddCounter(CommittedBytes);
    }

    /// <summary>True, wenn statt <c>% Processor Utility</c> nur <c>% Processor Time</c> verfügbar war.</summary>
    public bool UsesUtilityFallback { get; }

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
            Math.Clamp(mem.UsedPercent, 0, 100));
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

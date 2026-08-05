namespace ResMon.Core.Model;

/// <summary>Ein vollständiger Messpunkt, wie ihn der Collector veröffentlicht.</summary>
public sealed record SystemSnapshot(
    DateTime Timestamp,
    CpuMetrics Cpu,
    GpuMetrics Gpu,
    MemoryMetrics Memory,
    NetworkMetrics Network,
    IReadOnlyList<ProcessSample> Processes);

public sealed record CpuMetrics(
    double TotalPercent,
    double[] PerCorePercent,
    double? PackageTempC,
    double? ClockMhz,
    double? PackagePowerW);

public sealed record GpuMetrics(
    double TotalPercent,
    IReadOnlyDictionary<string, double> ByEngineType,
    double? TempC,
    long MemUsedBytes,
    long MemTotalBytes,
    double? FanRpm,
    double? PowerW)
{
    /// <summary>
    /// False, wenn weder <c>\GPU Engine</c> noch ein GPU-Sensor Daten liefert —
    /// das UI graut die GPU-Zeile dann aus, statt Nullen anzuzeigen.
    /// </summary>
    public bool Available { get; init; } = true;
}

public sealed record MemoryMetrics(
    long UsedBytes,
    long TotalBytes,
    long CommittedBytes,
    double Percent);

/// <summary>Netzdurchsatz über alle physischen Adapter, aus PDH.</summary>
public sealed record NetworkMetrics(
    double ReceivedBytesPerSec,
    double SentBytesPerSec,
    bool Available)
{
    public static readonly NetworkMetrics Empty = new(0, 0, false);
}

/// <summary>Verdichteter Eintrag für den Ringpuffer (DESIGN.md §10).</summary>
public readonly record struct AggregateSample(
    DateTime Timestamp,
    double CpuPercent,
    double GpuPercent,
    double MemoryPercent,
    double? CpuTempC,
    double? GpuTempC,
    double NetReceivedBytesPerSec,
    double NetSentBytesPerSec);

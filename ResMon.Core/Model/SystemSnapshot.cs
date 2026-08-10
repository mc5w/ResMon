using ResMon.Core.Processes;

namespace ResMon.Core.Model;

/// <summary>Ein vollständiger Messpunkt, wie ihn der Collector veröffentlicht.</summary>
public sealed record SystemSnapshot(
    DateTime Timestamp,
    CpuMetrics Cpu,
    GpuMetrics Gpu,
    MemoryMetrics Memory,
    NetworkMetrics Network,
    DiskMetrics Disk,
    IReadOnlyList<ProcessSample> Processes)
{
    /// <summary>
    /// Prozesse und Threads insgesamt, aus dem Toolhelp-Snapshot. Beide sind 0,
    /// solange die Prozesserfassung ruht (Detailfenster geschlossen).
    /// </summary>
    public int ProcessCount { get; init; }

    public int ThreadCount { get; init; }

    /// <summary>Leistungsaufnahme, Lüfter und Akku für den Reiter „Energie".</summary>
    public EnergyMetrics Energy { get; init; } = EnergyMetrics.Empty;

    /// <summary>
    /// Offene TCP- und UDP-Verbindungen. Wie die Prozessliste nur gefüllt,
    /// solange das Detailfenster offen ist.
    /// </summary>
    public IReadOnlyList<NetConnection> Connections { get; init; } = [];
}

public sealed record CpuMetrics(
    double TotalPercent,
    double[] PerCorePercent,
    double? PackageTempC,
    double? ClockMhz,
    double? PackagePowerW)
{
    /// <summary>
    /// Die Temperatur am Sockel, gemessen vom Super-I/O-Chip des Mainboards.
    /// Sie liegt unter der Paket-Temperatur aus dem Prozessor selbst und
    /// reagiert träger — sie misst das Gehäuse, nicht den Die. Auf gesperrtem
    /// Sensortreiber ist sie oft der einzige verfügbare CPU-Temperaturwert.
    /// </summary>
    public double? SocketTempC { get; init; }
}

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

/// <summary>Durchsatz über alle physischen Datenträger, aus PDH.</summary>
public sealed record DiskMetrics(
    double ReadBytesPerSec,
    double WriteBytesPerSec,
    double BusyPercent,
    bool Available)
{
    public static readonly DiskMetrics Empty = new(0, 0, 0, false);
}

/// <summary>Ein einzelner Lüfter mit Drehzahl und, falls vorhanden, Ansteuerung.</summary>
public sealed record FanSample(string Hardware, string Name, double? Rpm, double? Percent);

/// <summary>
/// Woher eine Temperatur kommt. Der Unterschied zählt: die CPU misst am Die, das
/// Mainboard am Sockel, und beide Werte tragen denselben Namen „CPU".
/// </summary>
public enum TemperatureSource
{
    Other,
    Cpu,
    Gpu,
    Board,
}

/// <summary>Ein Temperatursensor, gleich welcher Hardware er gehört.</summary>
public sealed record TemperatureSample(string Hardware, string Name, double Celsius, TemperatureSource Source);

/// <summary>
/// Ein Leistungssensor. Welche es gibt, hängt an der Hardware: bei Intel etwa
/// „CPU Package" und „CPU Cores", bei NVIDIA „GPU Power".
/// </summary>
public sealed record PowerRail(string Hardware, string Name, double Watts);

/// <summary>
/// Akkuzustand. Auf Desktop-Rechnern <c>null</c>. Die Kapazitäten sind in
/// Wattstunden umgerechnet, damit sie mit der Lade- und Entladeleistung
/// zusammenpassen.
/// </summary>
public sealed record BatteryMetrics(
    double? ChargePercent,
    bool OnAcPower,
    bool Charging,
    double? RateW,
    double? VoltageV,
    double? DesignedCapacityWh,
    double? FullChargedCapacityWh,
    double? RemainingCapacityWh,
    double? DegradationPercent,
    TimeSpan? Remaining);

/// <summary>
/// Alles, was der Reiter „Energie" zeigt: Leistungsaufnahme nach Sensor,
/// Lüfterdrehzahlen und der Akku.
/// </summary>
public sealed record EnergyMetrics(
    double? CpuPackagePowerW,
    double? GpuPowerW,
    IReadOnlyList<PowerRail> Rails,
    IReadOnlyList<FanSample> Fans,
    BatteryMetrics? Battery)
{
    public static readonly EnergyMetrics Empty = new(null, null, [], [], null);

    /// <summary>Alle Temperatursensoren, quer über CPU, Grafikkarte und Mainboard.</summary>
    public IReadOnlyList<TemperatureSample> Temperatures { get; init; } = [];

    /// <summary>
    /// Die messbare Leistungsaufnahme: CPU-Paket plus GPU. Das ist nicht die
    /// Aufnahme des ganzen Rechners — Mainboard, Datenträger, Lüfter und
    /// Netzteilverluste sind darin nicht enthalten.
    /// </summary>
    public double? MeasuredW => CpuPackagePowerW is null && GpuPowerW is null
        ? null
        : (CpuPackagePowerW ?? 0) + (GpuPowerW ?? 0);
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
    double NetSentBytesPerSec,
    double DiskReadBytesPerSec,
    double DiskWriteBytesPerSec)
{
    /// <summary>Leistungsaufnahme für den Verlauf im Reiter „Energie".</summary>
    public double CpuPowerW { get; init; }

    public double GpuPowerW { get; init; }
}

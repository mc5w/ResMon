namespace ResMon.Core.Inventory;

/// <summary>
/// Ein Schlüssel-Wert-Paar für die Systemübersicht. <paramref name="Help"/> wird
/// als Tooltip angezeigt, wenn der Wert ohne Erklärung in die Irre führen kann.
/// </summary>
public readonly record struct InfoItem(string Label, string Value, string? Help = null);

/// <summary>Eine benannte Gruppe von Angaben, etwa "Betriebssystem" oder "Prozessor".</summary>
public sealed record InfoGroup(string Title, IReadOnlyList<InfoItem> Items);

/// <summary>Ein logisches Laufwerk mit Kapazität.</summary>
public sealed record VolumeInfo(
    string Name,
    string? Label,
    string FileSystem,
    long TotalBytes,
    long FreeBytes)
{
    public long UsedBytes => TotalBytes - FreeBytes;

    public double UsedPercent => TotalBytes > 0 ? UsedBytes * 100.0 / TotalBytes : 0;
}

/// <summary>Ein physischer Datenträger samt der darauf liegenden Laufwerke.</summary>
public sealed record PhysicalDriveInfo(
    string Model,
    string? InterfaceType,
    string? MediaType,
    long SizeBytes,
    IReadOnlyList<VolumeInfo> Volumes);

/// <summary>Wie es um ein Gerät steht — bestimmt den Farbpunkt in der Übersicht.</summary>
public enum DeviceHealth
{
    /// <summary>Vorhanden und in Betrieb.</summary>
    Active,

    /// <summary>Vorhanden, aber gerade ohne Verbindung oder abgeschaltet.</summary>
    Idle,

    /// <summary>Windows meldet ein Problem mit dem Gerät.</summary>
    Problem,
}

/// <summary>Ein Gerät oder eine Verbindung in der Geräteübersicht.</summary>
public sealed record DeviceEntry(
    string Name,
    IReadOnlyList<InfoItem> Details,
    string Status,
    DeviceHealth Health);

/// <summary>Eine Kategorie der Geräteübersicht, etwa „Netzwerk" oder „USB-Geräte".</summary>
public sealed record DeviceGroup(string Title, string? Hint, IReadOnlyList<DeviceEntry> Items);

/// <summary>
/// Statische Angaben über den Rechner. Wird einmalig erhoben und auf Wunsch
/// erneuert — Geräte kommen und gehen, der Rest ändert sich zur Laufzeit nicht.
/// </summary>
public sealed record SystemInfo(
    IReadOnlyList<InfoGroup> Groups,
    IReadOnlyList<PhysicalDriveInfo> Drives,
    DateTime BootTime)
{
    public static readonly SystemInfo Empty = new([], [], DateTime.MinValue);

    /// <summary>Netzwerk, Funk und angeschlossene Geräte.</summary>
    public IReadOnlyList<DeviceGroup> Devices { get; init; } = [];

    /// <summary>Zeitpunkt der Erhebung, damit die Oberfläche sagen kann, wie frisch sie ist.</summary>
    public DateTime CollectedAt { get; init; } = DateTime.Now;
}

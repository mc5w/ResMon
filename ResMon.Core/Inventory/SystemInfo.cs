namespace ResMon.Core.Inventory;

/// <summary>Ein Schlüssel-Wert-Paar für die Systemübersicht.</summary>
public readonly record struct InfoItem(string Label, string Value);

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

/// <summary>
/// Statische Angaben über den Rechner. Wird einmalig erhoben — außer den freien
/// Laufwerkskapazitäten ändert sich davon zur Laufzeit nichts.
/// </summary>
public sealed record SystemInfo(
    IReadOnlyList<InfoGroup> Groups,
    IReadOnlyList<PhysicalDriveInfo> Drives,
    DateTime BootTime)
{
    public static readonly SystemInfo Empty = new([], [], DateTime.MinValue);
}

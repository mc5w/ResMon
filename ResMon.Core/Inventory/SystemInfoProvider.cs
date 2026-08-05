using System.Globalization;
using System.Management;
using System.Runtime.InteropServices;
using Microsoft.Win32;
using ResMon.Core.Native;

namespace ResMon.Core.Inventory;

/// <summary>
/// Sammelt die statischen Angaben über den Rechner. Die WMI-Abfragen brauchen
/// mehrere hundert Millisekunden und laufen deshalb genau einmal im Hintergrund.
/// </summary>
public static class SystemInfoProvider
{
    public static SystemInfo Collect()
    {
        DateTime bootTime = DateTime.Now - TimeSpan.FromMilliseconds(Environment.TickCount64);

        var groups = new List<InfoGroup>
        {
            new("Betriebssystem", OperatingSystemItems(bootTime)),
            new("Prozessor", ProcessorItems()),
            new("Grafik", GraphicsItems()),
            new("Arbeitsspeicher", MemoryItems()),
            new("Mainboard und BIOS", BoardItems()),
        };

        return new SystemInfo(groups.Where(g => g.Items.Count > 0).ToList(), Drives(), bootTime);
    }

    private static List<InfoItem> OperatingSystemItems(DateTime bootTime)
    {
        var items = new List<InfoItem>();

        // Die Registry nennt den Marketing-Namen; Environment.OSVersion meldet
        // auf Windows 11 weiterhin 10.0.
        using RegistryKey? key = Registry.LocalMachine.OpenSubKey(
            @"SOFTWARE\Microsoft\Windows NT\CurrentVersion");

        string? product = key?.GetValue("ProductName") as string;
        string? display = key?.GetValue("DisplayVersion") as string;
        string? build = key?.GetValue("CurrentBuild") as string;
        int? update = key?.GetValue("UBR") as int?;

        // Ab Build 22000 ist es Windows 11, die Registry sagt aber weiter "10".
        if (product is not null && int.TryParse(build, out int buildNumber) && buildNumber >= 22000)
            product = product.Replace("Windows 10", "Windows 11", StringComparison.Ordinal);

        Add(items, "Version", product);
        Add(items, "Ausgabe", display);
        Add(items, "Build", build is null ? null : update is null ? build : $"{build}.{update}");
        Add(items, "Architektur", RuntimeInformation.OSArchitecture.ToString());
        Add(items, "Rechnername", Environment.MachineName);
        Add(items, "Benutzer", $@"{Environment.UserDomainName}\{Environment.UserName}");
        Add(items, "Gestartet", bootTime.ToString("dddd, d. MMMM yyyy, HH:mm", CultureInfo.CurrentCulture));
        Add(items, "Laufzeit", FormatUptime(TimeSpan.FromMilliseconds(Environment.TickCount64)));
        return items;
    }

    private static List<InfoItem> ProcessorItems()
    {
        var items = new List<InfoItem>();

        using RegistryKey? key = Registry.LocalMachine.OpenSubKey(
            @"HARDWARE\DESCRIPTION\System\CentralProcessor\0");
        Add(items, "Modell", key?.GetValue("ProcessorNameString") as string);

        foreach (ManagementBaseObject cpu in Query("SELECT * FROM Win32_Processor"))
        {
            using (cpu)
            {
                Add(items, "Kerne", Text(cpu, "NumberOfCores"));
                Add(items, "Logische Prozessoren", Text(cpu, "NumberOfLogicalProcessors"));
                Add(items, "Basistakt", Number(cpu, "MaxClockSpeed") is { } mhz ? $"{mhz:N0} MHz" : null);
                Add(items, "Sockel", Text(cpu, "SocketDesignation"));
                Add(items, "L2-Cache", Number(cpu, "L2CacheSize") is { } l2 ? $"{l2 / 1024.0:N1} MB" : null);
                Add(items, "L3-Cache", Number(cpu, "L3CacheSize") is { } l3 ? $"{l3 / 1024.0:N1} MB" : null);
                Add(items, "Virtualisierung", cpu["VirtualizationFirmwareEnabled"] is bool enabled
                    ? enabled ? "aktiviert" : "deaktiviert"
                    : null);
                break;
            }
        }

        if (!items.Any(item => item.Label == "Logische Prozessoren"))
            Add(items, "Logische Prozessoren", Environment.ProcessorCount.ToString());

        return items;
    }

    private static List<InfoItem> GraphicsItems()
    {
        var items = new List<InfoItem>();
        int index = 0;

        foreach (ManagementBaseObject gpu in Query("SELECT * FROM Win32_VideoController"))
        {
            using (gpu)
            {
                string? name = Text(gpu, "Name");
                if (name is null)
                    continue;

                string suffix = index == 0 ? string.Empty : $" ({index + 1})";
                Add(items, "Modell" + suffix, name);
                Add(items, "Treiber" + suffix, Text(gpu, "DriverVersion"));

                // AdapterRAM ist ein 32-Bit-Feld: bei mehr als 4 GB VRAM meldet
                // WMI den gedeckelten Wert 4293918720 (eine RTX 4070 mit 12 GB
                // erscheint dort als "4 GB"). Alles nahe der Grenze ist deshalb
                // nicht vertrauenswürdig — der echte Wert steht in der GPU-Kachel
                // und kommt von NVAPI.
                if (Number(gpu, "AdapterRAM") is { } ram && ram is > 0 and < 4_000_000_000)
                    Add(items, "Speicher" + suffix, $"{ram / 1073741824.0:N1} GB");

                if (Number(gpu, "CurrentHorizontalResolution") is { } width &&
                    Number(gpu, "CurrentVerticalResolution") is { } height)
                {
                    string rate = Number(gpu, "CurrentRefreshRate") is { } hz ? $" @ {hz} Hz" : string.Empty;
                    Add(items, "Auflösung" + suffix, $"{width} × {height}{rate}");
                }

                index++;
            }
        }

        return items;
    }

    private static List<InfoItem> MemoryItems()
    {
        var items = new List<InfoItem>();
        PhysicalMemoryStatus status = SystemMemory.Read();

        Add(items, "Gesamt", $"{status.TotalBytes / 1073741824.0:N1} GB");
        Add(items, "Belegt", $"{status.UsedBytes / 1073741824.0:N1} GB ({status.UsedPercent:N0} %)");

        int slots = 0;
        long moduleTotal = 0;
        foreach (ManagementBaseObject module in Query("SELECT * FROM Win32_PhysicalMemory"))
        {
            using (module)
            {
                long capacity = (long)(Number(module, "Capacity") ?? 0);
                moduleTotal += capacity;
                slots++;

                string? speed = Number(module, "ConfiguredClockSpeed") is { } configured and > 0
                    ? $"{configured} MT/s"
                    : Number(module, "Speed") is { } rated ? $"{rated} MT/s" : null;

                var parts = new List<string> { $"{capacity / 1073741824.0:N0} GB" };
                if (speed is not null)
                    parts.Add(speed);
                if (Text(module, "Manufacturer") is { Length: > 0 } maker)
                    parts.Add(maker.Trim());
                if (Text(module, "PartNumber") is { Length: > 0 } part)
                    parts.Add(part.Trim());

                // Manche Boards vergeben denselben DeviceLocator mehrfach und
                // unterscheiden die Bänke nur über BankLabel.
                string locator = Text(module, "DeviceLocator")?.Trim() ?? $"Riegel {slots}";
                if (Text(module, "BankLabel") is { Length: > 0 } bank && !locator.Contains(bank.Trim(), StringComparison.OrdinalIgnoreCase))
                    locator = $"{bank.Trim()} / {locator}";

                Add(items, locator, string.Join("  ·  ", parts));
            }
        }

        if (moduleTotal > 0)
            Add(items, "Bestückt", $"{moduleTotal / 1073741824.0:N0} GB in {slots} Modulen");

        return items;
    }

    private static List<InfoItem> BoardItems()
    {
        var items = new List<InfoItem>();

        foreach (ManagementBaseObject board in Query("SELECT * FROM Win32_BaseBoard"))
        {
            using (board)
            {
                Add(items, "Hersteller", Text(board, "Manufacturer"));
                Add(items, "Modell", Text(board, "Product"));
                break;
            }
        }

        foreach (ManagementBaseObject bios in Query("SELECT * FROM Win32_BIOS"))
        {
            using (bios)
            {
                Add(items, "BIOS", Text(bios, "SMBIOSBIOSVersion"));

                // WMI liefert das Datum als yyyyMMdd mit angehängter Zeitzone.
                if (Text(bios, "ReleaseDate") is { Length: >= 8 } raw)
                    Add(items, "BIOS-Datum", $"{raw[6..8]}.{raw[4..6]}.{raw[0..4]}");
                break;
            }
        }

        return items;
    }

    /// <summary>
    /// Physische Datenträger mit ihren Laufwerken. Die Zuordnung läuft über die
    /// WMI-Assoziationen Datenträger → Partition → logisches Laufwerk.
    /// </summary>
    private static List<PhysicalDriveInfo> Drives()
    {
        var volumesByLetter = new Dictionary<string, VolumeInfo>(StringComparer.OrdinalIgnoreCase);
        foreach (DriveInfo drive in DriveInfo.GetDrives())
        {
            try
            {
                if (!drive.IsReady)
                    continue;

                string letter = drive.Name.TrimEnd('\\');
                volumesByLetter[letter] = new VolumeInfo(
                    letter,
                    string.IsNullOrWhiteSpace(drive.VolumeLabel) ? null : drive.VolumeLabel,
                    drive.DriveFormat,
                    drive.TotalSize,
                    drive.AvailableFreeSpace);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
            }
        }

        Dictionary<uint, List<string>> lettersByDisk = LettersByDiskIndex();

        var result = new List<PhysicalDriveInfo>();
        var assigned = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (ManagementBaseObject disk in Query("SELECT * FROM Win32_DiskDrive"))
        {
            using (disk)
            {
                if (Number(disk, "Index") is not { } index)
                    continue;

                var volumes = new List<VolumeInfo>();
                foreach (string letter in lettersByDisk.GetValueOrDefault((uint)index, []))
                {
                    if (volumesByLetter.TryGetValue(letter, out VolumeInfo? volume) && assigned.Add(letter))
                        volumes.Add(volume);
                }

                result.Add(new PhysicalDriveInfo(
                    Text(disk, "Model") ?? "Unbekannter Datenträger",
                    Text(disk, "InterfaceType"),
                    Text(disk, "MediaType"),
                    (long)(Number(disk, "Size") ?? 0),
                    volumes));
            }
        }

        // Laufwerke ohne zuordenbaren physischen Datenträger (Netz, virtuell).
        var orphans = volumesByLetter.Values.Where(volume => !assigned.Contains(volume.Name)).ToList();
        if (orphans.Count > 0)
            result.Add(new PhysicalDriveInfo("Weitere Laufwerke", null, null, 0, orphans));

        return result;
    }

    /// <summary>
    /// Ordnet Laufwerksbuchstaben ihrem physischen Datenträger zu, über die
    /// Assoziationsklasse statt über <c>ASSOCIATORS OF</c>: die Referenzform
    /// scheitert an Datenträgern ohne Partitionen und an der Maskierung der
    /// Geräte-IDs.
    /// </summary>
    private static Dictionary<uint, List<string>> LettersByDiskIndex()
    {
        var diskByPartition = new Dictionary<string, uint>(StringComparer.OrdinalIgnoreCase);
        foreach (ManagementBaseObject partition in Query("SELECT DeviceID, DiskIndex FROM Win32_DiskPartition"))
        {
            using (partition)
            {
                if (Text(partition, "DeviceID") is { } id && Number(partition, "DiskIndex") is { } index)
                    diskByPartition[id] = (uint)index;
            }
        }

        var result = new Dictionary<uint, List<string>>();
        foreach (ManagementBaseObject link in Query("SELECT * FROM Win32_LogicalDiskToPartition"))
        {
            using (link)
            {
                string? partitionId = DeviceIdOfReference(Text(link, "Antecedent"));
                string? letter = DeviceIdOfReference(Text(link, "Dependent"));
                if (partitionId is null || letter is null)
                    continue;

                if (!diskByPartition.TryGetValue(partitionId, out uint diskIndex))
                    continue;

                if (!result.TryGetValue(diskIndex, out List<string>? letters))
                    result[diskIndex] = letters = [];
                letters.Add(letter);
            }
        }

        return result;
    }

    /// <summary>
    /// Zieht die Geräte-ID aus einer WMI-Objektreferenz der Form
    /// <c>\\PC\root\cimv2:Win32_DiskPartition.DeviceID="Disk #0, Partition #1"</c>.
    /// </summary>
    private static string? DeviceIdOfReference(string? reference)
    {
        const string marker = "DeviceID=\"";
        if (reference is null)
            return null;

        int start = reference.IndexOf(marker, StringComparison.Ordinal);
        if (start < 0)
            return null;

        start += marker.Length;
        int end = reference.IndexOf('"', start);
        return end < 0 ? null : reference[start..end];
    }

    /// <summary>
    /// Führt eine WMI-Abfrage aus und materialisiert das Ergebnis. Die
    /// Aufzählung selbst kann werfen — etwa "Nicht gefunden" bei einem
    /// Datenträger ohne Partitionen —, deshalb liegt sie vollständig im
    /// <c>try</c> und kann nicht als <c>yield</c>-Iterator geschrieben werden.
    /// Der Aufrufer gibt die Elemente frei.
    /// </summary>
    private static List<ManagementBaseObject> Query(string query)
    {
        var items = new List<ManagementBaseObject>();
        try
        {
            using var searcher = new ManagementObjectSearcher(new ObjectQuery(query));
            using ManagementObjectCollection results = searcher.Get();

            foreach (ManagementBaseObject item in results)
                items.Add(item);
        }
        catch (Exception ex) when (ex is ManagementException or UnauthorizedAccessException)
        {
            // Einzelne Klassen und Assoziationen fehlen je nach Hardware und
            // Windows-Ausgabe. Der Abschnitt bleibt dann eben unvollständig.
        }

        return items;
    }

    private static void Add(List<InfoItem> items, string label, string? value)
    {
        if (!string.IsNullOrWhiteSpace(value))
            items.Add(new InfoItem(label, value.Trim()));
    }

    private static string? Text(ManagementBaseObject item, string property)
    {
        try
        {
            return item[property]?.ToString();
        }
        catch (ManagementException)
        {
            return null;
        }
    }

    private static ulong? Number(ManagementBaseObject item, string property)
    {
        try
        {
            return item[property] switch
            {
                ulong value => value,
                uint value => value,
                int value and >= 0 => (ulong)value,
                long value and >= 0 => (ulong)value,
                string text when ulong.TryParse(text, out ulong parsed) => parsed,
                _ => null,
            };
        }
        catch (ManagementException)
        {
            return null;
        }
    }

    private static string FormatUptime(TimeSpan uptime)
    {
        if (uptime.TotalDays >= 1)
            return $"{(int)uptime.TotalDays} Tage, {uptime.Hours} Std, {uptime.Minutes} Min";

        return uptime.TotalHours >= 1
            ? $"{(int)uptime.TotalHours} Std, {uptime.Minutes} Min"
            : $"{uptime.Minutes} Min";
    }
}

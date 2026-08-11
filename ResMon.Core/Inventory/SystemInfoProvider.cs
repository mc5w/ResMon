using System.Globalization;
using System.Management;
using System.Runtime.InteropServices;
using Microsoft.Win32;
using ResMon.Core.Diagnostics;
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
        BootRecord boot = BootHistory.Read();

        var groups = new List<InfoGroup>
        {
            new("Betriebssystem", OperatingSystemItems()),
            new("Laufzeit", UptimeItems(bootTime, boot)),
            new("Prozessor", ProcessorItems()),
            new("Grafik", GraphicsItems()),
            new("Arbeitsspeicher", MemoryItems()),
            new("Mainboard und BIOS", BoardItems()),
        };

        return new SystemInfo(groups.Where(g => g.Items.Count > 0).ToList(), Drives(), bootTime)
        {
            Devices = DeviceInventory.Collect(),
        };
    }

    private static List<InfoItem> OperatingSystemItems()
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
        return items;
    }

    /// <summary>
    /// Zwei verschiedene Antworten auf „wie lange läuft der Rechner schon", weil
    /// es zwei verschiedene Fragen sind — siehe <see cref="BootHistory"/>.
    /// </summary>
    /// <remarks>
    /// Die zweite Antwort steht nur da, wenn sie sich von der ersten
    /// unterscheidet. Bei einem Kaltstart fallen Einschaltzeitpunkt und Beginn
    /// der Kernelsitzung zusammen; zwei Zeilen mit derselben Uhrzeit unter
    /// verschiedenen Namen sind dann keine Auskunft, sondern eine Fangfrage.
    ///
    /// Dasselbe gilt für das letzte Herunterfahren: den Eintrag „geordnet
    /// beendet" schreibt der Ereignisprotokolldienst nur, wenn er wirklich
    /// stoppt — beim Schnellstart wird er mit der Sitzung eingefroren. Der
    /// jüngste Eintrag stammt deshalb fast immer von dem Neustart, mit dem die
    /// laufende Kernelsitzung begann, und trägt denselben Zeitstempel wie sie.
    /// </remarks>
    private static List<InfoItem> UptimeItems(DateTime kernelSessionStart, BootRecord boot)
    {
        var items = new List<InfoItem>();

        const string PowerOnHelp =
            "Seit dem letzten Einschalten des Rechners, aus dem Ereignisprotokoll. Das ist die " +
            "Laufzeit, die man meint, wenn man vom Hochfahren spricht: sie beginnt bei jedem " +
            "Einschalten neu, auch nach einem gewöhnlichen Herunterfahren.";

        if (boot.PowerOn is { } powerOn)
        {
            Add(items, "Eingeschaltet", powerOn.ToString("dddd, d. MMMM yyyy, HH:mm", CultureInfo.CurrentCulture), PowerOnHelp);
            Add(items, "Läuft seit", FormatUptime(DateTime.Now - powerOn), PowerOnHelp);
            Add(items, "Startart", BootKindText(boot.Kind), BootKindHelp(boot.Kind));
        }

        const string SessionHelp =
            "Der letzte vollständige Start — Neustart oder Kaltstart —, mit dem die laufende " +
            "Kernelsitzung begann. Windows' Schnellstart macht aus dem Herunterfahren einen " +
            "Ruhezustand dieser Sitzung: Ausschalten und wieder Einschalten setzt den Zeitpunkt " +
            "deshalb nicht zurück, ein Neustart schon. Der Task-Manager zählt genauso.";

        // Drei Minuten Toleranz: bei einem Kaltstart liegen Einschaltereignis und
        // Beginn der Kernelsitzung Sekunden auseinander, gemeint ist dasselbe.
        bool sessionBeganWithThisPowerOn =
            boot.PowerOn is { } start && Math.Abs((start - kernelSessionStart).TotalMinutes) < 3;

        if (!sessionBeganWithThisPowerOn)
        {
            Add(items, "Letzter vollständiger Start",
                kernelSessionStart.ToString("d. MMMM yyyy, HH:mm", CultureInfo.CurrentCulture), SessionHelp);
            Add(items, "Sitzung läuft seit",
                FormatUptime(TimeSpan.FromMilliseconds(Environment.TickCount64)), SessionHelp);
        }

        if (UnbiasedUptime() is { Ticks: > 0 } active)
        {
            Add(items, "Davon aktiv gerechnet", FormatUptime(active),
                "Die Sitzungslaufzeit ohne Standby und ohne den Ruhezustand des Schnellstarts — " +
                "also die Zeit, in der der Rechner tatsächlich gerechnet hat.");
        }

        AddShutdown(items, boot, kernelSessionStart);
        return items;
    }

    /// <summary>
    /// Das letzte Herunterfahren, sofern es überhaupt etwas Neues sagt. Ein
    /// unerwartetes Ende wird immer gemeldet — dessen Zeitstempel ist allerdings
    /// der des nächsten Starts, denn Windows kann den Eintrag erst schreiben,
    /// wenn es wieder läuft.
    /// </summary>
    private static void AddShutdown(List<InfoItem> items, BootRecord boot, DateTime kernelSessionStart)
    {
        if (boot.LastShutdown is not { } shutdown)
            return;

        if (!boot.ShutdownWasClean)
        {
            Add(items, "Unerwartetes Ende",
                $"beim Start am {shutdown.ToString("d. MMMM, HH:mm", CultureInfo.CurrentCulture)} gemeldet",
                "Der Lauf davor wurde nicht geordnet beendet — Absturz, Stromausfall oder ein " +
                "erzwungenes Ausschalten. Windows bemerkt das erst beim nächsten Start und trägt es " +
                "dann ein; die angegebene Zeit ist deshalb die des Starts, nicht die des Absturzes.");
            return;
        }

        // Fällt der Eintrag mit dem Beginn der Kernelsitzung zusammen, ist er der
        // Herunterfahr-Teil genau dieses Neustarts und steht schon oben.
        if (Math.Abs((shutdown - kernelSessionStart).TotalMinutes) < 5)
            return;

        Add(items, "Zuletzt geordnet beendet",
            shutdown.ToString("d. MMMM yyyy, HH:mm", CultureInfo.CurrentCulture),
            "Das letzte vollständige Herunterfahren, bei dem auch der Ereignisprotokolldienst " +
            "gestoppt wurde. Beim Schnellstart geschieht das nicht — dann liegt dieser Zeitpunkt " +
            "weit zurück, obwohl der Rechner seither mehrfach aus war.");
    }

    private static string BootKindText(BootKind kind) => kind switch
    {
        BootKind.Cold => "Kaltstart",
        BootKind.Hybrid => "Schnellstart",
        BootKind.Resume => "Aus dem Ruhezustand",
        _ => "unbekannt",
    };

    private static string BootKindHelp(BootKind kind) => kind switch
    {
        BootKind.Cold => "Vollständiger Start: der Kernel wurde neu geladen. So startet Windows nach " +
                         "einem Neustart und wenn der Schnellstart abgeschaltet ist.",
        BootKind.Hybrid => "Schnellstart: beim Herunterfahren wurde die Kernelsitzung in die Datei " +
                           "hiberfil.sys geschrieben und beim Einschalten wieder geladen. Das ist die " +
                           "Voreinstellung von Windows und der Grund, warum die Sitzungslaufzeit über " +
                           "das Ausschalten hinweg weiterläuft.",
        BootKind.Resume => "Der Rechner wurde aus dem Ruhezustand fortgesetzt.",
        _ => "Die Startart konnte dem Ereignisprotokoll nicht entnommen werden.",
    };

    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryUnbiasedInterruptTime(out ulong unbiasedTime);

    /// <summary>
    /// Laufzeit ohne die Zeit, die das System geschlafen hat. Die Einheit sind
    /// 100-Nanosekunden-Schritte, also dasselbe Raster wie <see cref="TimeSpan"/>.
    /// </summary>
    private static TimeSpan UnbiasedUptime()
        => QueryUnbiasedInterruptTime(out ulong unbiased)
            ? TimeSpan.FromTicks((long)unbiased)
            : TimeSpan.Zero;

    private static List<InfoItem> ProcessorItems()
    {
        var items = new List<InfoItem>();

        using RegistryKey? key = Registry.LocalMachine.OpenSubKey(
            @"HARDWARE\DESCRIPTION\System\CentralProcessor\0");
        Add(items, "Modell", key?.GetValue("ProcessorNameString") as string);

        // Die Cache-Größen kommen aus dem Kernel, nicht aus WMI: Win32_Processor
        // kennt kein Feld für L1.
        IReadOnlyList<CpuCacheGroup> caches = CpuCache.Read();

        foreach (ManagementBaseObject cpu in Query("SELECT * FROM Win32_Processor"))
        {
            using (cpu)
            {
                Add(items, "Kerne", Text(cpu, "NumberOfCores"));
                Add(items, "Logische Prozessoren", Text(cpu, "NumberOfLogicalProcessors"));
                Add(items, "Basistakt", Number(cpu, "MaxClockSpeed") is { } mhz ? $"{mhz:N0} MHz" : null);
                Add(items, "Sockel", Text(cpu, "SocketDesignation"));

                Add(items, "L1-Cache", CacheText(caches, 1),
                    "Der schnellste und kleinste Zwischenspeicher, einer je Kern und dort noch einmal " +
                    "getrennt in Daten- und Befehlscache. WMI meldet ihn nicht; der Wert kommt aus der " +
                    "Prozessortopologie des Kernels.");
                Add(items, "L2-Cache",
                    CacheText(caches, 2)
                    ?? (Number(cpu, "L2CacheSize") is { } l2 ? $"{l2 / 1024.0:N1} MB" : null),
                    "Zweite Ebene, üblicherweise ebenfalls je Kern und für Daten und Befehle gemeinsam.");
                Add(items, "L3-Cache",
                    CacheText(caches, 3)
                    ?? (Number(cpu, "L3CacheSize") is { } l3 ? $"{l3 / 1024.0:N1} MB" : null),
                    "Dritte Ebene, die sich alle Kerne eines Chiplets teilen. Der größte und langsamste " +
                    "der drei — und der, mit dem Hersteller werben.");

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

    /// <summary>
    /// Eine Cache-Ebene als Text: Gesamtgröße, dahinter die Aufteilung. Ein
    /// einzelner Cache braucht keine Aufteilung — „32,0 MB (1 × 32,0 MB)" sagt
    /// zweimal dasselbe.
    /// </summary>
    private static string? CacheText(IReadOnlyList<CpuCacheGroup> caches, int level)
    {
        List<CpuCacheGroup> groups = caches.Where(group => group.Level == level).ToList();
        if (groups.Count == 0)
            return null;

        string total = CacheSize(groups.Sum(group => group.TotalBytes));
        if (groups is [{ Count: 1 }])
            return total;

        string detail = string.Join(" + ", groups.Select(group =>
            $"{group.Count} × {CacheSize(group.BytesEach)}{CacheKindSuffix(group.Kind)}"));

        return $"{total}  ({detail})";
    }

    private static string CacheSize(long bytes)
        => bytes >= 1048576 ? $"{bytes / 1048576.0:N1} MB" : $"{bytes / 1024.0:N0} KB";

    private static string CacheKindSuffix(CpuCacheKind kind) => kind switch
    {
        CpuCacheKind.Data => " Daten",
        CpuCacheKind.Instruction => " Befehle",
        CpuCacheKind.Trace => " Trace",
        _ => string.Empty,
    };

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
                // WMI den gedeckelten Wert 4293918720 — eine Karte mit 12 GB
                // erscheint dort als "4 GB". Alles nahe der Grenze ist deshalb
                // nicht vertrauenswürdig; der echte Wert steht in der GPU-Kachel
                // und kommt vom Sensor-Treiber.
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
        var types = new List<string>();
        var forms = new List<string>();
        var moduleRows = new List<InfoItem>();

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

                if (MemoryTypeName(Number(module, "SMBIOSMemoryType")) is { } type && !types.Contains(type))
                    types.Add(type);
                if (FormFactorName(Number(module, "FormFactor")) is { } form && !forms.Contains(form))
                    forms.Add(form);

                var parts = new List<string> { $"{capacity / 1073741824.0:N0} GB" };
                if (speed is not null)
                    parts.Add(speed);
                // Riegel ohne hinterlegte Herstellerkennung melden wörtlich
                // "Unknown"; das als Hersteller anzuzeigen wäre schlechter als nichts.
                if (Text(module, "Manufacturer") is { Length: > 0 } maker
                    && !maker.Equals("Unknown", StringComparison.OrdinalIgnoreCase))
                {
                    parts.Add(maker.Trim());
                }
                if (Text(module, "PartNumber") is { Length: > 0 } part)
                    parts.Add(part.Trim());
                if (Number(module, "ConfiguredVoltage") is { } millivolt and > 0)
                    parts.Add($"{millivolt / 1000.0:N2} V");

                // Manche Boards vergeben denselben DeviceLocator mehrfach und
                // unterscheiden die Bänke nur über BankLabel.
                string locator = Text(module, "DeviceLocator")?.Trim() ?? $"Riegel {slots}";
                if (Text(module, "BankLabel") is { Length: > 0 } bank && !locator.Contains(bank.Trim(), StringComparison.OrdinalIgnoreCase))
                    locator = $"{bank.Trim()} / {locator}";

                moduleRows.Add(new InfoItem(locator, string.Join("  ·  ", parts)));
            }
        }

        // Bauart und Steckplatzbelegung stehen vor den einzelnen Riegeln — das
        // ist die Angabe, die man beim Aufrüsten sucht.
        Add(items, "Bauart", string.Join(" / ", types.Concat(forms)));

        long banks = 0;
        foreach (ManagementBaseObject array in Query("SELECT MemoryDevices FROM Win32_PhysicalMemoryArray"))
        {
            using (array)
                banks += (long)(Number(array, "MemoryDevices") ?? 0);
        }

        if (moduleTotal > 0)
        {
            string free = banks > slots ? $", {banks - slots} frei" : string.Empty;
            Add(items, "Bestückt",
                banks > 0
                    ? $"{moduleTotal / 1073741824.0:N0} GB in {slots} von {banks} Steckplätzen{free}"
                    : $"{moduleTotal / 1073741824.0:N0} GB in {slots} Modulen",
                banks > slots
                    ? "Es sind noch Steckplätze frei — Arbeitsspeicher lässt sich ergänzen, ohne vorhandene Riegel zu ersetzen."
                    : null);
        }

        items.AddRange(moduleRows);
        return items;
    }

    /// <summary>SMBIOS-Speichertypen nach DMTF-Spezifikation, Feld 7.18.2.</summary>
    private static string? MemoryTypeName(ulong? code) => code switch
    {
        20 => "DDR",
        21 => "DDR2",
        24 => "DDR3",
        26 => "DDR4",
        34 => "DDR5",
        35 => "LPDDR5",
        30 => "LPDDR3",
        31 => "LPDDR4",
        _ => null,
    };

    private static string? FormFactorName(ulong? code) => code switch
    {
        8 => "DIMM",
        12 => "SO-DIMM",
        13 => "SRIMM",
        _ => null,
    };

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
                    drive.AvailableFreeSpace)
                {
                    Type = drive.DriveType,
                };
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
            // Windows-Ausgabe. Der Abschnitt bleibt dann eben unvollständig —
            // im Reiter „Logs" steht, welche Abfrage es war.
            DiagnosticLog.Report("Systemübersicht (WMI)", ex, $"Abfrage »{query}«");
        }

        return items;
    }

    private static void Add(List<InfoItem> items, string label, string? value, string? help = null)
    {
        if (!string.IsNullOrWhiteSpace(value))
            items.Add(new InfoItem(label, value.Trim(), help));
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
            // WMI bildet uint16-Felder auf ushort ab, uint32 auf uint und so fort.
            // Ohne die schmalen Fälle bleibt etwa Win32_PhysicalMemory.FormFactor
            // stumm — der Fehler fällt nur nicht auf, weil er wie ein fehlendes
            // Feld aussieht.
            return item[property] switch
            {
                ulong value => value,
                uint value => value,
                ushort value => value,
                byte value => value,
                int value and >= 0 => (ulong)value,
                long value and >= 0 => (ulong)value,
                short value and >= 0 => (ulong)value,
                sbyte value and >= 0 => (ulong)value,
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

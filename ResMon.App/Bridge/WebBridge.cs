using System.Text.Json;
using System.Text.Json.Serialization;
using ResMon.Core.Config;
using ResMon.Core.Diagnostics;
using ResMon.Core.Inventory;
using ResMon.Core.Model;
using ResMon.Core.Native;
using ResMon.Core.Processes;
using ResMon.Core.Startup;
using ResMon.Core.Storage;

namespace ResMon.App.Bridge;

/// <summary>
/// Zustandsmeldungen, die das Detailfenster als Hinweis anzeigt — etwa wenn die
/// CPU-Sensoren durch einen blockierten Kernel-Treiber ausfallen.
/// </summary>
public readonly record struct HostDiagnostics(
    bool CpuSensorsBlocked,
    bool GpuCountersMissing,
    bool NetworkCountersMissing,
    bool DiskCountersMissing,
    bool ProcessCountersMissing,
    bool LegacyProcessCounters,
    string? NetworkTraceError)
{
    /// <summary>Kein Zugriff auf den Super-I/O-Chip: keine Sockeltemperatur, keine Gehäuselüfter.</summary>
    public bool BoardSensorsMissing { get; init; }

    /// <summary>
    /// Ob der Rechner einen Akku hat. Auf Notebooks fehlt der Super-I/O-Chip
    /// nicht wegen eines gesperrten Treibers, sondern weil dort der Embedded
    /// Controller zuständig ist — das ist ein anderer Hinweis.
    /// </summary>
    public bool HasBattery { get; init; }

    /// <summary>Ob die ACPI-Thermalzonen als treiberfreier Ersatz einspringen können.</summary>
    public bool ThermalZonesAvailable { get; init; }

    /// <summary>Ob sich der Takt ohne Sensortreiber aus dem Leistungszähler schätzen lässt.</summary>
    public bool ClockEstimateAvailable { get; init; }

    /// <summary>Fehlermeldung, falls sich die Sensorbibliothek nicht öffnen ließ.</summary>
    public string? SensorDriverError { get; init; }

    /// <summary>
    /// Ob der Prozess erhöht läuft. Ohne Adminrechte fehlen Sensortreiber und
    /// ETW-Sitzung — im Reiter „Logs" ist das die erste Frage.
    /// </summary>
    public bool Elevated { get; init; }
}

/// <summary>Ein von der Oberfläche gesendetes Kommando (DESIGN.md §12).</summary>
public sealed class WebCommand
{
    public string? Cmd { get; set; }
    public double? Value { get; set; }
    public int? Pid { get; set; }
    public string? Name { get; set; }

    /// <summary>Benennt bei Einstellungen die betroffene Reihe, etwa "gpu" oder "net".</summary>
    public string? Key { get; set; }

    public bool? On { get; set; }

    /// <summary>
    /// Laufwerkswurzel für den Ordner-Scan, etwa <c>C:\</c>. Der Host nimmt nur
    /// Wurzeln an; einen beliebigen Pfad soll die Seite ihm nicht zum Durchlaufen
    /// geben können. Bewusst ein eigenes Feld und nicht <see cref="Name"/>: dies
    /// ist der einzige Wert von der Seite, der geprüft wird, und ein geprüfter
    /// Wert soll sich keinen Platz mit einem ungeprüften teilen.
    /// </summary>
    public string? Path { get; set; }

    /// <summary>
    /// Kennung des Laufs, zu dem sich <see cref="Node"/> zählt. Ein Nachschlag
    /// aus einem überholten Scan zeigte sonst in einen anderen Baum.
    /// </summary>
    public int? Scan { get; set; }

    /// <summary>Index eines Knotens im Ergebnis des benannten Laufs.</summary>
    public int? Node { get; set; }
}

/// <summary>
/// Übersetzt zwischen Datenmodell und WebView2. C# → JS als JSON-Nachricht,
/// JS → C# über ein bewusst schmales Command-Set (DESIGN.md §12).
/// </summary>
public static class WebBridge
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals,
        // Prozessart und Verbindungszustand gehen als sprechende Namen an die
        // Seite; als Zahlen müsste die Oberfläche die Aufzählung nachbilden.
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    /// <summary>Anzahl Punkte der Sparklines im Overlay.</summary>
    private const int OverlayHistoryPoints = 60;

    /// <summary>
    /// Obergrenze für die Verbindungstabelle. Auf einem Rechner mit viel
    /// Netzverkehr stehen dort schnell mehrere tausend Einträge; darüber hinaus
    /// ist die Liste ohnehin nicht mehr zu lesen und würde nur die Nachricht
    /// aufblähen.
    /// </summary>
    private const int MaxConnections = 2000;

    public static WebCommand? ParseCommand(string json)
    {
        try
        {
            return JsonSerializer.Deserialize<WebCommand>(json, Options);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>Schlanke Nutzlast für das Overlay — ohne Prozessliste.</summary>
    public static string BuildOverlayPayload(
        SystemSnapshot snapshot,
        AggregateSample[] history,
        VisibilitySettings visible)
    {
        var payload = new
        {
            type = "overlay",
            cpu = new
            {
                percent = Round(snapshot.Cpu.TotalPercent),
                tempC = Round(snapshot.Cpu.PackageTempC),
                tempOrigin = snapshot.Cpu.TempOrigin,
                clockMhz = Round(snapshot.Cpu.ClockMhz, 0),
                powerW = Round(snapshot.Cpu.PackagePowerW),
            },
            gpu = new
            {
                available = snapshot.Gpu.Available,
                percent = Round(snapshot.Gpu.TotalPercent),
                tempC = Round(snapshot.Gpu.TempC),
                memUsedBytes = snapshot.Gpu.MemUsedBytes,
                memTotalBytes = snapshot.Gpu.MemTotalBytes,
                fanRpm = Round(snapshot.Gpu.FanRpm, 0),
                powerW = Round(snapshot.Gpu.PowerW),
            },
            ram = new
            {
                percent = Round(snapshot.Memory.Percent),
                usedBytes = snapshot.Memory.UsedBytes,
                totalBytes = snapshot.Memory.TotalBytes,
            },
            net = new
            {
                available = snapshot.Network.Available,
                rx = Round(snapshot.Network.ReceivedBytesPerSec, 0),
                tx = Round(snapshot.Network.SentBytesPerSec, 0),
            },
            disk = new
            {
                available = snapshot.Disk.Available,
                read = Round(snapshot.Disk.ReadBytesPerSec, 0),
                write = Round(snapshot.Disk.WriteBytesPerSec, 0),
                busyPercent = Round(snapshot.Disk.BusyPercent),
            },
            visible = new
            {
                cpu = visible.Cpu,
                gpu = visible.Gpu,
                ram = visible.Ram,
                net = visible.Net,
                disk = visible.Disk,
                temps = visible.Temps,
            },
            history = new
            {
                cpu = Series(history, OverlayHistoryPoints, s => s.CpuPercent),
                gpu = Series(history, OverlayHistoryPoints, s => s.GpuPercent),
                ram = Series(history, OverlayHistoryPoints, s => s.MemoryPercent),
                // Netz- und Datenträgerraten haben keine feste Obergrenze — die
                // Sparklines skalieren sich in JavaScript auf ihr eigenes Maximum.
                net = Series(history, OverlayHistoryPoints, s => s.NetReceivedBytesPerSec + s.NetSentBytesPerSec, digits: 0),
                disk = Series(history, OverlayHistoryPoints, s => s.DiskReadBytesPerSec + s.DiskWriteBytesPerSec, digits: 0),
            },
        };

        return JsonSerializer.Serialize(payload, Options);
    }

    /// <summary>
    /// Nutzlast für das Detailfenster. <paramref name="processes"/> ist
    /// <c>null</c>, wenn sich die Prozessliste seit dem letzten Takt nicht
    /// geändert hat — die Oberfläche behält dann ihren Stand.
    /// </summary>
    public static string BuildDetailPayload(
        SystemSnapshot snapshot,
        AggregateSample[] history,
        IReadOnlyList<ProcessSample>? processes,
        IReadOnlyList<NetConnection>? connections,
        HostDiagnostics diagnostics,
        IReadOnlyList<DiagnosticEntry>? logs)
    {
        BatteryMetrics? battery = snapshot.Energy.Battery;

        var payload = new
        {
            type = "detail",
            timestamp = snapshot.Timestamp,
            cpu = new
            {
                percent = Round(snapshot.Cpu.TotalPercent),
                tempC = Round(snapshot.Cpu.PackageTempC),
                tempOrigin = snapshot.Cpu.TempOrigin,
                socketTempC = Round(snapshot.Cpu.SocketTempC),
                clockMhz = Round(snapshot.Cpu.ClockMhz, 0),
                clockEstimated = snapshot.Cpu.ClockIsEstimated,
                powerW = Round(snapshot.Cpu.PackagePowerW),
                cores = snapshot.Cpu.PerCorePercent.Select(c => Round(c, 0)).ToArray(),
            },
            gpu = new
            {
                available = snapshot.Gpu.Available,
                percent = Round(snapshot.Gpu.TotalPercent),
                tempC = Round(snapshot.Gpu.TempC),
                fanRpm = Round(snapshot.Gpu.FanRpm, 0),
                powerW = Round(snapshot.Gpu.PowerW),
                memUsedBytes = snapshot.Gpu.MemUsedBytes,
                memTotalBytes = snapshot.Gpu.MemTotalBytes,
                byEngineType = snapshot.Gpu.ByEngineType
                    .Where(e => e.Value >= 0.05)
                    .OrderByDescending(e => e.Value)
                    .ToDictionary(e => e.Key, e => Round(e.Value)),
            },
            ram = new
            {
                percent = Round(snapshot.Memory.Percent),
                usedBytes = snapshot.Memory.UsedBytes,
                totalBytes = snapshot.Memory.TotalBytes,
                committedBytes = snapshot.Memory.CommittedBytes,
            },
            net = new
            {
                available = snapshot.Network.Available,
                rx = Round(snapshot.Network.ReceivedBytesPerSec, 0),
                tx = Round(snapshot.Network.SentBytesPerSec, 0),
            },
            disk = new
            {
                available = snapshot.Disk.Available,
                read = Round(snapshot.Disk.ReadBytesPerSec, 0),
                write = Round(snapshot.Disk.WriteBytesPerSec, 0),
                busyPercent = Round(snapshot.Disk.BusyPercent),
            },
            diag = new
            {
                cpuSensorsBlocked = diagnostics.CpuSensorsBlocked,
                gpuCountersMissing = diagnostics.GpuCountersMissing,
                networkCountersMissing = diagnostics.NetworkCountersMissing,
                diskCountersMissing = diagnostics.DiskCountersMissing,
                processCountersMissing = diagnostics.ProcessCountersMissing,
                legacyProcessCounters = diagnostics.LegacyProcessCounters,
                networkTraceError = diagnostics.NetworkTraceError,
                boardSensorsMissing = diagnostics.BoardSensorsMissing,
                hasBattery = diagnostics.HasBattery,
                thermalZonesAvailable = diagnostics.ThermalZonesAvailable,
                clockEstimateAvailable = diagnostics.ClockEstimateAvailable,
                sensorDriverError = diagnostics.SensorDriverError,
                elevated = diagnostics.Elevated,
            },
            // Null, solange sich am Protokoll nichts geändert hat — es steht
            // meistens still, und die Seite behält ihren Stand.
            logs = logs?.Select(entry => new
            {
                source = entry.Source,
                message = entry.Message,
                severity = entry.Severity,
                first = entry.First,
                last = entry.Last,
                count = entry.Count,
            }).ToArray(),
            system = new
            {
                processes = snapshot.ProcessCount,
                threads = snapshot.ThreadCount,
            },
            energy = new
            {
                cpuW = Round(snapshot.Energy.CpuPackagePowerW),
                gpuW = Round(snapshot.Energy.GpuPowerW),
                measuredW = Round(snapshot.Energy.MeasuredW),
                rails = snapshot.Energy.Rails
                    .OrderByDescending(r => r.Watts)
                    .Select(r => new { hardware = r.Hardware, name = r.Name, watts = Round(r.Watts) })
                    .ToArray(),
                fans = snapshot.Energy.Fans
                    .Select(f => new
                    {
                        hardware = f.Hardware,
                        name = f.Name,
                        rpm = Round(f.Rpm, 0),
                        percent = Round(f.Percent, 0),
                    })
                    .ToArray(),
                temperatures = snapshot.Energy.Temperatures
                    .Select(t => new
                    {
                        hardware = t.Hardware,
                        name = t.Name,
                        celsius = Round(t.Celsius),
                        source = t.Source,
                    })
                    .ToArray(),
                battery = battery is null ? null : new
                {
                    percent = Round(battery.ChargePercent),
                    onAc = battery.OnAcPower,
                    charging = battery.Charging,
                    rateW = Round(battery.RateW),
                    voltageV = Round(battery.VoltageV, 2),
                    designedWh = Round(battery.DesignedCapacityWh),
                    fullWh = Round(battery.FullChargedCapacityWh),
                    remainingWh = Round(battery.RemainingCapacityWh),
                    degradation = Round(battery.DegradationPercent),
                    // Minuten statt einer Zeitspanne: die Seite formatiert selbst.
                    remainingMinutes = battery.Remaining is { } left ? (int)left.TotalMinutes : (int?)null,
                },
            },
            history = new
            {
                cpu = Series(history, history.Length, s => s.CpuPercent),
                gpu = Series(history, history.Length, s => s.GpuPercent),
                ram = Series(history, history.Length, s => s.MemoryPercent),
                // Raten ohne feste Obergrenze; das Diagramm skaliert sie auf ihr
                // eigenes Maximum und schreibt es an die Legende.
                net = Series(history, history.Length, s => s.NetReceivedBytesPerSec + s.NetSentBytesPerSec, digits: 0),
                disk = Series(history, history.Length, s => s.DiskReadBytesPerSec + s.DiskWriteBytesPerSec, digits: 0),
                cpuPower = Series(history, history.Length, s => s.CpuPowerW),
                gpuPower = Series(history, history.Length, s => s.GpuPowerW),
                seconds = history.Length,
            },
            processes = processes?.Select(p => new
            {
                pid = p.Pid,
                parentPid = p.ParentPid,
                name = p.Name,
                description = p.Description,
                cpu = Round(p.CpuPercent),
                ws = p.WorkingSetBytes,
                priv = p.PrivateBytes,
                gpu = Round(p.GpuPercent),
                gpuEngines = p.GpuByEngineType
                    .Where(e => e.Value >= 0.05)
                    .ToDictionary(e => e.Key, e => Round(e.Value)),
                gpuMem = p.GpuMemBytes,
                services = p.ServiceNames,
                rx = Round(p.NetReceivedBytesPerSec, 0),
                tx = Round(p.NetSentBytesPerSec, 0),
                ioRead = Round(p.IoReadBytesPerSec, 0),
                ioWrite = Round(p.IoWriteBytesPerSec, 0),
                path = p.ImagePath,
                threads = p.ThreadCount,
                user = p.UserName,
                category = p.Category,
                window = p.WindowTitle,
                hung = p.NotResponding,
                fault = p.FaultNote,
                tcpPorts = p.ListeningTcpPorts,
                udpPorts = p.ListeningUdpPorts,
                connections = p.ConnectionCount,
            }).ToArray(),
            connections = connections?.Take(MaxConnections).Select(c => new
            {
                protocol = c.Protocol,
                local = c.LocalAddress,
                localPort = c.LocalPort,
                remote = c.RemoteAddress,
                remotePort = c.RemotePort,
                state = c.State,
                pid = c.Pid,
            }).ToArray(),
            connectionTotal = connections?.Count,
        };

        return JsonSerializer.Serialize(payload, Options);
    }

    /// <summary>
    /// Der vollständige Einstellungsstand, für beide Fenster identisch. Er geht
    /// nach jeder Änderung sofort raus — an das Overlay, damit es sich anpasst,
    /// und an das Detailfenster, damit die Einstellungsseite den Stand zeigt,
    /// auch wenn er aus dem Tray-Menü kam.
    /// </summary>
    public static string BuildSettingsPayload(AppSettings settings)
    {
        var payload = new
        {
            type = "settings",
            theme = settings.Theme,
            overlay = new
            {
                opacity = settings.Overlay.Opacity,
                scale = settings.Overlay.Scale,
                clickThrough = settings.Overlay.ClickThrough,
            },
            visible = new
            {
                cpu = settings.Visible.Cpu,
                gpu = settings.Visible.Gpu,
                ram = settings.Visible.Ram,
                net = settings.Visible.Net,
                disk = settings.Visible.Disk,
                temps = settings.Visible.Temps,
            },
            chart = new
            {
                cpu = settings.Chart.Cpu,
                gpu = settings.Chart.Gpu,
                ram = settings.Chart.Ram,
                net = settings.Chart.Net,
                disk = settings.Chart.Disk,
            },
        };

        return JsonSerializer.Serialize(payload, Options);
    }

    /// <summary>
    /// Meldet dem Overlay, dass es gerade trotz Klick-Durchlässigkeit bedienbar
    /// ist, weil der Notausstieg gehalten wird.
    /// </summary>
    public static string BuildBypassPayload(bool active)
        => JsonSerializer.Serialize(new { type = "bypass", active }, Options);

    /// <summary>
    /// Die Systemübersicht. Der feste Teil ändert sich nicht, die Geräte schon —
    /// deshalb kann die Seite eine neue Erhebung anfordern.
    /// </summary>
    public static string BuildSystemPayload(SystemInfo info)
    {
        var payload = new
        {
            type = "system",
            collectedAt = info.CollectedAt,
            groups = info.Groups.Select(group => new
            {
                title = group.Title,
                items = group.Items.Select(item => new { label = item.Label, value = item.Value, help = item.Help }),
            }),
            devices = info.Devices.Select(group => new
            {
                title = group.Title,
                hint = group.Hint,
                items = group.Items.Select(device => new
                {
                    name = device.Name,
                    status = device.Status,
                    health = device.Health,
                    details = device.Details.Select(item => new { label = item.Label, value = item.Value }),
                }),
            }),
            drives = info.Drives.Select(drive => new
            {
                model = drive.Model,
                interfaceType = drive.InterfaceType,
                mediaType = drive.MediaType,
                sizeBytes = drive.SizeBytes,
                volumes = drive.Volumes.Select(volume => new
                {
                    name = volume.Name,
                    label = volume.Label,
                    driveType = volume.Type,
                    fileSystem = volume.FileSystem,
                    totalBytes = volume.TotalBytes,
                    freeBytes = volume.FreeBytes,
                    usedBytes = volume.UsedBytes,
                    usedPercent = Round(volume.UsedPercent),
                }),
            }),
        };

        return JsonSerializer.Serialize(payload, Options);
    }

    /// <summary>
    /// Nur die Kapazität der Laufwerke, im Takt. Hält die Angabe „frei" in der
    /// Laufwerksauswahl des Speicher-Reiters aktuell, während man aufräumt.
    /// </summary>
    /// <remarks>
    /// Bewusst nicht Teil der Systemübersicht: die ist teuer und wird einmal
    /// erhoben. Und bewusst nicht Teil der Messnutzlast: die geht im Sekundentakt
    /// an Overlay <em>und</em> Detailfenster, während diese Angabe nur einen
    /// Reiter betrifft und sich langsam ändert.
    /// </remarks>
    public static string BuildVolumesPayload(IReadOnlyList<VolumeInfo> volumes)
    {
        var payload = new
        {
            type = "volumes",
            volumes = volumes.Select(volume => new
            {
                name = volume.Name,
                label = volume.Label,
                driveType = volume.Type,
                totalBytes = volume.TotalBytes,
                freeBytes = volume.FreeBytes,
                usedBytes = volume.UsedBytes,
                usedPercent = Round(volume.UsedPercent),
            }).ToArray(),
        };

        return JsonSerializer.Serialize(payload, Options);
    }

    /// <summary>
    /// Die Startanalyse. Wie die Systemübersicht ein eigener Nachrichtentyp und
    /// kein Anhängsel der Messnutzlast: sie wird auf Anforderung erhoben, ändert
    /// sich zwischen zwei Systemstarts nicht und wäre im Sekundentakt reine Last.
    /// </summary>
    public static string BuildStartupPayload(StartupReport report, BootTraceStatus trace)
    {
        var payload = new
        {
            type = "startup",
            collectedAt = report.CollectedAt,
            powerOn = report.Boot.PowerOn,
            bootKind = report.Boot.Kind,
            sessionStart = report.SessionStart,
            performance = report.Performance is not { } performance ? null : new
            {
                when = performance.When,
                total = Round(performance.BootSeconds),
                mainPath = Round(performance.MainPathSeconds),
                postBoot = Round(performance.PostBootSeconds),
                apps = performance.StartupAppCount,
                degraded = performance.Degraded,
                degradation = Round(performance.DegradationSeconds),
                phases = performance.Phases
                    .Select(p => new { key = p.Key, label = p.Label, seconds = Round(p.Seconds, 2) })
                    .ToArray(),
            },
            // Die Anmeldeaufgaben der Shell sind zu Dutzenden vorhanden und je
            // wenige Millisekunden lang; einzeln aufgeführt verstopfen sie die
            // Zeitleiste, für die Gesamtrechnung fehlen dürfen sie nicht.
            chain = report.Chain
                .Where(item => item.Kind != ChainKind.LogonTask)
                .Select(item => new
                {
                    kind = item.Kind,
                    command = item.Command,
                    started = item.Started,
                    seconds = item.Duration is { } span ? Round(span.TotalSeconds, 2) : (double?)null,
                    pid = item.Pid,
                })
                .ToArray(),
            logonTasks = report.Chain.Count(item => item.Kind == ChainKind.LogonTask),
            entries = report.Entries.Select(entry => new
            {
                name = entry.Name,
                source = entry.Source,
                sourceLabel = entry.SourceLabel,
                command = entry.Command,
                path = entry.ImagePath,
                args = entry.Arguments,
                enabled = entry.Enabled,
                disabledAt = entry.DisabledAt,
                publisher = entry.Publisher,
                description = entry.Description,
                exists = entry.FileExists,
                pid = entry.Pid,
                offset = entry.Offset is { } offset ? Round(offset.TotalSeconds, 2) : (double?)null,
                seconds = entry.Duration is { } duration ? Round(duration.TotalSeconds, 2) : (double?)null,
                detail = entry.Detail,
                issues = entry.Issues == StartupIssue.None ? null : entry.Issues.ToString(),
            }).ToArray(),
            findings = report.Findings.Select(finding => new
            {
                severity = finding.Severity,
                title = finding.Title,
                why = finding.Why,
                seconds = Round(finding.CostSeconds),
                when = finding.When,
                evidence = finding.Evidence,
            }).ToArray(),
            limitations = report.Limitations,
            trace = new
            {
                state = trace.State,
                message = trace.Message,
                armedAt = trace.ArmedAt,
                path = trace.TracePath,
                sizeBytes = trace.SizeBytes,
                error = trace.Error,
                warning = BootTrace.Warning,
            },
        };

        return JsonSerializer.Serialize(payload, Options);
    }

    /// <summary>
    /// Nur der Zustand der Startaufzeichnung. Eigene Nachricht, damit ein Klick
    /// auf „Scharfstellen“ nicht die ganze Analyse neu erheben muss.
    /// </summary>
    public static string BuildTracePayload(BootTraceStatus trace)
    {
        var payload = new
        {
            type = "trace",
            state = trace.State,
            message = trace.Message,
            armedAt = trace.ArmedAt,
            path = trace.TracePath,
            sizeBytes = trace.SizeBytes,
            error = trace.Error,
            warning = BootTrace.Warning,
            // Windows zeichnet jeden Start selbst auf; das ist ohne Neustart
            // auswertbar und deshalb der erste Weg, den die Seite anbietet.
            windowsTrace = BootTraceAnalyzer.WindowsTraceAvailable(),
        };

        return JsonSerializer.Serialize(payload, Options);
    }

    /// <summary>
    /// Das Ergebnis einer ausgewerteten Startaufzeichnung: was jeder Prozess
    /// während des Starts an Rechenzeit und Datenträgerzugriffen gekostet hat.
    /// </summary>
    public static string BuildTraceSummaryPayload(BootTraceSummary? summary, int limit)
    {
        var payload = new
        {
            type = "traceSummary",
            available = summary is not null,
            path = summary?.Path,
            when = summary?.When,
            fileTime = summary?.FileTime,
            seconds = Round(summary?.DurationSeconds ?? 0),
            samples = summary?.SampleCount ?? 0,
            hasCpu = summary?.HasCpuSamples ?? false,
            fromLastBoot = summary?.FromLastBoot ?? true,
            fromWindows = summary?.FromWindows ?? false,
            error = summary?.Error,
            processes = summary?.Processes.Take(limit).Select(process => new
            {
                pid = process.Pid,
                name = process.Name,
                cpuMs = Round(process.CpuMs, 0),
                readBytes = process.DiskReadBytes,
                writeBytes = process.DiskWriteBytes,
                operations = process.DiskOperations,
                startMs = Round(process.StartOffsetMs, 0),
            }).ToArray(),
        };

        return JsonSerializer.Serialize(payload, Options);
    }

    /// <summary>Die Wartekette eines Prozesses oder die Handle-Auskunft dazu.</summary>
    public static string BuildInspectPayload(
        int pid, string? name, WaitChainResult? chain, IReadOnlyList<OpenFile>? files, int? handleCount)
    {
        var payload = new
        {
            type = "inspect",
            pid,
            name,
            handleCount,
            cycle = chain?.IsCycle,
            chain = chain?.Nodes.Select(node => new
            {
                objectType = node.Type,
                status = node.Status,
                pid = node.ProcessId == 0 ? (int?)null : node.ProcessId,
                threadId = node.ThreadId == 0 ? (int?)null : node.ThreadId,
                waitMs = node.WaitMilliseconds == 0 ? (long?)null : node.WaitMilliseconds,
                objectName = node.ObjectName,
            }).ToArray(),
            files = files?.Select(file => new { handle = file.Handle, kind = file.Kind, name = file.Name }).ToArray(),
        };

        return JsonSerializer.Serialize(payload, Options);
    }

    /// <summary>Die Handle-Zählung aller Prozesse, absteigend.</summary>
    public static string BuildHandlesPayload(
        IReadOnlyList<ProcessHandles> handles, IReadOnlyDictionary<int, string> names, int limit)
    {
        var payload = new
        {
            type = "handles",
            collectedAt = DateTime.Now,
            total = handles.Sum(h => h.Total),
            processes = handles.Take(limit).Select(entry => new
            {
                pid = entry.Pid,
                name = names.TryGetValue(entry.Pid, out string? found) ? found : null,
                total = entry.Total,
                byType = entry.ByType,
            }).ToArray(),
        };

        return JsonSerializer.Serialize(payload, Options);
    }

    /// <summary>
    /// Das Ergebnis eines Ordner-Scans, auf einen Auszug beschnitten. Ein eigener
    /// Nachrichtentyp und kein Anhängsel der Messnutzlast: die geht im
    /// Sekundentakt und ist darauf gebaut, auszulassen was sich nicht geändert
    /// hat — ein Scan ist stoßweise, unverwandt, und bis zu eine Sekunde Verzug
    /// auf den Übergang „fertig" fühlte sich kaputt an.
    /// </summary>
    public static string BuildScanPayload(FolderScanResult result, int scanId)
    {
        var payload = new
        {
            type = "scan",
            phase = "done",
            scanId,
            root = result.Root,
            cancelled = result.Cancelled,
            totalBytes = result.TotalBytes,
            volumeTotalBytes = result.VolumeTotalBytes,
            volumeUsedBytes = result.VolumeTotalBytes - result.VolumeFreeBytes,
            files = result.TotalFileCount,
            dirs = result.DirectoryCount,
            denied = result.DeniedFolders,
            reparse = result.ReparsePoints,
            cloudBytes = result.CloudBytes,
            seconds = Round(result.Duration.TotalSeconds),
            nodes = result.Prune().Select(ScanNode).ToArray(),

            // Die Deutung reist mit dem Baum: sie entsteht aus demselben Ergebnis
            // und wäre als eigene Nachricht nur eine zweite Gelegenheit, die
            // beiden auseinanderlaufen zu lassen.
            findings = StorageFindings.Collect(result).Select(finding => new
            {
                severity = finding.Severity,
                title = finding.Title,
                why = finding.Why,
                bytes = finding.Bytes,
                path = finding.Path,
                node = finding.NodeId,
                evidence = finding.Evidence,
                commands = finding.Commands,
                caveat = finding.Caveat,
            }).ToArray(),
        };

        return JsonSerializer.Serialize(payload, Options);
    }

    /// <summary>
    /// Das Programm-Inventar. Eigene Nachricht und eigener Knopf: die Erhebung
    /// misst Installationsordner und liest den Nutzungsverlauf, das gehört nicht
    /// in einen Sekundentakt (DESIGN.md §9).
    /// </summary>
    public static string BuildProgramsPayload(ProgramReport report)
    {
        var payload = new
        {
            type = "programs",
            collectedAt = report.CollectedAt,
            rawEntryCount = report.RawEntryCount,
            programs = report.Programs.Select(entry => new
            {
                name = entry.Name,
                scope = entry.ScopeLabel,
                version = entry.Version,
                publisher = entry.Publisher,
                installedOn = entry.InstalledOn,
                location = entry.InstallLocation,
                executable = entry.MainExecutable,
                bytes = entry.Bytes,
                sizeFrom = entry.SizeFrom,
                lastUsed = entry.LastUsed,
                launchCount = entry.LaunchCount,

                // Als Text statt als Flag-Zahl: die Oberfläche zeigt die Herkunft
                // unverändert an, damit ein Datum nicht mehr behauptet als es weiß.
                usageFrom = entry.UsageFrom == UsageSource.None ? null : entry.UsageFrom.ToString(),
                uninstall = entry.UninstallCommand,
            }).ToArray(),
            limitations = report.Limitations,
        };

        return JsonSerializer.Serialize(payload, Options);
    }

    /// <summary>Die nachgeforderten Kinder eines Knotens.</summary>
    public static string BuildScanChildrenPayload(FolderScanResult result, int scanId, int parent)
    {
        var payload = new
        {
            type = "scan",
            phase = "children",
            scanId,
            parent,
            nodes = result.ChildrenOf(parent).Select(ScanNode).ToArray(),
        };

        return JsonSerializer.Serialize(payload, Options);
    }

    /// <summary>Zwischenstand eines laufenden Scans.</summary>
    public static string BuildScanProgressPayload(
        int scanId, int dirs, int files, long bytes, string current)
    {
        var payload = new
        {
            type = "scan",
            phase = "running",
            scanId,
            dirs,
            files,
            bytes,
            current,
        };

        return JsonSerializer.Serialize(payload, Options);
    }

    /// <summary>Ein Scan, der nicht mit einem Baum endet — Fehler oder abgelehnt.</summary>
    public static string BuildScanStatusPayload(int scanId, string phase, string? message)
    {
        var payload = new { type = "scan", phase, scanId, message };
        return JsonSerializer.Serialize(payload, Options);
    }

    /// <summary>
    /// Ein Knoten auf der Leitung. Kurze Schlüssel wie bei den Prozessen
    /// (<c>ws</c>, <c>priv</c>, <c>rx</c>): bei knapp zweitausend Knoten macht
    /// der Name jedes Feldes den Unterschied zwischen 150 und 300 KB. Felder mit
    /// dem Wert null fallen über <see cref="Options"/> ganz weg — und bei einem
    /// Baum aus Ordnern ohne Großdateien ist das die Mehrzahl.
    /// </summary>
    private static object ScanNode(FolderSlice slice) => new
    {
        i = slice.Id,
        p = slice.Parent,
        n = slice.Name,
        b = slice.TotalBytes,
        o = NonZero(slice.OwnBytes),
        k = NonZero(slice.ChildCount),
        c = NonZero(slice.TotalFileCount),
        f = slice.IsFile ? true : (bool?)null,
        g = slice.Flags == FolderFlags.None ? null : (FolderFlags?)slice.Flags,
    };

    private static long? NonZero(long value) => value != 0 ? value : null;

    private static int? NonZero(int value) => value != 0 ? value : null;

    private static double[] Series(AggregateSample[] history, int points, Func<AggregateSample, double> selector, int digits = 1)
    {
        int skip = Math.Max(0, history.Length - points);
        var result = new double[history.Length - skip];
        for (int i = 0; i < result.Length; i++)
            result[i] = Round(selector(history[skip + i]), digits);
        return result;
    }

    private static double Round(double value, int digits = 1) => Math.Round(value, digits);

    private static double? Round(double? value, int digits = 1)
        => value is { } v ? Math.Round(v, digits) : null;
}

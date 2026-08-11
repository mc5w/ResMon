using System.Text.Json;
using System.Text.Json.Serialization;
using ResMon.Core.Config;
using ResMon.Core.Diagnostics;
using ResMon.Core.Inventory;
using ResMon.Core.Model;
using ResMon.Core.Processes;

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

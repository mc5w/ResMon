using System.Text.Json;
using System.Text.Json.Serialization;
using ResMon.Core.Config;
using ResMon.Core.Inventory;
using ResMon.Core.Model;

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
    string? NetworkTraceError);

/// <summary>Ein von der Oberfläche gesendetes Kommando (DESIGN.md §12).</summary>
public sealed class WebCommand
{
    public string? Cmd { get; set; }
    public double? Value { get; set; }
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
    };

    /// <summary>Anzahl Punkte der Sparklines im Overlay.</summary>
    private const int OverlayHistoryPoints = 60;

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
    public static string BuildOverlayPayload(SystemSnapshot snapshot, AggregateSample[] history, VisibilitySettings visible)
    {
        var payload = new
        {
            type = "overlay",
            cpu = new
            {
                percent = Round(snapshot.Cpu.TotalPercent),
                tempC = Round(snapshot.Cpu.PackageTempC),
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
        HostDiagnostics diagnostics)
    {
        var payload = new
        {
            type = "detail",
            timestamp = snapshot.Timestamp,
            cpu = new
            {
                percent = Round(snapshot.Cpu.TotalPercent),
                tempC = Round(snapshot.Cpu.PackageTempC),
                clockMhz = Round(snapshot.Cpu.ClockMhz, 0),
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
            },
            history = new
            {
                cpu = Series(history, history.Length, s => s.CpuPercent),
                gpu = Series(history, history.Length, s => s.GpuPercent),
                ram = Series(history, history.Length, s => s.MemoryPercent),
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
            }).ToArray(),
        };

        return JsonSerializer.Serialize(payload, Options);
    }

    /// <summary>Die Systemübersicht wird einmalig gesendet — sie ändert sich nicht.</summary>
    public static string BuildSystemPayload(SystemInfo info)
    {
        var payload = new
        {
            type = "system",
            groups = info.Groups.Select(group => new
            {
                title = group.Title,
                items = group.Items.Select(item => new { label = item.Label, value = item.Value }),
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

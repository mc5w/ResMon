using System.Globalization;
using System.Runtime.Versioning;
using ResMon.Core.Inventory;
using ResMon.Core.Model;
using ResMon.Core.Native;
using ResMon.Core.Processes;
using ResMon.Core.Sensors;

[assembly: SupportedOSPlatform("windows")]

namespace ResMon.Probe;

/// <summary>
/// Konsolen-Testharness für die Schritte 1 und 2 aus DESIGN.md §15. Gibt die
/// PDH-Rohwerte und die vollständige Sensorliste der Zielhardware aus — daraus
/// ergibt sich, welche Werte das Overlay überhaupt anzeigen kann.
/// </summary>
internal static class Program
{
    private static int Main(string[] args)
    {
        string mode = args.Length > 0 ? args[0].ToLowerInvariant() : "help";
        return mode switch
        {
            "sensors" => DumpSensors(),
            "counters" => DumpCounters(Iterations(args, 5)),
            "gpu" => DumpGpu(Iterations(args, 5)),
            "processes" => DumpProcesses(Iterations(args, 3)),
            "connections" => DumpConnections(),
            "energy" => DumpEnergy(Iterations(args, 3)),
            "paths" => DumpPaths(),
            "system" => DumpSystem(),
            _ => Help(),
        };
    }

    private static int Help()
    {
        Console.WriteLine("""
            ResMon.Probe — Diagnosewerkzeug

              sensors            Alle von LibreHardwareMonitor gefundenen Sensoren auflisten
              counters [n]       CPU-, RAM-, GPU- und Netzaggregate n-mal im Sekundentakt ausgeben
              gpu [n]            GPU-Engine-Instanzen roh ausgeben
              processes [n]      Top-15-Prozesse nach CPU ausgeben
              connections        Offene TCP- und UDP-Verbindungen mit besitzendem Prozess
              energy [n]         Leistungsaufnahme, Lüfterdrehzahlen und Akku
              system             Systemübersicht: OS, CPU, GPU, RAM, Mainboard, Datenträger
              paths              Prüfen, welche PDH-Zählerpfade dieses System kennt

            Temperaturen und der Netzverkehr pro Prozess brauchen Administratorrechte.
            """);
        return 1;
    }

    private static int DumpSensors()
    {
        using var hardware = new HardwareSource();
        if (!hardware.Open())
        {
            Console.Error.WriteLine($"LibreHardwareMonitor konnte nicht geöffnet werden: {hardware.OpenError}");
            Console.Error.WriteLine("Als Administrator ausführen.");
            return 2;
        }

        IReadOnlyList<SensorInfo> sensors = hardware.EnumerateSensors();
        Console.WriteLine($"{sensors.Count} Sensoren gefunden.");
        Console.WriteLine();

        foreach (IGrouping<string, SensorInfo> group in sensors.GroupBy(s => $"{s.Hardware} [{s.HardwareType}]"))
        {
            Console.WriteLine(group.Key);
            foreach (SensorInfo sensor in group.OrderBy(s => s.SensorType).ThenBy(s => s.Name))
            {
                string value = sensor.Value is { } v ? v.ToString("F2", CultureInfo.CurrentCulture) : "—";
                Console.WriteLine($"    {sensor.SensorType,-14} {sensor.Name,-32} {value,12}");
            }

            Console.WriteLine();
        }

        Console.WriteLine("Ausgewertete Werte:");
        HardwareReading reading = hardware.Update();
        Console.WriteLine($"    CPU Temp     {Format(reading.CpuPackageTempC, "°C")}");
        Console.WriteLine($"    CPU Takt     {Format(reading.CpuClockMhz, "MHz")}");
        Console.WriteLine($"    CPU Power    {Format(reading.CpuPackagePowerW, "W")}");
        Console.WriteLine($"    GPU Temp     {Format(reading.GpuTempC, "°C")}");
        Console.WriteLine($"    GPU Last     {Format(reading.GpuLoadPercent, "%")}");
        Console.WriteLine($"    GPU Lüfter   {Format(reading.GpuFanRpm, "rpm")}");
        Console.WriteLine($"    GPU Power    {Format(reading.GpuPowerW, "W")}");
        Console.WriteLine($"    VRAM         {reading.GpuMemUsedBytes / 1048576} / {reading.GpuMemTotalBytes / 1048576} MB");
        return 0;
    }

    private static int DumpCounters(int iterations)
    {
        using var query = new PdhQuery();
        var counters = new CounterSource(query);
        var gpu = new GpuEngineSource(query);
        var network = new NetworkSource(query);

        Console.WriteLine($"% Processor Utility verfügbar: {!counters.UsesUtilityFallback}");
        Console.WriteLine($"GPU-Engine-Zähler verfügbar:   {gpu.Available}");
        Console.WriteLine($"Netz-Zähler verfügbar:         {network.Available}");
        Console.WriteLine();

        for (int i = 0; i <= iterations; i++)
        {
            // Das erste Sample liefert keine Deltas und wird verworfen.
            if (!query.Collect())
            {
                Thread.Sleep(1000);
                continue;
            }

            CounterReading reading = counters.Read();
            GpuEngineReading gpuReading = gpu.Read();
            NetworkMetrics net = network.Read();

            Console.WriteLine(
                $"CPU {reading.CpuTotalPercent,6:F1} %   " +
                $"RAM {reading.MemoryPercent,6:F1} % ({reading.MemoryUsedBytes / 1048576} / {reading.MemoryTotalBytes / 1048576} MB)   " +
                $"Zugesichert {reading.CommittedBytes / 1048576} MB   " +
                $"GPU {gpuReading.TotalPercent,6:F1} %   " +
                $"Netz ↓{net.ReceivedBytesPerSec / 1024,8:F1} ↑{net.SentBytesPerSec / 1024,8:F1} kB/s");

            if (reading.CpuPerCorePercent.Length > 0)
                Console.WriteLine("    Kerne: " + string.Join(" ", reading.CpuPerCorePercent.Select(c => $"{c,5:F0}")));

            if (gpuReading.ByEngineType.Count > 0)
                Console.WriteLine("    Engines: " + string.Join("  ", gpuReading.ByEngineType.OrderByDescending(e => e.Value).Select(e => $"{e.Key}={e.Value:F1}%")));

            Thread.Sleep(1000);
        }

        return 0;
    }

    private static int DumpGpu(int iterations)
    {
        using var query = new PdhQuery();
        PdhCounter? engine = query.TryAddCounter(@"\GPU Engine(*)\Utilization Percentage");
        if (engine is null)
        {
            Console.Error.WriteLine(@"Zählersatz \GPU Engine ist auf diesem System nicht vorhanden.");
            return 2;
        }

        for (int i = 0; i <= iterations; i++)
        {
            if (!query.Collect())
            {
                Thread.Sleep(1000);
                continue;
            }

            var active = engine.ReadArrayDouble()
                .Where(v => v.Value > 0.05)
                .OrderByDescending(v => v.Value)
                .Take(20)
                .ToList();

            Console.WriteLine($"--- {active.Count} aktive Engine-Instanzen ---");
            foreach (PdhInstanceValue value in active)
            {
                GpuEngineSource.TryParseEngineInstance(value.Instance, out int pid, out string engineType);
                Console.WriteLine($"    pid {pid,6}  {engineType,-16} {value.Value,7:F2} %   {value.Instance}");
            }

            Thread.Sleep(1000);
        }

        return 0;
    }

    private static int DumpProcesses(int iterations)
    {
        var services = new ServiceResolver();
        services.Refresh();

        using var query = new PdhQuery();
        var gpu = new GpuEngineSource(query);
        using var sampler = new ProcessSampler(services);
        using var network = new NetworkTracer();

        if (!sampler.Available)
        {
            Console.Error.WriteLine("Prozess-Zählersatz nicht verfügbar.");
            return 2;
        }

        network.Start();
        Console.WriteLine($"Legacy-Zählersatz (kein Process V2): {sampler.UsesLegacyCounterSet}");
        Console.WriteLine($"ETW-Netzerfassung: {(network.IsRunning ? "aktiv" : "nicht verfügbar — " + network.Error)}");

        for (int i = 0; i <= iterations; i++)
        {
            query.Collect();
            IReadOnlyDictionary<int, GpuProcessUsage> gpuByPid = gpu.Read().ByProcess;
            IReadOnlyList<NetConnection> connections = ConnectionSource.Read();
            IReadOnlyList<ProcessSample> samples =
                sampler.Sample(gpuByPid, network.Read(), ConnectionSource.ByProcess(connections));

            if (samples.Count == 0)
            {
                Thread.Sleep(2000);
                continue;
            }

            Console.WriteLine();
            Console.WriteLine($"{"Name",-24} {"PID",7} {"CPU %",7} {"RAM MB",8} {"Art",-12} {"Benutzer",-22} Ports");
            foreach (ProcessSample sample in samples.OrderByDescending(s => s.CpuPercent).Take(15))
            {
                Console.WriteLine(
                    $"{Truncate(sample.Name, 24),-24} {sample.Pid,7} {sample.CpuPercent,7:F1} " +
                    $"{sample.WorkingSetBytes / 1048576,8} {sample.Category,-12} " +
                    $"{Truncate(sample.UserName ?? "—", 22),-22} {Truncate(Ports(sample), 30)}");
            }

            Thread.Sleep(2000);
        }

        return 0;
    }

    private static string Ports(ProcessSample sample)
    {
        var parts = new List<string>();
        if (sample.ListeningTcpPorts.Count > 0)
            parts.Add("TCP " + string.Join(", ", sample.ListeningTcpPorts));
        if (sample.ListeningUdpPorts.Count > 0)
            parts.Add("UDP " + string.Join(", ", sample.ListeningUdpPorts));
        if (sample.ConnectionCount > 0)
            parts.Add($"{sample.ConnectionCount} Verb.");
        return string.Join("  ", parts);
    }

    private static int DumpConnections()
    {
        IReadOnlyList<NetConnection> connections = ConnectionSource.Read();
        Dictionary<int, ProcessTreeEntry> tree = Toolhelp.Snapshot();

        Console.WriteLine($"{connections.Count} Einträge.");
        Console.WriteLine();
        Console.WriteLine($"{"Proto",-7} {"Lokal",-46} {"Remote",-46} {"Zustand",-13} {"PID",7}  Prozess");

        foreach (NetConnection connection in connections
                     .OrderBy(c => c.Protocol, StringComparer.Ordinal)
                     .ThenBy(c => c.LocalPort))
        {
            string local = $"{connection.LocalAddress}:{connection.LocalPort}";
            string remote = connection.RemoteAddress is null
                ? "—"
                : $"{connection.RemoteAddress}:{connection.RemotePort}";
            string name = tree.TryGetValue(connection.Pid, out ProcessTreeEntry entry) ? entry.ExeName : "";

            Console.WriteLine(
                $"{connection.Protocol,-7} {Truncate(local, 46),-46} {Truncate(remote, 46),-46} " +
                $"{connection.State,-13} {connection.Pid,7}  {name}");
        }

        return 0;
    }

    private static int DumpEnergy(int iterations)
    {
        using var hardware = new HardwareSource();
        if (!hardware.Open())
        {
            Console.Error.WriteLine($"LibreHardwareMonitor konnte nicht geöffnet werden: {hardware.OpenError}");
            Console.Error.WriteLine("Als Administrator ausführen.");
            return 2;
        }

        for (int i = 0; i <= iterations; i++)
        {
            HardwareReading reading = hardware.Update();

            Console.WriteLine();
            Console.WriteLine($"Leistungssensoren ({reading.Rails.Count})");
            foreach (PowerRail rail in reading.Rails.OrderByDescending(r => r.Watts))
                Console.WriteLine($"    {rail.Hardware,-28} {rail.Name,-26} {rail.Watts,8:F1} W");

            Console.WriteLine($"Lüfter ({reading.Fans.Count})");
            foreach (FanSample fan in reading.Fans)
            {
                string rpm = fan.Rpm is { } value ? $"{value,8:F0} rpm" : $"{"—",12}";
                string percent = fan.Percent is { } control ? $"{control,7:F0} %" : "";
                Console.WriteLine($"    {fan.Hardware,-28} {fan.Name,-26} {rpm} {percent}");
            }

            if (reading.Battery is { } battery)
            {
                Console.WriteLine("Akku");
                Console.WriteLine($"    Ladestand      {Format(battery.ChargePercent, "%")}");
                Console.WriteLine($"    Netzbetrieb    {battery.OnAcPower}, lädt: {battery.Charging}");
                Console.WriteLine($"    Leistung       {Format(battery.RateW, "W")}");
                Console.WriteLine($"    Spannung       {Format(battery.VoltageV, "V")}");
                Console.WriteLine($"    Kapazität      {Format(battery.RemainingCapacityWh, "Wh")} von " +
                                  $"{Format(battery.FullChargedCapacityWh, "Wh")} (neu: {Format(battery.DesignedCapacityWh, "Wh")})");
                Console.WriteLine($"    Verschleiß     {Format(battery.DegradationPercent, "%")}");
                Console.WriteLine($"    Restlaufzeit   {battery.Remaining?.ToString(@"hh\:mm") ?? "—"}");
            }
            else
            {
                Console.WriteLine("Akku: keiner gefunden.");
            }

            Thread.Sleep(2000);
        }

        return 0;
    }

    /// <summary>Prüft, welche der benötigten Zählerpfade dieses System kennt.</summary>
    private static int DumpPaths()
    {
        string[] paths =
        [
            @"\Processor Information(_Total)\% Processor Utility",
            @"\Processor Information(*)\% Processor Utility",
            @"\Memory\Committed Bytes",
            @"\GPU Engine(*)\Utilization Percentage",
            @"\GPU Process Memory(*)\Local Usage",
            @"\Process V2(*)\% Processor Time",
            @"\Process V2(*)\ID Process",
            @"\Process V2(*)\Process ID",
            @"\Process V2(*)\Creating Process ID",
            @"\Process V2(*)\Working Set - Private",
            @"\Process V2(*)\Private Bytes",
            @"\Process(*)\% Processor Time",
            @"\Process(*)\ID Process",
            @"\Process(*)\Working Set - Private",
            @"\Process(*)\Private Bytes",
        ];

        using var query = new PdhQuery();
        var added = new List<(string Path, PdhCounter? Counter)>();
        foreach (string path in paths)
        {
            PdhCounter? counter = query.TryAddCounter(path);
            added.Add((path, counter));
            Console.WriteLine($"{(counter is null ? "FEHLT " : "ok    ")} {path}");
        }

        Console.WriteLine();
        query.Collect();
        Thread.Sleep(1000);
        query.Collect();

        foreach ((string path, PdhCounter? counter) in added)
        {
            if (counter is null || !path.Contains('*'))
                continue;
            IReadOnlyList<PdhInstanceValue> instances = counter.ReadArrayDouble(noCap100: true);
            Console.WriteLine($"{instances.Count,6} Instanzen  {path}");
        }

        Console.WriteLine();
        Console.WriteLine("Beispiel-Instanznamen Process V2:");
        PdhCounter? v2 = added.First(a => a.Path == @"\Process V2(*)\% Processor Time").Counter;
        foreach (PdhInstanceValue value in v2!.ReadArrayDouble(noCap100: true).Take(8))
            Console.WriteLine($"    {value.Instance}");

        return 0;
    }

    /// <summary>Gibt die Systemübersicht aus, wie sie das Detailfenster anzeigt.</summary>
    private static int DumpSystem()
    {
        SystemInfo info = SystemInfoProvider.Collect();

        foreach (InfoGroup group in info.Groups)
        {
            Console.WriteLine(group.Title);
            foreach (InfoItem item in group.Items)
                Console.WriteLine($"    {item.Label,-24} {item.Value}");
            Console.WriteLine();
        }

        foreach (DeviceGroup group in info.Devices)
        {
            Console.WriteLine($"{group.Title} ({group.Items.Count})");
            foreach (DeviceEntry device in group.Items)
            {
                Console.WriteLine($"    [{device.Health,-7}] {device.Name}  — {device.Status}");
                foreach (InfoItem detail in device.Details)
                    Console.WriteLine($"        {detail.Label,-16} {detail.Value}");
            }

            Console.WriteLine();
        }

        Console.WriteLine("Datenträger");
        foreach (PhysicalDriveInfo drive in info.Drives)
        {
            string size = drive.SizeBytes > 0 ? $"{drive.SizeBytes / 1000000000.0:N0} GB" : "";
            Console.WriteLine($"    {drive.Model}  {drive.InterfaceType} {drive.MediaType} {size}");
            foreach (VolumeInfo volume in drive.Volumes)
            {
                Console.WriteLine(
                    $"        {volume.Name} {volume.Label ?? "",-18} {volume.FileSystem,-6} " +
                    $"{volume.UsedBytes / 1073741824.0,8:N1} / {volume.TotalBytes / 1073741824.0,8:N1} GB  " +
                    $"({volume.UsedPercent:N0} % belegt)");
            }
        }

        return 0;
    }

    private static string Format(double? value, string unit)
        => value is { } v ? $"{v,10:F1} {unit}" : $"{"—",10}";

    private static string Truncate(string value, int length)
        => value.Length <= length ? value : value[..(length - 1)] + "…";

    private static int Iterations(string[] args, int fallback)
        => args.Length > 1 && int.TryParse(args[1], out int n) && n > 0 ? n : fallback;
}

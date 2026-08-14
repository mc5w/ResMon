using System.Globalization;
using System.Runtime.Versioning;
using System.Text.Json;
using ResMon.Core;
using ResMon.Core.Config;
using ResMon.Core.Inventory;
using ResMon.Core.Model;
using ResMon.Core.Native;
using ResMon.Core.Processes;
using ResMon.Core.Sensors;
using ResMon.Core.Startup;
using ResMon.Core.Storage;

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
            "snapshot" => DumpSnapshot(Iterations(args, 3)),
            "paths" => DumpPaths(),
            "system" => DumpSystem(),
            "startup" => DumpStartup(),
            "boottrace" => DumpBootTrace(args.Length > 1 ? args[1] : null),
            "scan" => DumpScan(args.Length > 1 ? args[1] : @"C:\"),
            "programs" => DumpPrograms(args.Length > 1 ? args[1] : null),
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
              snapshot [n]       Was der Collector ausliefert, samt Herkunft jedes CPU-Werts
              system             Systemübersicht: OS, CPU, GPU, RAM, Mainboard, Datenträger
              startup            Systemstart: Phasen, Startkette, Autostart-Einträge, Befunde
              boottrace [datei]  ETW-Startaufzeichnung auswerten (Vorgabe: die von Windows selbst)
              paths              Prüfen, welche PDH-Zählerpfade dieses System kennt
              scan [Laufwerk]    Ordnerbelegung einer Partition messen (Vorgabe C:\), mit Befunden
              programs [Lw]      Installierte Programme: gemessene Größe und letzte Nutzung

            Temperaturen, der Netzverkehr pro Prozess und die letzte Nutzung aus Prefetch
            brauchen Administratorrechte.
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

        // Die treiberfreien Quellen hängen an PDH und laufen deshalb in einer
        // eigenen Abfrage neben der Sensorbibliothek her.
        using var query = new PdhQuery();
        var zones = new ThermalZoneSource(query);
        var counters = new CounterSource(query);
        Console.WriteLine($"ACPI-Thermalzonen verfügbar: {zones.Available}");
        Console.WriteLine($"Taktschätzung verfügbar:     {counters.ClockEstimateAvailable}");

        for (int i = 0; i <= iterations; i++)
        {
            HardwareReading reading = hardware.Update();
            query.Collect();

            ThermalZoneReading zoneReading = zones.Read();
            Console.WriteLine();
            Console.WriteLine($"ACPI-Thermalzonen ({zoneReading.Zones.Count})");
            foreach (TemperatureSample zone in zoneReading.Zones)
                Console.WriteLine($"    {zone.Hardware,-28} {zone.Name,-26} {zone.Celsius,8:F1} °C");
            Console.WriteLine($"    davon als CPU-Ersatz:    {Format(zoneReading.CpuZoneTempC, "°C")}");
            Console.WriteLine($"    Takt (geschätzt):        {Format(counters.Read().ClockMhz, "MHz")}");

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

    /// <summary>
    /// Der Weg durch den ganzen Erfassungsteil: nicht was die Quellen können,
    /// sondern was am Ende in der Kachel steht — und aus welcher Quelle. Damit
    /// lässt sich auf fremder Hardware in einem Aufruf klären, ob die Rückfälle
    /// greifen.
    /// </summary>
    private static int DumpSnapshot(int iterations)
    {
        using var collector = new Collector(new AppSettings());
        var done = new CountdownEvent(iterations);
        bool described = false;

        collector.SnapshotReady += snapshot =>
        {
            // Erst nach dem ersten Takt: Ob die CPU-Sensoren schweigen, steht
            // fest, sobald die Sensorbibliothek einmal gelesen wurde.
            if (!described)
            {
                described = true;
                Console.WriteLine($"Sensortreiber:         {collector.HardwareError ?? "geöffnet"}");
                Console.WriteLine($"CPU-Sensoren gesperrt: {collector.CpuSensorsBlocked}");
                Console.WriteLine($"Mainboard-Sensoren:    {(collector.BoardSensorsMissing ? "fehlen" : "vorhanden")}");
                Console.WriteLine($"Akku vorhanden:        {collector.HasBattery}");
                Console.WriteLine($"ACPI-Thermalzonen:     {collector.ThermalZonesAvailable}");
                Console.WriteLine($"Taktschätzung:         {collector.ClockEstimateAvailable}");
            }

            CpuMetrics cpu = snapshot.Cpu;
            string origin = cpu.TempOrigin switch
            {
                CpuTempOrigin.Die => "Die-Sensor des Prozessors",
                CpuTempOrigin.Socket => "Sockel, vom Mainboard",
                CpuTempOrigin.AcpiZone => "ACPI-Thermalzone (Ersatz)",
                _ => "keine Quelle",
            };

            Console.WriteLine();
            Console.WriteLine($"CPU   {cpu.TotalPercent,6:F1} %   {Format(cpu.PackageTempC, "°C")}   " +
                              $"{(cpu.ClockIsEstimated ? "≈" : " ")}{Format(cpu.ClockMhz, "MHz")}   {Format(cpu.PackagePowerW, "W")}");
            Console.WriteLine($"    Temperatur aus:  {origin}");
            Console.WriteLine($"    Takt:            {(cpu.ClockIsEstimated ? "aus dem Leistungszähler gerechnet" : "gemessen")}");
            Console.WriteLine($"    Temperaturen:    {snapshot.Energy.Temperatures.Count} " +
                              $"({string.Join(", ", snapshot.Energy.Temperatures.GroupBy(t => t.Source).Select(g => $"{g.Key}: {g.Count()}"))})");
            Console.WriteLine($"GPU   {snapshot.Gpu.TotalPercent,6:F1} %   {Format(snapshot.Gpu.TempC, "°C")}   " +
                              $"Speicher {snapshot.Gpu.MemUsedBytes / 1048576} / {snapshot.Gpu.MemTotalBytes / 1048576} MB");

            if (!done.IsSet)
                done.Signal();
        };

        collector.Start();

        if (!done.Wait(TimeSpan.FromSeconds(10 + (iterations * 2))))
            Console.Error.WriteLine("Zeitüberschreitung: der Collector hat nicht genug Takte geliefert.");

        collector.Stop();
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
    /// <summary>
    /// Die Startanalyse als Text. Der Reihe nach: was Windows selbst gemessen
    /// hat, die Kette der Autostart-Befehle mit ihren Dauern, die Befunde und
    /// zuletzt das vollständige Inventar.
    /// </summary>
    private static int DumpStartup()
    {
        StartupReport report = StartupAnalyzer.Analyze();

        Console.WriteLine($"Erhoben          {report.CollectedAt:dd.MM.yyyy HH:mm:ss}");
        Console.WriteLine($"Eingeschaltet    {Time(report.Boot.PowerOn)}  ({report.Boot.Kind})");
        Console.WriteLine($"Angemeldet       {Time(report.SessionStart)}");
        Console.WriteLine();

        if (report.Performance is { } performance)
        {
            Console.WriteLine("Startmessung von Windows");
            Console.WriteLine($"    Gesamt       {performance.BootSeconds,7:N1} s");
            Console.WriteLine($"    Hauptpfad    {performance.MainPathSeconds,7:N1} s");
            Console.WriteLine($"    Nachlauf     {performance.PostBootSeconds,7:N1} s");
            Console.WriteLine($"    Programme    {performance.StartupAppCount,7}");
            if (performance.Degraded)
                Console.WriteLine($"    Verschlechterung {performance.DegradationSeconds:N1} s gegenüber sonst");

            Console.WriteLine();
            foreach (BootPhase phase in performance.Phases)
                Console.WriteLine($"    {phase.Label,-22} {phase.Seconds,7:N2} s");
        }
        else
        {
            Console.WriteLine("Startmessung von Windows: keine (Protokoll gesperrt oder leer)");
        }

        Console.WriteLine();
        Console.WriteLine($"Startkette ({report.Chain.Count} Glieder)");
        foreach (ChainItem item in report.Chain.Where(i => i.Kind != ChainKind.LogonTask))
        {
            string duration = item.Duration is { } span ? $"{span.TotalSeconds,7:N2} s" : "      offen";
            Console.WriteLine($"    {item.Started:HH:mm:ss.fff} {duration}  [{item.Kind}] {item.Command}");
        }

        int tasks = report.Chain.Count(i => i.Kind == ChainKind.LogonTask);
        if (tasks > 0)
            Console.WriteLine($"    … dazu {tasks} Anmeldeaufgaben der Shell");

        Console.WriteLine();
        Console.WriteLine($"Befunde ({report.Findings.Count})");
        foreach (StartupFinding finding in report.Findings)
        {
            string cost = finding.CostSeconds is { } seconds ? $"{seconds,7:N1} s" : "       –";
            Console.WriteLine($"    [{finding.Severity,-6}] {cost}  {finding.Title}");
            Console.WriteLine($"                       {finding.Evidence}");
        }

        Console.WriteLine();
        Console.WriteLine($"Autostart-Einträge ({report.Entries.Count})");
        foreach (IGrouping<StartupSource, StartupEntry> group in report.Entries.GroupBy(e => e.Source))
        {
            Console.WriteLine($"  {group.Key} ({group.Count()})");
            foreach (StartupEntry entry in group.Take(20))
            {
                string state = entry.Enabled ? "an " : "aus";
                string duration = entry.Duration is { } span ? $"{span.TotalSeconds,6:N2} s" : "        ";
                string issues = entry.Issues == StartupIssue.None ? string.Empty : $"  ⚠ {entry.Issues}";
                Console.WriteLine($"    [{state}] {duration} {entry.Name,-34}{issues}");
            }

            if (group.Count() > 20)
                Console.WriteLine($"    … und {group.Count() - 20} weitere");
        }

        Console.WriteLine();
        Console.WriteLine("Einschränkungen");
        foreach (string note in report.Limitations)
            Console.WriteLine($"    • {note}");

        return 0;

        static string Time(DateTime? value) => value is { } when ? when.ToString("dd.MM.yyyy HH:mm:ss") : "unbekannt";
    }

    /// <summary>
    /// Wertet eine Startaufzeichnung aus. Ohne Argument die, die Windows bei
    /// jedem Hochfahren selbst anlegt — sie liegt in einem nur erhöht lesbaren
    /// Ordner.
    /// </summary>
    private static int DumpBootTrace(string? path)
    {
        string target = path ?? BootTraceAnalyzer.WindowsTracePath;
        Console.WriteLine($"Aufzeichnung: {target}");

        if (path is null && !BootTraceAnalyzer.WindowsTraceAvailable())
        {
            Console.Error.WriteLine("Nicht vorhanden oder nicht lesbar. Als Administrator ausführen.");
            return 1;
        }

        Console.WriteLine("Wird ausgewertet …");
        BootTraceSummary summary = BootTraceAnalyzer.Analyze(target);

        if (summary.Error is { } error)
        {
            Console.Error.WriteLine($"Fehlgeschlagen: {error}");
            return 1;
        }

        Console.WriteLine($"Sitzung ab:   {summary.When:dd.MM.yyyy HH:mm:ss}");
        Console.WriteLine($"Datei vom:    {summary.FileTime:dd.MM.yyyy HH:mm:ss}");
        Console.WriteLine($"Dauer:        {summary.DurationSeconds:N1} s");
        Console.WriteLine($"Abtastungen:  {summary.SampleCount:N0}");
        Console.WriteLine($"Prozesse:     {summary.Processes.Count}");

        if (!summary.FromLastBoot)
        {
            Console.WriteLine();
            Console.WriteLine(
                "ACHTUNG: Diese Aufzeichnung stammt NICHT vom letzten Start. Die Startdiagnose von");
            Console.WriteLine(
                "Windows läuft nicht bei jedem Hochfahren — die Zahlen unten zeigen einen früheren Start.");
        }

        if (!summary.HasCpuSamples)
        {
            Console.WriteLine();
            Console.WriteLine(
                "Ohne Profilablaufverfolgung: diese Aufzeichnung enthält keine CPU-Abtastungen.");
            Console.WriteLine(
                "Die Spalte „CPU“ bleibt deshalb leer — Datenträgerzugriffe und Startzeitpunkte stimmen.");
        }

        Console.WriteLine();
        Console.WriteLine($"{"Prozess",-34}{"CPU",10}{"Lesen",12}{"Schreiben",12}{"Zugriffe",10}{"Start",10}");

        foreach (TraceProcess process in summary.Processes.Take(30))
        {
            string start = process.StartOffsetMs is { } offset ? $"{offset / 1000:N1} s" : "vorher";
            Console.WriteLine(
                $"{Cut(process.Name, 32),-34}" +
                (summary.HasCpuSamples ? $"{process.CpuMs / 1000:N2} s" : "–").PadLeft(10) +
                $"{process.DiskReadBytes / 1048576.0:N1} MB".PadLeft(12) +
                $"{process.DiskWriteBytes / 1048576.0:N1} MB".PadLeft(12) +
                $"{process.DiskOperations:N0}".PadLeft(10) +
                start.PadLeft(10));
        }

        return 0;

        static string Cut(string value, int max) => value.Length <= max ? value : value[..(max - 1)] + "…";
    }

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

    /// <summary>
    /// Messbank für den Ordner-Scan. Beantwortet die drei Fragen, die über den
    /// Zuschnitt entscheiden: wie lange er dauert, wie viel Müll er erzeugt und
    /// wie groß die beschnittene Nutzlast wird.
    /// </summary>
    private static int DumpScan(string root)
    {
        if (!root.EndsWith('\\'))
            root += '\\';

        bool? seekPenalty = StorageDevice.HasSeekPenalty(root);
        string medium = seekPenalty switch
        {
            true => "Festplatte (Kopfbewegung)",
            false => "SSD",
            null => "unbekannt",
        };

        Console.WriteLine($"Durchlaufe {root} — Datenträger: {medium}");
        Console.WriteLine();

        long allocatedBefore = GC.GetTotalAllocatedBytes(precise: false);
        int gen2Before = GC.CollectionCount(2);

        var scanner = new FolderScanner();
        FolderScanResult result = scanner.Run(root, CancellationToken.None);

        long allocated = GC.GetTotalAllocatedBytes(precise: false) - allocatedBefore;
        int gen2 = GC.CollectionCount(2) - gen2Before;

        double seconds = result.Duration.TotalSeconds;
        long entries = result.DirectoryCount + (long)result.TotalFileCount;

        Console.WriteLine($"Dauer               {seconds,10:N2} s");
        Console.WriteLine($"Ordner              {result.DirectoryCount,10:N0}");
        Console.WriteLine($"Dateien             {result.TotalFileCount,10:N0}");
        Console.WriteLine($"Einträge/s          {(seconds > 0 ? entries / seconds : 0),10:N0}");
        Console.WriteLine($"Großdateien         {result.BigFileCount,10:N0}  (ab 16 MB, eigener Eintrag)");
        Console.WriteLine($"Nicht lesbar        {result.DeniedFolders,10:N0}  Ordner");
        Console.WriteLine($"Abzweigungen        {result.ReparsePoints,10:N0}  nicht verfolgt");
        Console.WriteLine();

        Console.WriteLine($"Summe               {Gib(result.TotalBytes),10} GiB");
        Console.WriteLine($"Cloud-Platzhalter   {Gib(result.CloudBytes),10} GiB  (liegen nicht auf dem Datenträger)");

        long volumeUsed = result.VolumeTotalBytes - result.VolumeFreeBytes;
        Console.WriteLine($"Windows meldet      {Gib(volumeUsed),10} GiB belegt von {Gib(result.VolumeTotalBytes)} GiB");

        // Die Differenz ist eine Auskunft, kein Fehler: harte Verknüpfungen
        // zählen doppelt (+), Cluster-Verschnitt, $MFT, verweigerte Teilbäume und
        // Schattenkopien fehlen (−).
        long delta = result.TotalBytes - volumeUsed;
        Console.WriteLine($"Differenz           {Gib(delta),10} GiB");
        Console.WriteLine();

        Console.WriteLine($"Zuweisungen         {allocated / 1048576.0,10:N0} MB   Gen-2-Sammlungen: {gen2}");
        Console.WriteLine();

        IReadOnlyList<FolderSlice> payload = result.Prune();
        string json = JsonSerializer.Serialize(payload);
        Console.WriteLine($"Nutzlast            {payload.Count,10:N0} Knoten, {json.Length / 1024.0:N0} KB JSON (unbeschnittene Schlüssel)");
        Console.WriteLine();

        Console.WriteLine("Die 30 größten Einträge der Nutzlast:");
        foreach (FolderSlice slice in payload
                     .OrderByDescending(entry => entry.TotalBytes)
                     .Take(30))
        {
            double percent = result.TotalBytes > 0 ? slice.TotalBytes * 100.0 / result.TotalBytes : 0;
            string mark = slice.IsFile ? "·" : slice.Flags == FolderFlags.None ? " " : "!";
            Console.WriteLine(
                $"    {Gib(slice.TotalBytes),8} GiB  {percent,5:N1} %  {mark} {Truncate(result.PathOf(slice.Id), 90)}");
        }

        DumpFindings(result);
        return 0;
    }

    /// <summary>
    /// Die Deutung des Scans. Hier steht, ob eine Regel überhaupt greift — ein
    /// Befund, der auf der Referenzmaschine ausbleibt, ist der Beleg dafür, dass
    /// der Pfad-Nachschlag ins Leere läuft.
    /// </summary>
    private static void DumpFindings(FolderScanResult result)
    {
        IReadOnlyList<StorageFinding> findings = StorageFindings.Collect(result);

        Console.WriteLine();
        Console.WriteLine($"Befunde: {findings.Count}");
        Console.WriteLine();

        foreach (StorageFinding finding in findings)
        {
            string amount = finding.Bytes is { } bytes ? $"{Gib(bytes),8} GiB" : $"{"—",12}";
            Console.WriteLine($"  [{finding.Severity,-6}] {amount}  {finding.Title}");

            if (finding.Path is { } path)
                Console.WriteLine($"                            Ort:       {Truncate(path, 80)}");

            if (finding.Evidence is { } evidence)
                Console.WriteLine($"                            Beleg:     {evidence}");

            for (int step = 0; step < finding.Commands.Count; step++)
            {
                string label = finding.Commands.Count > 1 ? $"Schritt {step + 1}:" : "Befehl:   ";
                Console.WriteLine($"                            {label} {finding.Commands[step]}");
            }

            // Der Vorbehalt ist der Teil, der beim Abschreiben als Erstes
            // wegfällt — deshalb steht er hier ungekürzt.
            if (finding.Caveat is { } caveat)
                Console.WriteLine($"                            Vorbehalt: {caveat}");

            Console.WriteLine();
        }
    }

    /// <summary>
    /// Das Programm-Inventar. Mit Laufwerksangabe läuft vorher ein voller Scan,
    /// damit die Größen aus dem Baum kommen; ohne sie misst jeder
    /// Installationsordner sich selbst.
    /// </summary>
    private static int DumpPrograms(string? root)
    {
        FolderScanResult? scan = null;

        if (root is not null)
        {
            if (!root.EndsWith('\\'))
                root += '\\';

            Console.WriteLine($"Durchlaufe {root} für die Größen …");
            scan = new FolderScanner().Run(root, CancellationToken.None);
            Console.WriteLine($"  {scan.DirectoryCount:N0} Ordner in {scan.Duration.TotalSeconds:N1} s");
            Console.WriteLine();
        }

        var watch = System.Diagnostics.Stopwatch.StartNew();
        ProgramReport report = ProgramInventory.Collect(scan, CancellationToken.None);
        watch.Stop();

        Console.WriteLine(
            $"Erhoben in {watch.Elapsed.TotalSeconds:N1} s — {report.Programs.Count} Programme " +
            $"aus {report.RawEntryCount} Registry-Einträgen");
        Console.WriteLine();

        int measured = report.Programs.Count(entry => entry.Bytes is not null);
        int used = report.Programs.Count(entry => entry.LastUsed is not null);
        Console.WriteLine($"Mit gemessener Größe:   {measured,4} von {report.Programs.Count}");
        Console.WriteLine($"Mit letzter Nutzung:    {used,4} von {report.Programs.Count}");
        Console.WriteLine();

        Console.WriteLine($"{"Größe",10}  {"Zuletzt",12}  {"Starts",6}  {"Quelle",-10}  Programm");
        foreach (ProgramEntry entry in report.Programs.Take(40))
        {
            string size = entry.Bytes is { } bytes ? $"{Gib(bytes)} GiB" : "—";
            string last = entry.LastUsed?.ToString("dd.MM.yyyy", CultureInfo.CurrentCulture) ?? "—";
            string count = entry.LaunchCount?.ToString(CultureInfo.CurrentCulture) ?? "—";
            string origin = entry.UsageFrom == UsageSource.None ? "—" : entry.UsageFrom.ToString();

            Console.WriteLine(
                $"{size,10}  {last,12}  {count,6}  {origin,-10}  {Truncate(entry.Name, 44)}");
        }

        Console.WriteLine();
        Console.WriteLine("Was die Zahlen einschränkt:");
        foreach (string limitation in report.Limitations)
            Console.WriteLine($"  — {limitation}");

        return 0;
    }

    private static string Gib(long bytes) => (bytes / 1073741824.0).ToString("N1", CultureInfo.CurrentCulture);

    private static string Format(double? value, string unit)
        => value is { } v ? $"{v,10:F1} {unit}" : $"{"—",10}";

    private static string Truncate(string value, int length)
        => value.Length <= length ? value : value[..(length - 1)] + "…";

    private static int Iterations(string[] args, int fallback)
        => args.Length > 1 && int.TryParse(args[1], out int n) && n > 0 ? n : fallback;
}

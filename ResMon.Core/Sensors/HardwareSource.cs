using LibreHardwareMonitor.Hardware;
using ResMon.Core.Diagnostics;
using ResMon.Core.Model;
using ResMon.Core.Native;

namespace ResMon.Core.Sensors;

/// <summary>
/// Messwerte aus LibreHardwareMonitor. Jeder Wert ist optional — welche Sensoren
/// existieren, ist hardwareabhängig; fehlende Felder werden im UI ausgeblendet.
/// </summary>
public sealed record HardwareReading(
    double? CpuPackageTempC,
    double? CpuClockMhz,
    double? CpuPackagePowerW,
    double? GpuTempC,
    double? GpuLoadPercent,
    double? GpuFanRpm,
    double? GpuPowerW,
    long GpuMemUsedBytes,
    long GpuMemTotalBytes,
    bool CpuSensorsBlocked)
{
    public static readonly HardwareReading Empty = new(null, null, null, null, null, null, null, 0, 0, false);

    /// <summary>Alle Leistungssensoren, für die Aufschlüsselung im Reiter „Energie".</summary>
    public IReadOnlyList<PowerRail> Rails { get; init; } = [];

    /// <summary>Alle Lüfter, quer über Mainboard und Grafikkarte.</summary>
    public IReadOnlyList<FanSample> Fans { get; init; } = [];

    /// <summary>Alle Temperatursensoren, quer über alle Hardwareklassen.</summary>
    public IReadOnlyList<TemperatureSample> Temperatures { get; init; } = [];

    /// <summary>Temperatur am CPU-Sockel, gemessen vom Super-I/O-Chip des Mainboards.</summary>
    public double? CpuSocketTempC { get; init; }

    /// <summary>
    /// False, wenn LibreHardwareMonitor keinen einzigen Sensor am Mainboard
    /// findet. Dann fehlen Sockeltemperatur und Gehäuselüfter — beides hängt am
    /// Super-I/O-Chip, den der Kernel-Treiber ansprechen muss.
    /// </summary>
    public bool BoardSensorsAvailable { get; init; }

    /// <summary>Akkuzustand; auf Desktop-Rechnern <c>null</c>.</summary>
    public BatteryMetrics? Battery { get; init; }
}

/// <summary>Ein einzelner Sensor, wie ihn das Probe-Werkzeug ausgibt.</summary>
public readonly record struct SensorInfo(string Hardware, string HardwareType, string SensorType, string Name, float? Value);

/// <summary>
/// Zugriff auf LibreHardwareMonitorLib (DESIGN.md §8.6). <see cref="Update"/> ist
/// teuer und gehört deshalb in einen eigenen, langsameren Takt.
/// </summary>
public sealed class HardwareSource : IDisposable
{
    /// <summary>Name im Reiter „Logs".</summary>
    private const string SensorSource = "Sensor-Treiber (LibreHardwareMonitor)";

    private readonly Computer _computer;
    private readonly UpdateVisitor _visitor = new();
    private bool _opened;

    public HardwareSource()
    {
        _computer = new Computer
        {
            IsCpuEnabled = true,
            IsGpuEnabled = true,
            IsMemoryEnabled = true,
            IsMotherboardEnabled = true,
            // Für den Reiter „Energie": Lüfter hängen am Super-I/O-Chip des
            // Mainboards, der Akku ist eine eigene Hardwareklasse.
            IsBatteryEnabled = true,
        };
    }

    /// <summary>Enthält die Fehlermeldung, falls <see cref="Open"/> fehlgeschlagen ist (z. B. fehlende Adminrechte).</summary>
    public string? OpenError { get; private set; }

    public bool IsOpen => _opened;

    public bool Open()
    {
        if (_opened)
            return true;

        try
        {
            _computer.Open();
            _opened = true;
            OpenError = null;
        }
        catch (Exception ex)
        {
            // Ohne Adminrechte lässt sich der Kernel-Treiber nicht laden. Die
            // Anwendung läuft dann ohne Temperaturen weiter, statt abzustürzen.
            OpenError = ex.Message;
            _opened = false;
            DiagnosticLog.Report(SensorSource, ex,
                "Die Sensorbibliothek ließ sich nicht öffnen — Temperaturen, Takt und Leistungsaufnahme fehlen",
                DiagnosticSeverity.Error);
        }

        return _opened;
    }

    public HardwareReading Update()
    {
        if (!_opened)
            return HardwareReading.Empty;

        try
        {
            _computer.Accept(_visitor);
        }
        catch (Exception ex)
        {
            OpenError = ex.Message;
            DiagnosticLog.Report(SensorSource, ex, "Sensoren konnten nicht aktualisiert werden", DiagnosticSeverity.Error);
            return HardwareReading.Empty;
        }

        double? cpuTemp = null, cpuClock = null, cpuPower = null;
        double? gpuLoad = null, gpuFan = null;
        double? gpuMemUsedMb = null, gpuMemUsedFallbackMb = null, gpuMemTotalMb = null;
        var coreTemps = new List<float>();
        var coreClocks = new List<float>();
        var gpuTemps = new List<(string Name, float Value)>();
        var gpuPowers = new List<(string Name, float Value)>();
        var rails = new List<PowerRail>();
        var fans = new List<FanSample>();
        var temperatures = new List<TemperatureSample>();

        foreach (IHardware hardware in _computer.Hardware)
        {
            // Leistung, Lüfter und Temperaturen hängen quer über alle
            // Hardwareklassen und teils erst in der Unterhardware — der
            // Super-I/O-Chip des Mainboards ist ein eigenes IHardware unterhalb
            // des Mainboards.
            CollectSensors(hardware, rails, fans, temperatures);

            switch (hardware.HardwareType)
            {
                case HardwareType.Cpu:
                    foreach (ISensor sensor in hardware.Sensors)
                    {
                        if (sensor.Value is not { } value)
                            continue;

                        switch (sensor.SensorType)
                        {
                            case SensorType.Temperature when IsPackageTemp(sensor.Name):
                                cpuTemp ??= value;
                                break;
                            case SensorType.Temperature when sensor.Name.StartsWith("CPU Core", StringComparison.OrdinalIgnoreCase):
                                coreTemps.Add(value);
                                break;
                            case SensorType.Power when sensor.Name.Contains("Package", StringComparison.OrdinalIgnoreCase):
                                cpuPower ??= value;
                                break;
                            case SensorType.Clock when sensor.Name.StartsWith("CPU Core", StringComparison.OrdinalIgnoreCase):
                                coreClocks.Add(value);
                                break;
                        }
                    }

                    break;

                case HardwareType.GpuNvidia:
                case HardwareType.GpuAmd:
                case HardwareType.GpuIntel:
                    foreach (ISensor sensor in hardware.Sensors)
                    {
                        if (sensor.Value is not { } value)
                            continue;

                        switch (sensor.SensorType)
                        {
                            case SensorType.Temperature:
                                gpuTemps.Add((sensor.Name, value));
                                break;
                            case SensorType.Load when sensor.Name.Contains("Core", StringComparison.OrdinalIgnoreCase):
                                gpuLoad ??= value;
                                break;
                            case SensorType.Fan:
                                gpuFan ??= value;
                                break;
                            case SensorType.Power:
                                gpuPowers.Add((sensor.Name, value));
                                break;
                            // Exakte Namen: NVIDIA meldet zusätzlich "D3D Dedicated
                            // Memory Used", das nur einen Teil des VRAM abdeckt.
                            case SensorType.SmallData when sensor.Name.Equals("GPU Memory Used", StringComparison.OrdinalIgnoreCase):
                                gpuMemUsedMb ??= value;
                                break;
                            case SensorType.SmallData when sensor.Name.Equals("GPU Memory Total", StringComparison.OrdinalIgnoreCase):
                                gpuMemTotalMb ??= value;
                                break;
                            case SensorType.SmallData when sensor.Name.Contains("Dedicated Memory Used", StringComparison.OrdinalIgnoreCase):
                                gpuMemUsedFallbackMb ??= value;
                                break;
                        }
                    }

                    break;
            }
        }

        // Ohne Package-Sensor ist der heißeste Kern die brauchbarste Näherung.
        cpuTemp ??= coreTemps.Count > 0 ? coreTemps.Max() : null;
        cpuClock ??= coreClocks.Count > 0 ? coreClocks.Max() : null;

        // "GPU Core" bzw. "GPU Package" bevorzugen, sonst den erstbesten Sensor —
        // die Reihenfolge innerhalb der Sensorliste ist nicht garantiert.
        double? gpuTemp = Prefer(gpuTemps, "Core");
        double? gpuPower = Prefer(gpuPowers, "Package");

        // Auf gesperrtem WinRing0 (Speicherintegrität, Sperrliste für verwundbare
        // Treiber) existieren die CPU-Sensoren, melden aber konstant 0. Eine
        // CPU-Temperatur von 0 °C oder ein Takt von 0 MHz ist physikalisch
        // unmöglich — das als Messwert anzuzeigen wäre gelogen.
        bool cpuBlocked = cpuTemp is <= 0 || cpuClock is <= 0;

        return new HardwareReading(
            NonZero(cpuTemp),
            NonZero(cpuClock),
            NonZero(cpuPower),
            gpuTemp,
            gpuLoad,
            gpuFan,
            gpuPower,
            ToBytes(gpuMemUsedMb ?? gpuMemUsedFallbackMb),
            ToBytes(gpuMemTotalMb),
            cpuBlocked)
        {
            Rails = rails,
            Fans = fans,
            Temperatures = temperatures,
            Battery = ReadBattery(),
            CpuSocketTempC = SocketTemperature(temperatures),
            BoardSensorsAvailable = HasBoardSensors(),
        };
    }

    /// <summary>
    /// Ob der Super-I/O-Chip des Mainboards überhaupt erreichbar ist. Er liefert
    /// Sockeltemperatur und Gehäuselüfter; ohne geladenen Kernel-Treiber taucht
    /// er in der Hardwareliste gar nicht erst auf.
    /// </summary>
    private bool HasBoardSensors()
    {
        foreach (IHardware hardware in _computer.Hardware)
        {
            if (hardware.HardwareType != HardwareType.Motherboard)
                continue;

            foreach (IHardware sub in hardware.SubHardware)
            {
                if (sub.Sensors.Length > 0)
                    return true;
            }

            if (hardware.Sensors.Length > 0)
                return true;
        }

        return false;
    }

    /// <summary>
    /// Die Sockeltemperatur kommt nicht aus dem Prozessor, sondern vom
    /// Mainboard: der Super-I/O-Chip misst am Sockel und nennt den Sensor je nach
    /// Hersteller „CPU", „CPU Socket" oder „CPU Package". Sensoren der
    /// CPU-Hardware selbst scheiden aus — die liefern die Die-Temperatur, die
    /// schon in der Kachel steht.
    /// </summary>
    private static double? SocketTemperature(List<TemperatureSample> temperatures)
    {
        foreach (TemperatureSample sample in temperatures)
        {
            if (sample.Source != TemperatureSource.Board)
                continue;

            if (sample.Name.Contains("CPU", StringComparison.OrdinalIgnoreCase)
                && !sample.Name.Contains("Fan", StringComparison.OrdinalIgnoreCase))
            {
                return sample.Celsius;
            }
        }

        return null;
    }

    /// <summary>
    /// Sammelt Leistungs-, Lüfter- und Temperatursensoren rekursiv ein. Ein
    /// Lüfter kann eine Drehzahl melden, eine Ansteuerung in Prozent oder beides;
    /// die beiden Sensoren tragen denselben Namen und gehören deshalb in eine
    /// Zeile.
    /// </summary>
    private static void CollectSensors(
        IHardware hardware, List<PowerRail> rails, List<FanSample> fans, List<TemperatureSample> temperatures)
    {
        var rpmByName = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        var percentByName = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);

        // Der Akku meldet seine Lade- und Entladeleistung ebenfalls als Power —
        // das ist keine Aufnahme einer Komponente und steht in seiner eigenen
        // Kachel.
        bool isBattery = hardware.HardwareType == HardwareType.Battery;
        TemperatureSource source = SourceOf(hardware.HardwareType);

        foreach (ISensor sensor in hardware.Sensors)
        {
            if (sensor.Value is not { } value)
                continue;

            switch (sensor.SensorType)
            {
                case SensorType.Power when value > 0 && !isBattery:
                    rails.Add(new PowerRail(hardware.Name, sensor.Name, value));
                    break;
                // 0 °C ist physikalisch möglich, aber praktisch immer ein
                // gesperrter Sensortreiber. Ihn anzuzeigen wäre gelogen.
                case SensorType.Temperature when value > 0:
                    temperatures.Add(new TemperatureSample(hardware.Name, sensor.Name, value, source));
                    break;
                // Ein stehender Lüfter meldet 0 und gehört trotzdem in die Liste —
                // "0 rpm" ist eine Aussage, ein fehlender Eintrag wäre keine.
                case SensorType.Fan:
                    rpmByName[sensor.Name] = value;
                    break;
                case SensorType.Control:
                    percentByName[sensor.Name] = value;
                    break;
            }
        }

        foreach ((string name, double rpm) in rpmByName)
            fans.Add(new FanSample(hardware.Name, name, rpm, Lookup(percentByName, name)));

        // Ansteuerungen ohne zugehörigen Drehzahlsensor — bei Notebooks häufig der
        // einzige Hinweis darauf, dass der Lüfter überhaupt läuft.
        foreach ((string name, double percent) in percentByName)
        {
            if (!rpmByName.ContainsKey(name))
                fans.Add(new FanSample(hardware.Name, name, null, percent));
        }

        foreach (IHardware sub in hardware.SubHardware)
            CollectSensors(sub, rails, fans, temperatures);
    }

    /// <summary>
    /// Der Super-I/O-Chip erscheint als eigene Hardware unterhalb des
    /// Mainboards; seine Sensoren zählen als Mainboard-Sensoren.
    /// </summary>
    private static TemperatureSource SourceOf(HardwareType type) => type switch
    {
        HardwareType.Cpu => TemperatureSource.Cpu,
        HardwareType.GpuNvidia or HardwareType.GpuAmd or HardwareType.GpuIntel => TemperatureSource.Gpu,
        HardwareType.Motherboard or HardwareType.SuperIO or HardwareType.EmbeddedController => TemperatureSource.Board,
        _ => TemperatureSource.Other,
    };

    private static double? Lookup(Dictionary<string, double> values, string name)
        => values.TryGetValue(name, out double value) ? value : null;

    /// <summary>
    /// Akkuzustand aus zwei Quellen: Ladestand, Netzbetrieb und Restlaufzeit
    /// kommen von Windows selbst und sind auf jedem Gerät verfügbar; Spannung,
    /// Lade- oder Entladeleistung und die Kapazitäten liefert der Akkusensor,
    /// sofern es ihn gibt. Fehlt beides, gibt es keinen Akku.
    /// </summary>
    private BatteryMetrics? ReadBattery()
    {
        SystemPower power = PowerStatus.Read();

        double? charge = null, rate = null, voltage = null;
        double? designed = null, full = null, remaining = null, degradation = null;
        bool found = false;

        foreach (IHardware hardware in _computer.Hardware)
        {
            if (hardware.HardwareType != HardwareType.Battery)
                continue;

            found = true;
            foreach (ISensor sensor in hardware.Sensors)
            {
                if (sensor.Value is not { } value)
                    continue;

                switch (sensor.SensorType)
                {
                    case SensorType.Level when sensor.Name.Contains("Degradation", StringComparison.OrdinalIgnoreCase):
                        degradation ??= value;
                        break;
                    case SensorType.Level:
                        charge ??= value;
                        break;
                    case SensorType.Voltage:
                        voltage ??= value;
                        break;
                    // Der Sensor ist vorzeichenbehaftet: positiv beim Laden,
                    // negativ beim Entladen.
                    case SensorType.Power:
                        rate ??= value;
                        break;
                    case SensorType.Energy when sensor.Name.Contains("Designed", StringComparison.OrdinalIgnoreCase):
                        designed ??= value;
                        break;
                    case SensorType.Energy when sensor.Name.Contains("Full", StringComparison.OrdinalIgnoreCase):
                        full ??= value;
                        break;
                    case SensorType.Energy:
                        remaining ??= value;
                        break;
                }
            }

            break;
        }

        if (!found && !power.HasBattery)
            return null;

        // Die Kapazitätssensoren melden Milliwattstunden.
        return new BatteryMetrics(
            charge ?? power.ChargePercent,
            power.OnAcPower,
            power.Charging,
            rate,
            voltage,
            ToWattHours(designed),
            ToWattHours(full),
            ToWattHours(remaining),
            degradation ?? Wear(designed, full),
            power.Remaining);
    }

    private static double? ToWattHours(double? milliWattHours)
        => milliWattHours is { } value && value > 0 ? value / 1000.0 : null;

    /// <summary>
    /// Verschleiß aus Soll- und Ist-Kapazität, falls der Akku ihn nicht selbst
    /// meldet: wie viel Prozent der ursprünglichen Ladung fehlen.
    /// </summary>
    private static double? Wear(double? designed, double? full)
        => designed is > 0 && full is > 0
            ? Math.Max(0, (1 - full.Value / designed.Value) * 100)
            : null;

    /// <summary>
    /// Listet alle gefundenen Sensoren auf. Grundlage für den Kontrollpunkt aus
    /// DESIGN.md §15 Schritt 2 — genutzt vom Probe-Werkzeug.
    /// </summary>
    public IReadOnlyList<SensorInfo> EnumerateSensors()
    {
        if (!_opened)
            return [];

        _computer.Accept(_visitor);

        var result = new List<SensorInfo>();
        foreach (IHardware hardware in _computer.Hardware)
            Collect(hardware, result);
        return result;
    }

    private static void Collect(IHardware hardware, List<SensorInfo> sink)
    {
        foreach (ISensor sensor in hardware.Sensors)
        {
            sink.Add(new SensorInfo(
                hardware.Name,
                hardware.HardwareType.ToString(),
                sensor.SensorType.ToString(),
                sensor.Name,
                sensor.Value));
        }

        foreach (IHardware sub in hardware.SubHardware)
            Collect(sub, sink);
    }

    private static double? Prefer(List<(string Name, float Value)> candidates, string keyword)
    {
        if (candidates.Count == 0)
            return null;

        foreach ((string name, float value) in candidates)
        {
            if (name.Contains(keyword, StringComparison.OrdinalIgnoreCase))
                return value;
        }

        return candidates[0].Value;
    }

    private static bool IsPackageTemp(string name)
        => name.Contains("Package", StringComparison.OrdinalIgnoreCase)
           || name.Contains("Tdie", StringComparison.OrdinalIgnoreCase)
           || name.Equals("Core (Tctl/Tdie)", StringComparison.OrdinalIgnoreCase);

    /// <summary>Blendet Nullwerte aus — bei CPU-Sensoren bedeuten sie „kein Zugriff", nicht „null".</summary>
    private static double? NonZero(double? value) => value is > 0 ? value : null;

    private static long ToBytes(double? megabytes)
        => megabytes is { } mb && mb > 0 ? (long)(mb * 1024 * 1024) : 0;

    public void Dispose()
    {
        if (!_opened)
            return;

        try
        {
            _computer.Close();
        }
        catch
        {
            // Beim Herunterfahren ist ein fehlgeschlagenes Entladen des Treibers
            // nicht mehr behebbar und darf das Beenden nicht blockieren.
        }

        _opened = false;
    }

    private sealed class UpdateVisitor : IVisitor
    {
        public void VisitComputer(IComputer computer) => computer.Traverse(this);

        public void VisitHardware(IHardware hardware)
        {
            hardware.Update();
            foreach (IHardware sub in hardware.SubHardware)
                sub.Accept(this);
        }

        public void VisitSensor(ISensor sensor) { }

        public void VisitParameter(IParameter parameter) { }
    }
}

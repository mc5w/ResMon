using LibreHardwareMonitor.Hardware;

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
}

/// <summary>Ein einzelner Sensor, wie ihn das Probe-Werkzeug ausgibt.</summary>
public readonly record struct SensorInfo(string Hardware, string HardwareType, string SensorType, string Name, float? Value);

/// <summary>
/// Zugriff auf LibreHardwareMonitorLib (DESIGN.md §8.6). <see cref="Update"/> ist
/// teuer und gehört deshalb in einen eigenen, langsameren Takt.
/// </summary>
public sealed class HardwareSource : IDisposable
{
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
            return HardwareReading.Empty;
        }

        double? cpuTemp = null, cpuClock = null, cpuPower = null;
        double? gpuLoad = null, gpuFan = null;
        double? gpuMemUsedMb = null, gpuMemUsedFallbackMb = null, gpuMemTotalMb = null;
        var coreTemps = new List<float>();
        var coreClocks = new List<float>();
        var gpuTemps = new List<(string Name, float Value)>();
        var gpuPowers = new List<(string Name, float Value)>();

        foreach (IHardware hardware in _computer.Hardware)
        {
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
            cpuBlocked);
    }

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

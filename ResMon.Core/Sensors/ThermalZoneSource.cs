using ResMon.Core.Model;
using ResMon.Core.Native;

namespace ResMon.Core.Sensors;

/// <summary>Alle belegten Thermalzonen eines Takts, samt der für die CPU zuständigen.</summary>
public sealed record ThermalZoneReading(IReadOnlyList<TemperatureSample> Zones, double? CpuZoneTempC)
{
    public static readonly ThermalZoneReading Empty = new([], null);
}

/// <summary>
/// Temperaturen aus den ACPI-Thermalzonen über den PDH-Zählersatz
/// <c>Thermal Zone Information</c>. Die Werte kommen aus der Firmware und gehen
/// über den ACPI-Treiber von Windows: kein eigener Kernel-Treiber, keine
/// Adminrechte. Damit sind sie genau dort noch da, wo LibreHardwareMonitor
/// aufgibt — bei gesperrtem WinRing0 und auf Notebooks, deren Lüfter- und
/// Temperaturwerte am Embedded Controller hängen statt an einem lesbaren
/// Super-I/O-Chip.
/// </summary>
/// <remarks>
/// Eine Zone ist kein Sensor am Die. Sie misst dort, wo die Firmware ihre
/// Kühlgrenzen festmacht, mittelt über die Umgebung und reagiert träger. Als
/// Ersatz für eine fehlende Paket-Temperatur taugt sie, als Messwert für
/// Lastspitzen nicht.
/// </remarks>
public sealed class ThermalZoneSource
{
    private const string HighPrecisionPath = @"\Thermal Zone Information(*)\High Precision Temperature";
    private const string WholeKelvinPath = @"\Thermal Zone Information(*)\Temperature";

    /// <summary>Nicht belegte Zonen melden exakt 273,2 K. Alles darunter ist keine Messung.</summary>
    private const double MinimumCelsius = 5;

    /// <summary>Darüber hat jede Firmware längst abgeschaltet — dann ist der Zähler unbrauchbar.</summary>
    private const double MaximumCelsius = 130;

    private readonly PdhCounter? _counter;
    private readonly double _kelvinPerCount;

    public ThermalZoneSource(PdhQuery query)
    {
        // Der hochauflösende Zähler zählt in Zehntel-Kelvin; der ganzzahlige ist
        // der Rückfall für Systeme, die ihn nicht führen.
        _counter = query.TryAddCounter(HighPrecisionPath);
        _kelvinPerCount = 0.1;

        if (_counter is null)
        {
            _counter = query.TryAddCounter(WholeKelvinPath);
            _kelvinPerCount = 1;
        }
    }

    /// <summary>False, wenn das System den Zählersatz gar nicht führt.</summary>
    public bool Available => _counter is not null;

    public ThermalZoneReading Read()
    {
        if (_counter is null)
            return ThermalZoneReading.Empty;

        IReadOnlyList<PdhInstanceValue> values = _counter.ReadArrayDouble();
        if (values.Count == 0)
            return ThermalZoneReading.Empty;

        var zones = new List<TemperatureSample>(values.Count);
        double? cpu = null;

        foreach (PdhInstanceValue value in values)
        {
            double celsius = (value.Value * _kelvinPerCount) - 273.15;
            if (celsius is < MinimumCelsius or > MaximumCelsius)
                continue;

            string zone = ZoneName(value.Instance);
            zones.Add(new TemperatureSample($"ACPI {value.Instance}", Label(zone), celsius, TemperatureSource.Acpi));

            if (cpu is null && zone.StartsWith("CPU", StringComparison.OrdinalIgnoreCase))
                cpu = celsius;
        }

        // PDH gibt die Instanzen nicht in fester Reihenfolge zurück; ohne
        // Sortierung springen die Zeilen im Sekundentakt umeinander.
        zones.Sort((left, right) => string.CompareOrdinal(left.Hardware, right.Hardware));
        return new ThermalZoneReading(zones, cpu);
    }

    /// <summary>
    /// Der Instanzname ist ein ACPI-Pfad wie <c>\_TZ.CPUZ</c> oder
    /// <c>\_SB.PCI0.LPCB.EC0.HEPZ</c>. Interessant ist nur sein letztes Glied.
    /// </summary>
    private static string ZoneName(string instance)
    {
        int dot = instance.LastIndexOf('.');
        return dot >= 0 && dot < instance.Length - 1 ? instance[(dot + 1)..] : instance;
    }

    /// <summary>
    /// Zonennamen sind vier Zeichen lang und vom Hersteller vergeben. Übersetzt
    /// wird nur, was eindeutig ist; alles andere behält seinen Bezeichner, weil
    /// ein geratener Name schlechter ist als ein roher.
    /// </summary>
    private static string Label(string zone) => zone.ToUpperInvariant() switch
    {
        "CPUZ" or "CPU" => "Prozessor",
        "GFXZ" or "GPUZ" => "Grafik",
        "PCHZ" => "Chipsatz",
        "BATZ" => "Akku",
        "SKIN" => "Gehäuseoberfläche",
        _ => zone,
    };
}

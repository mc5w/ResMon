namespace ResMon.Core.Startup;

/// <summary>
/// Windows' eigene Startmessung aus
/// <c>Microsoft-Windows-Diagnostics-Performance/Operational</c>.
/// </summary>
/// <remarks>
/// Windows misst jeden Start selbst und schreibt in Ereignis 100 die vollständige
/// Aufteilung — Kernel, Treiber, Geräte, Dienste, Profil, Explorer, Nachlauf — in
/// Millisekunden. Daneben stehen Einzelbefunde: 101 nennt eine Anwendung, die
/// länger als üblich brauchte, 103 einen Dienst, der den Start verzögert hat, und
/// so fort. Jeder trägt neben der Gesamtzeit die <c>DegradationTime</c>, also den
/// Anteil, den Windows gegenüber dem gewohnten Verlauf als Verschlechterung
/// wertet.
/// <para>
/// Das Protokoll ist zugriffsgeschützt: unerhöht wirft schon das Öffnen eine
/// <c>UnauthorizedAccessException</c>. Die Anwendung läuft erhöht (DESIGN.md
/// §14); trifft sie den Fall dennoch an, wird das als Einschränkung gemeldet
/// statt als leerer Abschnitt.
/// </para>
/// </remarks>
public static class BootPerformanceReader
{
    public const string LogName = "Microsoft-Windows-Diagnostics-Performance/Operational";

    private const string BootQuery = "*[System[(EventID=100)]]";

    private const string DegradationQuery =
        "*[System[(EventID=101 or EventID=102 or EventID=103 or EventID=106 or EventID=109)]]";

    /// <summary>Die jüngste Startmessung, oder <c>null</c>, wenn keine vorliegt.</summary>
    public static BootPerformance? ReadLatest()
    {
        List<RawEvent> events = StartupEvents.Read(
            LogName, BootQuery, limit: 1, source: "Startmessung (Diagnostics-Performance)");

        if (events.Count == 0)
            return null;

        RawEvent record = events[0];
        double total = record.Seconds("BootTime") ?? 0;
        double mainPath = record.Seconds("MainPathBootTime") ?? 0;
        double postBoot = record.Seconds("BootPostBootTime") ?? 0;

        if (total <= 0 && mainPath <= 0)
            return null;

        return new BootPerformance(record.Time, total, mainPath, postBoot)
        {
            StartupAppCount = record.Number("BootNumStartupApps") ?? 0,
            Degraded = record.Field("BootIsDegradation") is "true" or "1",
            DegradationSeconds = record.Seconds("BootDegradationTime") ?? 0,
            Phases = BuildPhases(record, mainPath, postBoot),
        };
    }

    /// <summary>
    /// Die Abschnitte in der Reihenfolge, in der sie ablaufen. Abschnitte ohne
    /// Wert fallen heraus — nicht jede Windows-Fassung schreibt alle Felder, und
    /// eine Null-Kachel im Band sähe aus wie eine gemessene Null.
    /// </summary>
    private static IReadOnlyList<BootPhase> BuildPhases(RawEvent record, double mainPath, double postBoot)
    {
        (string Key, string Label, string Field)[] definitions =
        [
            ("kernel", "Kernel-Init", "BootKernelInitTime"),
            ("drivers", "Treiber", "BootDriverInitTime"),
            ("devices", "Geräte", "BootDevicesInitTime"),
            ("prefetch", "Prefetch", "BootPrefetchInitTime"),
            ("smss", "Sitzungsmanager", "BootSmssInitTime"),
            ("services", "Kritische Dienste", "BootCriticalServicesInitTime"),
            ("machineProfile", "Maschinenprofil", "BootMachineProfileProcessingTime"),
            ("userProfile", "Benutzerprofil", "BootUserProfileProcessingTime"),
            ("explorer", "Explorer", "BootExplorerInitTime"),
        ];

        var phases = new List<BootPhase>(definitions.Length + 2);
        double measured = 0;

        foreach ((string key, string label, string field) in definitions)
        {
            if (record.Seconds(field) is { } seconds && seconds > 0.001)
            {
                phases.Add(new BootPhase(key, label, seconds));
                measured += seconds;
            }
        }

        // Die Einzelabschnitte decken den Hauptpfad nicht vollständig ab; was
        // übrig bleibt, muss als eigener Posten sichtbar sein. Ohne ihn ergäbe
        // die Summe der Balken weniger als die Kachel darüber, und das Band
        // behauptete eine Vollständigkeit, die es nicht hat.
        double rest = mainPath - measured;
        if (rest > 0.05)
            phases.Add(new BootPhase("other", "Übriger Hauptpfad", rest));

        if (postBoot > 0.001)
            phases.Add(new BootPhase("postBoot", "Nachlauf (Autostart)", postBoot));

        return phases;
    }

    /// <summary>
    /// Die Einzelbefunde der letzten Starts als fertige Meldungen. Der Leser
    /// formuliert sie selbst, weil nur er weiß, was die Ereigniskennung bedeutet.
    /// </summary>
    /// <param name="notBefore">Nur Befunde ab diesem Zeitpunkt.</param>
    public static IReadOnlyList<StartupFinding> ReadDegradations(DateTime? notBefore)
    {
        List<RawEvent> events = StartupEvents.Read(
            LogName, DegradationQuery, notBefore, limit: 60,
            source: "Startmessung (Diagnostics-Performance)");

        var findings = new List<StartupFinding>();

        foreach (RawEvent record in events)
        {
            string name = record.Field("Name") ?? record.Field("FriendlyName") ?? "unbenannt";
            double total = record.Seconds("TotalTime") ?? 0;
            double degradation = record.Seconds("DegradationTime") ?? 0;

            // Windows schreibt diese Ereignisse ab einer eigenen Schwelle. Was
            // darunter bleibt, hat es selbst nicht für berichtenswert gehalten —
            // das hier noch einmal zu unterbieten hieße, die Liste mit Rauschen
            // zu füllen.
            if (total < 0.5)
                continue;

            (string title, string why) = Describe(record.Id, name, degradation);

            findings.Add(new StartupFinding(
                total >= 10 ? FindingSeverity.High : FindingSeverity.Medium, title, why)
            {
                CostSeconds = total,
                When = record.Time,
                Evidence = $"Startmessung, Ereignis {record.Id}",
            });
        }

        return findings;
    }

    private static (string Title, string Why) Describe(int id, string name, double degradation)
    {
        string slower = degradation >= 0.5
            ? $" Davon {degradation:0.#} s langsamer als bei den vorherigen Starts."
            : string.Empty;

        return id switch
        {
            101 => ($"Anwendung „{name}“ brauchte länger als üblich zum Starten",
                    "Windows hat den Eintrag selbst als auffällig vermerkt." + slower),
            102 => ($"Treiber „{name}“ brauchte länger zum Initialisieren",
                    "Ein Treiber im Hauptpfad hält den Start unmittelbar auf — hier hilft nur ein Treiberwechsel oder das Abschalten des Geräts." + slower),
            103 => ($"Dienst „{name}“ hat den Systemstart verzögert",
                    "Der Dienst läuft im Hauptpfad und wird abgewartet. Ein Starttyp „Automatisch (verzögert)“ nähme ihn aus dem Weg." + slower),
            106 => ($"Hintergrundoptimierung „{name}“ lief in den Start hinein",
                    "Prefetch- oder Wartungsarbeiten, die noch nicht durch waren." + slower),
            109 => ($"Gerät „{name}“ brauchte länger zum Initialisieren",
                    "Windows wartet beim Start auf die Geräteanmeldung." + slower),
            _ => ($"„{name}“ hat den Start verzögert", "Aus der Startmessung von Windows." + slower),
        };
    }
}

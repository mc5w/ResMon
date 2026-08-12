using System.IO;
using ResMon.Core.Inventory;

namespace ResMon.Core.Startup;

/// <summary>
/// Führt die Startanalyse zusammen: Inventar, gemessene Kette, Windows' eigene
/// Startmessung und die Befunde.
/// </summary>
/// <remarks>
/// Läuft ausschließlich auf Anforderung und blockiert dabei mehrere hundert
/// Millisekunden bis zu einigen Sekunden — Ereignisprotokolle sind teuer, und ihr
/// Inhalt ändert sich zwischen zwei Systemstarts ohnehin nicht. Ein Takt wäre
/// hier reine Last ohne neue Erkenntnis (DESIGN.md §9).
/// </remarks>
public static class StartupAnalyzer
{
    /// <summary>
    /// Wie weit vor dem Anmeldezeitpunkt Befunde noch mitgenommen werden. Was den
    /// Start aufhält, passiert teilweise <b>vor</b> der Anmeldung — Dienste
    /// starten, während der Anmeldebildschirm noch steht, und ein Zeitlimit von
    /// 90 Sekunden liegt dann komplett davor.
    /// </summary>
    private static readonly TimeSpan PreLogonWindow = TimeSpan.FromMinutes(10);

    public static StartupReport Analyze()
    {
        BootRecord boot = BootHistory.Read();
        DateTime? session = ReadSessionStart();

        IReadOnlyList<ChainItem> chain = BootChain.Read(session?.Add(-PreLogonWindow));
        IReadOnlyList<StartupEntry> entries = StartupInventory.Collect();

        // Der Bezugspunkt für die Kette ist ihr eigener Anfang, nicht die
        // Anmeldung: zwischen Anmeldung und dem ersten Autostart-Befehl liegen
        // Profilverarbeitung und Shell-Start, und ein Balkendiagramm, das mit
        // zwanzig Sekunden Leerraum beginnt, ist nicht zu lesen.
        DateTime? chainStart = chain.Count > 0 ? chain.Min(item => item.Started) : null;

        entries = Attach(entries, chain, chainStart);

        IReadOnlyList<StartupFinding> findings =
            StartupFindings.Collect(session?.Add(-PreLogonWindow), entries, chain);

        return new StartupReport(DateTime.Now)
        {
            Boot = boot,
            SessionStart = session,
            Performance = BootPerformanceReader.ReadLatest(),
            Chain = chain,
            Entries = entries,
            Findings = findings,
            Limitations = Limitations(boot, session, chain),
        };
    }

    /// <summary>
    /// Der Beginn der letzten Anmeldesitzung, aus der Anmeldebenachrichtigung von
    /// Winlogon (System-Protokoll, Ereignis 7001).
    /// </summary>
    /// <remarks>
    /// Bewusst nicht der Einschaltzeitpunkt: bei einem Rechner, der aus dem
    /// Ruhezustand kommt oder an dem sich jemand ab- und wieder angemeldet hat,
    /// liegen zwischen beiden Stunden bis Tage. Die Autostart-Kette hängt an der
    /// Anmeldung, nicht am Einschalten — dieselbe Unterscheidung, die schon die
    /// Laufzeitangabe im Reiter „System“ trifft (DESIGN.md §8.9).
    /// </remarks>
    private static DateTime? ReadSessionStart()
    {
        List<RawEvent> events = StartupEvents.Read(
            "System",
            "*[System[Provider[@Name='Microsoft-Windows-Winlogon'] and (EventID=7001)]]",
            limit: 1,
            source: "Startanalyse");

        return events.Count > 0 ? events[0].Time : null;
    }

    /// <summary>
    /// Verbindet die Einträge des Inventars mit dem, was gemessen wurde: Dauer,
    /// Abstand zum Kettenbeginn und die vergebene Prozesskennung.
    /// </summary>
    /// <remarks>
    /// Der Explorer protokolliert den Befehl so, wie er im Run-Schlüssel steht,
    /// aber ohne das Verzeichnis — aus
    /// <c>"C:\Program Files\…\iCUE Launcher.exe" --autorun</c> wird
    /// <c>iCUE Launcher.exe" --autorun</c>, mitsamt dem übrig gebliebenen
    /// Anführungszeichen. Verglichen wird deshalb über den Dateinamen, den
    /// <see cref="ExecutableName"/> aus beiden Seiten herauslöst.
    /// </remarks>
    private static IReadOnlyList<StartupEntry> Attach(
        IReadOnlyList<StartupEntry> entries, IReadOnlyList<ChainItem> chain, DateTime? chainStart)
    {
        if (chain.Count == 0)
            return entries;

        // Je Dateiname die gemessenen Glieder. Mehrere gleichnamige Einträge
        // kommen vor — dann bekommt jeder eines, in der Reihenfolge des Ablaufs.
        var byName = new Dictionary<string, Queue<ChainItem>>(StringComparer.OrdinalIgnoreCase);

        foreach (ChainItem item in chain.Where(i => i.Kind != ChainKind.LogonTask).OrderBy(i => i.Started))
        {
            string name = ExecutableName(item.Command);
            if (name.Length == 0)
                continue;

            if (!byName.TryGetValue(name, out Queue<ChainItem>? queue))
                byName[name] = queue = new Queue<ChainItem>();
            queue.Enqueue(item);
        }

        var result = new List<StartupEntry>(entries.Count);

        foreach (StartupEntry entry in entries)
        {
            string name = entry.ImagePath is { Length: > 0 } path
                ? Path.GetFileName(path)
                : ExecutableName(entry.Command);

            if (name.Length == 0
                || !byName.TryGetValue(name, out Queue<ChainItem>? queue)
                || queue.Count == 0)
            {
                result.Add(entry);
                continue;
            }

            ChainItem measured = queue.Dequeue();
            StartupIssue issues = entry.Issues;
            if (measured.Duration is { TotalSeconds: >= 2 })
                issues |= StartupIssue.SlowStart;

            result.Add(entry with
            {
                Pid = measured.Pid ?? entry.Pid,
                Duration = measured.Duration,
                Offset = chainStart is { } start ? measured.Started - start : null,
                Issues = issues,
            });
        }

        return result;
    }

    /// <summary>
    /// Löst den Dateinamen der ausführbaren Datei aus einer Befehlszeile, gleich
    /// ob sie vollständig ist oder so verkürzt, wie der Explorer sie protokolliert.
    /// </summary>
    private static string ExecutableName(string command)
    {
        if (string.IsNullOrWhiteSpace(command))
            return string.Empty;

        string text = command.Trim().TrimStart('"');

        // Die Endung ist der verlässliche Anker: sie überlebt sowohl das
        // abgeschnittene Verzeichnis als auch Dateinamen mit Leerzeichen, an
        // denen eine Trennung am ersten Leerzeichen scheitern würde.
        int cut = text.IndexOf(".exe", StringComparison.OrdinalIgnoreCase);
        if (cut >= 0)
            return Path.GetFileName(text[..(cut + 4)]);

        int stop = text.IndexOfAny(['"', ' ']);
        return Path.GetFileName(stop > 0 ? text[..stop] : text);
    }

    /// <summary>
    /// Was die Zahlen einschränkt. Ohne diese Angaben wären sie falsch zu lesen —
    /// dieselbe Regel wie bei der Ordnerbelegung (DESIGN.md §13.5).
    /// </summary>
    private static IReadOnlyList<string> Limitations(
        BootRecord boot, DateTime? session, IReadOnlyList<ChainItem> chain)
    {
        var notes = new List<string>();

        if (!StartupEvents.CanRead(BootPerformanceReader.LogName))
        {
            notes.Add(
                "Windows' eigene Startmessung ist nicht lesbar — das Protokoll " +
                "„Diagnostics-Performance“ ist zugriffsgeschützt und verlangt erhöhte Rechte. " +
                "Ohne sie fehlen die Startdauer und die Aufteilung in Phasen; die Startkette " +
                "darunter ist davon nicht betroffen.");
        }

        if (!StartupEvents.CanRead("Microsoft-Windows-GroupPolicy/Operational"))
        {
            notes.Add(
                "Das Gruppenrichtlinien-Protokoll ist nicht lesbar. Auf einem Rechner im " +
                "Firmennetz fehlt damit einer der häufigsten Verzögerer.");
        }

        if (chain.Count == 0)
        {
            notes.Add(
                "Die Startkette ist leer. Entweder ist das Protokoll " +
                "„Shell-Core/Operational“ abgeschaltet, oder seit dem letzten Anmelden " +
                "sind so viele Ereignisse aufgelaufen, dass die Starteinträge herausgerollt sind.");
        }

        if (session is null)
        {
            notes.Add(
                "Der Anmeldezeitpunkt ließ sich nicht bestimmen; die Zeiten beziehen sich " +
                "auf das erste gemessene Glied der Kette statt auf die Anmeldung.");
        }

        if (boot.Kind == BootKind.Hybrid)
        {
            notes.Add(
                "Der letzte Start war ein Schnellstart: Windows hat die Kernelsitzung aus " +
                "„hiberfil.sys“ zurückgeladen, statt sie neu aufzubauen. Treiber- und " +
                "Dienstphasen fallen dadurch kürzer aus als bei einem echten Kaltstart — " +
                "ein Vergleich mit einem Rechner ohne Schnellstart führt in die Irre.");
        }

        notes.Add(
            "Was ein einzelner Autostart-Eintrag an CPU-Zeit und Datenträgerzugriffen " +
            "gekostet hat, steht in keinem Protokoll. Dafür braucht es eine " +
            "Ablaufverfolgung, die vor dem Neustart scharfgestellt wird — die Schaltfläche " +
            "„Startaufzeichnung“ richtet genau das ein.");

        return notes;
    }
}

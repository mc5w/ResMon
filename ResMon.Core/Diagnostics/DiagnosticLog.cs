namespace ResMon.Core.Diagnostics;

/// <summary>Wie schwer ein Eintrag wiegt — bestimmt allein die Darstellung.</summary>
public enum DiagnosticSeverity
{
    /// <summary>Belanglos für die Messung, aber erwähnenswert.</summary>
    Info,

    /// <summary>Ein Teil der Anzeige bleibt leer, der Rest läuft weiter.</summary>
    Warning,

    /// <summary>Eine ganze Datenquelle fällt aus.</summary>
    Error,
}

/// <summary>
/// Ein Eintrag im Protokoll. <paramref name="Count"/> zählt, wie oft derselbe
/// Fehler seit <paramref name="First"/> aufgetreten ist — ein Fehler, der im
/// Sekundentakt wiederkehrt, soll die Liste nicht fluten.
/// </summary>
public sealed record DiagnosticEntry(
    string Source,
    string Message,
    DiagnosticSeverity Severity,
    DateTime First,
    DateTime Last,
    int Count);

/// <summary>
/// Sammelstelle für alles, was beim Erfassen schiefgeht. Die Datenquellen
/// fangen ihre Ausnahmen bewusst ab — eine gesperrte Registry, ein
/// abgeschaltetes Ereignisprotokoll oder ein fehlender WMI-Anbieter darf die
/// Anwendung nicht anhalten. Bis hierher war der Preis dafür, dass niemand
/// erfährt, <em>warum</em> eine Angabe fehlt; genau das steht jetzt im Reiter
/// „Logs".
/// </summary>
/// <remarks>
/// Statisch und ohne Konfiguration, weil die Melder quer über alle Schichten
/// liegen und ihre Fehler nicht durch die halbe Aufrufkette reichen sollen.
/// Gleiche Meldung derselben Quelle wird zusammengefasst; die Liste ist auf
/// <see cref="MaxEntries"/> begrenzt, damit sie auch bei einem sich ständig
/// ändernden Fehlertext nicht unbegrenzt wächst.
/// </remarks>
public static class DiagnosticLog
{
    private const int MaxEntries = 200;

    private static readonly Lock Gate = new();
    private static readonly Dictionary<(string Source, string Message), DiagnosticEntry> Entries = [];

    /// <summary>
    /// Zählt jede Änderung. Das Detailfenster schickt die Liste nur weiter, wenn
    /// sich der Stand seit dem letzten Takt bewegt hat.
    /// </summary>
    public static int Version { get; private set; }

    public static void Report(string source, string message, DiagnosticSeverity severity = DiagnosticSeverity.Warning)
    {
        if (string.IsNullOrWhiteSpace(message))
            return;

        var key = (source, message);
        var now = DateTime.Now;

        lock (Gate)
        {
            if (Entries.TryGetValue(key, out DiagnosticEntry? known))
            {
                Entries[key] = known with { Last = now, Count = known.Count + 1 };
            }
            else
            {
                if (Entries.Count >= MaxEntries)
                    return;

                Entries[key] = new DiagnosticEntry(source, message, severity, now, now, 1);
            }

            Version++;
        }
    }

    /// <summary>
    /// Meldet eine gefangene Ausnahme. Der Typname steht dabei: „Zugriff
    /// verweigert" allein sagt nicht, wer den Zugriff verweigert hat.
    /// </summary>
    public static void Report(string source, Exception exception, string what,
        DiagnosticSeverity severity = DiagnosticSeverity.Warning)
        => Report(source, $"{what}: {exception.GetType().Name} — {exception.Message}", severity);

    /// <summary>
    /// Nimmt eine Meldung zurück, wenn die Quelle wieder liefert. Ohne das bliebe
    /// ein Fehler stehen, den es längst nicht mehr gibt.
    /// </summary>
    public static void Clear(string source)
    {
        lock (Gate)
        {
            List<(string, string)> stale = Entries.Keys.Where(key => key.Source == source).ToList();
            if (stale.Count == 0)
                return;

            foreach ((string, string) key in stale)
                Entries.Remove(key);

            Version++;
        }
    }

    /// <summary>Der aktuelle Stand, jüngste Meldung zuerst.</summary>
    public static IReadOnlyList<DiagnosticEntry> Snapshot()
    {
        lock (Gate)
            return Entries.Values.OrderByDescending(entry => entry.Last).ToList();
    }
}

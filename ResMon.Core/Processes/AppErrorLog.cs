using System.Diagnostics.Eventing.Reader;
using ResMon.Core.Diagnostics;

namespace ResMon.Core.Processes;

/// <summary>Ein gemeldeter Anwendungsfehler, wie ihn die Prozesstabelle anzeigt.</summary>
public sealed record AppFault(string Executable, DateTime When, bool IsHang)
{
    public string Summary => IsHang
        ? $"reagierte nicht mehr ({When:HH:mm})"
        : $"abgestürzt ({When:HH:mm})";
}

/// <summary>
/// Liest die Abstürze und Hänger der letzten Stunden aus dem Anwendungsprotokoll.
/// Damit steht in der Prozesstabelle nicht nur, was gerade hängt, sondern auch,
/// was kurz zuvor weggebrochen ist — genau die Fälle, die man beim Nachsehen im
/// Task-Manager schon verpasst hat.
/// </summary>
/// <remarks>
/// Die Zuordnung läuft über den Dateinamen, nicht über die PID: der abgestürzte
/// Prozess ist zum Zeitpunkt des Eintrags längst weg, und ein neu gestarteter
/// trägt eine andere Kennung. Ein Eintrag gilt deshalb für alle Prozesse
/// desselben Namens.
/// </remarks>
public sealed class AppErrorLog
{
    /// <summary>Ereignis 1000 der Quelle „Application Error": ein Prozess ist abgestürzt.</summary>
    private const int CrashEventId = 1000;

    /// <summary>Ereignis 1002 der Quelle „Application Hang": ein Prozess hat aufgehört zu antworten.</summary>
    private const int HangEventId = 1002;

    /// <summary>Wie weit zurück gesucht wird. Ältere Einträge sagen über den jetzigen Zustand nichts mehr.</summary>
    private static readonly TimeSpan Window = TimeSpan.FromHours(6);

    /// <summary>Name im Reiter „Logs".</summary>
    private const string Source = "Ereignisprotokoll „Anwendung“";

    private readonly Lock _gate = new();
    private IReadOnlyDictionary<string, AppFault> _cache =
        new Dictionary<string, AppFault>(StringComparer.OrdinalIgnoreCase);

    /// <summary>Der zuletzt gelesene Stand, nach Dateiname. Nie <c>null</c>, anfangs leer.</summary>
    public IReadOnlyDictionary<string, AppFault> Current
    {
        get
        {
            lock (_gate)
                return _cache;
        }
    }

    public AppFault? ForName(string executable)
        => Current.TryGetValue(executable, out AppFault? fault) ? fault : null;

    /// <summary>Fragt das Protokoll ab. Blockiert — gehört auf einen Hintergrund-Takt.</summary>
    public void Refresh()
    {
        var found = new Dictionary<string, AppFault>(StringComparer.OrdinalIgnoreCase);

        try
        {
            // timediff rechnet in Millisekunden. Der Filter gehört in die Abfrage
            // und nicht in eine Schleife darüber: sonst liest der Reader das ganze
            // Protokoll durch.
            string query =
                $"*[System[(EventID={CrashEventId} or EventID={HangEventId}) and " +
                $"TimeCreated[timediff(@SystemTime) <= {(long)Window.TotalMilliseconds}]]]";

            var request = new EventLogQuery("Application", PathType.LogName, query)
            {
                ReverseDirection = true,
            };

            using var reader = new EventLogReader(request);

            // Der Deckel schützt vor Protokollen, in denen eine Anwendung im
            // Minutentakt abstürzt.
            for (int i = 0; i < 200; i++)
            {
                using EventRecord? record = reader.ReadEvent();
                if (record is null)
                    break;

                // Erstes Datenfeld ist bei beiden Ereignissen der Dateiname.
                if (record.Properties.Count == 0 || record.Properties[0].Value is not string name)
                    continue;

                name = name.Trim();
                if (name.Length == 0 || record.TimeCreated is not { } when)
                    continue;

                // Rückwärts gelesen: der erste Treffer je Name ist der jüngste.
                if (!found.ContainsKey(name))
                    found[name] = new AppFault(name, when, record.Id == HangEventId);
            }

            lock (_gate)
                _cache = found;

            DiagnosticLog.Clear(Source);
        }
        catch (Exception ex) when (ex is EventLogException or UnauthorizedAccessException or InvalidOperationException)
        {
            // Ohne Leserecht am Anwendungsprotokoll bleibt die Spalte leer; die
            // übrige Tabelle funktioniert weiter.
            DiagnosticLog.Report(Source, ex, "Anwendungsprotokoll konnte nicht gelesen werden");
        }
    }
}

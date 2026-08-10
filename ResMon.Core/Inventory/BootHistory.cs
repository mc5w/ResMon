using System.Diagnostics.Eventing.Reader;
using ResMon.Core.Diagnostics;

namespace ResMon.Core.Inventory;

/// <summary>Wie Windows zuletzt hochgekommen ist.</summary>
public enum BootKind
{
    /// <summary>Nicht ermittelbar — das Protokoll war nicht lesbar.</summary>
    Unknown,

    /// <summary>Vollständiger Start aus dem ausgeschalteten Zustand.</summary>
    Cold,

    /// <summary>Schnellstart: die Kernelsitzung wurde aus dem Ruhezustand geholt.</summary>
    Hybrid,

    /// <summary>Fortsetzung aus dem Ruhezustand.</summary>
    Resume,
}

/// <summary>
/// Der letzte Einschaltvorgang, aus dem Ereignisprotokoll.
/// <paramref name="PowerOn"/> ist <c>null</c>, wenn das Protokoll nicht gelesen
/// werden konnte.
/// </summary>
public readonly record struct BootRecord(DateTime? PowerOn, BootKind Kind, DateTime? LastShutdown, bool ShutdownWasClean)
{
    public static readonly BootRecord Unknown = new(null, BootKind.Unknown, null, true);
}

/// <summary>
/// Beantwortet die Frage „wie lange läuft der Rechner schon", die sich mit
/// <c>GetTickCount64</c> nicht beantworten lässt.
/// </summary>
/// <remarks>
/// Windows' Schnellstart macht aus dem Herunterfahren einen Ruhezustand der
/// Kernelsitzung. Der Tickzähler wird beim Aufwachen um die Schlafenszeit
/// fortgeschrieben und läuft dadurch über das Ausschalten hinweg weiter — der
/// Task-Manager zeigt deshalb Laufzeiten von Wochen an, obwohl der Rechner jeden
/// Abend ausgeschaltet wurde. Auch <c>QueryUnbiasedInterruptTime</c> hilft
/// nicht: sie lässt zwar die Schlafenszeit weg, zählt aber die Laufzeit der
/// vorherigen Sitzungen weiter mit.
///
/// Verlässlich ist nur das Ereignisprotokoll. <c>Microsoft-Windows-Kernel-Boot</c>
/// schreibt bei jedem Einschalten das Ereignis 27 und vermerkt darin die
/// Startart. Dessen Zeitstempel ist der Zeitpunkt, den der Benutzer meint, wenn
/// er „hochgefahren" sagt.
/// </remarks>
public static class BootHistory
{
    private const string BootProvider = "Microsoft-Windows-Kernel-Boot";
    private const int BootEventId = 27;

    // 6006 schreibt der Ereignisprotokolldienst beim geordneten Herunterfahren,
    // 6008 nach einem Absturz oder Stromausfall beim nächsten Start.
    private const int CleanShutdownEventId = 6006;
    private const int DirtyShutdownEventId = 6008;

    public static BootRecord Read()
    {
        (DateTime? powerOn, BootKind kind) = ReadPowerOn();
        (DateTime? shutdown, bool clean) = ReadShutdown();
        return new BootRecord(powerOn, kind, shutdown, clean);
    }

    private static (DateTime?, BootKind) ReadPowerOn()
    {
        EventRecord? record = Newest(
            "System",
            $"*[System[Provider[@Name='{BootProvider}'] and (EventID={BootEventId})]]");

        if (record is null)
            return (null, BootKind.Unknown);

        using (record)
        {
            // Das einzige Datenfeld des Ereignisses ist BootType.
            BootKind kind = record.Properties.Count > 0 && record.Properties[0].Value is { } raw
                ? Convert.ToInt32(raw) switch
                {
                    0 => BootKind.Cold,
                    1 => BootKind.Hybrid,
                    2 => BootKind.Resume,
                    _ => BootKind.Unknown,
                }
                : BootKind.Unknown;

            return (record.TimeCreated, kind);
        }
    }

    private static (DateTime?, bool) ReadShutdown()
    {
        EventRecord? record = Newest(
            "System",
            $"*[System[(EventID={CleanShutdownEventId} or EventID={DirtyShutdownEventId})]]");

        if (record is null)
            return (null, true);

        using (record)
            return (record.TimeCreated, record.Id == CleanShutdownEventId);
    }

    /// <summary>
    /// Das jüngste Ereignis zu einer Abfrage. Der Aufrufer gibt es frei.
    /// Rückwärts gelesen, damit der Reader nicht das ganze Protokoll durchgeht.
    /// </summary>
    private static EventRecord? Newest(string log, string query)
    {
        try
        {
            var request = new EventLogQuery(log, PathType.LogName, query) { ReverseDirection = true };
            using var reader = new EventLogReader(request);
            return reader.ReadEvent();
        }
        catch (Exception ex) when (ex is EventLogException or UnauthorizedAccessException or InvalidOperationException)
        {
            // Ein abgeschaltetes oder gesperrtes Protokoll darf die Übersicht
            // nicht verhindern; die Angabe fehlt dann eben.
            DiagnosticLog.Report("Ereignisprotokoll „System“", ex, $"Abfrage im Protokoll »{log}«");
            return null;
        }
    }
}

using System.Diagnostics.Eventing.Reader;
using System.Xml.Linq;
using ResMon.Core.Diagnostics;

namespace ResMon.Core.Startup;

/// <summary>Ein gelesenes Ereignis, auf das reduziert, was hier gebraucht wird.</summary>
/// <param name="Id">Ereigniskennung.</param>
/// <param name="Time">Zeitstempel.</param>
/// <param name="Data">Die benannten Datenfelder.</param>
/// <param name="Message">Der aufbereitete Text, sofern der Anbieter ihn liefert.</param>
internal readonly record struct RawEvent(
    int Id, DateTime Time, IReadOnlyDictionary<string, string> Data, string? Message)
{
    public string? Field(string name) => Data.TryGetValue(name, out string? value) ? value : null;

    /// <summary>
    /// Das n-te Datenfeld eines Ereignisses ohne Manifest.
    /// </summary>
    /// <remarks>
    /// Der Dienststeuerungs-Manager benennt seine Felder <c>param1</c>,
    /// <c>param2</c> und so fort — das sind die Platzhalter der Meldungsvorlage.
    /// Andere alte Quellen lassen den Namen ganz weg; für die trägt
    /// <see cref="StartupEvents"/> die Position als Namen ein. Beide Formen
    /// abzufragen kostet einen Wörterbuchzugriff und erspart die Frage, welche
    /// Quelle welche Form verwendet.
    /// </remarks>
    public string? Positional(int position)
        => Field($"param{position}") ?? Field(position.ToString());

    /// <summary>Ein Zahlenfeld in Millisekunden, als Sekunden.</summary>
    public double? Seconds(string name)
        => double.TryParse(Field(name), System.Globalization.NumberStyles.Any,
            System.Globalization.CultureInfo.InvariantCulture, out double ms)
            ? ms / 1000.0
            : null;

    public int? Number(string name)
        => int.TryParse(Field(name), out int value) ? value : null;
}

/// <summary>
/// Der Zugriff auf die Ereignisprotokolle, aus denen die Startanalyse besteht.
/// </summary>
/// <remarks>
/// Gelesen wird ausschließlich über <c>EventData</c>, nie über den angezeigten
/// Text: der ist lokalisiert und würde die Auswertung an die Systemsprache
/// binden. Die Felder tragen dagegen feste englische Namen — dieselbe Überlegung
/// wie bei <c>PdhAddEnglishCounterW</c> in DESIGN.md §8.1.
/// </remarks>
internal static class StartupEvents
{
    /// <summary>
    /// Obergrenze je Abfrage. Die Protokolle reichen Monate zurück; für die Frage
    /// „was war beim letzten Start“ genügt ein Bruchteil, und ein unbegrenzter
    /// Lauf über ein volles Protokoll dauert Sekunden.
    /// </summary>
    private const int MaxRecords = 600;

    /// <summary>
    /// Liest ein Protokoll rückwärts und liefert die Treffer in
    /// <b>chronologischer</b> Reihenfolge zurück.
    /// </summary>
    /// <param name="log">Protokollname.</param>
    /// <param name="query">XPath-Abfrage.</param>
    /// <param name="notBefore">Ereignisse davor werden nicht mehr gelesen.</param>
    /// <param name="limit">Höchstzahl gelesener Einträge.</param>
    /// <param name="source">Name für das Fehlerprotokoll; <c>null</c> meldet nichts.</param>
    public static List<RawEvent> Read(
        string log, string query, DateTime? notBefore = null, int limit = MaxRecords, string? source = null)
    {
        var events = new List<RawEvent>();

        try
        {
            var request = new EventLogQuery(log, PathType.LogName, query) { ReverseDirection = true };
            using var reader = new EventLogReader(request);

            while (events.Count < limit && reader.ReadEvent() is { } record)
            {
                using (record)
                {
                    // Rückwärts gelesen: der erste Eintrag vor der Grenze ist das
                    // Signal zum Aufhören, alles Weitere ist noch älter.
                    if (notBefore is { } floor && record.TimeCreated < floor)
                        break;

                    events.Add(Convert(record));
                }
            }
        }
        catch (Exception ex) when (ex is EventLogException or UnauthorizedAccessException or InvalidOperationException)
        {
            if (source is not null)
                DiagnosticLog.Report(source, ex, $"Das Protokoll »{log}« ließ sich nicht lesen");
        }

        events.Reverse();
        return events;
    }

    /// <summary>Ob ein Protokoll überhaupt gelesen werden darf.</summary>
    /// <remarks>
    /// <c>Diagnostics-Performance</c> und <c>GroupPolicy</c> sind
    /// zugriffsgeschützt; ohne erhöhte Rechte scheitert das Lesen. Das vorher zu
    /// wissen ist der Unterschied zwischen „hier steht nichts“ und „hier darf ich
    /// nicht nachsehen“ — und nur die zweite Aussage ist für den Anwender
    /// brauchbar.
    /// <para>
    /// Geprüft wird mit einem echten Lesezugriff. Der naheliegende Weg über
    /// <c>EventLogConfiguration</c> taugt nicht: die Kanaldefinition gibt Windows
    /// auch unerhöht heraus, gemessen meldet sie für beide gesperrten Protokolle
    /// Erfolg, während der erste <c>ReadEvent</c> wirft. Eine Prüfung, die den
    /// gesperrten Fall nicht erkennt, ist schlechter als keine — sie lässt den
    /// Abschnitt kommentarlos leer.
    /// </para>
    /// </remarks>
    public static bool CanRead(string log)
    {
        try
        {
            var request = new EventLogQuery(log, PathType.LogName) { ReverseDirection = true };
            using var reader = new EventLogReader(request);
            reader.ReadEvent()?.Dispose();
            return true;
        }
        catch (Exception ex) when (ex is EventLogException or UnauthorizedAccessException or InvalidOperationException)
        {
            return false;
        }
    }

    /// <summary>
    /// Übersetzt einen Protokolleintrag in die benannten Datenfelder. Der Weg
    /// über das XML ist der einzige, der die Namen mitliefert —
    /// <c>record.Properties</c> ist eine reine Positionsliste.
    /// </summary>
    private static RawEvent Convert(EventRecord record)
    {
        var data = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        try
        {
            XElement? payload = XDocument.Parse(record.ToXml()).Root?
                .Elements().FirstOrDefault(e => e.Name.LocalName is "EventData" or "UserData");

            // UserData schachtelt die Felder noch eine Ebene tiefer, unter einem
            // anbietereigenen Element.
            if (payload?.Name.LocalName == "UserData")
                payload = payload.Elements().FirstOrDefault() ?? payload;

            if (payload is not null)
            {
                // Anbieter mit Manifest benennen ihre Felder; die alten
                // Ereignisquellen — allen voran der Dienststeuerungs-Manager —
                // schreiben unbenannte <Data>-Elemente. Die bekommen ihre
                // Position als Namen, also dieselbe Nummer, die auch die
                // Meldungsvorlage als %1, %2 einsetzt. Ohne das fielen alle
                // Felder eines solchen Ereignisses auf denselben Schlüssel und
                // nur das letzte überlebte.
                int position = 0;
                foreach (XElement item in payload.Elements())
                {
                    position++;
                    string? name = item.Attribute("Name")?.Value;
                    data[string.IsNullOrEmpty(name) ? position.ToString() : name] = item.Value;
                }
            }
        }
        catch (Exception ex) when (ex is System.Xml.XmlException or EventLogException)
        {
            // Ein Eintrag ohne lesbare Nutzlast bleibt mit seinem Zeitstempel
            // stehen; die Zeile fehlt dann eben in der Auswertung.
        }

        string? message = null;
        try
        {
            message = record.FormatDescription();
        }
        catch (EventLogException)
        {
            // Der Anbieter ist deinstalliert — dann gibt es keinen Text, nur die
            // Felder. Für die Auswertung reicht das; für die Anzeige nicht.
        }

        return new RawEvent(record.Id, record.TimeCreated ?? DateTime.MinValue, data, Trim(message));
    }

    private static string? Trim(string? message)
    {
        if (string.IsNullOrWhiteSpace(message))
            return null;

        // Die Anbieter hängen an fast jede Meldung noch Absätze mit Hinweisen an.
        // Für eine Tabellenzeile ist der erste Satz das Brauchbare.
        int cut = message.IndexOfAny(['\r', '\n']);
        string first = cut > 0 ? message[..cut] : message;
        return first.Trim();
    }
}

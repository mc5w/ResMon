namespace ResMon.Core.Startup;

/// <summary>
/// Die Startkette: was der Explorer beim Anmelden ausgeführt hat, mit Anfang und
/// Ende je Eintrag.
/// </summary>
/// <remarks>
/// Die Quelle ist <c>Microsoft-Windows-Shell-Core/Operational</c>. Der Explorer
/// schreibt dort zu jedem Autostart-Befehl ein Start- und ein Ende-Ereignis,
/// letzteres mit der vergebenen Prozesskennung. Das ist die einzige Stelle im
/// System, an der die Startdauer eines einzelnen Autostart-Eintrags rückwirkend
/// auf die Millisekunde nachlesbar ist — der Task-Manager fasst dieselbe
/// Grundlage zu drei Stufen „Startauswirkung“ zusammen.
/// <para>
/// Der entscheidende Befund steckt in den Zeitstempeln: das Ende-Ereignis eines
/// Befehls trägt denselben Zeitstempel wie das Start-Ereignis des nächsten. Der
/// Explorer arbeitet die Einträge also <b>nacheinander</b> ab, und die Dauer
/// eines Glieds ist damit die Wartezeit aller folgenden. Ein Eintrag, dessen
/// Startaufruf hängt, ist hier als langer Balken sichtbar, ohne dass man raten
/// muss, wer den Start aufhält.
/// </para>
/// <para>
/// Das Protokoll ist nicht zugriffsgeschützt und auch ohne Adminrechte lesbar —
/// anders als die Startmessung in <see cref="BootPerformanceReader"/>.
/// </para>
/// </remarks>
public static class BootChain
{
    public const string LogName = "Microsoft-Windows-Shell-Core/Operational";

    private const string Query =
        "*[System[(EventID=9707 or EventID=9708 or EventID=62408 or EventID=62409 " +
        "or EventID=62170 or EventID=62171)]]";

    /// <summary>
    /// Liest die Glieder der Kette ab <paramref name="sessionStart"/>. Ohne
    /// Bezugspunkt wird das gelesene Fenster auf die letzten Ereignisse begrenzt.
    /// </summary>
    public static IReadOnlyList<ChainItem> Read(DateTime? sessionStart)
    {
        List<RawEvent> events = StartupEvents.Read(
            LogName, Query, sessionStart, source: "Startkette (Shell-Core)");

        // Je Bezeichner die Plätze der noch offenen Anfänge. Ein Befehl kann in
        // einer Sitzung mehrfach vorkommen — deshalb ein Stapel und kein
        // einzelner Wert. Gemerkt wird der Index und nicht das Glied selbst:
        // zwei gleiche Befehle im selben Augenblick wären als Werte nicht
        // auseinanderzuhalten.
        var pending = new Dictionary<(ChainKind, string), Stack<int>>();
        var items = new List<ChainItem>();

        foreach (RawEvent record in events)
        {
            (ChainKind kind, string? key) = Classify(record);
            if (key is null)
                continue;

            if (IsStart(record.Id))
            {
                if (!pending.TryGetValue((kind, key), out Stack<int>? stack))
                    pending[(kind, key)] = stack = new Stack<int>();

                stack.Push(items.Count);
                items.Add(new ChainItem(kind, key, record.Time));
            }
            else if (pending.TryGetValue((kind, key), out Stack<int>? stack) && stack.Count > 0)
            {
                int index = stack.Pop();
                items[index] = items[index] with { Finished = record.Time, Pid = record.Number("PID") };
            }
        }

        return items;
    }

    private static bool IsStart(int id) => id is 9707 or 62408 or 62170;

    private static (ChainKind Kind, string? Key) Classify(RawEvent record) => record.Id switch
    {
        9707 or 9708 => (ChainKind.RunKey, record.Field("Command")),
        62408 or 62409 => (ChainKind.StartupFolder, record.Field("Command")),
        62170 or 62171 => (ChainKind.LogonTask, record.Field("TaskName")),
        _ => (ChainKind.KeyScan, null),
    };
}

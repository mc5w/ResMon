using System.Globalization;
using System.IO;
using System.Security;
using Microsoft.Win32;
using ResMon.Core.Diagnostics;

namespace ResMon.Core.Inventory;

/// <summary>Was über den letzten Start einer Anwendung bekannt ist.</summary>
public readonly record struct UsageRecord(DateTime? LastUsed, int? LaunchCount, UsageSource Source);

/// <summary>
/// Wann eine Anwendung zuletzt lief — aus den beiden Quellen, die Windows dafür
/// von sich aus führt.
/// </summary>
/// <remarks>
/// Diese Angabe ist der eigentliche Zweck des Programm-Reiters. „Groß“ allein ist
/// kein Grund, etwas zu deinstallieren; „groß <b>und</b> seit anderthalb Jahren
/// nicht gestartet“ ist einer. Im Ordnerbaum kommt sie nicht vor, sie muss also
/// von woanders kommen.
/// <para>
/// Zwei Quellen, die sich gegenseitig auffangen:
/// <b>Prefetch</b> legt für jede gestartete Anwendung eine <c>.pf</c>-Datei an,
/// deren Änderungszeitpunkt der letzte Start ist — maschinenweit, aber ohne
/// Benutzer und ohne Zähler. <b>UserAssist</b> führt der Explorer je Benutzer und
/// zählt mit, kennt aber nur, was über die Oberfläche gestartet wurde: eine Exe,
/// die nur von einem Dienst oder aus einem Skript aufgerufen wird, fehlt dort.
/// </para>
/// <para>
/// Beide kennen nur den <em>Dateinamen</em> der Anwendung, nicht das Programm.
/// Die Zuordnung übernimmt <see cref="ProgramInventory"/>.
/// </para>
/// </remarks>
public sealed class UsageHistory
{
    private const string Source = "Nutzungsverlauf";

    private const string UserAssistPath =
        @"SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer\UserAssist";

    /// <summary>
    /// Die Struktur hinter einem UserAssist-Wert ab Windows 7: 72 Byte, davon der
    /// Startzähler bei 4 und eine FILETIME bei 60. Kürzere Werte stammen aus
    /// älteren Formaten und werden übergangen.
    /// </summary>
    private const int UserAssistRecordSize = 72;

    private const int RunCountOffset = 4;
    private const int LastRunOffset = 60;

    /// <summary>
    /// Ein Startzähler jenseits dessen ist ein Lesefehler und keine Zahl. Der
    /// Explorer zählt bei manchen Einträgen ab einem Versatz hoch, was ohne diese
    /// Schranke als „vier Milliarden Starts“ in der Oberfläche landet.
    /// </summary>
    private const int MaxPlausibleRunCount = 100_000;

    private readonly Dictionary<string, UsageRecord> _byExecutable =
        new(StringComparer.OrdinalIgnoreCase);

    private readonly List<string> _limitations = [];

    private UsageHistory()
    {
    }

    /// <summary>Was die Auskunft einschränkt — gehört mit den Zahlen zusammen angezeigt.</summary>
    public IReadOnlyList<string> Limitations => _limitations;

    /// <summary>Wie viele Anwendungen überhaupt bekannt sind.</summary>
    public int Count => _byExecutable.Count;

    /// <summary>Ob Prefetch gelesen werden konnte.</summary>
    public bool PrefetchAvailable { get; private set; }

    /// <summary>Ob UserAssist gelesen werden konnte.</summary>
    public bool UserAssistAvailable { get; private set; }

    /// <summary>
    /// Liest beide Quellen ein. Prefetch verlangt erhöhte Rechte; ohne sie bleibt
    /// UserAssist allein übrig, und das steht dann in <see cref="Limitations"/>.
    /// </summary>
    public static UsageHistory Collect()
    {
        var history = new UsageHistory();

        history.ReadPrefetch();
        history.ReadUserAssist();

        if (!history.PrefetchAvailable)
        {
            history._limitations.Add(
                "Prefetch war nicht lesbar — ohne Administratorrechte bleibt der Ordner gesperrt. " +
                "Die letzte Nutzung stammt dann allein aus UserAssist und kennt nur Starts " +
                "über die Oberfläche dieses Benutzers.");
        }

        if (!history.UserAssistAvailable)
            history._limitations.Add("UserAssist war nicht lesbar — es fehlen die Startzähler.");

        if (history._byExecutable.Count == 0)
            history._limitations.Add("Keine Quelle zur letzten Nutzung verfügbar. Die Spalte bleibt leer.");

        return history;
    }

    /// <summary>
    /// Schlägt eine Anwendung über ihren Dateinamen nach, etwa
    /// <c>TeamSpeak.exe</c>. Ein Treffer heißt „zuletzt gestartet am“; ein
    /// fehlender Treffer heißt <b>nicht</b> „nie gestartet“, sondern nur, dass
    /// keine der beiden Quellen den Namen führt.
    /// </summary>
    public UsageRecord? ForExecutable(string? fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName))
            return null;

        return _byExecutable.TryGetValue(Path.GetFileName(fileName), out UsageRecord record) ? record : null;
    }

    /// <summary>
    /// Trägt einen Fund ein. Denselben Dateinamen gibt es mehrfach — dieselbe
    /// Anwendung an zwei Orten, oder beide Quellen kennen sie. Genommen wird der
    /// jüngste Start; die Frage lautet „wann zuletzt“, nicht „wann zuerst“.
    /// </summary>
    private void Merge(string fileName, DateTime? lastUsed, int? launchCount, UsageSource source)
    {
        if (_byExecutable.TryGetValue(fileName, out UsageRecord existing))
        {
            _byExecutable[fileName] = new UsageRecord(
                Later(existing.LastUsed, lastUsed),
                launchCount is null ? existing.LaunchCount : (existing.LaunchCount ?? 0) + launchCount,
                existing.Source | source);
            return;
        }

        _byExecutable[fileName] = new UsageRecord(lastUsed, launchCount, source);
    }

    private static DateTime? Later(DateTime? left, DateTime? right)
        => left is null ? right
            : right is null ? left
            : left > right ? left : right;

    /// <summary>
    /// Liest <c>C:\Windows\Prefetch</c>. Der Inhalt der Dateien bleibt
    /// ungeöffnet — er ist komprimiert und bräuchte einen eigenen Entpacker,
    /// während die beiden Angaben, um die es geht, außen stehen: der Dateiname
    /// trägt die Anwendung, der Änderungszeitpunkt den letzten Start.
    /// </summary>
    private void ReadPrefetch()
    {
        string folder = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.Windows), "Prefetch");

        try
        {
            var directory = new DirectoryInfo(folder);
            if (!directory.Exists)
            {
                _limitations.Add($"Der Ordner »{folder}« fehlt — Prefetch ist auf diesem System abgeschaltet.");
                return;
            }

            int found = 0;
            foreach (FileInfo file in directory.EnumerateFiles("*.pf"))
            {
                string? executable = ExecutableFromPrefetchName(file.Name);
                if (executable is null)
                    continue;

                Merge(executable, file.LastWriteTime, launchCount: null, UsageSource.Prefetch);
                found++;
            }

            PrefetchAvailable = true;

            // Ein lesbarer, aber leerer Ordner ist eine eigene Aussage: entweder
            // wurde er geleert oder der Prefetcher läuft nicht. Beides macht die
            // Spalte unvollständig, ohne dass ein Fehler aufträte.
            if (found == 0)
            {
                _limitations.Add(
                    "Prefetch war lesbar, enthielt aber keine Einträge. Windows legt sie beim " +
                    "Starten neu an; bis dahin bleibt die letzte Nutzung unvollständig.");
            }
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or SecurityException or IOException)
        {
            DiagnosticLog.Report(Source, ex, $"Der Ordner »{folder}« ließ sich nicht lesen");
        }
    }

    /// <summary>
    /// Löst <c>TEAMSPEAK.EXE-1A2B3C4D.pf</c> in <c>TEAMSPEAK.EXE</c> auf. Der
    /// Anhang ist ein Streuwert über den Pfad — dieselbe Anwendung an zwei Orten
    /// bekommt zwei Dateien, weshalb <see cref="Merge"/> zusammenführen muss.
    /// </summary>
    /// <remarks>
    /// Die Dateien der Startaufzeichnung (<c>ReadyBoot</c>) und der
    /// Anwendungsstart-Ablaufverfolgung tragen keinen solchen Anhang und fallen
    /// hier heraus.
    /// </remarks>
    private static string? ExecutableFromPrefetchName(string fileName)
    {
        string stem = Path.GetFileNameWithoutExtension(fileName);

        int separator = stem.LastIndexOf('-');
        if (separator <= 0 || separator == stem.Length - 1)
            return null;

        string executable = stem[..separator];
        return executable.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? executable : null;
    }

    /// <summary>
    /// Liest die <c>Count</c>-Unterschlüssel unter <c>UserAssist</c>. Jeder
    /// GUID-Schlüssel steht für eine Art von Eintrag; welcher welcher ist, spielt
    /// hier keine Rolle, weil ohnehin alle zusammengeführt werden.
    /// </summary>
    private void ReadUserAssist()
    {
        try
        {
            using RegistryKey? root = Registry.CurrentUser.OpenSubKey(UserAssistPath);
            if (root is null)
                return;

            foreach (string guid in root.GetSubKeyNames())
            {
                using RegistryKey? counts = root.OpenSubKey($@"{guid}\Count");
                if (counts is null)
                    continue;

                foreach (string encoded in counts.GetValueNames())
                    ReadUserAssistValue(counts, encoded);
            }

            UserAssistAvailable = true;

            _limitations.Add(
                "UserAssist gilt nur für den angemeldeten Benutzer und kennt nur Starts über " +
                "die Oberfläche. Ein Programm, das ein anderer Benutzer nutzt oder das nur aus " +
                "einem Skript heraus läuft, steht dort nicht.");
        }
        catch (Exception ex) when (ex is SecurityException or UnauthorizedAccessException or IOException)
        {
            DiagnosticLog.Report(Source, ex, "UserAssist ließ sich nicht lesen");
        }
    }

    private void ReadUserAssistValue(RegistryKey counts, string encoded)
    {
        if (counts.GetValue(encoded) is not byte[] value || value.Length < UserAssistRecordSize)
            return;

        string decoded = Rot13(encoded);

        // Die Namen tragen Pfade, teils mit einer bekannten Ordner-GUID davor.
        // Für die Zuordnung zählt nur das letzte Segment.
        string fileName = decoded.AsSpan()[(decoded.LastIndexOf('\\') + 1)..].ToString();
        if (!fileName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            return;

        int runCount = BitConverter.ToInt32(value, RunCountOffset);
        long filetime = BitConverter.ToInt64(value, LastRunOffset);

        DateTime? lastUsed = null;
        if (filetime > 0)
        {
            try
            {
                lastUsed = DateTime.FromFileTime(filetime);
            }
            catch (ArgumentOutOfRangeException)
            {
                // Ein Wert außerhalb des Kalenders — der Eintrag zählt trotzdem,
                // nur ohne Datum.
            }
        }

        Merge(
            fileName,
            lastUsed,
            runCount is > 0 and <= MaxPlausibleRunCount ? runCount : null,
            UsageSource.UserAssist);
    }

    /// <summary>
    /// Dreht die Buchstabendrehung, mit der der Explorer die Namen ablegt. Keine
    /// Verschlüsselung, sondern nur eine Hürde gegen versehentliches Mitlesen;
    /// die Umkehrung ist dieselbe Rechnung wie das Hinlegen.
    /// </summary>
    private static string Rot13(string value)
    {
        return string.Create(value.Length, value, static (target, source) =>
        {
            for (int index = 0; index < source.Length; index++)
            {
                char current = source[index];
                target[index] = current switch
                {
                    >= 'a' and <= 'z' => (char)('a' + ((current - 'a' + 13) % 26)),
                    >= 'A' and <= 'Z' => (char)('A' + ((current - 'A' + 13) % 26)),
                    _ => current,
                };
            }
        });
    }

    /// <summary>
    /// Die jüngste bekannte Nutzung überhaupt. Dient als Plausibilitätsprobe: ist
    /// sie Wochen alt, stimmt mit den Quellen etwas nicht.
    /// </summary>
    public DateTime? NewestUse
    {
        get
        {
            DateTime? newest = null;
            foreach (UsageRecord record in _byExecutable.Values)
                newest = Later(newest, record.LastUsed);

            return newest;
        }
    }

    /// <summary>Für die Ausgabe der Probe: alles, was bekannt ist.</summary>
    public IEnumerable<KeyValuePair<string, UsageRecord>> All => _byExecutable;

    /// <summary>Formatiert ein Datum als Abstand in Tagen, wie ihn die Probe ausgibt.</summary>
    public static string Describe(DateTime? lastUsed)
        => lastUsed is null
            ? "unbekannt"
            : ((int)(DateTime.Now - lastUsed.Value).TotalDays).ToString(CultureInfo.CurrentCulture) + " Tage";
}

using System.Diagnostics;
using System.IO;
using System.Text;
using ResMon.Core.Diagnostics;
using ResMon.Core.Inventory;

namespace ResMon.Core.Storage;

/// <summary>Wem ein Posten im Temp-Ordner zuzuordnen war.</summary>
public enum TempOwner
{
    /// <summary>
    /// Der Name passt zu einem Programm, das gerade läuft. Der einzige Zustand,
    /// bei dem Anfassen ausdrücklich falsch wäre.
    /// </summary>
    Running,

    /// <summary>Der Name passt zu einem installierten Programm.</summary>
    Installed,

    /// <summary>
    /// Der Name sieht nach einem Programm aus, aber keines dieses Namens ist
    /// installiert. Der Fall, um den es hier geht.
    /// </summary>
    Orphan,

    /// <summary>
    /// Der Name sagt nichts — eine GUID, ein <c>tmpXXXX.tmp</c>, eine
    /// Buchstabensuppe. Wem das gehört, ist von hier aus nicht zu entscheiden.
    /// </summary>
    Anonymous,

    /// <summary>
    /// Sieht verwaist aus, ist aber zu frisch. Was in den letzten Tagen
    /// geschrieben wurde, gehört mit einiger Wahrscheinlichkeit zu etwas, das
    /// gerade erst gelaufen ist — auch wenn der Name nirgends auftaucht.
    /// </summary>
    Recent,
}

/// <summary>Ein Posten unmittelbar in einem Temp-Ordner.</summary>
public sealed record TempEntry(string Path, string Name, TempOwner Owner)
{
    public long Bytes { get; init; }

    public DateTime LastWrite { get; init; }

    public bool IsDirectory { get; init; }

    /// <summary>Wie viele Dateien darin stecken — bei einer Datei genau eine.</summary>
    public int FileCount { get; init; }

    /// <summary>
    /// Das Programm, zu dem der Name passt. Bei <see cref="TempOwner.Orphan"/>
    /// leer — dort ist das Fehlen einer Entsprechung ja der Befund.
    /// </summary>
    public string? Program { get; init; }

    /// <summary>
    /// Woran die Einstufung hängt, in einem Satz. Eine Einstufung ohne Begründung
    /// wäre bei einer Liste, an deren Ende ein Löschknopf steht, zu wenig.
    /// </summary>
    public string Evidence { get; init; } = string.Empty;
}

/// <summary>Das Ergebnis einer Temp-Erhebung.</summary>
public sealed record TempReport(DateTime CollectedAt)
{
    public static readonly TempReport Empty = new(DateTime.Now);

    public IReadOnlyList<TempEntry> Entries { get; init; } = [];

    /// <summary>Die durchsuchten Ordner.</summary>
    public IReadOnlyList<string> Roots { get; init; } = [];

    public IReadOnlyList<string> Limitations { get; init; } = [];

    public long TotalBytes { get; init; }

    public long OrphanBytes { get; init; }

    /// <summary>Wie viele installierte Programme zum Abgleich zur Verfügung standen.</summary>
    public int KnownPrograms { get; init; }
}

/// <summary>
/// Geht die Temp-Ordner durch und hält jeden Posten gegen die installierten
/// Programme.
/// </summary>
/// <remarks>
/// Der Gedanke dahinter: ein Temp-Ordner wird nicht von Windows aufgeräumt,
/// sondern von dem Programm, das ihn angelegt hat — und ein deinstalliertes
/// Programm räumt nichts mehr auf. Solche Reste bleiben deshalb für immer
/// liegen. Sie sind der einzige Teil des Temp-Ordners, bei dem sich mit
/// Begründung sagen lässt, dass niemand sie je wieder anfasst.
/// <para>
/// <b>Was diese Erhebung nicht kann.</b> Sie schließt vom Namen auf den
/// Urheber, und der Schluss ist nicht sicher: ein Ordner <c>Foo</c> kann von
/// einem Programm stammen, das im Uninstall-Schlüssel ganz anders heißt. Der
/// Fehler geht in beide Richtungen und deshalb wird hier nichts von selbst
/// gelöscht — die Liste ist ein Vorschlag, ausgewählt wird von Hand
/// (DESIGN.md §13.5).
/// </para>
/// </remarks>
public static class TempInventory
{
    private const string Source = "Temp-Erhebung";

    /// <summary>
    /// So jung darf ein Posten höchstens sein, um als verwaist zu gelten.
    /// </summary>
    /// <remarks>
    /// Ein Installer, der vorgestern lief, hat seinen Ordner vielleicht noch
    /// nicht abgeräumt, weil er auf einen Neustart wartet. Der Name eines
    /// Programms, das gerade erst installiert wird, steht außerdem noch nicht in
    /// der Registry. Beides sind Fehlgriffe, die eine Wartezeit verhindert und
    /// sonst nichts.
    /// </remarks>
    private static readonly TimeSpan MinAge = TimeSpan.FromDays(7);

    /// <summary>Darunter lohnt der Eintrag in der Liste nicht.</summary>
    private const long MinBytes = 1L * 1024 * 1024;

    /// <summary>
    /// Obergrenze für die Zahl der Posten. Ein Temp-Ordner mit zehntausend
    /// Einträgen ist keine Liste mehr, die jemand durchsieht — und durchsehen ist
    /// hier die Voraussetzung.
    /// </summary>
    private const int MaxEntries = 300;

    /// <summary>
    /// Wortanfänge, die zu Windows selbst gehören oder zu etwas, das kein
    /// Uninstall-Eintrag je führen wird.
    /// </summary>
    /// <remarks>
    /// Ohne diese Liste wäre jeder davon „verwaist“ — sie tauchen in keinem
    /// Uninstall-Schlüssel auf, weil sie zu keinem installierten Programm
    /// gehören. Auf der Referenzmaschine traf das unter anderem
    /// <c>DiagOutputDir</c> und die Mitschnitte der Startaufzeichnung
    /// (<c>WPR_initiated_…</c>, zusammen 8 GB).
    /// <para>
    /// Verglichen wird der <b>Wortanfang</b> und nicht der ganze Name: die
    /// Ordner heißen <c>DiagOutputDir</c> und nicht <c>Diag</c>. Dass dabei
    /// gelegentlich zu viel passt — <c>Logitech</c> beginnt mit <c>log</c> —,
    /// ist der ungefährliche Fehler: er führt dazu, dass ein Posten <em>nicht</em>
    /// zum Löschen angeboten wird.
    /// </para>
    /// </remarks>
    private static readonly string[] WindowsOwned =
    [
        "microsoft", "windows", "winget", "dotnet", "netfx", "wpf", "clr", "mscoree",
        "onedrive", "edge", "msedge", "defender", "wer", "werfault",
        "diag", "cbs", "dism", "sfc", "msi", "msu", "wpr", "wct", "etl",
        "setup", "update", "install", "cab", "log", "crashpad", "scoped_dir",
        "temp", "tmp", "low", "history", "cache", "cookies", "profile",
        "fonts", "printers", "spool",
        "outlook", "office", "teams", "onenote", "excel", "word", "powerpoint",
    ];

    private static bool IsWindowsOwned(string token) =>
        WindowsOwned.Any(name => token.StartsWith(name, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Erhebt die Temp-Ordner. Liest das Dateisystem durch und gehört deshalb auf
    /// einen Hintergrund-Thread.
    /// </summary>
    /// <param name="programs">
    /// Das Programm-Inventar für den Abgleich. Wird keines gereicht, erhebt die
    /// Methode selbst eines — das kostet allerdings ein paar hundert
    /// Millisekunden extra, und der Aufrufer hat es meist schon.
    /// </param>
    public static TempReport Collect(ProgramReport? programs = null, CancellationToken token = default)
    {
        var limitations = new List<string>();
        programs ??= ProgramInventory.Collect(token: token);

        HashSet<string> known = KnownNames(programs);
        HashSet<string> running = RunningNames(limitations);

        var entries = new List<TempEntry>();
        var roots = new List<string>();

        foreach (string root in TempRoots())
        {
            if (!Directory.Exists(root))
                continue;

            roots.Add(root);
            Scan(root, entries, known, running, limitations, token);
        }

        // Größtes zuerst: die Frage lautet „was bringt am meisten“, und wer die
        // Liste nur zur Hälfte durchsieht, hat dann das Wichtige gesehen.
        entries.Sort(static (left, right) => right.Bytes.CompareTo(left.Bytes));

        if (entries.Count > MaxEntries)
        {
            limitations.Add(
                $"Es wurden {entries.Count} Posten gefunden; angezeigt sind die {MaxEntries} " +
                "größten. Was darunter liegt, ist einzeln kleiner als ein Bruchteil davon.");
            entries.RemoveRange(MaxEntries, entries.Count - MaxEntries);
        }

        if (known.Count == 0)
        {
            limitations.Add(
                "Es ließ sich kein installiertes Programm lesen. Ohne diese Liste ist kein " +
                "Abgleich möglich — jeder Posten stünde als verwaist da, und das wäre falsch.");
        }

        return new TempReport(DateTime.Now)
        {
            Entries = entries,
            Roots = roots,
            Limitations = limitations,
            TotalBytes = entries.Sum(entry => entry.Bytes),
            OrphanBytes = entries.Where(entry => entry.Owner == TempOwner.Orphan).Sum(entry => entry.Bytes),
            KnownPrograms = programs.Programs.Count,
        };
    }

    /// <summary>
    /// Die durchsuchten Ordner: der des angemeldeten Benutzers und der von
    /// Windows.
    /// </summary>
    /// <remarks>
    /// Die Temp-Ordner anderer Benutzer bleiben außen vor, obwohl die Anwendung
    /// erhöht läuft und hineinkäme. Wessen Reste dort liegen, ist von hier aus
    /// nicht zu beurteilen — der Abgleich läuft gegen die Programme <em>dieses</em>
    /// Benutzers, und ein anderer hat andere.
    /// </remarks>
    public static IEnumerable<string> TempRoots()
    {
        string user = Path.GetTempPath().TrimEnd('\\');
        if (!string.IsNullOrEmpty(user))
            yield return user;

        string windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        if (!string.IsNullOrEmpty(windows))
            yield return Path.Combine(windows, "Temp");
    }

    private static void Scan(
        string root,
        List<TempEntry> entries,
        HashSet<string> known,
        HashSet<string> running,
        List<string> limitations,
        CancellationToken token)
    {
        IEnumerable<string> children;
        try
        {
            children = Directory.EnumerateFileSystemEntries(root);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            limitations.Add($"„{root}“ ließ sich nicht lesen: {ex.Message}");
            return;
        }

        foreach (string path in children)
        {
            token.ThrowIfCancellationRequested();

            TempEntry? entry = Describe(path, known, running);
            if (entry is not null && entry.Bytes >= MinBytes)
                entries.Add(entry);
        }
    }

    private static TempEntry? Describe(string path, HashSet<string> known, HashSet<string> running)
    {
        string name = Path.GetFileName(path);
        if (string.IsNullOrEmpty(name))
            return null;

        bool isDirectory;
        long bytes;
        int files;
        DateTime lastWrite;

        try
        {
            var info = new DirectoryInfo(path);
            isDirectory = info.Exists;

            if (isDirectory)
            {
                (bytes, files, lastWrite) = Measure(info);
            }
            else
            {
                var file = new FileInfo(path);
                if (!file.Exists)
                    return null;

                bytes = file.Length;
                files = 1;
                lastWrite = file.LastWriteTime;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Ein Posten, der sich nicht messen lässt, lässt sich auch nicht
            // löschen — er gehört in keine Liste, an deren Ende ein Löschknopf steht.
            return null;
        }

        (TempOwner owner, string? program, string evidence) = Classify(name, lastWrite, known, running);

        return new TempEntry(path, name, owner)
        {
            Bytes = bytes,
            FileCount = files,
            LastWrite = lastWrite,
            IsDirectory = isDirectory,
            Program = program,
            Evidence = evidence,
        };
    }

    /// <summary>
    /// Summe, Dateizahl und das jüngste Schreibdatum eines Ordners.
    /// </summary>
    /// <remarks>
    /// Das <em>jüngste</em> Datum und nicht das des Ordners selbst: der
    /// Ordnerzeitstempel ändert sich nur, wenn unmittelbar darin etwas passiert.
    /// Ein Ordner, in dessen Unterordner gestern geschrieben wurde, sähe sonst
    /// zwei Jahre alt aus — und genau daran hängt hier die Einstufung.
    /// </remarks>
    private static (long Bytes, int Files, DateTime LastWrite) Measure(DirectoryInfo directory)
    {
        long bytes = 0;
        int files = 0;
        DateTime newest = directory.LastWriteTime;

        var stack = new Stack<DirectoryInfo>();
        stack.Push(directory);

        while (stack.Count > 0)
        {
            DirectoryInfo current = stack.Pop();

            try
            {
                foreach (FileSystemInfo item in current.EnumerateFileSystemInfos())
                {
                    if (item is DirectoryInfo child)
                    {
                        // Abzweigungen nicht verfolgen: eine Verknüpfung nach
                        // C:\Windows machte aus einem Temp-Ordner das halbe System.
                        if ((child.Attributes & FileAttributes.ReparsePoint) == 0)
                            stack.Push(child);

                        continue;
                    }

                    if (item is FileInfo file)
                    {
                        bytes += file.Length;
                        files++;
                    }

                    if (item.LastWriteTime > newest)
                        newest = item.LastWriteTime;
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Ein gesperrter Unterordner macht die Summe zu klein. Das ist
                // hier verkraftbar: zu klein heißt zu wenig versprochen.
            }
        }

        return (bytes, files, newest);
    }

    /// <summary>
    /// Die Einstufung eines Postens. Die ganze Erhebung hängt an dieser einen
    /// Entscheidung, deshalb steht sie in einer eigenen Methode und gibt ihre
    /// Begründung mit heraus.
    /// </summary>
    private static (TempOwner Owner, string? Program, string Evidence) Classify(
        string name, DateTime lastWrite, HashSet<string> known, HashSet<string> running)
    {
        string token = Token(name);

        if (token.Length < 3)
            return (TempOwner.Anonymous, null, "Der Name enthält kein verwertbares Wort.");

        if (running.Contains(token))
            return (TempOwner.Running, token, $"Ein laufender Prozess heißt „{token}“.");

        if (IsWindowsOwned(token))
            return (TempOwner.Installed, "Windows",
                "Der Name gehört zu Windows selbst oder zu einem seiner Bestandteile — die " +
                "stehen in keinem Uninstall-Schlüssel und wären hier sonst alle „verwaist“.");

        if (known.Contains(token))
            return (TempOwner.Installed, token, $"Ein installiertes Programm passt zu „{token}“.");

        if (LooksAnonymous(name))
            return (TempOwner.Anonymous, null,
                "Zufallsname, wie ihn Installer und Zwischenspeicher vergeben — er verrät " +
                "seinen Urheber nicht.");

        if (DateTime.Now - lastWrite < MinAge)
            return (TempOwner.Recent, null,
                $"Sieht verwaist aus, wurde aber vor weniger als {MinAge.TotalDays:N0} Tagen " +
                "noch beschrieben. Zu frisch, um sicher zu sein.");

        return (TempOwner.Orphan, null,
            $"Kein installiertes Programm und kein laufender Prozess passt zu „{token}“, und " +
            $"seit {(int)(DateTime.Now - lastWrite).TotalDays} Tagen hat niemand hineingeschrieben.");
    }

    /// <summary>
    /// Der Wortkern eines Namens: Buchstaben von vorn, bis etwas kommt, das kein
    /// Buchstabe ist. Aus <c>NVIDIA Corporation</c> wird <c>nvidia</c>, aus
    /// <c>pip-install-8fk2x</c> wird <c>pip</c>, aus <c>Foo_2.1.4</c> wird
    /// <c>foo</c>.
    /// </summary>
    private static string Token(string name)
    {
        var builder = new StringBuilder();

        foreach (char c in Path.GetFileNameWithoutExtension(name))
        {
            if (!char.IsLetter(c))
                break;

            builder.Append(char.ToLowerInvariant(c));
        }

        return builder.ToString();
    }

    /// <summary>
    /// Ob der Name nichts über seinen Urheber sagt.
    /// </summary>
    /// <remarks>
    /// Vier Formen, alle im Temp-Ordner dieses Rechners nachgewiesen:
    /// <list type="bullet">
    /// <item>die nackte GUID, mit oder ohne geschweifte Klammern;</item>
    /// <item>reines Hexadezimal — ein Zähler oder eine Prüfsumme;</item>
    /// <item>ein kurzes Kürzel mit angehängter Zufallszahl, wie es
    /// <c>GetTempFileName</c> und der Windows Installer vergeben:
    /// <c>DEL5795.tmp</c>, <c>MSIc442e.LOG</c>, <c>tmp4A2F.tmp</c>;</item>
    /// <item>die Buchstabensuppe ohne einen einzigen Vokal.</item>
    /// </list>
    /// Die dritte Form ist die tückischste: <c>DEL5795.tmp</c> beginnt mit drei
    /// Buchstaben, die wie ein Programmname aussehen, und landete ohne diese
    /// Regel als „verwaist“ in einer Liste mit Löschknopf.
    /// </remarks>
    internal static bool LooksAnonymous(string name)
    {
        string stem = Path.GetFileNameWithoutExtension(name);

        if (stem.Length == 0 || stem.StartsWith('{'))
            return true;

        if (Guid.TryParse(stem.Trim('{', '}'), out _))
            return true;

        int letters = 0;
        int vowels = 0;
        bool hexOnly = true;

        foreach (char c in stem)
        {
            if (!Uri.IsHexDigit(c))
                hexOnly = false;

            if (!char.IsLetter(c))
                continue;

            letters++;
            if ("aeiouäöü".Contains(char.ToLowerInvariant(c)))
                vowels++;
        }

        if (hexOnly)
            return true;

        // Kürzel plus Zufallszahl: höchstens vier Buchstaben vorn, danach nur
        // noch Hex-Zeichen, davon mindestens drei. Länger als vier Buchstaben
        // ist kein Kürzel mehr, sondern ein Name mit Versionsnummer dahinter —
        // und der sagt sehr wohl etwas.
        int prefix = 0;
        while (prefix < stem.Length && char.IsLetter(stem[prefix]))
            prefix++;

        if (prefix is >= 1 and <= 4 && stem.Length - prefix >= 3
            && stem[prefix..].All(Uri.IsHexDigit))
        {
            return true;
        }

        // Ohne einen einzigen Vokal ist es kein Wort, sondern eine Zeichenfolge.
        // Kurze Kürzel bleiben ausgenommen: „npm“ und „vs“ sagen sehr wohl etwas.
        return letters >= 6 && vowels == 0;
    }

    /// <summary>
    /// Woran ein installiertes Programm zu erkennen ist: sein Anzeigename, sein
    /// Herausgeber, der Name seines Installationsordners und der seiner
    /// Hauptanwendung.
    /// </summary>
    /// <remarks>
    /// Vier Quellen und nicht eine, weil ein Temp-Ordner nach jeder davon heißen
    /// kann. <c>Adobe</c> kommt vom Herausgeber, <c>vscode</c> vom Ordnernamen,
    /// <c>Zoom</c> vom Anzeigenamen — wer nur eine davon prüft, hält zwei Drittel
    /// aller Reste für verwaist.
    /// </remarks>
    private static HashSet<string> KnownNames(ProgramReport programs)
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        void Add(string? value)
        {
            string token = Token(value ?? string.Empty);
            if (token.Length >= 3)
                names.Add(token);
        }

        foreach (ProgramEntry program in programs.Programs)
        {
            Add(program.Name);
            Add(program.Publisher);

            if (!string.IsNullOrEmpty(program.InstallLocation))
                Add(Path.GetFileName(program.InstallLocation.TrimEnd('\\')));

            if (!string.IsNullOrEmpty(program.MainExecutable))
                Add(Path.GetFileNameWithoutExtension(program.MainExecutable));
        }

        return names;
    }

    /// <summary>
    /// Die Namen der laufenden Prozesse. Ein Posten, der zu einem davon passt,
    /// gehört unter keinen Umständen in die Auswahl — dort wird gerade
    /// gearbeitet.
    /// </summary>
    private static HashSet<string> RunningNames(List<string> limitations)
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        try
        {
            foreach (Process process in Process.GetProcesses())
            {
                using (process)
                {
                    string token = Token(process.ProcessName);
                    if (token.Length >= 3)
                        names.Add(token);
                }
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            DiagnosticLog.Report(Source, ex, "Die laufenden Prozesse ließen sich nicht lesen");
            limitations.Add(
                "Die laufenden Prozesse ließen sich nicht lesen. Ein Posten, der zu einem " +
                "gerade laufenden Programm gehört, ist deshalb möglicherweise nicht als " +
                "solcher gekennzeichnet.");
        }

        return names;
    }
}

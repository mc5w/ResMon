using System.Diagnostics;
using System.IO;
using System.Management;
using System.Security;
using System.Xml.Linq;
using Microsoft.Win32;
using ResMon.Core.Diagnostics;

namespace ResMon.Core.Startup;

/// <summary>
/// Sammelt alles ein, was beim Start ausgeführt wird — dieselben Quellen, die
/// auch Autoruns abfragt (DESIGN.md §8.12).
/// </summary>
/// <remarks>
/// Der Task-Manager zeigt nur die Run-Schlüssel, die Startordner und die
/// Store-Startaufgaben. Das ist die Hälfte: geplante Aufgaben mit Anmeldeauslöser
/// und automatisch startende Dienste laufen genauso beim Start an und sind
/// erfahrungsgemäß die teureren. Beide gehören deshalb dazu, aber getrennt
/// benannt — ein Dienst hält den Desktop anders auf als ein Run-Eintrag.
/// </remarks>
public static class StartupInventory
{
    private const string Source = "Autostart-Inventar";

    private const string RunPath = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run";
    private const string RunOncePath = @"SOFTWARE\Microsoft\Windows\CurrentVersion\RunOnce";
    private const string Run32Path = @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Run";
    private const string ApprovedPath = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved";
    private const string ServicesPath = @"SYSTEM\CurrentControlSet\Services";
    private const string AppModelPath =
        @"SOFTWARE\Classes\Local Settings\Software\Microsoft\Windows\CurrentVersion\AppModel\SystemAppData";

    public static IReadOnlyList<StartupEntry> Collect()
    {
        var entries = new List<StartupEntry>();

        ReadRunKey(entries, Registry.LocalMachine, RunPath, StartupSource.MachineRun, "Run");
        ReadRunKey(entries, Registry.LocalMachine, Run32Path, StartupSource.MachineRun32, "Run32");
        ReadRunKey(entries, Registry.CurrentUser, RunPath, StartupSource.UserRun, "Run");
        ReadRunKey(entries, Registry.LocalMachine, RunOncePath, StartupSource.MachineRunOnce, null);
        ReadRunKey(entries, Registry.CurrentUser, RunOncePath, StartupSource.UserRunOnce, null);

        ReadStartupFolder(entries, Environment.GetFolderPath(Environment.SpecialFolder.Startup),
            StartupSource.UserStartupFolder);
        ReadStartupFolder(entries, Environment.GetFolderPath(Environment.SpecialFolder.CommonStartup),
            StartupSource.MachineStartupFolder);

        ReadScheduledTasks(entries);
        ReadServices(entries);
        ReadAppxTasks(entries);

        return entries;
    }

    /// <summary>
    /// Liest einen Run-Schlüssel samt Zustand. <paramref name="approvedKey"/>
    /// benennt den Unterschlüssel unter <c>StartupApproved</c>; RunOnce hat
    /// keinen, weil ein Eintrag dort nach dem Ausführen ohnehin verschwindet.
    /// </summary>
    private static void ReadRunKey(
        List<StartupEntry> entries, RegistryKey hive, string path, StartupSource source, string? approvedKey)
    {
        try
        {
            using RegistryKey? key = hive.OpenSubKey(path);
            if (key is null)
                return;

            IReadOnlyDictionary<string, (bool Enabled, DateTime? DisabledAt)> approved =
                approvedKey is null ? new Dictionary<string, (bool, DateTime?)>() : ReadApproved(hive, approvedKey);

            foreach (string name in key.GetValueNames())
            {
                string command = key.GetValue(name) as string ?? string.Empty;
                (bool enabled, DateTime? disabledAt) = approved.TryGetValue(name, out var state)
                    ? state
                    : (true, (DateTime?)null);

                entries.Add(Describe(new StartupEntry(name, source, command)
                {
                    Enabled = enabled,
                    DisabledAt = disabledAt,
                }));
            }
        }
        catch (Exception ex) when (ex is SecurityException or UnauthorizedAccessException or IOException)
        {
            DiagnosticLog.Report(Source, ex, $"Der Schlüssel »{path}« ließ sich nicht lesen");
        }
    }

    /// <summary>
    /// Der Zustand eines Eintrags steht nicht am Eintrag, sondern unter
    /// <c>StartupApproved</c> als zwölf Byte.
    /// </summary>
    /// <remarks>
    /// Das erste Byte trägt den Zustand, die Bytes 4 bis 11 eine FILETIME. Für die
    /// Auswertung des ersten Bytes kursieren mehrere Regeln; nachprüfbar ist sie
    /// am Protokoll: auf der Referenzmaschine wurden am 12.08.2026 genau die
    /// Einträge mit gerader Zahl (2 und 6) vom Explorer ausgeführt und die mit
    /// ungerader (1 und 3) nicht. <b>Gerade heißt aktiv.</b> Der Zeitstempel ist
    /// der Moment des Abschaltens und bei aktiven Einträgen null — er beantwortet
    /// die Frage „habe ich das selbst abgeschaltet oder war das ein Programm“.
    /// </remarks>
    private static IReadOnlyDictionary<string, (bool Enabled, DateTime? DisabledAt)> ReadApproved(
        RegistryKey hive, string subKey)
    {
        var states = new Dictionary<string, (bool, DateTime?)>(StringComparer.OrdinalIgnoreCase);

        try
        {
            using RegistryKey? key = hive.OpenSubKey($@"{ApprovedPath}\{subKey}");
            if (key is null)
                return states;

            foreach (string name in key.GetValueNames())
            {
                if (key.GetValue(name) is not byte[] { Length: >= 1 } value)
                    continue;

                bool enabled = (value[0] & 1) == 0;
                DateTime? disabledAt = null;

                if (!enabled && value.Length >= 12)
                {
                    long filetime = BitConverter.ToInt64(value, 4);
                    if (filetime > 0)
                    {
                        try
                        {
                            disabledAt = DateTime.FromFileTime(filetime);
                        }
                        catch (ArgumentOutOfRangeException)
                        {
                            // Ein unplausibler Zeitstempel ist kein Grund, den
                            // Zustand zu verwerfen — der steht im ersten Byte.
                        }
                    }
                }

                states[name] = (enabled, disabledAt);
            }
        }
        catch (Exception ex) when (ex is SecurityException or UnauthorizedAccessException or IOException)
        {
            DiagnosticLog.Report(Source, ex, $"Der Zustand unter »StartupApproved\\{subKey}« ließ sich nicht lesen");
        }

        return states;
    }

    private static void ReadStartupFolder(List<StartupEntry> entries, string folder, StartupSource source)
    {
        if (string.IsNullOrEmpty(folder) || !Directory.Exists(folder))
            return;

        IReadOnlyDictionary<string, (bool Enabled, DateTime? DisabledAt)> approved =
            ReadApproved(source == StartupSource.UserStartupFolder ? Registry.CurrentUser : Registry.LocalMachine,
                "StartupFolder");

        try
        {
            foreach (string file in Directory.EnumerateFiles(folder))
            {
                string name = Path.GetFileName(file);
                if (name.Equals("desktop.ini", StringComparison.OrdinalIgnoreCase))
                    continue;

                // Eine Verknüpfung nennt ihr Ziel nicht im Dateinamen; ohne das
                // Ziel ließe sich weder prüfen, ob es die Datei noch gibt, noch
                // der Eintrag der gemessenen Startkette zuordnen.
                string target = Path.GetExtension(file).Equals(".lnk", StringComparison.OrdinalIgnoreCase)
                    ? ShellLink.ReadTarget(file) ?? file
                    : file;

                (bool enabled, DateTime? disabledAt) = approved.TryGetValue(name, out var state)
                    ? state
                    : (true, (DateTime?)null);

                entries.Add(Describe(new StartupEntry(Path.GetFileNameWithoutExtension(name), source, target)
                {
                    Enabled = enabled,
                    DisabledAt = disabledAt,
                    Detail = folder,
                }));
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            DiagnosticLog.Report(Source, ex, $"Der Startordner »{folder}« ließ sich nicht lesen");
        }
    }

    /// <summary>
    /// Geplante Aufgaben mit Anmelde- oder Startauslöser, gelesen aus den
    /// XML-Dateien unter <c>%windir%\System32\Tasks</c>.
    /// </summary>
    /// <remarks>
    /// Die Dateien sind die Aufgabendefinition selbst, dieselbe, die die
    /// Aufgabenplanung schreibt. Der Weg über die Dateien statt über die
    /// COM-Schnittstelle spart eine Interop-Schicht für Daten, die ohnehin als
    /// XML vorliegen — und der Auslöser samt seiner Verzögerung steht dort im
    /// Klartext. Der Ordner ist nur erhöht lesbar; die Anwendung läuft erhöht
    /// (DESIGN.md §14).
    /// </remarks>
    private static void ReadScheduledTasks(List<StartupEntry> entries)
    {
        string root = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.System), "Tasks");

        if (!Directory.Exists(root))
            return;

        try
        {
            foreach (string file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
            {
                if (ReadTask(file, root) is { } entry)
                    entries.Add(entry);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            DiagnosticLog.Report(Source, ex, "Die Aufgabenplanung ließ sich nicht lesen");
        }
    }

    private static StartupEntry? ReadTask(string file, string root)
    {
        try
        {
            XDocument document = XDocument.Load(file);
            XNamespace ns = document.Root?.Name.Namespace ?? XNamespace.None;

            XElement? triggers = document.Root?.Element(ns + "Triggers");
            XElement? trigger = triggers?.Elements()
                .FirstOrDefault(t => t.Name.LocalName is "LogonTrigger" or "BootTrigger");

            if (trigger is null)
                return null;

            // Ein abgeschalteter Auslöser zählt nicht, auch wenn die Aufgabe
            // selbst aktiviert ist.
            bool triggerEnabled = trigger.Element(ns + "Enabled")?.Value is not "false";
            bool taskEnabled = document.Root?.Element(ns + "Settings")?.Element(ns + "Enabled")?.Value is not "false";

            XElement? actions = document.Root?.Element(ns + "Actions");
            XElement? exec = actions?.Element(ns + "Exec");

            string kind = trigger.Name.LocalName == "BootTrigger" ? "Beim Start" : "Bei Anmeldung";
            string? delay = trigger.Element(ns + "Delay")?.Value;

            // Eine Aufgabe mit Verzögerung hält den Start nicht auf — das ist der
            // Unterschied zwischen „läuft beim Anmelden“ und „läuft in einer
            // Stunde“. Ohne diese Angabe stünden beide gleichwertig in der Liste.
            string detail = delay is { Length: > 0 } ? $"{kind}, +{FormatDelay(delay)}" : kind;
            string name = Path.GetRelativePath(root, file).Replace('\\', '/');

            // Nicht jede Aufgabe startet ein Programm. Der überwiegende Teil der
            // Windows-eigenen ruft über einen COM-Handler eine DLL im Kontext des
            // Aufgabenplaners auf — das ist der Normalfall und kein Defekt. Solche
            // Aufgaben durch Describe zu schicken hieße, sie als „leerer Eintrag“
            // zu melden: auf der Referenzmaschine waren das einundzwanzig von
            // sechsundzwanzig Befunden, und die fünf echten gingen darin unter.
            if (exec is null)
            {
                string? handler = actions?.Element(ns + "ComHandler")?.Element(ns + "ClassId")?.Value;
                return new StartupEntry(name, StartupSource.ScheduledTask, handler ?? string.Empty)
                {
                    Enabled = taskEnabled && triggerEnabled,
                    Detail = handler is null ? detail : $"{detail}, COM-Handler",
                };
            }

            string command = exec.Element(ns + "Command")?.Value ?? string.Empty;
            string? arguments = exec.Element(ns + "Arguments")?.Value;

            return Describe(new StartupEntry(
                name,
                StartupSource.ScheduledTask,
                string.IsNullOrEmpty(arguments) ? command : $"{Quote(command)} {arguments}")
            {
                Enabled = taskEnabled && triggerEnabled,
                Detail = detail,
            });
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Xml.XmlException)
        {
            // Eine einzelne unlesbare Aufgabe darf das Inventar nicht anhalten.
            return null;
        }
    }

    /// <summary>Macht aus der ISO-8601-Dauer einer Aufgabe eine lesbare Angabe.</summary>
    private static string FormatDelay(string iso)
    {
        try
        {
            TimeSpan delay = System.Xml.XmlConvert.ToTimeSpan(iso);
            return delay.TotalHours >= 1
                ? $"{delay.TotalHours:0.#} h"
                : delay.TotalMinutes >= 1
                    ? $"{delay.TotalMinutes:0} min"
                    : $"{delay.TotalSeconds:0} s";
        }
        catch (FormatException)
        {
            return iso;
        }
    }

    /// <summary>
    /// Dienste mit Starttyp „Automatisch“. Der verzögerte Start steht nicht in
    /// WMI, sondern als <c>DelayedAutostart</c> am Dienstschlüssel.
    /// </summary>
    private static void ReadServices(List<StartupEntry> entries)
    {
        try
        {
            using var searcher = new ManagementObjectSearcher(new ObjectQuery(
                "SELECT Name, DisplayName, PathName, State, ProcessId FROM Win32_Service WHERE StartMode = 'Auto'"));
            using ManagementObjectCollection results = searcher.Get();

            foreach (ManagementBaseObject item in results)
            {
                using (item)
                {
                    if (item["Name"] as string is not { Length: > 0 } name)
                        continue;

                    string display = item["DisplayName"] as string ?? name;
                    string command = item["PathName"] as string ?? string.Empty;
                    bool running = (item["State"] as string) == "Running";
                    int pid = item["ProcessId"] is uint raw ? (int)raw : 0;
                    bool delayed = IsDelayedStart(name);

                    StartupIssue issues = StartupIssue.None;
                    if (!running)
                        issues |= StartupIssue.NotRunning;
                    if (delayed)
                        issues |= StartupIssue.DelayedStart;

                    entries.Add(Describe(new StartupEntry(display, StartupSource.Service, command)
                    {
                        Pid = pid > 0 ? pid : null,
                        Detail = delayed ? "Automatisch (verzögert)" : "Automatisch",
                        Issues = issues,
                    }));
                }
            }
        }
        catch (Exception ex) when (ex is ManagementException or UnauthorizedAccessException)
        {
            DiagnosticLog.Report(Source, ex, "Die Dienstliste ließ sich nicht abfragen");
        }
    }

    private static bool IsDelayedStart(string service)
    {
        try
        {
            using RegistryKey? key = Registry.LocalMachine.OpenSubKey($@"{ServicesPath}\{service}");
            return key?.GetValue("DelayedAutostart") is int flag && flag == 1;
        }
        catch (Exception ex) when (ex is SecurityException or UnauthorizedAccessException or IOException)
        {
            return false;
        }
    }

    /// <summary>
    /// Startaufgaben von Store-Anwendungen. Sie stehen weder in einem
    /// Run-Schlüssel noch im Startordner, tauchen im Task-Manager aber in
    /// derselben Liste auf.
    /// </summary>
    /// <remarks>
    /// <c>State</c> ist hier keine Ja-Nein-Angabe: 0 deaktiviert, 1 aktiviert,
    /// 2 vom Benutzer aktiviert, 3 vom Benutzer deaktiviert. Gerade Werte als
    /// „aus“ zu lesen wäre die Regel der Run-Schlüssel — hier gilt sie nicht.
    /// </remarks>
    private static void ReadAppxTasks(List<StartupEntry> entries)
    {
        try
        {
            using RegistryKey? root = Registry.CurrentUser.OpenSubKey(AppModelPath);
            if (root is null)
                return;

            foreach (string package in root.GetSubKeyNames())
            {
                using RegistryKey? packageKey = root.OpenSubKey(package);
                if (packageKey is null)
                    continue;

                foreach (string task in packageKey.GetSubKeyNames())
                {
                    using RegistryKey? taskKey = packageKey.OpenSubKey(task);
                    if (taskKey?.GetValue("State") is not int state)
                        continue;

                    entries.Add(new StartupEntry(task, StartupSource.AppxTask, package)
                    {
                        Enabled = state is 1 or 2,
                        Detail = FamilyName(package),
                        FileExists = true,
                    });
                }
            }
        }
        catch (Exception ex) when (ex is SecurityException or UnauthorizedAccessException or IOException)
        {
            DiagnosticLog.Report(Source, ex, "Die Startaufgaben der Store-Anwendungen ließen sich nicht lesen");
        }
    }

    /// <summary>Der Paketname ohne den Herausgeber-Hash dahinter.</summary>
    private static string FamilyName(string package)
    {
        int cut = package.LastIndexOf('_');
        return cut > 0 ? package[..cut] : package;
    }

    /// <summary>
    /// Ergänzt einen Eintrag um alles, was aus seiner Befehlszeile folgt: Pfad,
    /// Argumente, Herausgeber und die Auffälligkeiten.
    /// </summary>
    private static StartupEntry Describe(StartupEntry entry)
    {
        if (string.IsNullOrWhiteSpace(entry.Command))
            return entry with { Issues = entry.Issues | StartupIssue.EmptyCommand, FileExists = false };

        (string? image, string? arguments) = SplitCommand(entry.Command);
        StartupIssue issues = entry.Issues;
        bool? exists = null;
        string? publisher = null;
        string? description = null;

        if (image is not null)
        {
            try
            {
                // Ein Befehl ohne Verzeichnis verlässt sich auf den Suchpfad —
                // „winget.exe“ etwa, das als Ausführungsalias unter WindowsApps
                // liegt. File.Exists prüft dann gegen das Arbeitsverzeichnis und
                // meldet zuverlässig „fehlt“, obwohl das Programm da ist.
                image = Resolve(image) ?? image;
                exists = File.Exists(image);

                if (exists == false && Path.IsPathRooted(image))
                    issues |= StartupIssue.MissingFile;
                else if (exists == false)
                {
                    // Nicht auffindbar und ohne Pfadangabe: das ist keine
                    // Aussage, sondern eine unbeantwortbare Frage. Eine leere
                    // Zelle ist ehrlicher als ein falscher Befund.
                    exists = null;
                }
                else
                {
                    FileVersionInfo version = FileVersionInfo.GetVersionInfo(image);
                    publisher = Blank(version.CompanyName);
                    description = Blank(version.FileDescription);
                }

                issues |= ClassifyPath(image);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
            {
                // Ein Pfad, der sich nicht prüfen lässt, ist keine Aussage — der
                // Eintrag bleibt ohne Merkmal stehen, statt „fehlt“ zu behaupten.
            }
        }

        return entry with
        {
            ImagePath = image,
            Arguments = arguments,
            FileExists = exists,
            Publisher = publisher,
            Description = description,
            Issues = issues,
        };
    }

    /// <summary>
    /// Sucht einen Befehl ohne Verzeichnisangabe im Suchpfad, so wie Windows es
    /// beim Starten täte. Liefert <c>null</c>, wenn er dort nicht steht.
    /// </summary>
    private static string? Resolve(string image)
    {
        if (Path.IsPathRooted(image) || image.Contains(Path.DirectorySeparatorChar))
            return null;

        string? search = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrEmpty(search))
            return null;

        foreach (string folder in search.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            try
            {
                string candidate = Path.Combine(folder.Trim(), image);
                if (File.Exists(candidate))
                    return candidate;
            }
            catch (ArgumentException)
            {
                // Ein Eintrag im Suchpfad mit unzulässigen Zeichen — überspringen.
            }
        }

        return null;
    }

    /// <summary>
    /// Merkmale, die sich am Pfad allein ablesen lassen. Alle drei sind bekannte
    /// Ursachen für einen hängenden Start beziehungsweise für Einträge, die dort
    /// nichts zu suchen haben.
    /// </summary>
    private static StartupIssue ClassifyPath(string image)
    {
        StartupIssue issues = StartupIssue.None;

        if (image.StartsWith(@"\\", StringComparison.Ordinal))
            return StartupIssue.NetworkPath;

        try
        {
            string? root = Path.GetPathRoot(image);
            if (!string.IsNullOrEmpty(root) && root.Length >= 2 && root[1] == ':')
            {
                var drive = new DriveInfo(root);
                issues |= drive.DriveType switch
                {
                    DriveType.Network => StartupIssue.NetworkPath,
                    DriveType.Removable or DriveType.CDRom => StartupIssue.RemovablePath,
                    _ => StartupIssue.None,
                };
            }
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException)
        {
            // Ein nicht bereitstehendes Laufwerk wirft hier; das ist für sich
            // genommen noch kein Befund.
        }

        string temp = Path.GetTempPath().TrimEnd('\\');
        if (temp.Length > 0 && image.StartsWith(temp, StringComparison.OrdinalIgnoreCase))
            issues |= StartupIssue.TempPath;

        return issues;
    }

    /// <summary>
    /// Trennt die ausführbare Datei von den Argumenten.
    /// </summary>
    /// <remarks>
    /// Bei einem Pfad in Anführungszeichen ist das eindeutig. Ohne
    /// Anführungszeichen ist es das nicht — <c>C:\Program Files\App\a.exe -x</c>
    /// kann auch <c>C:\Program</c> mit den Argumenten <c>Files\App\a.exe -x</c>
    /// meinen, und genau diese Mehrdeutigkeit ist der bekannte
    /// „unquoted service path“.
    /// <para>
    /// Der verlässlichste Anker ist die <b>erste Endung <c>.exe</c></b>, nicht das
    /// erste Leerzeichen und auch nicht die erste existierende Datei. Ein Schnitt
    /// am Leerzeichen zerlegt <c>Docker Desktop.exe</c> in der Mitte; eine Suche
    /// nach der ersten existierenden Datei findet für eine deinstallierte
    /// Anwendung gar nichts und meldet dann <c>C:\Program</c> als fehlende Datei
    /// statt des tatsächlichen Pfades. Die Endung trifft beide Fälle und schneidet
    /// zugleich bei <c>Update.exe --processStart Discord.exe</c> an der richtigen
    /// der beiden Nennungen.
    /// </para>
    /// </remarks>
    private static (string? Image, string? Arguments) SplitCommand(string command)
    {
        string text = Environment.ExpandEnvironmentVariables(command.Trim());
        if (text.Length == 0)
            return (null, null);

        if (text[0] == '"')
        {
            int close = text.IndexOf('"', 1);
            return close < 0
                ? (text.Trim('"'), null)
                : (text[1..close], Blank(text[(close + 1)..].Trim()));
        }

        int extension = text.IndexOf(".exe", StringComparison.OrdinalIgnoreCase);
        if (extension >= 0)
            return (text[..(extension + 4)], Blank(text[(extension + 4)..].Trim()));

        // Ohne Endung — Skripte, Verknüpfungen, Aufrufe über den Pfad. Dann bleibt
        // nur, die Möglichkeiten der Reihe nach durchzuprobieren, wie Windows es
        // auch tut.
        for (int i = text.IndexOf(' '); i > 0; i = text.IndexOf(' ', i + 1))
        {
            if (File.Exists(text[..i]))
                return (text[..i], Blank(text[(i + 1)..].Trim()));
        }

        return File.Exists(text) || !text.Contains(' ')
            ? (text, null)
            : (text[..text.IndexOf(' ')], Blank(text[(text.IndexOf(' ') + 1)..].Trim()));
    }

    private static string Quote(string value)
        => value.Contains(' ') && !value.StartsWith('"') ? $"\"{value}\"" : value;

    private static string? Blank(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value;
}

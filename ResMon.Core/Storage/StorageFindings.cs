using System.Globalization;
using System.IO;
using ResMon.Core.Startup;

namespace ResMon.Core.Storage;

/// <summary>
/// Ein Befund zum Speicherplatz: ein Ort, an dem Platz liegt, samt dem, was man
/// dagegen tun kann und was es kostet.
/// </summary>
/// <param name="Severity">Einstufung, hier nach freizumachender Menge.</param>
/// <param name="Title">Was gefunden wurde, in einem Satz.</param>
/// <param name="Why">Warum dort Platz liegt und was der Ort überhaupt ist.</param>
public sealed record StorageFinding(FindingSeverity Severity, string Title, string Why)
{
    /// <summary>Wie viel dort liegt. Nicht zwingend, wie viel frei würde.</summary>
    public long? Bytes { get; init; }

    /// <summary>Der Fundort, wie ihn der Baum führt.</summary>
    public string? Path { get; init; }

    /// <summary>
    /// Die Kennung des Knotens im Baum. Damit kommen „Im Explorer öffnen“ und
    /// „Pfad kopieren“ ohne neuen Weg aus: der Host schlägt den Pfad in seinem
    /// eigenen Ergebnis nach, statt einen von der Seite gereichten zu übernehmen.
    /// </summary>
    public int? NodeId { get; init; }

    /// <summary>Woher die Zahl stammt — ohne Fundstelle wäre der Befund eine Behauptung.</summary>
    public string? Evidence { get; init; }

    /// <summary>
    /// Der Handgriff als Befehle zum Kopieren, in der Reihenfolge, in der sie
    /// auszuführen sind. Leer, wenn es keinen gibt oder wenn ausdrücklich nichts
    /// zu tun ist.
    /// </summary>
    /// <remarks>
    /// Eine Liste und kein einzelner Befehl, weil die lohnendsten Handgriffe
    /// mehrschrittig sind und der erste Schritt allein nichts bringt: bei einem
    /// virtuellen Datenträger gibt erst das Kompaktieren nach dem Aufräumen den
    /// Platz an Windows zurück, und der Update-Zwischenspeicher will den Dienst
    /// angehalten und wieder gestartet haben. Ein Befund, der nur den ersten
    /// Schritt zeigt, führt in die Irre — und zwar besonders tückisch, weil der
    /// erste Schritt erfolgreich durchläuft.
    /// </remarks>
    public IReadOnlyList<string> Commands { get; init; } = [];

    /// <summary>
    /// Was der Handgriff kostet. Ein Befund, der nur den Gewinn nennt, ist eine
    /// Verkaufsanzeige — deshalb ist dieses Feld die Regel und nicht die Ausnahme.
    /// </summary>
    public string? Caveat { get; init; }
}

/// <summary>
/// Deutet das Ergebnis eines Ordner-Scans: welcher der großen Posten wofür steht
/// und wie man ihn los wird.
/// </summary>
/// <remarks>
/// Der Baum ist eine Landkarte — er sagt, <b>wo</b> der Platz liegt. Er kann nicht
/// sagen, was ein Ordner bedeutet. Dass <c>docker_data.vhdx</c> 38 GiB groß ist,
/// steht dort; dass eine solche Datei mitwächst und beim Löschen im Container
/// <em>nicht</em> wieder schrumpft, steht nirgends. Genau diesen Schritt macht
/// diese Auswertung, in derselben Form wie die Startbefunde (DESIGN.md §8.12):
/// Befund, Beleg, Handgriff — und der Vorbehalt dazu.
/// <para>
/// Die Regeln schlagen bekannte Orte über <see cref="FolderScanResult.FindByPath"/>
/// gezielt nach, statt den Baum abzusuchen. Ein Ort, den es auf dieser Partition
/// nicht gibt, kostet damit einen fehlgeschlagenen Nachschlag und sonst nichts.
/// </para>
/// <para>
/// <b>Nichts hiervon wird ausgeführt.</b> Die Anwendung läuft erhöht; ein
/// Fehlgriff träfe Systemordner ohne Papierkorb und ohne Rückgängig (DESIGN.md
/// §13.5). Der Befund legt den Befehl bereit, ausgelöst wird er anderswo.
/// </para>
/// </remarks>
public static class StorageFindings
{
    /// <summary>
    /// Ab hier lohnt ein Befund. Darunter steht der Aufwand in keinem Verhältnis
    /// — und eine Liste, in der Belanglosigkeiten stehen, liest niemand zu Ende.
    /// </summary>
    private const long Threshold = 1L * 1024 * 1024 * 1024;

    /// <summary>Für Posten, bei denen schon wenig auffällig ist.</summary>
    private const long SmallThreshold = 256L * 1024 * 1024;

    /// <summary>Ab dieser Größe lohnt das Verlagern eines Benutzerordners.</summary>
    private const long MoveThreshold = 5L * 1024 * 1024 * 1024;

    /// <summary>Ab dieser Belegung gilt eine Partition als eng.</summary>
    private const double TightShare = 0.90;

    /// <summary>Eine andere Partition muss so viel mehr frei haben, um als Ziel zu taugen.</summary>
    private const long MoveTargetHeadroom = 32L * 1024 * 1024 * 1024;

    /// <summary>Ein Ort im Baum: Kennung und Summe in einem.</summary>
    private readonly record struct Spot(int Id, long Bytes);

    public static IReadOnlyList<StorageFinding> Collect(FolderScanResult scan)
    {
        var findings = new List<StorageFinding>();

        AddVolumePressure(findings, scan);
        AddHibernation(findings, scan);
        AddComponentStore(findings, scan);
        AddUpdateCache(findings, scan);
        AddVirtualDisks(findings, scan);
        AddPackageCaches(findings, scan);
        AddTemporary(findings, scan);
        AddRecycleBin(findings, scan);
        AddInstallerStore(findings, scan);
        AddAppData(findings, scan);
        AddMoveCandidates(findings, scan);

        // Nach Menge ordnen — die Frage lautet „was bringt am meisten“.
        // Davor stehen die Befunde ohne Menge, die den Maßstab setzen: dass die
        // Partition eng ist, gewinnt keinen einzigen Kilobyte und entscheidet
        // trotzdem, ob die Zahlen darunter überhaupt der Rede wert sind.
        return [.. findings
            .OrderBy(finding => finding.Bytes is null && finding.Severity == FindingSeverity.High ? 0 : 1)
            .ThenByDescending(finding => finding.Bytes ?? -1)
            .ThenBy(finding => finding.Severity)];
    }

    /// <summary>Schlägt einen Pfad nach. <c>null</c>, wenn er nicht im Baum steht.</summary>
    private static Spot? Locate(FolderScanResult scan, string? path)
    {
        if (string.IsNullOrEmpty(path))
            return null;

        int id = scan.FindByPath(path);
        return id < 0 ? null : new Spot(id, scan.BytesOf(id));
    }

    /// <summary>
    /// Ein Sonderordner, aber nur, wenn er auf der durchsuchten Partition liegt.
    /// Auf einem Rechner mit umgezogenem Profil zeigt <c>%LocalAppData%</c> auf
    /// eine andere Partition, und dann gehört der Befund nicht hierher.
    /// </summary>
    private static string? Special(FolderScanResult scan, Environment.SpecialFolder folder, params string[] parts)
    {
        string root = Environment.GetFolderPath(folder);
        if (string.IsNullOrEmpty(root))
            return null;

        string full = parts.Length == 0 ? root : Path.Combine([root, .. parts]);
        return full.StartsWith(scan.Root.TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase) ? full : null;
    }

    private static string Gib(long bytes) => (bytes / 1073741824.0).ToString("N1");

    private static FindingSeverity Weigh(long bytes) => bytes switch
    {
        >= 10L * 1024 * 1024 * 1024 => FindingSeverity.High,
        >= Threshold => FindingSeverity.Medium,
        _ => FindingSeverity.Hint,
    };

    /// <summary>
    /// Wie voll die Partition ist. Steht als erster Befund da, weil er den
    /// Maßstab für alle folgenden setzt: 3 GiB freizumachen ist auf einer
    /// halbleeren Platte belanglos und auf einer vollen die Rettung.
    /// </summary>
    private static void AddVolumePressure(List<StorageFinding> findings, FolderScanResult scan)
    {
        if (scan.VolumeTotalBytes <= 0)
            return;

        long used = scan.VolumeTotalBytes - scan.VolumeFreeBytes;
        double share = (double)used / scan.VolumeTotalBytes;
        if (share < TightShare)
            return;

        // Ohne Menge: dieser Befund gibt nichts frei, er sagt nur, wie dringend
        // die anderen sind. Stünde die freie Menge im Zahlenfeld, läse sie sich
        // in einer nach Gewinn sortierten Liste als Gewinn.
        findings.Add(new StorageFinding(
            FindingSeverity.High,
            $"{scan.Root} ist zu {share:P0} belegt — {Gib(scan.VolumeFreeBytes)} GiB frei",
            "Windows braucht freien Platz für die Auslagerungsdatei, für Updates und für " +
            "Schattenkopien. Unter etwa 10 % beginnt das System, sich zu wehren — Updates " +
            "schlagen fehl, und die Auslagerungsdatei kann nicht mehr wachsen.")
        {
            Path = scan.Root,
            NodeId = 0,
            Evidence = "Freier Platz laut Dateisystem",
        });
    }

    /// <summary>
    /// Die Ruhezustandsdatei. Einer der wenigen Posten, bei denen ein einziger
    /// Befehl die volle Menge freigibt — und bei dem der Vorbehalt ebenso klar ist.
    /// </summary>
    private static void AddHibernation(List<StorageFinding> findings, FolderScanResult scan)
    {
        string path = Path.Combine(scan.Root, "hiberfil.sys");
        if (Locate(scan, path) is not { } spot || spot.Bytes < SmallThreshold)
            return;

        findings.Add(new StorageFinding(
            Weigh(spot.Bytes),
            "Ruhezustandsdatei belegt Platz in Höhe eines Teils des Arbeitsspeichers",
            "In diese Datei schreibt Windows den Arbeitsspeicher, wenn der Rechner in den " +
            "Ruhezustand geht. Ihre Größe richtet sich nach dem verbauten RAM, nicht nach " +
            "der Benutzung — sie schrumpft von allein nie.")
        {
            Bytes = spot.Bytes,
            Path = path,
            NodeId = spot.Id,
            Evidence = "Großdatei im Wurzelverzeichnis",
            Commands = ["powercfg /h off"],
            Caveat = "Danach gibt es keinen Ruhezustand mehr, und der Schnellstart von Windows " +
                     "entfällt ebenfalls — der Rechner startet dadurch spürbar langsamer. " +
                     "Rückgängig mit „powercfg /h on“.",
        });
    }

    /// <summary>
    /// Der Komponentenspeicher. Der wichtigste Befund der ganzen Liste, weil die
    /// gemessene Zahl hier <b>nicht</b> stimmt und ein Werkzeug, das sie
    /// unkommentiert anzeigt, zum Löschen von Hand verleitet.
    /// </summary>
    private static void AddComponentStore(List<StorageFinding> findings, FolderScanResult scan)
    {
        string path = Path.Combine(scan.Root, "Windows", "WinSxS");
        if (Locate(scan, path) is not { } spot || spot.Bytes < Threshold)
            return;

        findings.Add(new StorageFinding(
            FindingSeverity.Medium,
            "Komponentenspeicher WinSxS — die gemessene Größe ist deutlich zu hoch",
            "Hier liegen die Windows-Bausteine samt ihrer Vorgängerversionen. Der größte Teil " +
            "davon sind harte Verknüpfungen auf Dateien, die auch in System32 liegen: " +
            "derselbe Inhalt zählt in dieser Messung zweimal, belegt auf dem Datenträger " +
            "aber nur einmal. Tatsächlich frei wird also erheblich weniger als hier steht.")
        {
            Bytes = spot.Bytes,
            Path = path,
            NodeId = spot.Id,
            Evidence = "Ordnersumme des Scans, logische Größe (README „Abweichungen“)",
            Commands = ["DISM /Online /Cleanup-Image /StartComponentCleanup"],
            Caveat = "Diesen Ordner niemals von Hand leeren — Windows lässt sich danach nicht " +
                     "mehr aktualisieren und teilweise nicht mehr starten. Der Befehl entfernt " +
                     "nur die abgelösten Versionen; mit „/ResetBase“ zusätzlich die " +
                     "Rücknahmemöglichkeit für installierte Updates.",
        });
    }

    private static void AddUpdateCache(List<StorageFinding> findings, FolderScanResult scan)
    {
        string path = Path.Combine(scan.Root, "Windows", "SoftwareDistribution", "Download");
        if (Locate(scan, path) is not { } spot || spot.Bytes < SmallThreshold)
            return;

        findings.Add(new StorageFinding(
            Weigh(spot.Bytes),
            "Zwischenlager der Windows-Updates",
            "Hierhin lädt Windows Update seine Pakete, bevor es sie einspielt. Nach dem " +
            "Einspielen bleiben sie liegen; gebraucht werden sie dann nicht mehr.")
        {
            Bytes = spot.Bytes,
            Path = path,
            NodeId = spot.Id,
            Evidence = "Ordnersumme des Scans",
            Commands =
            [
                "net stop wuauserv",
                $"del /q /s \"{path}\\*\"",
                "net start wuauserv",
            ],
            Caveat = "Der mittlere Schritt löscht; die beiden anderen halten den Dienst " +
                     "solange an, weil er die Dateien sonst offen hält. Ein angefangenes " +
                     "Update muss danach neu geladen werden. Die Datenträgerbereinigung von " +
                     "Windows erledigt dasselbe ohne Handgriffe an Diensten.",
        });
    }

    /// <summary>
    /// Virtuelle Festplatten von Docker und Hyper-V. Der Fall, an dem die
    /// Landkarte am deutlichsten zu wenig sagt: die Datei ist groß, und die
    /// naheliegende Reaktion — innerhalb der Maschine aufräumen — ändert daran
    /// nichts.
    /// </summary>
    private static void AddVirtualDisks(List<StorageFinding> findings, FolderScanResult scan)
    {
        AddDiskImages(
            findings, scan,
            Special(scan, Environment.SpecialFolder.LocalApplicationData, "Docker", "wsl", "disk"),
            "Virtueller Datenträger von Docker",
            "Diese Datei ist die Festplatte der Linux-Maschine, in der Docker läuft. Sie " +
            "wächst mit und wird von allein nie wieder kleiner. Aufräumen innerhalb von " +
            "Docker gibt den Platz nur innen frei — nach außen bleibt die Datei so groß " +
            "wie zuvor, bis man sie ausdrücklich kompaktiert. Von hier aus ist nicht zu " +
            "sehen, wie viel davon noch belegt ist; das beantwortet „docker system df“.",
            [
                "docker system df",
                "docker system prune -a --volumes",
                "wsl --shutdown",
                "Optimize-VHD -Path \"{0}\" -Mode Full",
            ],
            "Der Reihe nach: „df“ zeigt, ob innen überhaupt noch etwas zu holen ist — steht " +
            "dort bei RECLAIMABLE eine 0, bringt „prune“ nichts mehr und der Schritt " +
            "entfällt. „prune“ löscht alle nicht laufenden Container, ungenutzten Abbilder " +
            "und Volumes, unwiderruflich und samt der Daten darin. Erst „Optimize-VHD“ gibt " +
            "den Platz an Windows zurück, und das nur bei angehaltenem Docker — es braucht " +
            "das Hyper-V-Modul. Dieselbe Wirkung hat in Docker Desktop die Schaltfläche " +
            "unter Einstellungen, Resources, Advanced.");

        AddDiskImages(
            findings, scan,
            Path.Combine(scan.Root, "ProgramData", "Microsoft", "Windows", "Virtual Hard Disks"),
            "Festplatte einer virtuellen Maschine",
            "Die Datei enthält das gesamte Betriebssystem einer virtuellen Maschine von " +
            "Hyper-V. Sie wächst mit der Benutzung und wird beim Löschen innerhalb der " +
            "Maschine nicht wieder kleiner.",
            ["Optimize-VHD -Path \"{0}\" -Mode Full"],
            "Wird die Maschine nicht mehr gebraucht, gehört sie im Hyper-V-Manager gelöscht " +
            "— dabei ist alles darin fort. Wird sie noch gebraucht, verkleinert der Befehl " +
            "die Datei, ohne Inhalte anzutasten; die Maschine muss dafür ausgeschaltet sein.");
    }

    /// <summary>
    /// Die Abbilddateien in einem Ordner, jede als eigener Befund. In den
    /// Befehlsvorlagen steht <c>{0}</c> für den vollen Pfad der Datei — er ist
    /// lang und enthält Leerzeichen, und ein Befehl, den man erst noch von Hand
    /// vervollständigen muss, wird falsch abgetippt.
    /// </summary>
    private static void AddDiskImages(
        List<StorageFinding> findings,
        FolderScanResult scan,
        string? folder,
        string title,
        string why,
        IReadOnlyList<string> commandTemplates,
        string caveat)
    {
        if (folder is null || Locate(scan, folder) is not { } parent)
            return;

        foreach (FolderSlice child in scan.ChildrenOf(parent.Id))
        {
            if (!child.IsFile || child.TotalBytes < Threshold)
                continue;

            if (!child.Name.EndsWith(".vhdx", StringComparison.OrdinalIgnoreCase) &&
                !child.Name.EndsWith(".vhd", StringComparison.OrdinalIgnoreCase))
                continue;

            string full = Path.Combine(folder, child.Name);

            findings.Add(new StorageFinding(Weigh(child.TotalBytes), $"{title}: {child.Name}", why)
            {
                Bytes = child.TotalBytes,
                Path = full,
                NodeId = child.Id,
                Evidence = "Großdatei im Scan",
                Commands = [.. commandTemplates.Select(template =>
                    string.Format(CultureInfo.InvariantCulture, template, full))],
                Caveat = caveat,
            });
        }
    }

    /// <summary>
    /// Paket-Zwischenlager der Entwicklungswerkzeuge. Der gutartigste Posten der
    /// Liste: alles darin ist jederzeit wieder zu beziehen.
    /// </summary>
    private static void AddPackageCaches(List<StorageFinding> findings, FolderScanResult scan)
    {
        (string? Path, string Label, string? Command)[] caches =
        [
            (Special(scan, Environment.SpecialFolder.UserProfile, ".nuget", "packages"),
                "NuGet-Pakete", "dotnet nuget locals all --clear"),
            (Special(scan, Environment.SpecialFolder.LocalApplicationData, "npm-cache"),
                "npm-Zwischenlager", "npm cache clean --force"),
            (Special(scan, Environment.SpecialFolder.LocalApplicationData, "pip", "Cache"),
                "pip-Zwischenlager", "pip cache purge"),
            (Special(scan, Environment.SpecialFolder.UserProfile, ".gradle", "caches"),
                "Gradle-Zwischenlager", null),
            (Special(scan, Environment.SpecialFolder.LocalApplicationData, "Yarn", "Cache"),
                "Yarn-Zwischenlager", "yarn cache clean"),
        ];

        foreach ((string? path, string label, string? command) in caches)
        {
            if (Locate(scan, path) is not { } spot || spot.Bytes < SmallThreshold)
                continue;

            findings.Add(new StorageFinding(
                Weigh(spot.Bytes),
                $"{label} — heruntergeladene Pakete",
                "Zwischenlager eines Paketwerkzeugs. Der Inhalt stammt vollständig aus dem " +
                "Netz und wird bei Bedarf neu geladen; nichts davon existiert nur hier.")
            {
                Bytes = spot.Bytes,
                Path = path,
                NodeId = spot.Id,
                Evidence = "Ordnersumme des Scans",
                Commands = command is null ? [] : [command],
                Caveat = "Der nächste Build lädt alles erneut herunter und dauert entsprechend " +
                         "länger. Ohne Netzverbindung schlägt er fehl.",
            });
        }
    }

    private static void AddTemporary(List<StorageFinding> findings, FolderScanResult scan)
    {
        (string? Path, string Label)[] folders =
        [
            (Special(scan, Environment.SpecialFolder.LocalApplicationData, "Temp"), "Temp-Ordner des Benutzers"),
            (Path.Combine(scan.Root, "Windows", "Temp"), "Temp-Ordner von Windows"),
        ];

        foreach ((string? path, string label) in folders)
        {
            if (Locate(scan, path) is not { } spot || spot.Bytes < SmallThreshold)
                continue;

            findings.Add(new StorageFinding(
                Weigh(spot.Bytes),
                label,
                "Ablage für Zwischenstände. Vieles darin gehört zu Programmen, die längst " +
                "beendet sind — aufgeräumt wird nur, wenn ein Programm es selbst tut.")
            {
                Bytes = spot.Bytes,
                Path = path,
                NodeId = spot.Id,
                Evidence = "Ordnersumme des Scans",
                Caveat = "Laufende Programme halten Dateien darin offen; die lassen sich nicht " +
                         "löschen und sollen es auch nicht. Die Speicheroptimierung von Windows " +
                         "räumt hier nach Alter auf und trifft dabei die richtige Auswahl.",
            });
        }
    }

    private static void AddRecycleBin(List<StorageFinding> findings, FolderScanResult scan)
    {
        string path = Path.Combine(scan.Root, "$Recycle.Bin");
        if (Locate(scan, path) is not { } spot || spot.Bytes < SmallThreshold)
            return;

        findings.Add(new StorageFinding(
            Weigh(spot.Bytes),
            "Papierkorb dieser Partition",
            "Gelöschte Dateien liegen weiter auf der Partition und zählen gegen den freien " +
            "Platz, bis der Papierkorb geleert wird.")
        {
            Bytes = spot.Bytes,
            Path = path,
            NodeId = spot.Id,
            Evidence = "Ordnersumme des Scans",
            Caveat = "Danach sind die Dateien endgültig fort. Vorher hineinsehen — der " +
                     "Papierkorb ist die letzte Stelle, an der ein Fehlgriff noch umkehrbar ist.",
        });
    }

    /// <summary>
    /// Ein Befund, dessen Handgriff ausdrücklich „nichts“ lautet. Der Ordner ist
    /// groß, taucht in jeder Anleitung im Netz als Löschkandidat auf und ist
    /// keiner — das gehört gesagt, gerade weil die Landkarte ihn prominent zeigt.
    /// </summary>
    private static void AddInstallerStore(List<StorageFinding> findings, FolderScanResult scan)
    {
        string path = Path.Combine(scan.Root, "Windows", "Installer");
        if (Locate(scan, path) is not { } spot || spot.Bytes < Threshold)
            return;

        findings.Add(new StorageFinding(
            FindingSeverity.Hint,
            "Installer-Ablage von Windows — groß, aber nicht zu löschen",
            "Hier liegen die Rücknahmedaten aller per MSI installierten Programme. Windows " +
            "braucht sie zum Reparieren, Aktualisieren und Deinstallieren.")
        {
            Bytes = spot.Bytes,
            Path = path,
            NodeId = spot.Id,
            Evidence = "Ordnersumme des Scans",
            Caveat = "Wird der Ordner geleert, lassen sich betroffene Programme weder " +
                     "aktualisieren noch sauber deinstallieren. Der Platz ist gebunden — " +
                     "er zählt hier nur mit, damit die Zahl niemanden in Versuchung führt.",
        });
    }

    /// <summary>
    /// Daten einzelner Anwendungen unter <c>Packages</c>. Anders als bei den
    /// bekannten Orten lässt sich hier nicht sagen, was der Inhalt bedeutet —
    /// deshalb nur der Hinweis und der Weg in den Explorer.
    /// </summary>
    private static void AddAppData(List<StorageFinding> findings, FolderScanResult scan)
    {
        string? packages = Special(scan, Environment.SpecialFolder.LocalApplicationData, "Packages");
        if (Locate(scan, packages) is not { } parent)
            return;

        foreach (FolderSlice app in scan.ChildrenOf(parent.Id, max: 20))
        {
            if (app.IsFile || app.TotalBytes < Threshold)
                continue;

            findings.Add(new StorageFinding(
                Weigh(app.TotalBytes),
                $"Anwendungsdaten von {PackageLabel(app.Name)}",
                "Eine installierte Anwendung legt hier ihre Daten und ihr Zwischenlager ab. " +
                "Was davon Zwischenlager ist und was gebraucht wird, weiß nur die Anwendung " +
                "selbst — meist gibt es in ihren Einstellungen einen Punkt dafür.")
            {
                Bytes = app.TotalBytes,
                Path = Path.Combine(packages!, app.Name),
                NodeId = app.Id,
                Evidence = "Ordnersumme des Scans",
                Caveat = "Von Hand gelöscht verliert die Anwendung ihre Anmeldung, ihren " +
                         "Verlauf und ihre Einstellungen. Erst in der Anwendung selbst nach " +
                         "einer Aufräumfunktion sehen.",
            });
        }
    }

    /// <summary>
    /// Macht aus <c>Claude_pzs8sxrjxfjjc</c> wieder <c>Claude</c>. Hinter dem
    /// letzten Unterstrich steht die Kennung des Herausgebers; sie ist für die
    /// Eindeutigkeit des Ordners nötig und für den Leser nur Rauschen.
    /// </summary>
    private static string PackageLabel(string folderName)
    {
        int separator = folderName.LastIndexOf('_');
        return separator > 0 ? folderName[..separator] : folderName;
    }

    /// <summary>
    /// Verschiebekandidaten: große Zweige mit Benutzerdaten, während eine andere
    /// Partition Luft hat. Das ist bei einer engen Systempartition oft die
    /// wirksamste Maßnahme und die einzige, die nichts löscht.
    /// </summary>
    private static void AddMoveCandidates(List<StorageFinding> findings, FolderScanResult scan)
    {
        if (scan.VolumeTotalBytes <= 0)
            return;

        double share = (double)(scan.VolumeTotalBytes - scan.VolumeFreeBytes) / scan.VolumeTotalBytes;
        if (share < TightShare)
            return;

        if (BestTarget(scan) is not { } target)
            return;

        Environment.SpecialFolder[] candidates =
        [
            Environment.SpecialFolder.MyDocuments,
            Environment.SpecialFolder.MyVideos,
            Environment.SpecialFolder.MyPictures,
            Environment.SpecialFolder.MyMusic,
        ];

        foreach (Environment.SpecialFolder folder in candidates)
        {
            string? path = Special(scan, folder);
            if (Locate(scan, path) is not { } spot || spot.Bytes < MoveThreshold)
                continue;

            findings.Add(new StorageFinding(
                Weigh(spot.Bytes),
                $"{Path.GetFileName(path)} könnte auf {target} liegen",
                $"Dieser Ordner enthält eigene Daten und muss nicht auf der Systempartition " +
                $"liegen. Auf {target} ist Platz. Windows kann solche Ordner offiziell " +
                "verlagern — über Rechtsklick, Eigenschaften, Reiter „Pfad“.")
            {
                Bytes = spot.Bytes,
                Path = path,
                NodeId = spot.Id,
                Evidence = $"Ordnersumme des Scans, freier Platz auf {target}",
                Caveat = "Das Verlagern über die Ordner-Eigenschaften verschiebt die Daten mit " +
                         "und hält alle Verweise gültig. Von Hand kopieren und dann löschen " +
                         "tut das nicht — Programme suchen danach ins Leere.",
            });
        }
    }

    /// <summary>
    /// Die Partition mit dem meisten freien Platz, sofern sie deutlich mehr hat
    /// als die durchsuchte. Wechselmedien und Netzlaufwerke scheiden aus: was
    /// heute Platz hat, muss morgen noch angesteckt sein.
    /// </summary>
    private static string? BestTarget(FolderScanResult scan)
    {
        string? best = null;
        long bestFree = scan.VolumeFreeBytes + MoveTargetHeadroom;

        foreach (DriveInfo drive in DriveInfo.GetDrives())
        {
            try
            {
                if (drive.DriveType != DriveType.Fixed || !drive.IsReady)
                    continue;

                if (string.Equals(drive.Name.TrimEnd('\\'), scan.Root.TrimEnd('\\'),
                        StringComparison.OrdinalIgnoreCase))
                    continue;

                if (drive.AvailableFreeSpace > bestFree)
                {
                    bestFree = drive.AvailableFreeSpace;
                    best = drive.Name.TrimEnd('\\');
                }
            }
            catch (IOException)
            {
                // Ein Laufwerk, das sich beim Abfragen entzieht, kommt als Ziel
                // ohnehin nicht in Frage.
            }
        }

        return best;
    }
}

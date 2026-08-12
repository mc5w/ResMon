namespace ResMon.Core.Startup;

/// <summary>Woher ein Autostart-Eintrag stammt.</summary>
/// <remarks>
/// Die Herkunft entscheidet, wer den Eintrag ausführt und wann — und damit, ob
/// eine Verzögerung überhaupt jemanden aufhält. Ein Run-Schlüssel wird vom
/// Explorer der Reihe nach abgearbeitet und blockiert alles Folgende; eine
/// geplante Aufgabe läuft nebenher und stört niemanden.
/// </remarks>
public enum StartupSource
{
    /// <summary>HKLM ..\Run — gilt für alle Benutzer.</summary>
    MachineRun,

    /// <summary>HKLM ..\WOW6432Node\..\Run — die 32-Bit-Sicht desselben Schlüssels.</summary>
    MachineRun32,

    /// <summary>HKCU ..\Run — nur für den angemeldeten Benutzer.</summary>
    UserRun,

    /// <summary>HKLM ..\RunOnce — wird nach dem Ausführen gelöscht.</summary>
    MachineRunOnce,

    /// <summary>HKCU ..\RunOnce.</summary>
    UserRunOnce,

    /// <summary>Startordner „Alle Benutzer“.</summary>
    MachineStartupFolder,

    /// <summary>Startordner des Benutzers.</summary>
    UserStartupFolder,

    /// <summary>Aufgabenplanung mit Auslöser „Bei Anmeldung“ oder „Beim Start“.</summary>
    ScheduledTask,

    /// <summary>Dienst mit Starttyp „Automatisch“, gegebenenfalls verzögert.</summary>
    Service,

    /// <summary>Startaufgabe einer Store-Anwendung.</summary>
    AppxTask,
}

/// <summary>
/// Auffälligkeiten an einem Eintrag. Bewusst nur, was sich ohne Deutung
/// feststellen lässt — „verdächtig“ ist keine Kategorie, „die Datei gibt es
/// nicht“ schon.
/// </summary>
[Flags]
public enum StartupIssue
{
    None = 0,

    /// <summary>Der Befehl zeigt auf eine Datei, die es nicht gibt.</summary>
    MissingFile = 1,

    /// <summary>Der Registry-Wert ist leer — ein Eintrag ohne Befehl.</summary>
    EmptyCommand = 2,

    /// <summary>
    /// Der Pfad liegt auf einem Netzlaufwerk oder einer UNC-Freigabe. Steht die
    /// Verbindung beim Anmelden noch nicht, wartet der Start auf das Zeitlimit
    /// des Redirectors.
    /// </summary>
    NetworkPath = 4,

    /// <summary>Der Pfad liegt auf einem Wechseldatenträger, der fehlen kann.</summary>
    RemovablePath = 8,

    /// <summary>Der Pfad liegt im temporären Verzeichnis oder bei den Downloads.</summary>
    TempPath = 16,

    /// <summary>Ein Dienst ist beim Start in ein Zeitlimit gelaufen.</summary>
    Timeout = 32,

    /// <summary>Der Eintrag hat die Startkette spürbar aufgehalten.</summary>
    SlowStart = 64,

    /// <summary>Automatisch startender Dienst, der nicht läuft.</summary>
    NotRunning = 128,

    /// <summary>Dienst mit verzögertem Start — läuft erst nach dem Anmelden an.</summary>
    DelayedStart = 256,
}

/// <summary>Ein Eintrag, der beim Start ausgeführt wird.</summary>
/// <param name="Name">Der Name des Registry-Werts, der Verknüpfung, der Aufgabe oder des Diensts.</param>
/// <param name="Source">Woher der Eintrag stammt.</param>
/// <param name="Command">Die Befehlszeile, wie sie hinterlegt ist.</param>
public sealed record StartupEntry(string Name, StartupSource Source, string Command)
{
    /// <summary>Die ausführbare Datei ohne Argumente, sofern sie sich herauslösen ließ.</summary>
    public string? ImagePath { get; init; }

    /// <summary>Die Argumente hinter der ausführbaren Datei.</summary>
    public string? Arguments { get; init; }

    /// <summary>
    /// Ob der Eintrag ausgeführt wird. Bei Run-Schlüsseln und Startordnern steht
    /// das nicht am Eintrag selbst, sondern unter <c>StartupApproved</c>.
    /// </summary>
    public bool Enabled { get; init; } = true;

    /// <summary>Wann der Eintrag abgeschaltet wurde, sofern vermerkt.</summary>
    public DateTime? DisabledAt { get; init; }

    /// <summary>Firma aus der Dateiversion — nicht aus der Signatur (siehe DESIGN.md §8.12).</summary>
    public string? Publisher { get; init; }

    /// <summary>Beschreibung aus der Dateiversion.</summary>
    public string? Description { get; init; }

    /// <summary>Ob die ausführbare Datei existiert. <c>null</c>, wenn kein Pfad ermittelt wurde.</summary>
    public bool? FileExists { get; init; }

    /// <summary>Die Prozesskennung, sofern der Eintrag beim letzten Start eine bekam.</summary>
    public int? Pid { get; init; }

    /// <summary>Abstand vom Beginn der Startkette bis zum Anlaufen dieses Eintrags.</summary>
    public TimeSpan? Offset { get; init; }

    /// <summary>
    /// Wie lange der Eintrag die Startkette belegt hat. Nur für das, was der
    /// Explorer ausführt — Dienste und Aufgaben tragen hier die Zeit aus dem
    /// jeweiligen Ereignis, sofern es eine gibt.
    /// </summary>
    public TimeSpan? Duration { get; init; }

    public StartupIssue Issues { get; init; }

    /// <summary>Erläuterung der Herkunft, etwa der Starttyp eines Diensts oder der Auslöser einer Aufgabe.</summary>
    public string? Detail { get; init; }

    /// <summary>Anzeigename der Herkunft für die Oberfläche.</summary>
    public string SourceLabel => Source switch
    {
        StartupSource.MachineRun => "HKLM Run",
        StartupSource.MachineRun32 => "HKLM Run (32)",
        StartupSource.UserRun => "HKCU Run",
        StartupSource.MachineRunOnce => "HKLM RunOnce",
        StartupSource.UserRunOnce => "HKCU RunOnce",
        StartupSource.MachineStartupFolder => "Startordner (alle)",
        StartupSource.UserStartupFolder => "Startordner",
        StartupSource.ScheduledTask => "Aufgabenplanung",
        StartupSource.Service => "Dienst",
        StartupSource.AppxTask => "Store-App",
        _ => "unbekannt",
    };
}

/// <summary>Wer ein Glied der Startkette ausgeführt hat.</summary>
public enum ChainKind
{
    /// <summary>Ein Befehl aus einem Run-Schlüssel.</summary>
    RunKey,

    /// <summary>Eine Verknüpfung aus einem Startordner.</summary>
    StartupFolder,

    /// <summary>Eine Anmeldeaufgabe der Shell.</summary>
    LogonTask,

    /// <summary>Die Auflistung eines Registry-Schlüssels, also die Klammer um mehrere Befehle.</summary>
    KeyScan,
}

/// <summary>
/// Ein Glied der Startkette: ein Befehl, den der Explorer beim Anmelden
/// ausgeführt hat, mit Anfang und Ende.
/// </summary>
/// <remarks>
/// Der Explorer arbeitet die Einträge <b>nacheinander</b> ab — das Ende-Ereignis
/// eines Befehls trägt denselben Zeitstempel wie das Start-Ereignis des
/// nächsten. Deshalb ist die Dauer eines Glieds nicht nur seine eigene Startzeit,
/// sondern die Zeit, die alle folgenden auf ihn gewartet haben. Genau das macht
/// die Kette zum Beweismittel: ein hängender Eintrag ist hier sichtbar, ohne dass
/// man raten muss.
/// </remarks>
public sealed record ChainItem(ChainKind Kind, string Command, DateTime Started)
{
    /// <summary>Fehlt, wenn zum Start kein passendes Ende gefunden wurde.</summary>
    public DateTime? Finished { get; init; }

    public int? Pid { get; init; }

    public TimeSpan? Duration => Finished is { } end ? end - Started : null;
}

/// <summary>Ein Abschnitt des Startvorgangs mit seiner Dauer.</summary>
public readonly record struct BootPhase(string Key, string Label, double Seconds);

/// <summary>
/// Die Startmessung von Windows selbst, aus dem Protokoll
/// <c>Microsoft-Windows-Diagnostics-Performance/Operational</c>, Ereignis 100.
/// </summary>
/// <remarks>
/// Windows misst jeden Start und schreibt die Aufteilung in dieses Ereignis. Das
/// ist dieselbe Quelle, aus der der Task-Manager seine Einstufung der
/// „Startauswirkung“ bildet — nur dass dort drei Stufen stehen und hier die
/// Sekunden. Das Protokoll ist zugriffsgeschützt: ohne erhöhte Rechte bleibt der
/// Abschnitt leer (DESIGN.md §8.12).
/// </remarks>
public sealed record BootPerformance(DateTime When, double BootSeconds, double MainPathSeconds, double PostBootSeconds)
{
    /// <summary>Anzahl der Autostart-Programme, die Windows gezählt hat.</summary>
    public int StartupAppCount { get; init; }

    /// <summary>Die Aufteilung des Hauptpfads, in der Reihenfolge des Ablaufs.</summary>
    public IReadOnlyList<BootPhase> Phases { get; init; } = [];

    /// <summary>Ob Windows diesen Start selbst als verschlechtert eingestuft hat.</summary>
    public bool Degraded { get; init; }

    /// <summary>Um wie viel der Start langsamer war als üblich.</summary>
    public double DegradationSeconds { get; init; }
}

/// <summary>Wie schwer ein Befund wiegt.</summary>
public enum FindingSeverity
{
    /// <summary>Kostet messbar viel Zeit oder ist ein echtes Zeitlimit.</summary>
    High,

    /// <summary>Kostet Zeit, aber im Rahmen.</summary>
    Medium,

    /// <summary>Kostet nichts, gehört aber in Ordnung gebracht.</summary>
    Hint,
}

/// <summary>
/// Ein Befund: etwas, das den Start aufgehalten hat oder aufhalten kann, mit
/// Beleg und Erklärung.
/// </summary>
/// <param name="Severity">Einstufung.</param>
/// <param name="Title">Was los ist, in einem Satz.</param>
/// <param name="Why">Warum das den Start aufhält und woher die Angabe stammt.</param>
public sealed record StartupFinding(FindingSeverity Severity, string Title, string Why)
{
    /// <summary>Was der Befund gekostet hat, sofern bezifferbar.</summary>
    public double? CostSeconds { get; init; }

    /// <summary>Wann er aufgetreten ist.</summary>
    public DateTime? When { get; init; }

    /// <summary>Die Fundstelle, etwa „System-Protokoll, Ereignis 7009“.</summary>
    public string? Evidence { get; init; }

    /// <summary>Wie oft dasselbe Muster in den betrachteten Starts auftrat.</summary>
    public int Count { get; init; } = 1;
}

/// <summary>
/// Das Ergebnis einer Startanalyse. Wird auf Anforderung erhoben, nicht im Takt —
/// die Ereignisprotokolle sind zu teuer für den Sekundentakt und ändern sich
/// zwischen zwei Starts ohnehin nicht (DESIGN.md §9).
/// </summary>
public sealed record StartupReport(DateTime CollectedAt)
{
    public static readonly StartupReport Empty = new(DateTime.Now);

    /// <summary>Wann der Rechner eingeschaltet wurde und wie.</summary>
    public Inventory.BootRecord Boot { get; init; } = Inventory.BootRecord.Unknown;

    /// <summary>
    /// Beginn der letzten Anmeldesitzung. Bezugspunkt für die Startkette — nicht
    /// der Einschaltzeitpunkt: zwischen beiden können bei einem Rechner, der aus
    /// dem Ruhezustand kommt, Tage liegen.
    /// </summary>
    public DateTime? SessionStart { get; init; }

    public BootPerformance? Performance { get; init; }

    public IReadOnlyList<ChainItem> Chain { get; init; } = [];

    public IReadOnlyList<StartupEntry> Entries { get; init; } = [];

    public IReadOnlyList<StartupFinding> Findings { get; init; } = [];

    /// <summary>
    /// Was die Zahlen einschränkt — ein gesperrtes Protokoll, ein fehlender
    /// Bezugspunkt. Ohne diese Angaben wären die Zahlen darüber falsch zu lesen,
    /// dieselbe Regel wie beim Ordner-Scan (DESIGN.md §13.5).
    /// </summary>
    public IReadOnlyList<string> Limitations { get; init; } = [];
}

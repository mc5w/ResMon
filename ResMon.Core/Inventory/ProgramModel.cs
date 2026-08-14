namespace ResMon.Core.Inventory;

/// <summary>Für wen ein Programm installiert ist.</summary>
/// <remarks>
/// Entscheidet, wer es wieder loswird: ein Eintrag unter HKCU gehört diesem
/// Benutzer allein und lässt sich ohne erhöhte Rechte deinstallieren, einer unter
/// HKLM gilt für alle und verlangt sie.
/// </remarks>
public enum ProgramScope
{
    /// <summary>HKLM — für alle Benutzer, 64-Bit-Sicht.</summary>
    AllUsers,

    /// <summary>HKLM\WOW6432Node — für alle Benutzer, 32-Bit-Sicht desselben Schlüssels.</summary>
    AllUsers32,

    /// <summary>HKCU — nur für den angemeldeten Benutzer.</summary>
    CurrentUser,
}

/// <summary>Woher die Angabe zur letzten Nutzung stammt.</summary>
/// <remarks>
/// Die Herkunft gehört an die Zahl, weil beide Quellen verschieden weit reichen:
/// Prefetch kennt jeden Start auf diesem Rechner, aber keinen Benutzer; UserAssist
/// kennt den Benutzer, aber nur Starts über die Oberfläche. Ein Datum ohne diese
/// Angabe verleitet zu einem Schluss, den es nicht trägt.
/// </remarks>
[Flags]
public enum UsageSource
{
    /// <summary>Keine Quelle hat den Namen gekannt.</summary>
    None = 0,

    /// <summary>Aus einer <c>.pf</c>-Datei in <c>C:\Windows\Prefetch</c>.</summary>
    Prefetch = 1,

    /// <summary>Aus <c>HKCU\…\Explorer\UserAssist</c>.</summary>
    UserAssist = 2,
}

/// <summary>Wie die Größe eines Programms zustande kam.</summary>
/// <remarks>
/// <c>EstimatedSize</c> aus der Registry taucht hier bewusst nicht auf. Der Wert
/// ist selbstgemeldet — der Installer schreibt hin, was er will — und fehlt auf
/// der Referenzmaschine bei 48 von 108 Programmen. Eine falsche Zahl ist
/// schlimmer als keine, weil man nach ihr sortiert.
/// </remarks>
public enum SizeOrigin
{
    /// <summary>Kein Installationsort hinterlegt — die Größe bleibt offen.</summary>
    Unknown,

    /// <summary>Im Baum eines gelaufenen Scans nachgeschlagen.</summary>
    FromScan,

    /// <summary>Der Installationsordner wurde eigens durchlaufen.</summary>
    Measured,
}

/// <summary>Ein installiertes Programm, wie es in den Uninstall-Schlüsseln steht.</summary>
/// <param name="Name">Der Anzeigename, unter dem es auch in „Apps &amp; Features“ steht.</param>
/// <param name="Scope">Für wen es installiert ist.</param>
public sealed record ProgramEntry(string Name, ProgramScope Scope)
{
    public string? Version { get; init; }

    public string? Publisher { get; init; }

    /// <summary>
    /// Aus <c>InstallDate</c>, das als <c>yyyyMMdd</c> ohne Uhrzeit hinterlegt ist.
    /// Fehlt bei rund der Hälfte der Einträge.
    /// </summary>
    public DateTime? InstalledOn { get; init; }

    /// <summary>Der Installationsordner, sofern hinterlegt — Grundlage der Messung.</summary>
    public string? InstallLocation { get; init; }

    /// <summary>Der Befehl, den „Apps &amp; Features“ zum Deinstallieren aufruft.</summary>
    public string? UninstallCommand { get; init; }

    /// <summary>
    /// Die Hauptanwendung, sofern sie sich bestimmen ließ. Meist aus
    /// <c>DisplayIcon</c> — der Eintrag zeigt fast immer auf genau die Exe, deren
    /// Symbol das Programm vertritt, und damit auf die, die man startet.
    /// </summary>
    public string? MainExecutable { get; init; }

    /// <summary>Die gemessene Größe des Installationsordners. <c>null</c>, wenn nicht messbar.</summary>
    public long? Bytes { get; init; }

    public SizeOrigin SizeFrom { get; init; }

    /// <summary>Wann die Hauptanwendung zuletzt gestartet wurde.</summary>
    public DateTime? LastUsed { get; init; }

    /// <summary>Wie oft sie über die Oberfläche gestartet wurde — nur aus UserAssist.</summary>
    public int? LaunchCount { get; init; }

    public UsageSource UsageFrom { get; init; }

    /// <summary>Anzeigename der Herkunft für die Oberfläche.</summary>
    public string ScopeLabel => Scope switch
    {
        ProgramScope.AllUsers => "Alle Benutzer",
        ProgramScope.AllUsers32 => "Alle Benutzer (32)",
        ProgramScope.CurrentUser => "Nur dieser Benutzer",
        _ => "unbekannt",
    };

    /// <summary>
    /// Tage seit dem letzten Start. <c>null</c>, wenn keine Quelle den Namen
    /// kannte — das ist ausdrücklich <b>nicht</b> dasselbe wie „nie benutzt“.
    /// </summary>
    public int? DaysSinceLastUse => LastUsed is { } used ? (int)(DateTime.Now - used).TotalDays : null;
}

/// <summary>
/// Das Ergebnis einer Inventur, samt dem, was ihre Zahlen einschränkt.
/// </summary>
/// <remarks>
/// <see cref="Limitations"/> trägt dieselbe Aufgabe wie beim Ordner-Scan und beim
/// Startbericht (DESIGN.md §13.5): ohne die Einschränkungen wären die Zahlen
/// darüber falsch zu lesen. Fällt Prefetch aus, ist eine leere Spalte „zuletzt
/// benutzt“ eine fehlende Messung und kein altes Programm.
/// </remarks>
public sealed record ProgramReport(DateTime CollectedAt)
{
    public static readonly ProgramReport Empty = new(DateTime.Now);

    public IReadOnlyList<ProgramEntry> Programs { get; init; } = [];

    public IReadOnlyList<string> Limitations { get; init; } = [];

    /// <summary>Wie viele Einträge die Uninstall-Schlüssel insgesamt trugen, vor dem Filtern.</summary>
    public int RawEntryCount { get; init; }
}

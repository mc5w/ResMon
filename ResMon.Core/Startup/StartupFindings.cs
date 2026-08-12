namespace ResMon.Core.Startup;

/// <summary>
/// Die bekannten Muster, an denen ein Systemstart hängen bleibt — und ihre
/// Belege in den Ereignisprotokollen.
/// </summary>
/// <remarks>
/// Die Liste ist der eigentliche Zweck des Reiters. Ein Balkendiagramm sagt, dass
/// der Start lange gedauert hat; erst diese Auswertung sagt, <b>woran</b>. Jeder
/// Befund nennt deshalb drei Dinge: was los ist, was es gekostet hat und wo es
/// nachzulesen ist. Ein Befund ohne Fundstelle wäre eine Behauptung.
/// <para>
/// Die wichtigste Gruppe sind die Zeitlimits des Dienststeuerungs-Managers
/// (7009, 7011). Sie tragen die gewartete Zeit als Zahl im Ereignis — 30 000 oder
/// 90 000 Millisekunden, in denen der Start stillsteht. Wer einen langsamen
/// Systemstart untersucht, sucht fast immer genau diese Zeile.
/// </para>
/// </remarks>
public static class StartupFindings
{
    private const string SystemLog = "System";
    private const string PolicyLog = "Microsoft-Windows-GroupPolicy/Operational";
    private const string ProfileLog = "Microsoft-Windows-User Profile Service/Operational";

    private const string ServiceQuery =
        "*[System[(EventID=7000 or EventID=7009 or EventID=7011 or EventID=7022 " +
        "or EventID=7023 or EventID=7031 or EventID=7034 or EventID=7043)]]";

    private const string NetlogonQuery = "*[System[(EventID=5719 or EventID=5783 or EventID=1129)]]";

    private const string PolicyQuery = "*[System[(EventID=8001 or EventID=8002)]]";

    private const string ProfileQuery = "*[System[(EventID=1 or EventID=2)]]";

    /// <summary>
    /// Sammelt alle Befunde ab <paramref name="notBefore"/> ein und ordnet sie
    /// nach dem, was sie gekostet haben.
    /// </summary>
    public static IReadOnlyList<StartupFinding> Collect(
        DateTime? notBefore,
        IReadOnlyList<StartupEntry> entries,
        IReadOnlyList<ChainItem> chain)
    {
        var findings = new List<StartupFinding>();

        findings.AddRange(FromServices(notBefore));
        findings.AddRange(FromNetlogon(notBefore));
        findings.AddRange(FromGroupPolicy(notBefore));
        findings.AddRange(FromUserProfile(notBefore));
        findings.AddRange(BootPerformanceReader.ReadDegradations(notBefore));
        findings.AddRange(FromChain(chain));
        findings.AddRange(FromEntries(entries));

        // Nach Kosten ordnen, Befunde ohne Zahl ans Ende: die Frage lautet „was
        // hat am meisten gekostet“, nicht „was ist zuletzt passiert“.
        return [.. findings
            .OrderByDescending(f => f.CostSeconds ?? -1)
            .ThenBy(f => f.Severity)];
    }

    /// <summary>
    /// Zeitlimits und Fehlstarts von Diensten. Die Zeitangabe steht als erstes
    /// Datenfeld im Ereignis und ist damit sprachunabhängig lesbar — der
    /// angezeigte Text wäre es nicht.
    /// </summary>
    private static IEnumerable<StartupFinding> FromServices(DateTime? notBefore)
    {
        foreach (RawEvent record in StartupEvents.Read(SystemLog, ServiceQuery, notBefore, limit: 80,
                     source: "Startbefunde (System-Protokoll)"))
        {
            switch (record.Id)
            {
                case 7009:
                    yield return Timeout(record,
                        $"Dienst „{Name(record, 2)}“ wurde beim Verbindungsversuch abgebrochen",
                        "Der Dienststeuerungs-Manager wartet die volle Zeit ab, bevor er weitergeht — " +
                        "der Start steht so lange still. Das ist das klassische Muster hinter einem " +
                        "unerklärlich langen Systemstart.");
                    break;

                case 7011:
                    yield return Timeout(record,
                        $"Dienst „{Name(record, 2)}“ hat nicht auf eine Anfrage geantwortet",
                        "Der Dienst hat innerhalb des Zeitlimits keine Rückmeldung gegeben. " +
                        "Tritt das bei jedem Start auf, ist es eine feste Wartezeit.");
                    break;

                case 7022:
                    yield return new StartupFinding(FindingSeverity.High,
                        $"Dienst „{Name(record, 1)}“ blieb beim Starten hängen",
                        "Der Dienst hat den Startvorgang begonnen und nie abgeschlossen.")
                    {
                        When = record.Time,
                        Evidence = "System-Protokoll, Ereignis 7022",
                    };
                    break;

                case 7000 or 7023:
                    yield return new StartupFinding(FindingSeverity.Medium,
                        $"Dienst „{Name(record, 1)}“ konnte nicht gestartet werden",
                        record.Message ?? "Der Dienst hat den Start mit einem Fehler beendet. " +
                        "Ein Dienst, der bei jedem Start scheitert, kostet vorher trotzdem seine Wartezeit.")
                    {
                        When = record.Time,
                        Evidence = $"System-Protokoll, Ereignis {record.Id}",
                    };
                    break;

                case 7031 or 7034:
                    yield return new StartupFinding(FindingSeverity.Hint,
                        $"Dienst „{Name(record, 1)}“ wurde unerwartet beendet",
                        "Kein Verzögerer für sich genommen — aber ein Dienst, der abstürzt und " +
                        "neu gestartet wird, tut das beim nächsten Start wieder.")
                    {
                        When = record.Time,
                        Evidence = $"System-Protokoll, Ereignis {record.Id}",
                    };
                    break;

                case 7043:
                    yield return new StartupFinding(FindingSeverity.Hint,
                        $"Dienst „{Name(record, 1)}“ wurde beim Herunterfahren nicht sauber beendet",
                        "Verzögert das Ausschalten, nicht den Start — fällt bei der Ursachensuche " +
                        "trotzdem auf dieselbe Software.")
                    {
                        When = record.Time,
                        Evidence = "System-Protokoll, Ereignis 7043",
                    };
                    break;
            }
        }
    }

    /// <summary>Ein Zeitlimit-Ereignis: die gewartete Zeit steht im ersten Feld, in Millisekunden.</summary>
    private static StartupFinding Timeout(RawEvent record, string title, string why)
    {
        double? seconds = ParseSeconds(record.Positional(1)) is { } ms ? ms / 1000.0 : null;

        return new StartupFinding(FindingSeverity.High, title, why)
        {
            CostSeconds = seconds,
            When = record.Time,
            Evidence = seconds is { } value
                ? $"System-Protokoll, Ereignis {record.Id} — Zeitlimit {value * 1000:0} ms"
                : $"System-Protokoll, Ereignis {record.Id}",
        };
    }

    private static string Name(RawEvent record, int position)
        => record.Positional(position) is { Length: > 0 } value ? value : "unbenannt";

    /// <summary>
    /// Anmeldung an einer Domäne. Im Firmennetz die häufigste Ursache für einen
    /// Start, der „einfach hängt“: steht das Netz beim Anmelden noch nicht,
    /// wartet Windows auf sein eigenes Zeitlimit.
    /// </summary>
    private static IEnumerable<StartupFinding> FromNetlogon(DateTime? notBefore)
    {
        foreach (RawEvent record in StartupEvents.Read(SystemLog, NetlogonQuery, notBefore, limit: 20,
                     source: "Startbefunde (System-Protokoll)"))
        {
            yield return new StartupFinding(FindingSeverity.High,
                "Beim Anmelden war kein Domänencontroller erreichbar",
                "Windows versucht die Anmeldung gegen die Domäne und wartet auf eine Antwort. " +
                "Typisch, wenn WLAN oder VPN erst nach der Anmeldung stehen — die Wartezeit " +
                "geht ungebremst in den Start ein und ist von außen nicht als Ursache zu erkennen.")
            {
                When = record.Time,
                Evidence = $"System-Protokoll, Ereignis {record.Id} (Netlogon)",
            };
        }
    }

    /// <summary>
    /// Gruppenrichtlinien-Verarbeitung. Die Dauer steht als benanntes Feld im
    /// Ereignis; im Firmennetz stehen dort regelmäßig zweistellige Sekundenwerte.
    /// </summary>
    private static IEnumerable<StartupFinding> FromGroupPolicy(DateTime? notBefore)
    {
        if (!StartupEvents.CanRead(PolicyLog))
            yield break;

        foreach (RawEvent record in StartupEvents.Read(PolicyLog, PolicyQuery, notBefore, limit: 20))
        {
            // Der Feldname ist im Manifest verschrieben („Elasped“); beide
            // Schreibweisen abzufragen kostet nichts und erspart die Frage,
            // welche Windows-Fassung welche schreibt.
            double? seconds = ParseSeconds(record.Field("PolicyElaspedTimeInSeconds"))
                ?? ParseSeconds(record.Field("PolicyElapsedTimeInSeconds"))
                ?? record.Seconds("ProcessingTimeInMilliseconds");

            if (seconds is not { } value || value < 3)
                continue;

            bool machine = record.Id == 8001;

            yield return new StartupFinding(
                value >= 15 ? FindingSeverity.High : FindingSeverity.Medium,
                machine
                    ? "Gruppenrichtlinien für den Computer brauchten lange"
                    : "Gruppenrichtlinien für den Benutzer brauchten lange",
                "Die Verarbeitung läuft vor dem Desktop und wird abgewartet. Lange Zeiten kommen " +
                "meist von Ordnerumleitung, Laufwerkszuordnungen oder Skripten, die auf eine " +
                "Freigabe zugreifen, die noch nicht antwortet.")
            {
                CostSeconds = value,
                When = record.Time,
                Evidence = $"Gruppenrichtlinien-Protokoll, Ereignis {record.Id}",
            };
        }
    }

    /// <summary>
    /// Der Benutzerprofildienst. Die beiden Ereignisse 1 und 2 klammern die
    /// Anmeldeverarbeitung ein; ihr Abstand ist die Zeit, die das Laden des
    /// Profils gekostet hat.
    /// </summary>
    private static IEnumerable<StartupFinding> FromUserProfile(DateTime? notBefore)
    {
        List<RawEvent> events = StartupEvents.Read(ProfileLog, ProfileQuery, notBefore, limit: 20);
        DateTime? started = null;

        foreach (RawEvent record in events)
        {
            if (record.Id == 1)
            {
                started = record.Time;
                continue;
            }

            if (started is not { } begin)
                continue;

            double seconds = (record.Time - begin).TotalSeconds;
            started = null;

            if (seconds < 5)
                continue;

            yield return new StartupFinding(
                seconds >= 20 ? FindingSeverity.High : FindingSeverity.Medium,
                "Das Laden des Benutzerprofils hat lange gedauert",
                "Zwischen Anmelde- und Abschlussmeldung des Profildienstes lag diese Zeit. " +
                "Ein Servergespeichertes Profil, ein sehr großes AppData oder eine langsame " +
                "Platte sind die üblichen Gründe.")
            {
                CostSeconds = seconds,
                When = record.Time,
                Evidence = "Benutzerprofildienst, Ereignisse 1 und 2",
            };
        }
    }

    /// <summary>
    /// Glieder der Startkette, die die folgenden aufgehalten haben. Die Schwelle
    /// liegt bei zwei Sekunden: darunter ist ein Autostart-Programm normal
    /// langsam und keine Meldung wert.
    /// </summary>
    private static IEnumerable<StartupFinding> FromChain(IReadOnlyList<ChainItem> chain)
    {
        foreach (ChainItem item in chain)
        {
            if (item.Kind == ChainKind.LogonTask || item.Duration is not { } duration || duration.TotalSeconds < 2)
                continue;

            yield return new StartupFinding(
                duration.TotalSeconds >= 8 ? FindingSeverity.High : FindingSeverity.Medium,
                $"„{Shorten(item.Command)}“ hat die Autostart-Kette blockiert",
                "Der Explorer arbeitet die Autostart-Einträge nacheinander ab. Solange dieser " +
                "Eintrag lief, stand alles Folgende still — die Zeit ist also nicht nur seine " +
                "eigene, sondern die Wartezeit aller danach.")
            {
                CostSeconds = duration.TotalSeconds,
                When = item.Started,
                Evidence = $"Startkette, Shell-Core {(item.Kind == ChainKind.RunKey ? "9707/9708" : "62408/62409")}",
            };
        }
    }

    /// <summary>Defekte, die am Eintrag selbst zu sehen sind — ohne jedes Protokoll.</summary>
    private static IEnumerable<StartupFinding> FromEntries(IReadOnlyList<StartupEntry> entries)
    {
        foreach (StartupEntry entry in entries)
        {
            if (!entry.Enabled)
                continue;

            if (entry.Issues.HasFlag(StartupIssue.NetworkPath))
            {
                yield return new StartupFinding(FindingSeverity.High,
                    $"Autostart-Eintrag „{entry.Name}“ liegt auf einem Netzlaufwerk",
                    $"„{entry.ImagePath}“ wird beim Anmelden geöffnet. Steht die Verbindung zu diesem " +
                    "Zeitpunkt noch nicht — und beim Anmelden steht sie oft noch nicht —, wartet der " +
                    "Start auf das Zeitlimit des Redirectors, ohne dass irgendwo ein Fehler erscheint.")
                {
                    Evidence = entry.SourceLabel,
                };
            }

            if (entry.Issues.HasFlag(StartupIssue.RemovablePath))
            {
                yield return new StartupFinding(FindingSeverity.Medium,
                    $"Autostart-Eintrag „{entry.Name}“ liegt auf einem Wechseldatenträger",
                    $"„{entry.ImagePath}“ ist nur vorhanden, solange der Datenträger steckt.")
                {
                    Evidence = entry.SourceLabel,
                };
            }

            if (entry.Issues.HasFlag(StartupIssue.MissingFile))
            {
                yield return new StartupFinding(FindingSeverity.Hint,
                    $"Autostart-Eintrag „{entry.Name}“ zeigt auf eine Datei, die es nicht gibt",
                    $"„{entry.ImagePath}“ existiert nicht — meist die Leiche einer Deinstallation. " +
                    "Kostet kaum Zeit, gehört aber weg.")
                {
                    Evidence = entry.SourceLabel,
                };
            }

            if (entry.Issues.HasFlag(StartupIssue.EmptyCommand))
            {
                yield return new StartupFinding(FindingSeverity.Hint,
                    $"Autostart-Eintrag „{entry.Name}“ ist leer",
                    "Ein Registry-Wert ohne Befehl. Tut nichts, steht aber in der Liste und " +
                    "verstellt die Sicht auf das, was wirklich läuft.")
                {
                    Evidence = entry.SourceLabel,
                };
            }

            if (entry.Issues.HasFlag(StartupIssue.TempPath))
            {
                yield return new StartupFinding(FindingSeverity.Medium,
                    $"Autostart-Eintrag „{entry.Name}“ startet aus dem temporären Verzeichnis",
                    $"„{entry.ImagePath}“ liegt unter %TEMP%. Ordentlich installierte Software tut das " +
                    "nicht — hier lohnt ein zweiter Blick, was das Programm ist.")
                {
                    Evidence = entry.SourceLabel,
                };
            }
        }
    }

    private static double? ParseSeconds(string? value)
        => double.TryParse(value, System.Globalization.NumberStyles.Any,
            System.Globalization.CultureInfo.InvariantCulture, out double seconds)
            ? seconds
            : null;

    private static string Shorten(string command)
        => command.Length <= 60 ? command : command[..57] + "…";
}

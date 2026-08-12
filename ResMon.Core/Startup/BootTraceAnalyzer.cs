using System.IO;
using Microsoft.Diagnostics.Tracing;
using Microsoft.Diagnostics.Tracing.Parsers.Kernel;
using ResMon.Core.Diagnostics;

namespace ResMon.Core.Startup;

/// <summary>Was ein Prozess während des aufgezeichneten Starts verbraucht hat.</summary>
/// <param name="Pid">Prozesskennung während des Starts.</param>
/// <param name="Name">Name der ausführbaren Datei.</param>
/// <param name="CpuMs">Belegte Rechenzeit in Millisekunden, aus den Abtastungen geschätzt.</param>
public sealed record TraceProcess(int Pid, string Name, double CpuMs)
{
    public long DiskReadBytes { get; init; }

    public long DiskWriteBytes { get; init; }

    /// <summary>Anzahl der Datenträgerzugriffe — sagt mehr über eine Festplatte aus als die Menge.</summary>
    public int DiskOperations { get; init; }

    /// <summary>Abstand vom Beginn der Aufzeichnung bis zum Start des Prozesses.</summary>
    public double? StartOffsetMs { get; init; }

    public long DiskBytes => DiskReadBytes + DiskWriteBytes;
}

/// <summary>Das Ergebnis einer ausgewerteten Startaufzeichnung.</summary>
public sealed record BootTraceSummary(string Path, DateTime When, double DurationSeconds)
{
    public IReadOnlyList<TraceProcess> Processes { get; init; } = [];

    /// <summary>Anzahl der CPU-Abtastungen; ohne sie sind die Rechenzeiten leer.</summary>
    public long SampleCount { get; init; }

    /// <summary>Woher die Aufzeichnung stammt: von Windows selbst oder aus einem eigenen Lauf.</summary>
    public bool FromWindows { get; init; }

    /// <summary>
    /// Zeitstempel der Datei. Steht neben <see cref="When"/>, weil die beiden
    /// auseinandergehen können — siehe <see cref="BootTraceAnalyzer"/>.
    /// </summary>
    public DateTime FileTime { get; init; }

    /// <summary>
    /// Ob die Aufzeichnung eine Profilablaufverfolgung enthält. Ohne sie bleiben
    /// alle Rechenzeiten null, und das ist kein Fehler, sondern eine Eigenschaft
    /// der Quelle.
    /// </summary>
    public bool HasCpuSamples => SampleCount > 0;

    /// <summary>
    /// Ob die Aufzeichnung vom laufenden Start stammt. Ist sie es nicht, zeigt
    /// sie einen früheren Start — und jede Zahl darin beantwortet eine andere
    /// Frage als die gestellte.
    /// </summary>
    public bool FromLastBoot { get; init; } = true;

    public string? Error { get; init; }
}

/// <summary>
/// Wertet eine ETW-Startaufzeichnung aus und beantwortet damit die Frage, die
/// kein Ereignisprotokoll beantwortet: <b>was hat ein einzelner Startvorgang an
/// Rechenzeit und Datenträgerzugriffen gekostet</b>.
/// </summary>
/// <remarks>
/// Zwei Quellen kommen infrage:
/// <list type="number">
/// <item>
/// <b>Windows zeichnet den Start gelegentlich selbst auf.</b> Der
/// Diagnoserichtliniendienst legt
/// <c>%windir%\System32\WDI\LogFiles\BootPerfDiagLogger.etl</c> an — dieselbe
/// Spur, aus der auch die Ereignisse 100 bis 110 entstehen. Sie ist ohne
/// Neustart und ohne Vorbereitung da; der Ordner ist allerdings nur erhöht
/// lesbar, und die Datei ist beim Lesen in Benutzung und muss vorher kopiert
/// werden.
/// <para>
/// <b>Sie stammt nicht zwangsläufig vom letzten Start.</b> Die Startdiagnose
/// läuft nicht bei jedem Hochfahren, sondern wenn sie anspringt; auf der
/// Referenzmaschine war die Datei ein volles Jahr alt. Wer das nicht bemerkt,
/// wertet ahnungslos einen längst vergangenen Start aus und sucht die Ursache
/// eines heutigen Problems in Zahlen, die es damals noch nicht gab. Die
/// Dateizeit wird deshalb gegen den Einschaltzeitpunkt geprüft und das Ergebnis
/// in <see cref="BootTraceSummary.FromLastBoot"/> gemeldet.
/// </para>
/// </item>
/// <item>
/// <b>Eine eigene Aufzeichnung</b> über <see cref="BootTrace"/>. Sie ist
/// ausführlicher — mehr Anbieter, feinere Abtastung —, kostet aber einen
/// Neustart und mehrere hundert Megabyte.
/// </item>
/// </list>
/// <para>
/// Die Rechenzeit ist eine <b>Schätzung aus Abtastungen</b>, keine Messung: der
/// Kernel unterbricht in festem Takt und notiert, welcher Thread gerade läuft.
/// Bei der üblichen Millisekunde je Abtastung und Kern entspricht eine Abtastung
/// rund einer CPU-Millisekunde. Kurze Spitzen zwischen zwei Abtastungen fallen
/// heraus; über einen Startvorgang von Sekunden mittelt sich das aus, für einen
/// Vorgang von 20 ms wäre die Zahl wertlos. Deshalb steht die Zahl der
/// Abtastungen mit im Ergebnis.
/// </para>
/// <para>
/// <b>Windows' eigene Aufzeichnung enthält keine Abtastungen.</b> Auf der
/// Referenzmaschine gemessen: 355 Prozesse, 43 000 Datenträgerzugriffe, aber
/// null Profilereignisse — der Diagnoserichtliniendienst schaltet die
/// Profilablaufverfolgung nicht ein. Aus dieser Quelle kommen also
/// Datenträgerzugriffe und Startzeitpunkte, aber keine Rechenzeit; dafür braucht
/// es die eigene Aufzeichnung über <see cref="BootTrace"/>. Die Zahl der
/// Abtastungen sagt das an, statt eine Spalte voller Nullen für eine Messung
/// ausgeben zu lassen.
/// </para>
/// <para>
/// Der Zeitstempel der Sitzung ist mit Vorsicht zu lesen: bei Windows' eigener
/// Aufzeichnung meldete er auf der Referenzmaschine ein Datum ein Jahr vor dem
/// letzten Start. Deshalb steht die Änderungszeit der Datei
/// (<see cref="BootTraceSummary.FileTime"/>) daneben — sie sagt, welchen Start
/// man tatsächlich vor sich hat.
/// </para>
/// </remarks>
public static class BootTraceAnalyzer
{
    /// <summary>Die Aufzeichnung, die Windows bei jedem Start selbst anlegt.</summary>
    public static string WindowsTracePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.System), "WDI", "LogFiles", "BootPerfDiagLogger.etl");

    /// <summary>Prozesse unterhalb dieser Rechenzeit fallen aus dem Ergebnis.</summary>
    private const double MinimumCpuMs = 5;

    /// <summary>Ob Windows' eigene Startaufzeichnung vorliegt und gelesen werden darf.</summary>
    public static bool WindowsTraceAvailable()
    {
        try
        {
            return File.Exists(WindowsTracePath);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
        {
            return false;
        }
    }

    /// <summary>
    /// Wertet Windows' eigene Startaufzeichnung aus. Sie liegt in einem nur
    /// erhöht lesbaren Ordner und ist in Benutzung — beides erledigt
    /// <see cref="Analyze"/> über die Arbeitskopie.
    /// </summary>
    public static BootTraceSummary? AnalyzeWindowsTrace()
        => WindowsTraceAvailable() ? Analyze(WindowsTracePath) with { FromWindows = true } : null;

    /// <summary>Wertet eine Aufzeichnung aus. Blockiert je nach Größe Sekunden bis Minuten.</summary>
    public static BootTraceSummary Analyze(string path)
    {
        string? copy = null;

        try
        {
            // Die laufende Sitzung hält die Datei offen. TraceEvent öffnet sie
            // ohne Freigabe und scheitert daran; eine Kopie mit ausdrücklicher
            // Freigabe umgeht das, ohne die Aufzeichnung anzurühren.
            copy = CopyForReading(path);
            return Read(copy ?? path, path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            DiagnosticLog.Report("Startaufzeichnung", ex, $"„{path}“ ließ sich nicht auswerten");
            return new BootTraceSummary(path, DateTime.MinValue, 0) { Error = ex.Message };
        }
        finally
        {
            if (copy is not null)
                TryDelete(copy);
        }
    }

    private static BootTraceSummary Read(string file, string reportedPath)
    {
        var cpu = new Dictionary<int, long>();
        var read = new Dictionary<int, long>();
        var written = new Dictionary<int, long>();
        var operations = new Dictionary<int, int>();
        var names = new Dictionary<int, string>();
        var started = new Dictionary<int, double>();
        long samples = 0;

        using var source = new ETWTraceEventSource(file);

        source.Kernel.ProcessStart += data =>
        {
            Remember(names, data.ProcessID, data.ProcessName);
            started.TryAdd(data.ProcessID, data.TimeStampRelativeMSec);
        };

        // Prozesse, die beim Beginn der Aufzeichnung schon liefen, tauchen nie in
        // einem Start-Ereignis auf. Ihre Namen stehen ausschließlich in der
        // Bestandsaufnahme, die ETW zu Beginn und Ende einer Sitzung schreibt —
        // ohne sie steht im Ergebnis „PID 4“ statt „System“, und gerade die
        // langlebigen Systemprozesse sind die mit den meisten Zugriffen.
        source.Kernel.ProcessDCStart += data => Remember(names, data.ProcessID, data.ProcessName);
        source.Kernel.ProcessDCStop += data => Remember(names, data.ProcessID, data.ProcessName);

        // Letzte Rückfallebene: das erste geladene Abbild eines Prozesses ist
        // seine ausführbare Datei.
        source.Kernel.ImageLoad += data =>
        {
            if (!names.ContainsKey(data.ProcessID)
                && data.FileName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            {
                Remember(names, data.ProcessID, Path.GetFileNameWithoutExtension(data.FileName));
            }
        };

        // Jede Abtastung ist rund eine CPU-Millisekunde auf einem Kern.
        source.Kernel.PerfInfoSample += data =>
        {
            samples++;
            cpu[data.ProcessID] = cpu.GetValueOrDefault(data.ProcessID) + 1;
            Remember(names, data.ProcessID, data.ProcessName);
        };

        source.Kernel.DiskIORead += data =>
        {
            read[data.ProcessID] = read.GetValueOrDefault(data.ProcessID) + data.TransferSize;
            operations[data.ProcessID] = operations.GetValueOrDefault(data.ProcessID) + 1;
        };

        source.Kernel.DiskIOWrite += data =>
        {
            written[data.ProcessID] = written.GetValueOrDefault(data.ProcessID) + data.TransferSize;
            operations[data.ProcessID] = operations.GetValueOrDefault(data.ProcessID) + 1;
        };

        source.Process();

        var pids = new HashSet<int>(cpu.Keys);
        pids.UnionWith(read.Keys);
        pids.UnionWith(written.Keys);

        var processes = pids
            .Select(pid => new TraceProcess(
                pid,
                names.TryGetValue(pid, out string? name) ? name : $"PID {pid}",
                cpu.GetValueOrDefault(pid))
            {
                DiskReadBytes = read.GetValueOrDefault(pid),
                DiskWriteBytes = written.GetValueOrDefault(pid),
                DiskOperations = operations.GetValueOrDefault(pid),
                StartOffsetMs = started.TryGetValue(pid, out double offset) ? offset : null,
            })
            // Der Leerlaufprozess bekommt jede Abtastung ab, in der nichts lief —
            // in einer Startaufzeichnung ist er zuverlässig die größte Zeile und
            // sagt genau nichts.
            .Where(process => process.Pid > 0 && (process.CpuMs >= MinimumCpuMs || process.DiskBytes > 0))
            // Nach Rechenzeit ordnen, aber der Datenträger entscheidet den
            // Gleichstand: enthält die Aufzeichnung keine Abtastungen, ist jede
            // Rechenzeit null und die Reihenfolge wäre sonst die zufällige der
            // Prozesskennungen.
            .OrderByDescending(process => process.CpuMs)
            .ThenByDescending(process => process.DiskBytes)
            .ToArray();

        DateTime fileTime = DateTime.MinValue;
        try
        {
            fileTime = File.GetLastWriteTime(reportedPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Ohne Dateizeit bleibt der Zeitstempel aus der Sitzung.
        }

        // Stammt die Aufzeichnung überhaupt vom laufenden Start? Verglichen wird
        // die Dateizeit, nicht der Zeitstempel der ETW-Sitzung — der ist bei
        // Windows' eigener Aufzeichnung unzuverlässig. Eine Minute Nachlauf,
        // weil die Datei erst nach dem Einschalten fertiggeschrieben wird.
        DateTime? powerOn = Inventory.BootHistory.Read().PowerOn;
        bool current = powerOn is not { } boot
            || fileTime == DateTime.MinValue
            || fileTime >= boot.AddMinutes(-1);

        return new BootTraceSummary(reportedPath, source.SessionStartTime, source.SessionDuration.TotalSeconds)
        {
            Processes = processes,
            SampleCount = samples,
            FileTime = fileTime,
            FromLastBoot = current,
        };
    }

    /// <summary>Merkt sich den Namen eines Prozesses, sofern noch keiner bekannt ist.</summary>
    private static void Remember(Dictionary<int, string> names, int pid, string? name)
    {
        if (name is { Length: > 0 } && !names.ContainsKey(pid))
            names[pid] = name;
    }

    /// <summary>
    /// Legt eine Arbeitskopie an. Liefert <c>null</c>, wenn die Datei sich auch
    /// so öffnen lässt — dann ist die Kopie unnötige Ein-/Ausgabe.
    /// </summary>
    private static string? CopyForReading(string path)
    {
        string target = Path.Combine(Path.GetTempPath(), $"resmon-{Guid.NewGuid():N}.etl");

        using (var input = new FileStream(
                   path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
        using (var output = new FileStream(target, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            input.CopyTo(output);
        }

        return target;
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Eine liegen gebliebene Arbeitskopie im Temp-Ordner ist kein Grund,
            // dem Anwender einen Fehler zu zeigen.
        }
    }
}

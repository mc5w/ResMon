using System.Diagnostics;
using System.IO;
using System.Text.Json;
using ResMon.Core.Diagnostics;

namespace ResMon.Core.Startup;

/// <summary>Wo die Startaufzeichnung gerade steht.</summary>
public enum BootTraceState
{
    /// <summary>Der Windows Performance Recorder ist auf diesem Rechner nicht vorhanden.</summary>
    Unavailable,

    /// <summary>Nichts scharfgestellt, nichts aufgezeichnet.</summary>
    Idle,

    /// <summary>Scharfgestellt — die Aufzeichnung beginnt beim nächsten Start.</summary>
    Armed,

    /// <summary>Der Neustart ist erfolgt, die Aufzeichnung läuft und will beendet werden.</summary>
    Recording,

    /// <summary>Eine fertige Aufzeichnung liegt vor.</summary>
    Recorded,
}

/// <summary>Der Zustand der Startaufzeichnung samt Erklärung für die Oberfläche.</summary>
public sealed record BootTraceStatus(BootTraceState State, string Message)
{
    /// <summary>Wann scharfgestellt wurde.</summary>
    public DateTime? ArmedAt { get; init; }

    /// <summary>Die fertige Aufzeichnung.</summary>
    public string? TracePath { get; init; }

    public long? SizeBytes { get; init; }

    /// <summary>Fehlertext des letzten Aufrufs, falls einer scheiterte.</summary>
    public string? Error { get; init; }
}

/// <summary>
/// Startaufzeichnung über den Windows Performance Recorder.
/// </summary>
/// <remarks>
/// Beantwortet die eine Frage, die aus den Ereignisprotokollen nicht zu
/// beantworten ist: <b>was hat ein einzelner Autostart-Vorgang an CPU-Zeit und
/// Datenträgerzugriffen gekostet</b>. Die Protokolle kennen nur Anfang und Ende;
/// was dazwischen passiert, sieht nur eine Ablaufverfolgung — und die muss
/// <i>vor</i> dem Neustart eingerichtet sein, weil sie den Kernel schon während
/// des Hochfahrens mitschreiben lässt.
/// <para>
/// <c>wpr.exe</c> liegt in jedem Windows 10 und 11 unter <c>System32</c>; das
/// früher übliche <c>xbootmgr</c> aus dem Windows Performance Toolkit ist damit
/// entbehrlich. <c>-addboot</c> richtet einen Autologger ein, der beim nächsten
/// Start greift, <c>-stopboot</c> hält ihn an und schreibt die Spur in eine
/// <c>.etl</c>-Datei, <c>-cancelboot</c> nimmt alles wieder zurück.
/// </para>
/// <para>
/// Der Ablauf verlangt einen Neustart, und deshalb stößt die Anwendung ihn
/// <b>nicht selbst an</b>. Sie stellt scharf, sagt es, und wartet. Ein Monitor,
/// der den Rechner neu startet, wäre ein Monitor, den man nicht laufen lassen
/// kann. Die entstehende Datei ist je nach Startdauer 100 bis 500 MB groß —
/// auch das gehört vorher gesagt und nicht hinterher entdeckt.
/// </para>
/// </remarks>
public static class BootTrace
{
    /// <summary>Aufzeichnungsprofil. „GeneralProfile“ deckt CPU, Datenträger, Datei-E/A und Prozesse ab.</summary>
    private const string Profile = "GeneralProfile";

    private const string MarkerName = "boottrace.json";

    private const int TimeoutMs = 120_000;

    /// <summary>Was der Anwender vor dem Scharfstellen wissen muss.</summary>
    public const string Warning =
        "Die Aufzeichnung beginnt beim nächsten Neustart und schreibt mit, was Kernel, " +
        "Treiber und Programme während des gesamten Starts tun. Die Datei wird je nach " +
        "Startdauer 100 bis 500 MB groß. Der Neustart wird nicht ausgelöst — das bleibt bei Ihnen.";

    private static string Folder => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "ResMon");

    private static string MarkerPath => Path.Combine(Folder, MarkerName);

    private static string Recorder => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.System), "wpr.exe");

    /// <summary>Der aktuelle Zustand, ohne etwas zu verändern.</summary>
    public static BootTraceStatus Read()
    {
        if (!File.Exists(Recorder))
        {
            return new BootTraceStatus(BootTraceState.Unavailable,
                "Der Windows Performance Recorder (wpr.exe) ist auf diesem Rechner nicht vorhanden.");
        }

        Marker? marker = ReadMarker();

        if (marker?.TracePath is { Length: > 0 } trace && File.Exists(trace))
        {
            return new BootTraceStatus(BootTraceState.Recorded,
                "Eine Startaufzeichnung liegt vor.")
            {
                TracePath = trace,
                SizeBytes = new FileInfo(trace).Length,
                ArmedAt = marker.ArmedAt,
            };
        }

        if (marker?.ArmedAt is not { } armed)
            return new BootTraceStatus(BootTraceState.Idle, "Keine Startaufzeichnung eingerichtet.");

        // Ob der Neustart schon war, entscheidet der Vergleich mit dem
        // Einschaltzeitpunkt aus dem Ereignisprotokoll — nicht GetTickCount64,
        // die über den Schnellstart hinweg weiterläuft (DESIGN.md §8.9).
        bool rebooted = Inventory.BootHistory.Read().PowerOn is { } powerOn && powerOn > armed;

        return rebooted
            ? new BootTraceStatus(BootTraceState.Recording,
                "Der Neustart ist erfolgt, die Aufzeichnung läuft. Jetzt beenden, um die Spur zu sichern.")
            {
                ArmedAt = armed,
            }
            : new BootTraceStatus(BootTraceState.Armed,
                "Scharfgestellt. Die Aufzeichnung beginnt beim nächsten Neustart.")
            {
                ArmedAt = armed,
            };
    }

    /// <summary>Richtet den Autologger für den nächsten Start ein.</summary>
    public static BootTraceStatus Arm()
    {
        if (!File.Exists(Recorder))
            return Read();

        // Ein bereits eingerichteter Autologger würde den zweiten Aufruf mit
        // einem Fehler quittieren; erst abräumen, dann neu einrichten.
        Run("-cancelboot", out _);

        if (!Run($"-addboot {Profile} -filemode", out string output))
        {
            return new BootTraceStatus(BootTraceState.Idle,
                "Die Startaufzeichnung ließ sich nicht einrichten.")
            {
                Error = output,
            };
        }

        WriteMarker(new Marker(DateTime.Now, null));
        return Read();
    }

    /// <summary>Nimmt eine scharfgestellte oder laufende Aufzeichnung zurück.</summary>
    public static BootTraceStatus Cancel()
    {
        Run("-cancelboot", out string output);
        DeleteMarker();

        BootTraceStatus status = Read();
        return string.IsNullOrWhiteSpace(output) ? status : status with { Error = null };
    }

    /// <summary>
    /// Beendet die laufende Aufzeichnung und schreibt sie in eine Datei unter
    /// <c>%ProgramData%\ResMon</c>.
    /// </summary>
    public static BootTraceStatus Stop()
    {
        Directory.CreateDirectory(Folder);
        string target = Path.Combine(Folder, $"start-{DateTime.Now:yyyyMMdd-HHmmss}.etl");

        // Der Beschreibungstext ist bei -stopboot verpflichtend.
        if (!Run($"-stopboot \"{target}\" \"ResMon Startanalyse\"", out string output))
        {
            return new BootTraceStatus(BootTraceState.Recording,
                "Die Aufzeichnung ließ sich nicht beenden.")
            {
                Error = output,
            };
        }

        WriteMarker(new Marker(ReadMarker()?.ArmedAt, target));
        return Read();
    }

    /// <summary>Vergisst eine fertige Aufzeichnung, ohne die Datei zu löschen.</summary>
    public static BootTraceStatus Forget()
    {
        DeleteMarker();
        return Read();
    }

    private static bool Run(string arguments, out string output)
    {
        var startInfo = new ProcessStartInfo(Recorder, arguments)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };

        try
        {
            using Process? process = Process.Start(startInfo);
            if (process is null)
            {
                output = "wpr.exe konnte nicht gestartet werden.";
                return false;
            }

            string stdout = process.StandardOutput.ReadToEnd();
            string stderr = process.StandardError.ReadToEnd();

            // -stopboot schreibt die gesammelten Puffer zusammen und braucht bei
            // einem langen Start deutlich länger als ein üblicher Aufruf.
            if (!process.WaitForExit(TimeoutMs))
            {
                output = "wpr.exe hat nicht innerhalb von zwei Minuten geantwortet.";
                return false;
            }

            output = string.IsNullOrWhiteSpace(stderr) ? stdout.Trim() : stderr.Trim();
            return process.ExitCode == 0;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or IOException)
        {
            DiagnosticLog.Report("Startaufzeichnung", ex, $"wpr.exe {arguments}");
            output = ex.Message;
            return false;
        }
    }

    private sealed record Marker(DateTime? ArmedAt, string? TracePath);

    private static Marker? ReadMarker()
    {
        try
        {
            return File.Exists(MarkerPath)
                ? JsonSerializer.Deserialize<Marker>(File.ReadAllText(MarkerPath))
                : null;
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static void WriteMarker(Marker marker)
    {
        try
        {
            Directory.CreateDirectory(Folder);
            File.WriteAllText(MarkerPath, JsonSerializer.Serialize(marker));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            DiagnosticLog.Report("Startaufzeichnung", ex, "Der Merkzettel ließ sich nicht schreiben");
        }
    }

    private static void DeleteMarker()
    {
        try
        {
            if (File.Exists(MarkerPath))
                File.Delete(MarkerPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            DiagnosticLog.Report("Startaufzeichnung", ex, "Der Merkzettel ließ sich nicht löschen");
        }
    }
}

using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Management;
using ResMon.Core.Diagnostics;
using ResMon.Core.Native;

namespace ResMon.Core.Storage;

/// <summary>
/// Wie zerstückelt eine Partition ist — und was Windows dagegen täte.
/// </summary>
/// <param name="Root">Die untersuchte Partition, etwa <c>C:\</c>.</param>
/// <param name="HasSeekPenalty">
/// Ob der Datenträger eine Kopfbewegung kennt. <c>true</c> heißt Festplatte,
/// <c>false</c> heißt SSD, <c>null</c> heißt nicht ermittelbar. Diese eine Angabe
/// entscheidet, ob die Zahlen darüber überhaupt etwas bedeuten.
/// </param>
public sealed record FragmentationReport(string Root, bool? HasSeekPenalty)
{
    /// <summary>Anteil zerstückelter Dateien in Prozent.</summary>
    public int? FilePercent { get; init; }

    /// <summary>Zerstückelung des freien Platzes in Prozent.</summary>
    public int? FreeSpacePercent { get; init; }

    /// <summary>Gesamtwert, wie ihn auch das Windows-Werkzeug nennt.</summary>
    public int? TotalPercent { get; init; }

    public long? TotalFiles { get; init; }

    public long? FragmentedFiles { get; init; }

    /// <summary>Bruchstücke über das eine hinaus, das jede Datei mindestens hat.</summary>
    public long? ExcessFragments { get; init; }

    public double? AverageFragmentsPerFile { get; init; }

    /// <summary>Belegung der Master File Table in Prozent.</summary>
    public int? MftPercentInUse { get; init; }

    /// <summary>Ob Windows selbst zu einem Durchlauf rät.</summary>
    public bool DefragRecommended { get; init; }

    public TimeSpan Duration { get; init; }

    /// <summary>Alle gelieferten Felder — für die Probe, damit nichts unbemerkt fehlt.</summary>
    public IReadOnlyDictionary<string, string> Raw { get; init; } =
        new Dictionary<string, string>();

    /// <summary>
    /// Was Windows auf diesem Medium täte. Auf einer SSD ist es <b>kein</b>
    /// Defragmentieren, sondern ein Retrim — und das ist keine Auslassung,
    /// sondern der Punkt.
    /// </summary>
    public string ActionLabel => HasSeekPenalty switch
    {
        true => "Defragmentieren",
        false => "TRIM ausführen",
        _ => "Optimieren",
    };

    /// <summary>
    /// Ob die Prozentzahlen als Handlungsbedarf zu lesen sind. Auf einer SSD gibt
    /// es keine Kopfbewegung, die eine zerstückelte Datei teurer machte; die Zahl
    /// beschreibt dort die Buchführung des Dateisystems und nicht die Leistung.
    /// </summary>
    public bool FragmentationMatters => HasSeekPenalty is true;
}

/// <summary>
/// Analyse und Optimierung einer Partition — beides über die Bordmittel von
/// Windows.
/// </summary>
/// <remarks>
/// Die Analyse kommt aus <c>Win32_Volume.DefragAnalysis()</c> und nicht aus der
/// Ausgabe von <c>defrag.exe</c>: die Methode liefert Zahlen in Feldern, der
/// Befehl liefert übersetzten Fließtext. Dieselbe Überlegung wie bei den
/// PDH-Zählernamen (DESIGN.md §8.1) — was übersetzt ist, taugt nicht als
/// Schnittstelle.
/// <para>
/// Ausgeführt wird <c>defrag.exe</c> mit <c>/O</c>. Der Schalter heißt
/// „optimieren" und nicht „defragmentieren", weil Windows selbst je Medium
/// entscheidet: auf einer Festplatte ein Defragmentierlauf, auf einer SSD ein
/// Retrim. Ein erzwungenes Defragmentieren einer SSD bringt keine Geschwindigkeit
/// und kostet Schreibzyklen — deshalb wird es hier nicht angeboten.
/// </para>
/// </remarks>
public static class VolumeMaintenance
{
    private const string Source = "Datenträger-Optimierung";

    /// <summary>
    /// Untersucht eine Partition. Blockiert, solange Windows misst, und gehört
    /// deshalb auf einen Hintergrund-Thread.
    /// </summary>
    public static FragmentationReport Analyze(string root)
    {
        var watch = Stopwatch.StartNew();
        string letter = Letter(root);
        bool? seekPenalty = StorageDevice.HasSeekPenalty(root);

        var raw = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        bool recommended = false;

        try
        {
            using var scope = new ManagementObjectSearcher(
                new ObjectQuery($"SELECT * FROM Win32_Volume WHERE DriveLetter = '{letter}'"));

            foreach (ManagementBaseObject item in scope.Get())
            {
                using var volume = (ManagementObject)item;
                using ManagementBaseObject result = volume.InvokeMethod(
                    "DefragAnalysis", null, null);

                recommended = result["DefragRecommended"] is true;

                if (result["DefragAnalysis"] is ManagementBaseObject analysis)
                {
                    using (analysis)
                    {
                        foreach (PropertyData property in analysis.Properties)
                        {
                            if (property.Value is { } value)
                                raw[property.Name] = Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty;
                        }
                    }
                }

                break;
            }
        }
        catch (Exception ex) when (ex is ManagementException or UnauthorizedAccessException)
        {
            // Ohne erhöhte Rechte verweigert WMI die Methode. Der Bericht kommt
            // dann ohne Zahlen zurück; die Oberfläche sagt das.
            DiagnosticLog.Report(Source, ex, $"Die Analyse von »{root}« ist fehlgeschlagen");
        }

        watch.Stop();

        return new FragmentationReport(root, seekPenalty)
        {
            FilePercent = Int(raw, "FilePercentFragmentation"),
            FreeSpacePercent = Int(raw, "FreeSpacePercentFragmentation"),
            TotalPercent = Int(raw, "TotalPercentFragmentation"),
            TotalFiles = Long(raw, "TotalFiles"),
            FragmentedFiles = Long(raw, "TotalFragmentedFiles"),
            ExcessFragments = Long(raw, "TotalExcessFragments"),
            AverageFragmentsPerFile = Double(raw, "AverageFragmentsPerFile"),
            MftPercentInUse = Int(raw, "MFTPercentInUse"),
            DefragRecommended = recommended,
            Duration = watch.Elapsed,
            Raw = raw,
        };
    }

    /// <summary>
    /// Startet die Optimierung und meldet jede Ausgabezeile weiter.
    /// </summary>
    /// <remarks>
    /// <c>defrag.exe</c> schreibt seinen Fortschritt zeilenweise; die Zeilen sind
    /// übersetzt und werden deshalb nur angezeigt, nie ausgewertet. Maßgeblich
    /// ist der Rückgabewert des Prozesses.
    /// <para>
    /// Der Abbruch beendet <c>defrag.exe</c>. Das ist unbedenklich: das Werkzeug
    /// arbeitet in Schritten, die jeder für sich abgeschlossen sind, und lässt
    /// kein halbfertiges Dateisystem zurück.
    /// </para>
    /// </remarks>
    public static async Task<int> OptimizeAsync(
        string root, Action<string> onLine, CancellationToken token)
    {
        string letter = Letter(root);

        var startInfo = new ProcessStartInfo("defrag.exe", $"{letter} /O /U")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };

        using var process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };

        process.OutputDataReceived += (_, args) =>
        {
            if (!string.IsNullOrWhiteSpace(args.Data))
                onLine(args.Data.Trim());
        };

        process.ErrorDataReceived += (_, args) =>
        {
            if (!string.IsNullOrWhiteSpace(args.Data))
                onLine(args.Data.Trim());
        };

        if (!process.Start())
            throw new InvalidOperationException("defrag.exe konnte nicht gestartet werden.");

        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        try
        {
            await process.WaitForExitAsync(token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            try
            {
                if (!process.HasExited)
                    process.Kill(entireProcessTree: true);
            }
            catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
            {
                // Zwischen Abbruch und Kill von selbst beendet.
            }

            throw;
        }

        return process.ExitCode;
    }

    /// <summary>
    /// Macht aus <c>C:\</c> das <c>C:</c>, das beide Werkzeuge erwarten. WMI
    /// vergleicht den Buchstaben mit Doppelpunkt und ohne Trennzeichen,
    /// <c>defrag.exe</c> nimmt dieselbe Form.
    /// </summary>
    private static string Letter(string root)
    {
        string trimmed = root.TrimEnd('\\', '/');
        return trimmed.Length >= 2 && trimmed[1] == ':' ? trimmed[..2].ToUpperInvariant() : trimmed;
    }

    private static int? Int(IReadOnlyDictionary<string, string> raw, string key)
        => raw.TryGetValue(key, out string? value)
            && int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsed)
            ? parsed
            : null;

    private static long? Long(IReadOnlyDictionary<string, string> raw, string key)
        => raw.TryGetValue(key, out string? value)
            && long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out long parsed)
            ? parsed
            : null;

    private static double? Double(IReadOnlyDictionary<string, string> raw, string key)
        => raw.TryGetValue(key, out string? value)
            && double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out double parsed)
            ? parsed
            : null;
}

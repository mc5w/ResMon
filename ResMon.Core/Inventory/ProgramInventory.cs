using System.Globalization;
using System.IO;
using System.Security;
using Microsoft.Win32;
using ResMon.Core.Diagnostics;
using ResMon.Core.Storage;

namespace ResMon.Core.Inventory;

/// <summary>
/// Was installiert ist, wie groß es wirklich ist und wann es zuletzt lief.
/// </summary>
/// <remarks>
/// Die Liste stammt aus denselben drei Uninstall-Schlüsseln, aus denen auch
/// „Apps &amp; Features“ liest. Zwei Dinge macht diese Inventur anders, und beide
/// sind der Grund, warum es sie gibt:
/// <para>
/// <b>Die Größe wird gemessen, nicht geglaubt.</b> Der Wert <c>EstimatedSize</c>
/// steht auf der Referenzmaschine nur bei 60 von 108 Programmen und ist auch dort
/// selbstgemeldet. Stattdessen wird der Installationsordner im Baum eines
/// gelaufenen Scans nachgeschlagen oder eigens durchlaufen. Wo das nicht geht,
/// bleibt die Zelle leer — eine falsche Zahl wäre schlimmer als keine, weil nach
/// ihr sortiert wird.
/// </para>
/// <para>
/// <b>Dazu kommt, wann das Programm zuletzt lief</b> (<see cref="UsageHistory"/>).
/// Erst diese Spalte macht die Liste zu einer Entscheidungsgrundlage: „groß“ ist
/// kein Grund, etwas zu deinstallieren, „groß und lange nicht angefasst“ schon.
/// </para>
/// </remarks>
public static class ProgramInventory
{
    private const string Source = "Programm-Inventar";

    private const string UninstallPath =
        @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall";

    private const string UninstallPath32 =
        @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall";

    /// <summary>
    /// Ein Installationsort mit weniger Segmenten als dieses ist keiner. Manche
    /// Installer tragen dort das Laufwerk oder <c>C:\Program Files</c> selbst ein;
    /// ein Durchlauf darüber maße dem einen Programm die halbe Partition zu.
    /// </summary>
    private const int MinInstallPathSegments = 2;

    /// <summary>
    /// Sammelt die Inventur ein.
    /// </summary>
    /// <param name="scan">
    /// Das Ergebnis eines gelaufenen Ordner-Scans. Liegt es vor, kommen die Größen
    /// daraus — kostenlos, weil der Baum die Ordner ohnehin schon enthält. Fehlt
    /// es, wird jeder Installationsordner einzeln durchlaufen.
    /// </param>
    /// <param name="token">Bricht das Messen ab; die Liste selbst bleibt erhalten.</param>
    public static ProgramReport Collect(FolderScanResult? scan = null, CancellationToken token = default)
    {
        var entries = new List<ProgramEntry>();
        int raw = 0;

        UsageHistory usage = UsageHistory.Collect();

        Read(entries, ref raw, Registry.LocalMachine, UninstallPath, ProgramScope.AllUsers, usage, scan, token);
        Read(entries, ref raw, Registry.LocalMachine, UninstallPath32, ProgramScope.AllUsers32, usage, scan, token);
        Read(entries, ref raw, Registry.CurrentUser, UninstallPath, ProgramScope.CurrentUser, usage, scan, token);

        var limitations = new List<string>(usage.Limitations);

        // Die wichtigste Zeile der ganzen Liste. Eine leere Spalte „zuletzt
        // benutzt“ lädt zu genau dem Fehlschluss ein, der Programme kostet.
        int withoutUse = entries.Count(entry => entry.LastUsed is null);
        if (withoutUse > 0)
        {
            limitations.Add(
                $"Bei {withoutUse} von {entries.Count} Programmen kennt keine der beiden Quellen " +
                "die Hauptanwendung. Das heißt „nicht gefunden“ und ausdrücklich nicht „nie " +
                "benutzt“ — Spiele etwa werden über ihre Plattform gestartet und tauchen unter " +
                "deren Namen auf, nicht unter ihrem eigenen.");
        }

        int withoutSize = entries.Count(entry => entry.Bytes is null);
        if (withoutSize > 0)
        {
            limitations.Add(
                $"{withoutSize} von {entries.Count} Programmen tragen keinen Installationsort in der " +
                "Registry. Ihre Größe bleibt offen — sie sind nicht etwa klein.");
        }

        limitations.Add(
            "Portabel entpackte Programme haben keinen Uninstall-Eintrag und fehlen in dieser " +
            "Liste vollständig, auch wenn sie Platz belegen.");

        return new ProgramReport(DateTime.Now)
        {
            Programs = [.. entries.OrderByDescending(entry => entry.Bytes ?? -1)],
            Limitations = limitations,
            RawEntryCount = raw,
        };
    }

    /// <summary>
    /// Liest einen Uninstall-Schlüssel. Aufbau wie
    /// <see cref="Startup.StartupInventory"/>: jeder Zugriff einzeln abgesichert,
    /// damit ein gesperrter Unterschlüssel nicht die ganze Erhebung mitnimmt.
    /// </summary>
    private static void Read(
        List<ProgramEntry> entries,
        ref int raw,
        RegistryKey hive,
        string path,
        ProgramScope scope,
        UsageHistory usage,
        FolderScanResult? scan,
        CancellationToken token)
    {
        try
        {
            using RegistryKey? root = hive.OpenSubKey(path);
            if (root is null)
                return;

            foreach (string name in root.GetSubKeyNames())
            {
                if (token.IsCancellationRequested)
                    return;

                raw++;

                try
                {
                    using RegistryKey? key = root.OpenSubKey(name);
                    if (key is null)
                        continue;

                    ProgramEntry? entry = Describe(key, scope, usage, scan, token);
                    if (entry is not null)
                        entries.Add(entry);
                }
                catch (Exception ex) when (ex is SecurityException or UnauthorizedAccessException or IOException)
                {
                    DiagnosticLog.Report(Source, ex, $"Der Eintrag »{name}« ließ sich nicht lesen");
                }
            }
        }
        catch (Exception ex) when (ex is SecurityException or UnauthorizedAccessException or IOException)
        {
            DiagnosticLog.Report(Source, ex, $"Der Schlüssel »{path}« ließ sich nicht lesen");
        }
    }

    /// <summary>
    /// Macht aus einem Registry-Eintrag ein Programm — oder nichts, wenn der
    /// Eintrag keines beschreibt.
    /// </summary>
    /// <remarks>
    /// Aussortiert wird dreierlei: Einträge ohne Anzeigenamen (Platzhalter von
    /// MSI), Einträge mit <c>SystemComponent</c> (Laufzeitpakete, die Windows
    /// selbst verwaltet) und Einträge mit <c>ParentKeyName</c> (Nachträge zu einem
    /// anderen Programm, etwa ein Sprachpaket). Ohne diesen Filter stünden auf der
    /// Referenzmaschine 246 statt 108 Zeilen, die meisten davon Bruchstücke.
    /// </remarks>
    private static ProgramEntry? Describe(
        RegistryKey key,
        ProgramScope scope,
        UsageHistory usage,
        FolderScanResult? scan,
        CancellationToken token)
    {
        if (key.GetValue("DisplayName") is not string displayName || string.IsNullOrWhiteSpace(displayName))
            return null;

        if (key.GetValue("SystemComponent") is int flag && flag != 0)
            return null;

        if (key.GetValue("ParentKeyName") is string parent && !string.IsNullOrWhiteSpace(parent))
            return null;

        string? installLocation = Clean(key.GetValue("InstallLocation") as string);
        string? mainExecutable = MainExecutable(key.GetValue("DisplayIcon") as string, installLocation);

        (long? bytes, SizeOrigin origin) = Measure(installLocation, scan, token);
        UsageRecord? record = usage.ForExecutable(mainExecutable);

        return new ProgramEntry(displayName.Trim(), scope)
        {
            Version = Clean(key.GetValue("DisplayVersion") as string),
            Publisher = Clean(key.GetValue("Publisher") as string),
            InstalledOn = ParseInstallDate(key.GetValue("InstallDate") as string),
            InstallLocation = installLocation,
            UninstallCommand = Clean(key.GetValue("UninstallString") as string),
            MainExecutable = mainExecutable,
            Bytes = bytes,
            SizeFrom = origin,
            LastUsed = record?.LastUsed,
            LaunchCount = record?.LaunchCount,
            UsageFrom = record?.Source ?? UsageSource.None,
        };
    }

    /// <summary>
    /// Bestimmt die Größe des Installationsordners.
    /// </summary>
    /// <remarks>
    /// Der Nachschlag im Baum eines gelaufenen Scans kostet nichts — die Summe je
    /// Ordner steht dort bereits, sie muss nur gefunden werden. Erst wenn kein
    /// Scan vorliegt oder der Ordner nicht darin steht (etwa weil er auf einer
    /// anderen Partition liegt), läuft ein eigener Durchlauf. Der ist für einen
    /// einzelnen Programmordner eine Sache von Millisekunden.
    /// </remarks>
    private static (long? Bytes, SizeOrigin Origin) Measure(
        string? installLocation, FolderScanResult? scan, CancellationToken token)
    {
        if (!IsUsableInstallPath(installLocation))
            return (null, SizeOrigin.Unknown);

        if (scan is not null)
        {
            int node = scan.FindByPath(installLocation!);
            if (node >= 0)
                return (scan.BytesOf(node), SizeOrigin.FromScan);
        }

        try
        {
            if (!Directory.Exists(installLocation))
                return (null, SizeOrigin.Unknown);

            FolderScanResult result = new FolderScanner().Run(installLocation!, token);
            return (result.TotalBytes, SizeOrigin.Measured);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or SecurityException)
        {
            DiagnosticLog.Report(Source, ex, $"Der Ordner »{installLocation}« ließ sich nicht messen");
            return (null, SizeOrigin.Unknown);
        }
    }

    /// <summary>
    /// Ob ein Installationsort taugt. Ein Laufwerksstamm oder ein Pfad mit einem
    /// einzigen Segment stammt von einem Installer, der das Feld falsch füllt —
    /// gemessen würde dann die halbe Partition und dem Programm zugeschlagen.
    /// </summary>
    private static bool IsUsableInstallPath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return false;

        string trimmed = path.TrimEnd('\\', '/');
        if (trimmed.Length == 0 || !Path.IsPathFullyQualified(trimmed))
            return false;

        return trimmed.Split('\\', StringSplitOptions.RemoveEmptyEntries).Length > MinInstallPathSegments;
    }

    /// <summary>
    /// Sucht die Hauptanwendung. <c>DisplayIcon</c> zeigt fast immer auf genau die
    /// Exe, deren Symbol das Programm vertritt — und damit auf die, die man
    /// startet. Nur wenn dort ein <c>.ico</c> steht oder gar nichts, wird der
    /// Installationsordner nach einer Exe abgesucht.
    /// </summary>
    private static string? MainExecutable(string? displayIcon, string? installLocation)
    {
        string? fromIcon = ExecutableFromIcon(displayIcon);
        if (fromIcon is not null)
            return fromIcon;

        if (!IsUsableInstallPath(installLocation))
            return null;

        try
        {
            if (!Directory.Exists(installLocation))
                return null;

            // Die oberste Ebene genügt: eine Anwendung, die man startet, liegt
            // dort. Ein Durchlauf in die Tiefe fände Hilfsprogramme und
            // Aktualisierer, die niemand von Hand aufruft.
            FileInfo? largest = null;
            foreach (FileInfo file in new DirectoryInfo(installLocation!).EnumerateFiles("*.exe"))
            {
                if (largest is null || file.Length > largest.Length)
                    largest = file;
            }

            return largest?.Name;
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or SecurityException)
        {
            return null;
        }
    }

    /// <summary>
    /// Löst <c>"C:\Pfad\app.exe",0</c> in <c>app.exe</c> auf. Der Zähler hinter
    /// dem Komma ist der Index des Symbols in der Datei und gehört nicht zum Pfad.
    /// </summary>
    private static string? ExecutableFromIcon(string? displayIcon)
    {
        string? value = Clean(displayIcon);
        if (value is null)
            return null;

        value = value.Trim('"');

        int comma = value.LastIndexOf(',');
        if (comma > 0 && int.TryParse(value.AsSpan()[(comma + 1)..], out _))
            value = value[..comma];

        value = value.Trim().Trim('"');

        return value.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? Path.GetFileName(value) : null;
    }

    /// <summary>
    /// <c>InstallDate</c> steht als <c>yyyyMMdd</c> ohne Uhrzeit da. Was nicht
    /// diesem Muster folgt, wird verworfen statt geraten — ein falsches
    /// Installationsdatum wäre ein stiller Fehler in einer Spalte, nach der
    /// sortiert wird.
    /// </summary>
    private static DateTime? ParseInstallDate(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;

        return DateTime.TryParseExact(
            value.Trim(), "yyyyMMdd", CultureInfo.InvariantCulture, DateTimeStyles.None, out DateTime parsed)
            ? parsed
            : null;
    }

    private static string? Clean(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}

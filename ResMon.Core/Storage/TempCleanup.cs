using System.IO;
using ResMon.Core.Diagnostics;

namespace ResMon.Core.Storage;

/// <summary>Was ein Löschlauf ausgerichtet hat.</summary>
public sealed record TempRemoval
{
    public int Removed { get; init; }

    public int Failed { get; init; }

    /// <summary>Was tatsächlich frei geworden ist — nicht, was vorgesehen war.</summary>
    public long BytesFreed { get; init; }

    /// <summary>
    /// Je fehlgeschlagenem Posten eine Zeile. Vollständig und nicht
    /// zusammengefasst: „3 Posten ließen sich nicht löschen“ beantwortet die
    /// einzige Frage nicht, die dann noch offen ist.
    /// </summary>
    public IReadOnlyList<string> Errors { get; init; } = [];
}

/// <summary>
/// Löscht ausgewählte Posten aus den Temp-Ordnern.
/// </summary>
/// <remarks>
/// Die einzige Stelle dieser Anwendung, die Dateien entfernt — der Grundsatz
/// aus DESIGN.md §13.5 („gelöscht wird nicht aus der Anwendung heraus“) bleibt
/// davon unberührt, weil er sich auf den <em>Ordnerbaum</em> bezieht: dort kann
/// jeder Pfad stehen, auch <c>C:\Windows\System32</c>, und die Anwendung läuft
/// erhöht. Hier ist die Menge des Löschbaren dagegen von vornherein
/// eingegrenzt, und die Eingrenzung wird bei jedem einzelnen Posten erneut
/// geprüft:
/// <list type="bullet">
/// <item>Der Pfad muss <b>unmittelbar</b> in einem der beiden Temp-Ordner liegen
/// — ein Unterordner davon, kein Enkel und nichts außerhalb.</item>
/// <item>Der Posten darf nicht zu einem laufenden Prozess gehören.</item>
/// <item>Er muss in der Auswahl des Benutzers stehen; ausgewählt wird von Hand,
/// Haken für Haken.</item>
/// </list>
/// Geprüft wird gegen die Pfade, die der Host selbst erhoben hat, nicht gegen
/// die, die von der Seite kommen — dieselbe Regel wie beim „Im Explorer
/// öffnen“ des Ordnerbaums.
/// <para>
/// Gelöscht wird endgültig und nicht in den Papierkorb. Das ist hier die
/// richtige Wahl und keine Nachlässigkeit: Zweck des Ganzen ist, Platz frei zu
/// machen, und ein Papierkorb gäbe genau ihn nicht her.
/// </para>
/// </remarks>
public static class TempCleanup
{
    private const string Source = "Temp-Aufräumen";

    /// <summary>
    /// Löscht die übergebenen Posten. Blockiert und gehört auf einen
    /// Hintergrund-Thread.
    /// </summary>
    public static TempRemoval Remove(IEnumerable<TempEntry> entries, CancellationToken token = default)
    {
        string[] roots = [.. TempInventory.TempRoots().Select(root => root.TrimEnd('\\'))];

        int removed = 0;
        long freed = 0;
        var errors = new List<string>();

        foreach (TempEntry entry in entries)
        {
            token.ThrowIfCancellationRequested();

            if (!IsDirectChild(entry.Path, roots))
            {
                // Kommt bei ordnungsgemäßem Ablauf nie vor. Steht trotzdem da:
                // eine Prüfung, die nur bei erwartetem Ablauf greift, ist keine.
                errors.Add($"{entry.Name}: liegt nicht unmittelbar in einem Temp-Ordner — übergangen.");
                continue;
            }

            if (entry.Owner == TempOwner.Running)
            {
                errors.Add($"{entry.Name}: gehört zu einem laufenden Programm — übergangen.");
                continue;
            }

            try
            {
                if (entry.IsDirectory)
                    Directory.Delete(entry.Path, recursive: true);
                else
                    File.Delete(entry.Path);

                removed++;
                freed += entry.Bytes;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Der Regelfall unter den Fehlschlägen: ein Programm hält eine
                // Datei darin offen. Das ist kein Grund, den Lauf abzubrechen —
                // die übrigen Posten sind davon nicht betroffen.
                DiagnosticLog.Report(Source, ex, $"„{entry.Path}“ ließ sich nicht löschen");
                errors.Add($"{entry.Name}: {ex.Message}");
            }
        }

        return new TempRemoval
        {
            Removed = removed,
            Failed = errors.Count,
            BytesFreed = freed,
            Errors = errors,
        };
    }

    /// <summary>
    /// Ob der Pfad ein unmittelbares Kind eines der Temp-Ordner ist.
    /// </summary>
    /// <remarks>
    /// Über das übergeordnete Verzeichnis und nicht über einen Präfixvergleich:
    /// <c>StartsWith</c> ließe <c>…\Temp\a\b\c</c> durch, und ein
    /// <c>..</c> im Pfad führte damit beliebig weit hinaus.
    /// </remarks>
    private static bool IsDirectChild(string path, IReadOnlyList<string> roots)
    {
        if (string.IsNullOrWhiteSpace(path))
            return false;

        string parent;
        try
        {
            parent = Path.GetDirectoryName(Path.GetFullPath(path))?.TrimEnd('\\') ?? string.Empty;
        }
        catch (Exception ex) when (ex is ArgumentException or PathTooLongException or NotSupportedException)
        {
            return false;
        }

        return roots.Any(root => string.Equals(parent, root, StringComparison.OrdinalIgnoreCase));
    }
}

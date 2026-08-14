using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using Clipboard = System.Windows.Clipboard;
using MessageBox = System.Windows.MessageBox;

namespace ResMon.App;

/// <summary>
/// Was der Reiter „Speicher" mit einem gefundenen Pfad anbietet.
/// </summary>
/// <remarks>
/// Aufgeräumt wird bewusst <em>nicht</em> hier, sondern im Explorer: die
/// Anwendung läuft erhöht (DESIGN.md §14), ein Fehlgriff träfe also auch
/// Systemordner — und zwar ohne Papierkorb und ohne Rückgängig. Nach dem Muster
/// von <see cref="ProcessTerminator"/>: erst prüfen, sonst verständlich melden.
/// </remarks>
internal static class PathActions
{
    private const string Title = "Speicher";

    /// <summary>Zeigt den Eintrag im Explorer, im übergeordneten Ordner markiert.</summary>
    public static void Reveal(Window owner, string path)
    {
        if (!Exists(owner, path))
            return;

        // explorer.exe ist ein Win32-Verbraucher und kennt die erweiterte
        // Pfadform nicht; jenseits von 260 Zeichen scheitert es stumm.
        if (path.Length >= 260)
        {
            Warn(owner,
                $"Der Pfad ist {path.Length} Zeichen lang. Der Explorer kann ihn nicht öffnen — " +
                "er unterstützt lange Pfade nur, wenn sie systemweit eingeschaltet sind.\n\n" +
                "„Pfad kopieren“ funktioniert weiterhin.");
            return;
        }

        try
        {
            // Bei einer Laufwerkswurzel gibt es keinen übergeordneten Ordner, in
            // dem sich etwas markieren ließe.
            bool isRoot = string.Equals(Path.GetPathRoot(path), path, StringComparison.OrdinalIgnoreCase);
            string arguments = isRoot ? $"\"{path}\"" : $"/select,\"{path}\"";

            using Process? _ = Process.Start(new ProcessStartInfo
            {
                FileName = "explorer.exe",
                Arguments = arguments,
                UseShellExecute = false,
            });
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException)
        {
            Warn(owner, $"Der Explorer ließ sich nicht öffnen: {ex.Message}");
        }
    }

    /// <summary>
    /// Legt Text in die Zwischenablage — einen vollen Pfad oder den Befehl eines
    /// Befunds. Kopiert wird nur; ausgeführt wird aus dieser Anwendung heraus
    /// nichts, aus demselben Grund, aus dem hier nicht gelöscht wird.
    /// </summary>
    public static void Copy(Window owner, string text)
    {
        if (string.IsNullOrEmpty(text))
            return;

        try
        {
            Clipboard.SetText(text);
        }
        catch (Exception ex) when (ex is ExternalException or InvalidOperationException)
        {
            // Die Zwischenablage gehört kurzzeitig einem anderen Prozess.
            Warn(owner, $"Der Text ließ sich nicht kopieren: {ex.Message}");
        }
    }

    private static bool Exists(Window owner, string path)
    {
        if (string.IsNullOrEmpty(path))
            return false;

        if (Directory.Exists(path) || File.Exists(path))
            return true;

        // Zwischen Scan und Klick kann viel passieren — gerade in den Ordnern,
        // die man aufräumen will.
        Warn(owner, $"„{path}“ gibt es nicht mehr. Der Scan ist von vorhin; ein neuer Lauf bringt ihn auf Stand.");
        return false;
    }

    private static void Warn(Window owner, string message)
        => MessageBox.Show(owner, message, Title, MessageBoxButton.OK, MessageBoxImage.Information);
}

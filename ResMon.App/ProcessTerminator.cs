using System.ComponentModel;
using System.Diagnostics;
using System.Windows;
using MessageBox = System.Windows.MessageBox;

namespace ResMon.App;

/// <summary>
/// Beendet einen Prozess nach ausdrücklicher Rückfrage.
/// </summary>
/// <remarks>
/// DESIGN.md §12 hatte ein solches Kommando bewusst ausgeschlossen. Es ist auf
/// Wunsch nachgezogen worden — mit Bestätigungsdialog und einer Sperre für die
/// Prozesse, ohne die Windows nicht weiterläuft.
/// </remarks>
public static class ProcessTerminator
{
    /// <summary>
    /// Prozesse, deren Ende einen Bluescreen oder eine erzwungene Abmeldung
    /// auslöst. Der Task-Manager verweigert sie ebenfalls.
    /// </summary>
    private static readonly string[] CriticalNames =
    [
        "system", "smss.exe", "csrss.exe", "wininit.exe", "winlogon.exe",
        "services.exe", "lsass.exe", "memory compression", "registry", "idle",
    ];

    public static void RequestKill(Window owner, int pid, string? name)
    {
        if (pid <= 4)
        {
            Warn(owner, $"„{name ?? "Prozess"}“ (PID {pid}) ist ein Kernprozess des Systems und lässt sich nicht beenden.");
            return;
        }

        Process process;
        try
        {
            process = Process.GetProcessById(pid);
        }
        catch (ArgumentException)
        {
            Warn(owner, $"Der Prozess mit PID {pid} läuft nicht mehr.");
            return;
        }

        using (process)
        {
            string label = string.IsNullOrWhiteSpace(name) ? process.ProcessName : name;

            if (IsCritical(label) || IsCritical(process.ProcessName))
            {
                Warn(owner,
                    $"„{label}“ ist ein kritischer Systemprozess. Ihn zu beenden führt zum Absturz " +
                    "oder zur erzwungenen Abmeldung — ResMon verweigert das.");
                return;
            }

            MessageBoxResult answer = MessageBox.Show(
                owner,
                $"„{label}“ (PID {pid}) beenden?\n\n" +
                "Nicht gespeicherte Daten dieses Programms gehen verloren. Der Prozess wird " +
                "hart beendet, er bekommt keine Gelegenheit zum Aufräumen.",
                "Prozess beenden",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning,
                MessageBoxResult.No);

            if (answer != MessageBoxResult.Yes)
                return;

            try
            {
                process.Kill(entireProcessTree: false);
            }
            catch (Win32Exception ex)
            {
                Warn(owner, $"„{label}“ konnte nicht beendet werden: {ex.Message}");
            }
            catch (InvalidOperationException)
            {
                // Zwischen Rückfrage und Kill beendet — das Ziel ist erreicht.
            }
            catch (NotSupportedException ex)
            {
                Warn(owner, $"„{label}“ konnte nicht beendet werden: {ex.Message}");
            }
        }
    }

    private static bool IsCritical(string name)
    {
        string trimmed = name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? name : name + ".exe";
        foreach (string critical in CriticalNames)
        {
            if (name.Equals(critical, StringComparison.OrdinalIgnoreCase) ||
                trimmed.Equals(critical, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static void Warn(Window owner, string message)
        => MessageBox.Show(owner, message, "Prozess beenden", MessageBoxButton.OK, MessageBoxImage.Information);
}

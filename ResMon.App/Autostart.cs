using System.Diagnostics;
using System.IO;

namespace ResMon.App;

/// <summary>
/// Autostart über die Aufgabenplanung statt über den Registry-Run-Key: bei einer
/// Anwendung mit Administratorrechten führt der Run-Key bei jeder Anmeldung zu
/// einer UAC-Abfrage (DESIGN.md §14).
/// </summary>
public static class Autostart
{
    public const string TaskName = "ResMon Overlay";

    /// <summary>True, wenn die geplante Aufgabe existiert.</summary>
    public static bool IsEnabled()
        => RunSchtasks($"/Query /TN \"{TaskName}\"", out _);

    /// <summary>
    /// Legt die Aufgabe an: Trigger "Bei Anmeldung", mit höchsten Privilegien.
    /// Erfordert Administratorrechte.
    /// </summary>
    public static bool Enable(out string? error)
    {
        string exePath = Path.ChangeExtension(Environment.ProcessPath ?? string.Empty, ".exe");
        if (string.IsNullOrEmpty(exePath) || !File.Exists(exePath))
        {
            error = "Pfad der ausführbaren Datei konnte nicht bestimmt werden.";
            return false;
        }

        // Vollqualifizierter Benutzer, damit schtasks den interaktiven Token
        // verwendet und nicht nach einem Kennwort fragt.
        string account = $"{Environment.UserDomainName}\\{Environment.UserName}";
        string arguments =
            $"/Create /F /TN \"{TaskName}\" /TR \"\\\"{exePath}\\\"\" /SC ONLOGON /RL HIGHEST /RU \"{account}\"";

        bool ok = RunSchtasks(arguments, out string output);
        error = ok ? null : output;
        return ok;
    }

    public static bool Disable(out string? error)
    {
        bool ok = RunSchtasks($"/Delete /F /TN \"{TaskName}\"", out string output);
        error = ok ? null : output;
        return ok;
    }

    private static bool RunSchtasks(string arguments, out string output)
    {
        var startInfo = new ProcessStartInfo("schtasks.exe", arguments)
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
                output = "schtasks.exe konnte nicht gestartet werden.";
                return false;
            }

            string stdout = process.StandardOutput.ReadToEnd();
            string stderr = process.StandardError.ReadToEnd();
            process.WaitForExit(10_000);

            output = string.IsNullOrWhiteSpace(stderr) ? stdout.Trim() : stderr.Trim();
            return process.ExitCode == 0;
        }
        catch (Exception ex)
        {
            output = ex.Message;
            return false;
        }
    }
}

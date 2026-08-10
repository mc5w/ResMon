using System.Runtime.InteropServices;
using System.Text;

namespace ResMon.Core.Native;

/// <summary>
/// Findet die Prozesse, die ein sichtbares eigenes Fenster besitzen. Das ist das
/// Merkmal, an dem der Task-Manager „Apps" von Hintergrundprozessen trennt — ein
/// Prozess ohne Fenster hat für den Benutzer keine Oberfläche, egal wie er heißt.
/// </summary>
public static class WindowOwners
{
    private const int GWL_EXSTYLE = -20;
    private const int GW_OWNER = 4;
    private const int WS_EX_TOOLWINDOW = 0x00000080;

    private delegate bool EnumWindowsProc(IntPtr window, IntPtr parameter);

    /// <summary>
    /// Das sichtbare Fenster eines Prozesses. <paramref name="Hung"/> ist true,
    /// wenn eines seiner Fenster keine Nachrichten mehr abholt — das ist der
    /// Zustand, den Windows mit „(Keine Rückmeldung)" in die Titelleiste
    /// schreibt.
    /// </summary>
    public readonly record struct WindowState(string Title, bool Hung);

    /// <summary>
    /// PIDs mit sichtbarem Fenster oberster Ebene, dazu dessen Titel und
    /// Zustand. Der Aufruf kostet wenige Millisekunden und läuft im Prozess-Takt
    /// mit. Hat ein Prozess mehrere Fenster, gewinnt der Titel des ersten — die
    /// Reihenfolge entspricht der Z-Ordnung, also steht das vorderste vorn —,
    /// als hängend gilt er aber, sobald auch nur eines seiner Fenster hängt.
    /// </summary>
    public static Dictionary<int, WindowState> Snapshot()
    {
        var result = new Dictionary<int, WindowState>();
        var buffer = new StringBuilder(256);

        EnumWindows((window, parameter) =>
        {
            if (!IsWindowVisible(window))
                return true;

            // Fenster mit Besitzer sind Dialoge und Werkzeugfenster ihrer
            // Anwendung, keine eigenständigen Einträge.
            if (GetWindow(window, GW_OWNER) != IntPtr.Zero)
                return true;

            if ((GetWindowLongPtr(window, GWL_EXSTYLE).ToInt64() & WS_EX_TOOLWINDOW) != 0)
                return true;

            // Unbeschriftete Fenster sind unsichtbare Nachrichtenfenster und
            // Hüllen, wie sie jede Laufzeitumgebung anlegt.
            int length = GetWindowTextLengthW(window);
            if (length == 0)
                return true;

            _ = GetWindowThreadProcessId(window, out int pid);
            if (pid <= 0)
                return true;

            bool hung = IsHungAppWindow(window);
            if (result.TryGetValue(pid, out WindowState known))
            {
                // Ein weiteres Fenster desselben Prozesses: der Titel steht schon,
                // aber ein hängendes Fenster darf nicht untergehen.
                if (hung && !known.Hung)
                    result[pid] = known with { Hung = true };
                return true;
            }

            if (buffer.Capacity < length + 1)
                buffer.Capacity = length + 1;

            int written = GetWindowTextW(window, buffer, buffer.Capacity);
            if (written > 0)
                result[pid] = new WindowState(buffer.ToString(0, written), hung);

            return true;
        }, IntPtr.Zero);

        return result;
    }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumWindows(EnumWindowsProc callback, IntPtr parameter);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindowVisible(IntPtr window);

    [DllImport("user32.dll")]
    private static extern IntPtr GetWindow(IntPtr window, int command);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW", SetLastError = true)]
    private static extern IntPtr GetWindowLongPtr(IntPtr window, int index);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowTextLengthW(IntPtr window);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowTextW(IntPtr window, StringBuilder text, int count);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint GetWindowThreadProcessId(IntPtr window, out int processId);

    /// <summary>
    /// True, wenn das Fenster seit fünf Sekunden keine Nachricht mehr abgeholt
    /// hat. Dieselbe Prüfung, mit der der Explorer „(Keine Rückmeldung)"
    /// anzeigt, und sie blockiert nicht — anders als das Senden einer
    /// Testnachricht.
    /// </summary>
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsHungAppWindow(IntPtr window);
}

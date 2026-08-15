using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows;
using Microsoft.Win32.SafeHandles;
using MessageBox = System.Windows.MessageBox;

namespace ResMon.App;

/// <summary>
/// Öffnet ein PowerShell-Fenster, in dem der Befehl eines Befunds bereits
/// getippt steht — abgeschickt wird er nicht.
/// </summary>
/// <remarks>
/// Der Mittelweg zwischen den beiden Enden, die beide falsch wären. Den Befehl
/// selbst auszuführen verbietet sich: die Anwendung läuft erhöht, und was diese
/// Befehle anrichten, ist unumkehrbar (DESIGN.md §13.5). Ihn nur in die
/// Zwischenablage zu legen verlangt vom Benutzer die drei Schritte, bei denen
/// erfahrungsgemäß etwas schiefgeht — Fenster öffnen, einfügen, und dabei nicht
/// aus Versehen das falsche Fenster erwischen.
/// <para>
/// Hier steht der Befehl am Ende sichtbar in einer Eingabezeile. Wer ihn
/// abschickt, ist der Benutzer, und er sieht vorher genau, was er abschickt.
/// Das ist derselbe Umgang wie beim Beenden eines Prozesses: der Eingriff
/// bleibt beim Benutzer, die Anwendung nimmt ihm nur die Fehlerquellen ab.
/// </para>
/// <para>
/// <b>Wie der Text in die fremde Eingabezeile kommt.</b> Eine Konsole hat einen
/// Eingabepuffer, in dem die Tastendrücke stehen, bevor das Programm sie liest.
/// Genau dorthin wird geschrieben: die Anwendung hängt sich mit
/// <c>AttachConsole</c> an die Konsole des gestarteten Prozesses und legt über
/// <c>WriteConsoleInput</c> die Zeichen des Befehls als Tastenereignisse hinein.
/// PowerShell liest sie, als wären sie getippt worden. Ein „Zeilenende“ ist
/// ausdrücklich nicht dabei — das ist der Tastendruck, den der Benutzer selbst
/// tut.
/// </para>
/// <para>
/// Der heikle Teil daran ist der Zeitpunkt, nicht das Schreiben. Wird zu früh
/// geschrieben, verwirft PowerShell den Puffer beim Hochfahren wieder. Deshalb
/// wird nicht geraten und gewartet, sondern verabredet: die Anwendung legt ein
/// benanntes Ereignis an, das gestartete PowerShell setzt es als allerletzten
/// Schritt vor der ersten Eingabeaufforderung. Erst danach wird geschrieben.
/// </para>
/// </remarks>
internal static class ShellLauncher
{
    private const string Title = "Speicher";

    /// <summary>
    /// Wie lange auf das Bereitschaftszeichen von PowerShell gewartet wird. Auf
    /// einem trägen Rechner mit kaltem Zwischenspeicher dauert der Start mehrere
    /// Sekunden; darüber hinaus stimmt etwas anderes nicht.
    /// </summary>
    private static readonly TimeSpan ReadyTimeout = TimeSpan.FromSeconds(20);

    /// <summary>
    /// Nach dem Zeichen noch dieser Abstand. Das Ereignis wird gesetzt, bevor die
    /// Eingabezeile tatsächlich liest — dazwischen liegt der Aufbau der
    /// Eingabezeile selbst.
    /// </summary>
    private static readonly TimeSpan SettleDelay = TimeSpan.FromMilliseconds(400);

    /// <summary>
    /// Startet PowerShell und schreibt den Befehl in dessen Eingabezeile.
    /// </summary>
    /// <remarks>
    /// Läuft im Hintergrund: zwischen Start und Bereitschaftszeichen liegen
    /// Sekunden, und die gehören nicht auf den Oberflächen-Thread. Der Aufrufer
    /// bekommt sofort die Steuerung zurück.
    /// </remarks>
    public static void Run(Window owner, string command)
    {
        if (string.IsNullOrWhiteSpace(command))
            return;

        // Vor allem anderen in die Zwischenablage. Schlägt das Schreiben in die
        // Konsole fehl — eine andere Standardkonsole, ein Zugriffsfehler, ein
        // PowerShell, das gar nicht erst hochkommt —, ist der Befehl trotzdem
        // zur Hand, und der Benutzer steht nicht vor einem leeren Fenster.
        PathActions.Copy(owner, command);

        string signal = $"ResMon.Shell.{Guid.NewGuid():N}";

        Task.Run(() => Launch(signal, command)).ContinueWith(
            task =>
            {
                if (task.Exception?.GetBaseException() is { } error)
                {
                    owner.Dispatcher.BeginInvoke(() => MessageBox.Show(
                        owner,
                        $"Das PowerShell-Fenster ließ sich nicht öffnen: {error.Message}\n\n"
                        + "Der Befehl liegt in der Zwischenablage.",
                        Title, MessageBoxButton.OK, MessageBoxImage.Information));
                }
            },
            TaskScheduler.Default);
    }

    private static void Launch(string signal, string command)
    {
        // Das Ereignis muss stehen, bevor PowerShell es öffnen will.
        using var ready = new EventWaitHandle(false, EventResetMode.ManualReset, signal);

        // -NoExit hält das Fenster offen, -NoLogo spart den Begrüßungsblock, und
        // das Startskript tut genau zweierlei: sagen, worum es geht, und
        // Bescheid geben, dass die Eingabezeile gleich bereitsteht.
        // `$e.Set()` gibt einen Wahrheitswert zurück, und PowerShell schreibt
        // alles, was eine Anweisung zurückgibt, in die Ausgabe. Ohne das
        // vorangestellte `[void]` stand über der Eingabezeile ein nacktes
        // „True“ — die Verabredung zwischen Host und Fenster gehört aber nicht
        // auf den Bildschirm, sie ist Innenleben.
        string bootstrap =
            "$ErrorActionPreference='SilentlyContinue';" +
            "Write-Host 'Der Befehl steht unten in der Eingabezeile. Lesen, dann Enter.' " +
            "-ForegroundColor Yellow;" +
            $"$e=[System.Threading.EventWaitHandle]::OpenExisting('{signal}');" +
            "[void]$e.Set();$e.Dispose()";

        var startInfo = new ProcessStartInfo("powershell.exe")
        {
            Arguments = $"-NoLogo -NoExit -ExecutionPolicy Bypass -Command \"{bootstrap}\"",

            // Eigenes Fenster mit eigener Konsole — ohne die gäbe es keinen
            // Eingabepuffer, in den sich schreiben ließe.
            UseShellExecute = true,
        };

        using Process? process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("powershell.exe wurde nicht gestartet.");

        if (!ready.WaitOne(ReadyTimeout))
        {
            // Kein Zeichen. Das Fenster steht, der Befehl liegt in der
            // Zwischenablage — nur getippt wird er nicht. Nichts zu melden, was
            // der Benutzer nicht schon sähe.
            return;
        }

        Thread.Sleep(SettleDelay);

        if (process.HasExited)
            return;

        TypeIntoConsole(process.Id, command);
    }

    /// <summary>
    /// Legt den Text als Tastenereignisse in den Eingabepuffer der Konsole eines
    /// anderen Prozesses.
    /// </summary>
    private static void TypeIntoConsole(int processId, string text)
    {
        // Eine Anwendung kann immer nur an einer Konsole hängen. Diese hier ist
        // eine Fensteranwendung und hat keine — der Aufruf schlägt fehl und das
        // ist in Ordnung. Er steht trotzdem da, weil ein zweites geöffnetes
        // Fenster in derselben Sitzung sonst an der ersten Konsole hinge.
        FreeConsole();

        if (!AttachConsole(processId))
            throw new Win32Exception(Marshal.GetLastWin32Error(),
                "Die Konsole des PowerShell-Fensters ließ sich nicht ansprechen.");

        try
        {
            using SafeFileHandle input = CreateConsoleInput();
            INPUT_RECORD[] records = KeyEvents(text);

            if (!WriteConsoleInput(input, records, (uint)records.Length, out _))
                throw new Win32Exception(Marshal.GetLastWin32Error(),
                    "Der Befehl ließ sich nicht in die Eingabezeile schreiben.");
        }
        finally
        {
            FreeConsole();
        }
    }

    private static SafeFileHandle CreateConsoleInput()
    {
        SafeFileHandle handle = CreateFile(
            "CONIN$",
            GENERIC_READ | GENERIC_WRITE,
            FILE_SHARE_READ | FILE_SHARE_WRITE,
            IntPtr.Zero,
            OPEN_EXISTING,
            0,
            IntPtr.Zero);

        if (handle.IsInvalid)
            throw new Win32Exception(Marshal.GetLastWin32Error(),
                "Der Eingabepuffer der Konsole ließ sich nicht öffnen.");

        return handle;
    }

    /// <summary>
    /// Je Zeichen ein Nieder- und ein Hochereignis.
    /// </summary>
    /// <remarks>
    /// Der Tastencode bleibt leer: maßgeblich ist das Zeichen selbst, und ein
    /// erfundener Tastencode ergäbe auf einer anderen Tastaturbelegung ein
    /// anderes Zeichen.
    /// <para>
    /// Das hat eine zweite, hier willkommene Folge. Die Eingabezeile von
    /// PowerShell bindet Sondertasten über den <em>Tastencode</em> und nicht
    /// über das Zeichen — ein Wagenrücklauf, der sich in einen Befehl verirrt,
    /// wird deshalb als Zeichen eingefügt und schickt die Zeile nicht ab. Ohne
    /// <c>VK_RETURN</c> kann von hier aus also nichts ausgelöst werden, auch
    /// nicht versehentlich. Nachgemessen: mit Tastencode 0 bleibt der Befehl
    /// stehen, erst mit <c>VK_RETURN</c> läuft er los.
    /// </para>
    /// </remarks>
    private static INPUT_RECORD[] KeyEvents(string text)
    {
        var records = new INPUT_RECORD[text.Length * 2];

        for (int i = 0; i < text.Length; i++)
        {
            records[i * 2] = KeyEvent(text[i], down: true);
            records[(i * 2) + 1] = KeyEvent(text[i], down: false);
        }

        return records;
    }

    private static INPUT_RECORD KeyEvent(char character, bool down) => new()
    {
        EventType = KEY_EVENT,
        KeyEvent = new KEY_EVENT_RECORD
        {
            bKeyDown = down ? 1 : 0,
            wRepeatCount = 1,
            UnicodeChar = character,
        },
    };

    // ---------- Win32 ----------

    private const ushort KEY_EVENT = 0x0001;
    private const uint GENERIC_READ = 0x80000000;
    private const uint GENERIC_WRITE = 0x40000000;
    private const uint FILE_SHARE_READ = 0x00000001;
    private const uint FILE_SHARE_WRITE = 0x00000002;
    private const uint OPEN_EXISTING = 3;

    [StructLayout(LayoutKind.Sequential)]
    private struct KEY_EVENT_RECORD
    {
        public int bKeyDown;
        public ushort wRepeatCount;
        public ushort wVirtualKeyCode;
        public ushort wVirtualScanCode;
        public char UnicodeChar;
        public uint dwControlKeyState;
    }

    /// <summary>
    /// Die Vereinigung aus der Konsolen-Schnittstelle. Der Kopf ist ein WORD, der
    /// Rumpf beginnt wegen der Ausrichtung des ersten DWORD darin bei 4 — auf
    /// 32 wie auf 64 Bit gleichermaßen.
    /// </summary>
    [StructLayout(LayoutKind.Explicit)]
    private struct INPUT_RECORD
    {
        [FieldOffset(0)]
        public ushort EventType;

        [FieldOffset(4)]
        public KEY_EVENT_RECORD KeyEvent;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AttachConsole(int processId);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool FreeConsole();

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern SafeFileHandle CreateFile(
        string fileName,
        uint desiredAccess,
        uint shareMode,
        IntPtr securityAttributes,
        uint creationDisposition,
        uint flagsAndAttributes,
        IntPtr templateFile);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool WriteConsoleInput(
        SafeFileHandle input,
        INPUT_RECORD[] buffer,
        uint length,
        out uint written);
}

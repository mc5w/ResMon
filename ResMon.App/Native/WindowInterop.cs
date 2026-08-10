using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace ResMon.App.Native;

/// <summary>
/// Erweiterte Fensterstile für die Klick-Durchlässigkeit des Overlays
/// (DESIGN.md §11). Der WorkerW-Trick zum Verankern auf Wallpaper-Ebene ist
/// bewusst nicht umgesetzt.
/// </summary>
internal static class WindowInterop
{
    private const int GWL_EXSTYLE = -20;
    private const int WS_EX_TRANSPARENT = 0x00000020;
    private const int WS_EX_LAYERED = 0x00080000;
    private const int WS_EX_TOOLWINDOW = 0x00000080;

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW", SetLastError = true)]
    private static extern IntPtr GetWindowLongPtr(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW", SetLastError = true)]
    private static extern IntPtr SetWindowLongPtr(IntPtr hWnd, int nIndex, IntPtr dwNewLong);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ReleaseCapture();

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr SendMessageW(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    private const uint WM_NCLBUTTONDOWN = 0x00A1;
    private const int HTCAPTION = 2;

    /// <summary>
    /// Startet das systemeigene Verschieben, als wäre auf eine Titelleiste
    /// geklickt worden. <c>Window.DragMove()</c> ist hier nicht brauchbar: es
    /// prüft <c>Mouse.LeftButton</c> aus dem WPF-Input-Stack, und der Mausklick
    /// ist im Child-HWND der WebView2 gelandet, nicht im WPF-Fenster.
    /// </summary>
    public static void BeginDragMove(Window window)
    {
        IntPtr handle = new WindowInteropHelper(window).Handle;
        if (handle == IntPtr.Zero)
            return;

        ReleaseCapture();
        SendMessageW(handle, WM_NCLBUTTONDOWN, new IntPtr(HTCAPTION), IntPtr.Zero);
    }

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int vKey);

    private const int VK_SHIFT = 0x10;
    private const int VK_CONTROL = 0x11;

    /// <summary>
    /// True, solange Strg und Umschalt zusammen gedrückt sind. Das ist der
    /// Notausstieg aus der Klick-Durchlässigkeit: ohne ihn wäre ein Overlay, das
    /// keine Klicks mehr annimmt, nur noch über das Tray-Menü erreichbar.
    /// </summary>
    public static bool IsBypassChordDown()
        => (GetAsyncKeyState(VK_CONTROL) & 0x8000) != 0
           && (GetAsyncKeyState(VK_SHIFT) & 0x8000) != 0;

    public static void SetClickThrough(Window window, bool enabled)
    {
        IntPtr handle = new WindowInteropHelper(window).Handle;
        if (handle == IntPtr.Zero)
            return;

        long style = GetWindowLongPtr(handle, GWL_EXSTYLE).ToInt64();
        style = enabled
            ? style | WS_EX_TRANSPARENT | WS_EX_LAYERED
            : style & ~WS_EX_TRANSPARENT;

        SetWindowLongPtr(handle, GWL_EXSTYLE, new IntPtr(style));
    }

    private const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;
    private const int DWMWA_BORDER_COLOR = 34;
    private const int DWMWA_CAPTION_COLOR = 35;
    private const int DWMWA_TEXT_COLOR = 36;

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hWnd, int attribute, ref int value, int size);

    /// <summary>
    /// Färbt Titelleiste und Rahmen passend zum Schema. Ohne das behält Windows
    /// seine eigene helle Umrandung, die neben einem dunklen Fensterinhalt als
    /// heller Rand stehen bleibt. Die Attribute gibt es seit Windows 11 21H2;
    /// auf älteren Systemen liefert DWM einen Fehlercode, den wir ignorieren —
    /// dann bleibt es beim Standardrahmen.
    /// </summary>
    public static void ApplyWindowChrome(Window window, WindowTheme theme)
    {
        IntPtr handle = new WindowInteropHelper(window).Handle;
        if (handle == IntPtr.Zero)
            return;

        int dark = theme.DarkChrome ? 1 : 0;
        DwmSetWindowAttribute(handle, DWMWA_USE_IMMERSIVE_DARK_MODE, ref dark, sizeof(int));

        int caption = unchecked((int)theme.BackgroundColorRef);
        DwmSetWindowAttribute(handle, DWMWA_CAPTION_COLOR, ref caption, sizeof(int));

        int border = unchecked((int)theme.BorderColorRef);
        DwmSetWindowAttribute(handle, DWMWA_BORDER_COLOR, ref border, sizeof(int));

        // Der Titeltext muss dem Hintergrund folgen, sonst steht schwarze Schrift
        // auf dunkler Leiste.
        int text = theme.DarkChrome ? 0x00E9E6E6 : 0x00272119;
        DwmSetWindowAttribute(handle, DWMWA_TEXT_COLOR, ref text, sizeof(int));
    }

    /// <summary>Hält das Overlay aus Alt-Tab und Taskleiste heraus.</summary>
    public static void HideFromTaskSwitcher(Window window)
    {
        IntPtr handle = new WindowInteropHelper(window).Handle;
        if (handle == IntPtr.Zero)
            return;

        long style = GetWindowLongPtr(handle, GWL_EXSTYLE).ToInt64();
        SetWindowLongPtr(handle, GWL_EXSTYLE, new IntPtr(style | WS_EX_TOOLWINDOW));
    }
}

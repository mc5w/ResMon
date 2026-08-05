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

using System.Windows.Media;
// WinForms ist wegen des Tray-Icons referenziert und bringt einen eigenen Color-Typ mit.
using Color = System.Windows.Media.Color;

namespace ResMon.App;

/// <summary>
/// Die Fensterfarben eines Schemas. Sie gelten für alles, was nicht die Seite
/// zeichnet: Fensterhintergrund, Titelleiste und Rahmen des Detailfensters.
/// </summary>
/// <remarks>
/// Die Werte sind das Gegenstück zu den CSS-Variablen in <c>detail.css</c> und
/// <c>overlay.css</c> (Selektor <c>:root[data-theme="…"]</c>) und müssen mit
/// ihnen übereinstimmen — sonst blitzt beim Vergrößern des Fensters ein
/// andersfarbiger Rand auf.
/// </remarks>
internal sealed record WindowTheme(string Key, string Background, string Border, bool DarkChrome)
{
    private static readonly WindowTheme[] All =
    [
        new("dark", "#111214", "#313439", DarkChrome: true),
        new("light", "#F3F5F9", "#C9D0DC", DarkChrome: false),
        new("blue", "#06182E", "#1D5488", DarkChrome: true),
        new("red", "#191012", "#4E2229", DarkChrome: true),
        new("green", "#0B1712", "#1F4636", DarkChrome: true),
        new("sepia", "#F4EDE1", "#D6C8B0", DarkChrome: false),
    ];

    public static WindowTheme Default => All[0];

    /// <summary>Unbekannte Schlüssel fallen auf das dunkle Schema zurück.</summary>
    public static WindowTheme For(string? key)
        => All.FirstOrDefault(theme => string.Equals(theme.Key, key, StringComparison.OrdinalIgnoreCase))
           ?? Default;

    public static bool IsKnown(string? key)
        => All.Any(theme => string.Equals(theme.Key, key, StringComparison.OrdinalIgnoreCase));

    public Color BackgroundColor => ToColor(Background);

    /// <summary>Fensterhintergrund als COLORREF (0x00BBGGRR) für die DWM-Attribute.</summary>
    public uint BackgroundColorRef => ToColorRef(Background);

    public uint BorderColorRef => ToColorRef(Border);

    private static Color ToColor(string hex)
    {
        int value = Convert.ToInt32(hex.TrimStart('#'), 16);
        return Color.FromRgb((byte)(value >> 16), (byte)(value >> 8), (byte)value);
    }

    /// <summary>COLORREF dreht die Reihenfolge um: 0x00BBGGRR statt 0xRRGGBB.</summary>
    private static uint ToColorRef(string hex)
    {
        Color color = ToColor(hex);
        return (uint)(color.R | (color.G << 8) | (color.B << 16));
    }
}

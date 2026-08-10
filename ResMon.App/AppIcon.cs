using System.IO;
using System.Windows.Resources;
// WPF und WinForms sind beide referenziert (Tray-Icon) und bringen gleichnamige
// Typen mit.
using Application = System.Windows.Application;
using Icon = System.Drawing.Icon;
using SystemInformation = System.Windows.Forms.SystemInformation;
using Window = System.Windows.Window;

namespace ResMon.App;

/// <summary>
/// Zugriff auf das Anwendungssymbol <c>ResMon.ico</c> — ein Linienzug wie eine
/// Herzschlagkurve, in dem R, S und M stecken.
/// </summary>
/// <remarks>
/// Die Datei trägt acht Auflösungen von 16 bis 256 Pixeln; unterhalb von 24
/// Pixeln enthält sie statt des Schriftzugs eine vereinfachte Pulslinie, weil die
/// Buchstaben dort zulaufen. Erzeugt wird sie von <c>tools\make-icon.ps1</c>, wo
/// auch die Form steht.
///
/// Die Fenster brauchen hier nichts: <c>ApplicationIcon</c> legt den ganzen
/// Symbolverbund in die Exe, und ein WPF-Fenster ohne eigenes
/// <see cref="Window.Icon"/> holt sich daraus für Titelleiste und Taskleiste
/// jeweils die passende Auflösung. Eine gesetzte Eigenschaft wäre ein einzelnes
/// Bild, das WPF für alle Größen herunterrechnen müsste — genau der Fall, für
/// den die kleinen Auflösungen gemacht sind.
/// </remarks>
internal static class AppIcon
{
    private static readonly Uri ResourceUri = new("pack://application:,,,/ResMon.ico", UriKind.Absolute);

    /// <summary>
    /// Das Symbol für den Infobereich, in der Größe, die Windows dort erwartet
    /// (16 Pixel, bei hoher Skalierung mehr). Der Aufrufer gibt es frei;
    /// <c>null</c>, wenn die Ressource nicht lesbar ist.
    /// </summary>
    /// <remarks>
    /// Bewusst mit ausdrücklicher Größe geladen: ohne sie greift
    /// <see cref="Icon"/> auf das größte Bild zu, und das ist in dieser Datei
    /// PNG-komprimiert — GDI+ kann solche Bilder in einem Symbol nicht auspacken.
    /// </remarks>
    public static Icon? CreateTrayIcon()
    {
        try
        {
            System.Drawing.Size size = SystemInformation.SmallIconSize;
            StreamResourceInfo? resource = Application.GetResourceStream(ResourceUri);
            if (resource is null)
                return null;

            using Stream stream = resource.Stream;
            return new Icon(stream, size.Width, size.Height);
        }
        catch (Exception ex) when (ex is IOException or ArgumentException)
        {
            return null;
        }
    }
}

using System.IO;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;

namespace ResMon.App;

/// <summary>
/// Gemeinsame WebView2-Initialisierung für Overlay und Detailfenster.
/// Die Oberfläche wird über <c>SetVirtualHostNameToFolderMapping</c> eingebunden —
/// sauberer als <c>file://</c> und mit normaler CORS-Semantik (DESIGN.md §11).
/// </summary>
internal static class WebViewHost
{
    public const string VirtualHost = "app.local";

    private static readonly string WwwRoot = Path.Combine(AppContext.BaseDirectory, "wwwroot");

    private static CoreWebView2Environment? _environment;

    public static Uri PageUri(string fileName) => new($"https://{VirtualHost}/{fileName}");

    /// <summary>
    /// Startet die WebView, bindet <c>wwwroot</c> ein und navigiert zur Seite.
    /// Das Benutzerdatenverzeichnis liegt unter <c>%LocalAppData%</c>, weil neben
    /// der Exe (ggf. unter <c>Program Files</c>) nicht geschrieben werden darf.
    /// </summary>
    public static async Task InitializeAsync(WebView2 webView, string page, bool transparent)
    {
        _environment ??= await CoreWebView2Environment.CreateAsync(
            browserExecutableFolder: null,
            userDataFolder: Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "ResMon",
                "WebView2"));

        if (transparent)
        {
            // Muss vor der Initialisierung gesetzt sein, sonst rendert WebView2
            // ein deckend weißes Rechteck über das transparente WPF-Fenster
            // (DESIGN.md §11).
            webView.DefaultBackgroundColor = System.Drawing.Color.Transparent;
        }

        await webView.EnsureCoreWebView2Async(_environment);

        CoreWebView2 core = webView.CoreWebView2;
        core.SetVirtualHostNameToFolderMapping(VirtualHost, WwwRoot, CoreWebView2HostResourceAccessKind.Allow);

        CoreWebView2Settings settings = core.Settings;
        settings.AreDefaultContextMenusEnabled = false;
        settings.IsStatusBarEnabled = false;
        settings.AreBrowserAcceleratorKeysEnabled = false;
        settings.IsZoomControlEnabled = false;
        settings.IsSwipeNavigationEnabled = false;
        settings.IsGeneralAutofillEnabled = false;
        settings.IsPasswordAutosaveEnabled = false;

        core.Navigate(PageUri(page).ToString());
    }
}

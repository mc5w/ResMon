using System.Windows;
using System.Windows.Threading;
using Microsoft.Web.WebView2.Core;
using ResMon.App.Bridge;
using ResMon.App.Native;
using ResMon.Core.Config;
using ResMon.Core.Model;
// WinForms ist wegen des Tray-Icons referenziert und bringt eigene Typen mit.
using MessageBox = System.Windows.MessageBox;

namespace ResMon.App;

/// <summary>
/// Randloses, transparentes Always-on-Top-Fenster mit der Live-Anzeige
/// (DESIGN.md §11). Der Inhalt ist HTML in einer WebView2.
/// </summary>
public partial class OverlayWindow : Window
{
    /// <summary>Breite der Karte in CSS-Pixeln; die Höhe meldet die Seite.</summary>
    private const double ContentWidth = 248;

    private readonly AppSettings _settings;
    private readonly DispatcherTimer _saveDebounce;
    private readonly DispatcherTimer _bypassWatch;
    private bool _webReady;
    private bool _bypassActive;
    private double _contentHeight = 220;

    public OverlayWindow(AppSettings settings)
    {
        _settings = settings;
        InitializeComponent();

        Left = settings.Overlay.X;
        Top = settings.Overlay.Y;
        // Window.Opacity bleibt bewusst auf 1 — siehe ApplyOpacity.

        // Position erst nach kurzer Ruhe speichern, statt bei jedem Pixel.
        _saveDebounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(600) };
        _saveDebounce.Tick += (_, _) =>
        {
            _saveDebounce.Stop();
            PersistPosition();
        };

        // Läuft nur, solange das Overlay klick-durchlässig ist. Ein Tastenzustand
        // lässt sich nicht abonnieren, wenn die Tastatur woanders hingeht —
        // deshalb abfragen statt warten.
        _bypassWatch = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(120) };
        _bypassWatch.Tick += (_, _) => UpdateBypass();

        LocationChanged += (_, _) => _saveDebounce.Start();
        Loaded += OnLoaded;
    }

    /// <summary>Wird ausgelöst, wenn die Oberfläche das Detailfenster anfordert.</summary>
    public event Action? DetailRequested;

    /// <summary>Wird ausgelöst, wenn die Oberfläche das Beenden anfordert.</summary>
    public event Action? CloseRequested;

    /// <summary>
    /// Wird ausgelöst, wenn Mausrad oder Ziehen eine Einstellung geändert haben.
    /// Der Anwendungsrumpf speichert und zieht die anderen Oberflächen nach.
    /// </summary>
    public event Action? SettingsChanged;

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        WindowInterop.HideFromTaskSwitcher(this);
        ApplyClickThrough(_settings.Overlay.ClickThrough);

        Web.WebMessageReceived += OnWebMessageReceived;

        try
        {
            await WebViewHost.InitializeAsync(Web, "overlay.html", transparent: true);
            _webReady = true;
            ApplySettings();
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                $"WebView2 konnte nicht initialisiert werden.\n\n{ex.Message}\n\n" +
                "Die WebView2-Runtime muss installiert sein (Evergreen Runtime).",
                "ResMon",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            CloseRequested?.Invoke();
        }
    }

    /// <summary>Schiebt einen Messpunkt in die Oberfläche. Muss auf dem UI-Thread laufen.</summary>
    public void Push(SystemSnapshot snapshot, AggregateSample[] history)
    {
        if (!_webReady)
            return;

        Web.CoreWebView2.PostWebMessageAsJson(
            WebBridge.BuildOverlayPayload(snapshot, history, _settings.Visible));
    }

    public void ApplyClickThrough(bool enabled)
    {
        _settings.Overlay.ClickThrough = enabled;
        ApplyClickThroughState();
    }

    /// <summary>
    /// Setzt den erweiterten Fensterstil und schaltet die Tastenabfrage passend
    /// dazu ein oder aus. Während des Notausstiegs bleibt das Overlay klickbar,
    /// obwohl die Einstellung auf durchlässig steht.
    /// </summary>
    private void ApplyClickThroughState()
    {
        bool clickThrough = _settings.Overlay.ClickThrough;

        if (clickThrough)
        {
            _bypassWatch.Start();
        }
        else
        {
            _bypassWatch.Stop();
            _bypassActive = false;
        }

        WindowInterop.SetClickThrough(this, clickThrough && !_bypassActive);
        PushBypassState();
    }

    /// <summary>
    /// Prüft den Notausstieg: solange Strg und Umschalt gehalten werden, nimmt das
    /// Overlay wieder Klicks an. Ohne diesen Weg zurück wäre eine einmal
    /// eingeschaltete Klick-Durchlässigkeit eine Einbahnstraße — sie wird
    /// gespeichert, und die Schaltfläche "Details" ist dann nicht mehr klickbar.
    /// </summary>
    private void UpdateBypass()
    {
        bool held = WindowInterop.IsBypassChordDown();
        if (held == _bypassActive)
            return;

        _bypassActive = held;
        WindowInterop.SetClickThrough(this, _settings.Overlay.ClickThrough && !held);
        PushBypassState();
    }

    /// <summary>Die Seite zeigt den Notausstieg an, sonst bliebe er unsichtbar.</summary>
    private void PushBypassState()
    {
        if (_webReady)
            Web.CoreWebView2.PostWebMessageAsJson(WebBridge.BuildBypassPayload(_bypassActive));
    }

    /// <summary>
    /// Überträgt den kompletten Einstellungsstand in die Seite und ans Fenster.
    /// Wird nach jeder Änderung aufgerufen, egal woher sie kam.
    /// </summary>
    public void ApplySettings()
    {
        ApplyClickThroughState();
        ApplyWindowSize();

        if (!_webReady)
            return;

        // Der Zoom vergrößert Fenster und Inhalt im selben Maß: die Seite rechnet
        // unverändert in ihren CSS-Pixeln, WebView2 skaliert das Ergebnis.
        Web.ZoomFactor = Math.Clamp(_settings.Overlay.Scale, 0.8, 2.5);
        Web.CoreWebView2.PostWebMessageAsJson(WebBridge.BuildSettingsPayload(_settings));
    }

    /// <summary>
    /// Die Fenstergröße folgt dem Inhalt: die Seite meldet, wie hoch die Karte mit
    /// den eingeblendeten Zeilen ausfällt, der Zoom multipliziert das. Damit ist
    /// die Mindestgröße genau das, was der Benutzer eingeschaltet hat.
    /// </summary>
    private void ApplyWindowSize()
    {
        double scale = Math.Clamp(_settings.Overlay.Scale, 0.8, 2.5);
        Width = ContentWidth * scale;
        Height = _contentHeight * scale;
    }

    /// <summary>
    /// Setzt die Deckkraft in der Seite, nicht am Fenster. <c>Window.Opacity</c>
    /// ist hier zweimal falsch, beides nachgemessen:
    ///
    /// Sie ändert an der Darstellung nichts. Die WebView2 ist ein eigener
    /// Child-HWND und zeichnet sich selbst; die Deckkraft des Layered Window
    /// wirkt nur auf die WPF-Fläche darunter — und die ist bis auf Alpha 1/255
    /// leer. Ein Pixel mitten in der Karte behält bei Opacity 0.2 exakt seine
    /// Farbe.
    ///
    /// Sie macht das Overlay unklickbar. Das Alpha 1/255 des Hintergrunds, das
    /// überhaupt erst für Treffer beim Hit-Testing sorgt, wird mit der Deckkraft
    /// multipliziert. Ab 0.5 rundet 1 × 0.5 auf 0, und Windows reicht Klicks auf
    /// vollständig transparente Pixel an das Fenster darunter weiter.
    /// </summary>
    public void ApplyOpacity(double opacity)
    {
        _settings.Overlay.Opacity = Math.Clamp(opacity, 0.2, 1.0);
        SettingsChanged?.Invoke();
    }

    /// <summary>Vergrößert Fenster und Inhalt gemeinsam.</summary>
    public void ApplyScale(double scale)
    {
        _settings.Overlay.Scale = Math.Clamp(scale, 0.8, 2.5);
        SettingsChanged?.Invoke();
    }

    private void OnWebMessageReceived(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        WebCommand? command = WebBridge.ParseCommand(e.WebMessageAsJson);
        switch (command?.Cmd)
        {
            case "drag":
                // WebView2 fängt die Mausereignisse ab; die Kopfzeile meldet den
                // Ziehbeginn deshalb per Nachricht (DESIGN.md §11).
                WindowInterop.BeginDragMove(this);
                break;
            case "openDetail":
                DetailRequested?.Invoke();
                break;
            case "setOpacity" when command.Value is { } value:
                ApplyOpacity(value);
                break;
            case "setScale" when command.Value is { } scale:
                ApplyScale(scale);
                break;
            case "size" when command.Value is { } height and > 0:
                // Die Seite kennt ihre nötige Höhe genauer als jede Rechnung im
                // Host — sie hängt an den eingeblendeten Zeilen.
                _contentHeight = height;
                ApplyWindowSize();
                break;
            case "close":
                CloseRequested?.Invoke();
                break;
        }
    }

    private void PersistPosition()
    {
        _settings.Overlay.X = Left;
        _settings.Overlay.Y = Top;
        _settings.Save();
    }

    protected override void OnClosed(EventArgs e)
    {
        _bypassWatch.Stop();
        _saveDebounce.Stop();
        PersistPosition();
        base.OnClosed(e);
    }
}

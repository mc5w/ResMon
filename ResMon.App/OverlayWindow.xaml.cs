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
    private readonly AppSettings _settings;
    private readonly DispatcherTimer _saveDebounce;
    private bool _webReady;

    public OverlayWindow(AppSettings settings)
    {
        _settings = settings;
        InitializeComponent();

        Left = settings.Overlay.X;
        Top = settings.Overlay.Y;
        Opacity = settings.Overlay.Opacity;

        // Position erst nach kurzer Ruhe speichern, statt bei jedem Pixel.
        _saveDebounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(600) };
        _saveDebounce.Tick += (_, _) =>
        {
            _saveDebounce.Stop();
            PersistPosition();
        };

        LocationChanged += (_, _) => _saveDebounce.Start();
        Loaded += OnLoaded;
    }

    /// <summary>Wird ausgelöst, wenn die Oberfläche das Detailfenster anfordert.</summary>
    public event Action? DetailRequested;

    /// <summary>Wird ausgelöst, wenn die Oberfläche das Beenden anfordert.</summary>
    public event Action? CloseRequested;

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        WindowInterop.HideFromTaskSwitcher(this);
        ApplyClickThrough(_settings.Overlay.ClickThrough);

        Web.WebMessageReceived += OnWebMessageReceived;

        try
        {
            await WebViewHost.InitializeAsync(Web, "overlay.html", transparent: true);
            _webReady = true;
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
        WindowInterop.SetClickThrough(this, enabled);
    }

    public void ApplyOpacity(double opacity)
    {
        double clamped = Math.Clamp(opacity, 0.2, 1.0);
        Opacity = clamped;
        _settings.Overlay.Opacity = clamped;
        _saveDebounce.Start();
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
        _saveDebounce.Stop();
        PersistPosition();
        base.OnClosed(e);
    }
}

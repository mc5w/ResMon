using System.Windows;
using ResMon.App.Bridge;
using ResMon.Core.Model;
// WinForms ist wegen des Tray-Icons referenziert und bringt eigene Typen mit.
using MessageBox = System.Windows.MessageBox;

namespace ResMon.App;

/// <summary>
/// Normales WPF-Fenster mit WebView2. Sortierung, Filterung und Aggregation der
/// Prozesstabelle laufen vollständig in JavaScript (DESIGN.md §13).
/// </summary>
public partial class DetailWindow : Window
{
    private bool _webReady;
    private IReadOnlyList<ProcessSample>? _lastSentProcesses;

    public DetailWindow()
    {
        InitializeComponent();
        Loaded += OnLoaded;
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        try
        {
            await WebViewHost.InitializeAsync(Web, "detail.html", transparent: false);
            _webReady = true;
        }
        catch (Exception ex)
        {
            MessageBox.Show($"WebView2 konnte nicht initialisiert werden.\n\n{ex.Message}",
                "ResMon", MessageBoxButton.OK, MessageBoxImage.Error);
            Close();
        }
    }

    /// <summary>Schiebt einen Messpunkt in die Oberfläche. Muss auf dem UI-Thread laufen.</summary>
    public void Push(SystemSnapshot snapshot, AggregateSample[] history, HostDiagnostics diagnostics)
    {
        if (!_webReady)
            return;

        // Der Collector tauscht die Liste nur beim Prozess-Takt aus. Bleibt die
        // Referenz gleich, gab es nichts Neues — dann sparen wir uns die
        // Serialisierung von mehreren hundert Prozessen pro Sekunde.
        bool processesChanged = !ReferenceEquals(_lastSentProcesses, snapshot.Processes);
        IReadOnlyList<ProcessSample>? processes = processesChanged ? snapshot.Processes : null;
        _lastSentProcesses = snapshot.Processes;

        Web.CoreWebView2.PostWebMessageAsJson(
            WebBridge.BuildDetailPayload(snapshot, history, processes, diagnostics));
    }
}

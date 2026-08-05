using System.Windows;
using Microsoft.Web.WebView2.Core;
using ResMon.App.Bridge;
using ResMon.Core.Inventory;
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
    private SystemInfo? _systemInfo;

    public DetailWindow()
    {
        InitializeComponent();
        Loaded += OnLoaded;
    }

    /// <summary>Wird ausgelöst, wenn die Oberfläche das Beenden eines Prozesses anfordert.</summary>
    public event Action<int, string?>? KillRequested;

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        try
        {
            Web.WebMessageReceived += OnWebMessageReceived;
            await WebViewHost.InitializeAsync(Web, "detail.html", transparent: false);
            _webReady = true;

            // Die Systemübersicht kann schon eingetroffen sein, während die Seite
            // noch lud.
            if (_systemInfo is { } pending)
                PushSystemInfo(pending);
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

    /// <summary>
    /// Schiebt die Systemübersicht nach. Sie ändert sich nicht und wird deshalb
    /// nur einmal gesendet — sobald die WMI-Abfragen durch sind.
    /// </summary>
    public void PushSystemInfo(SystemInfo info)
    {
        // Merken, damit sie nach der Initialisierung nachgereicht werden kann.
        _systemInfo = info;
        if (!_webReady)
            return;

        Web.CoreWebView2.PostWebMessageAsJson(WebBridge.BuildSystemPayload(info));
    }

    private void OnWebMessageReceived(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        WebCommand? command = WebBridge.ParseCommand(e.WebMessageAsJson);
        switch (command?.Cmd)
        {
            case "killProcess" when command.Pid is { } pid:
                KillRequested?.Invoke(pid, command.Name);
                break;
            case "requestSystemInfo" when _systemInfo is { } info:
                // Die Übersicht wird nur einmal gesendet; die Oberfläche fragt
                // nach, falls sie sie nicht bekommen hat.
                Web.CoreWebView2.PostWebMessageAsJson(WebBridge.BuildSystemPayload(info));
                break;
        }
    }
}

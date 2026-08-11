using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Web.WebView2.Core;
using ResMon.App.Bridge;
using ResMon.App.Native;
using ResMon.Core.Config;
using ResMon.Core.Diagnostics;
using ResMon.Core.Inventory;
using ResMon.Core.Model;
using ResMon.Core.Processes;
using ResMon.Core.Storage;
// WinForms ist wegen des Tray-Icons referenziert und bringt eigene Typen mit.
using MessageBox = System.Windows.MessageBox;

namespace ResMon.App;

/// <summary>
/// Normales WPF-Fenster mit WebView2. Sortierung, Filterung und Aggregation der
/// Prozesstabelle laufen vollständig in JavaScript (DESIGN.md §13).
/// </summary>
public partial class DetailWindow : Window
{
    private readonly AppSettings _settings;
    private bool _webReady;
    private IReadOnlyList<ProcessSample>? _lastSentProcesses;
    private IReadOnlyList<NetConnection>? _lastSentConnections;
    private SystemInfo? _systemInfo;

    /// <summary>–1 erzwingt das Senden des ersten Protokollstands, auch wenn er leer ist.</summary>
    private int _lastSentLogVersion = -1;

    private FolderScanSession? _scan;
    private DispatcherTimer? _scanProgress;
    private int _scanId;

    public DetailWindow(AppSettings settings)
    {
        _settings = settings;
        InitializeComponent();
        Loaded += OnLoaded;

        // Erst mit dem Fensterhandle greifen die DWM-Attribute für Titelleiste
        // und Rahmen.
        SourceInitialized += (_, _) => ApplyTheme();
    }

    /// <summary>Wird ausgelöst, wenn die Oberfläche das Beenden eines Prozesses anfordert.</summary>
    public event Action<int, string?>? KillRequested;

    /// <summary>
    /// Wird ausgelöst, wenn die Einstellungsseite etwas geändert hat. Der
    /// Anwendungsrumpf speichert dann und zieht Overlay und Tray-Menü nach.
    /// </summary>
    public event Action? SettingsChanged;

    /// <summary>
    /// Wird ausgelöst, wenn die Systemübersicht neu erhoben werden soll. Geräte
    /// kommen und gehen; der feste Teil der Übersicht wird dabei mit erneuert.
    /// </summary>
    public event Action? SystemInfoRefreshRequested;

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        try
        {
            Web.WebMessageReceived += OnWebMessageReceived;
            await WebViewHost.InitializeAsync(Web, "detail.html", transparent: false);
            _webReady = true;
            PushSettings();

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
        // Serialisierung von mehreren hundert Prozessen pro Sekunde. Für die
        // Verbindungstabelle gilt dasselbe; sie entsteht im selben Takt.
        bool processesChanged = !ReferenceEquals(_lastSentProcesses, snapshot.Processes);
        IReadOnlyList<ProcessSample>? processes = processesChanged ? snapshot.Processes : null;
        _lastSentProcesses = snapshot.Processes;

        bool connectionsChanged = !ReferenceEquals(_lastSentConnections, snapshot.Connections);
        IReadOnlyList<NetConnection>? connections = connectionsChanged ? snapshot.Connections : null;
        _lastSentConnections = snapshot.Connections;

        // Das Protokoll steht die meiste Zeit still; sein Zähler sagt, ob sich
        // das Mitschicken überhaupt lohnt.
        int logVersion = DiagnosticLog.Version;
        IReadOnlyList<DiagnosticEntry>? logs = logVersion != _lastSentLogVersion
            ? DiagnosticLog.Snapshot()
            : null;
        _lastSentLogVersion = logVersion;

        Web.CoreWebView2.PostWebMessageAsJson(
            WebBridge.BuildDetailPayload(snapshot, history, processes, connections, diagnostics, logs));
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

    /// <summary>Schiebt den Einstellungsstand in die Seite.</summary>
    public void PushSettings()
    {
        if (!_webReady)
            return;

        Web.CoreWebView2.PostWebMessageAsJson(WebBridge.BuildSettingsPayload(_settings));
    }

    /// <summary>Zieht Fensterhintergrund, Titelleiste und Rahmen auf das Schema nach.</summary>
    public void ApplyTheme()
    {
        WindowTheme theme = WindowTheme.For(_settings.Theme);
        Background = new SolidColorBrush(theme.BackgroundColor);
        WindowInterop.ApplyWindowChrome(this, theme);
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
                // Die Übersicht wird nur einmal von sich aus gesendet; die
                // Oberfläche fragt nach, falls sie sie nicht bekommen hat.
                Web.CoreWebView2.PostWebMessageAsJson(WebBridge.BuildSystemPayload(info));
                break;
            case "refreshSystemInfo":
                SystemInfoRefreshRequested?.Invoke();
                break;
            case "requestSettings":
                PushSettings();
                break;
            case "startFolderScan" when command.Path is { } path:
                StartFolderScan(path);
                break;
            case "cancelFolderScan":
                _scan?.Cancel();
                break;
            case "expandFolder" when command.Scan is { } scan && command.Node is { } node:
                PushFolderChildren(scan, node);
                break;
            case "openFolder" when command.Scan is { } scan && command.Node is { } node:
                if (ResolveScanPath(scan, node) is { } target)
                    PathActions.Reveal(this, target);
                break;
            case "copyFolderPath" when command.Scan is { } scan && command.Node is { } node:
                if (ResolveScanPath(scan, node) is { } copied)
                    PathActions.Copy(this, copied);
                break;
            default:
                if (ApplySetting(command))
                {
                    ApplyTheme();
                    SettingsChanged?.Invoke();
                }
                break;
        }
    }

    /// <summary>
    /// Räumt einen laufenden Scan ab. Ein Durchlauf über sechs Threads für ein
    /// Fenster, das niemand mehr ansieht, widerspräche DESIGN.md §9 — genauso wie
    /// die Prozessabtastung, die beim Schließen ebenfalls aufhört.
    /// </summary>
    protected override void OnClosed(EventArgs e)
    {
        // Reihenfolge: erst die Seite abmelden, dann stornieren. Eine Nutzlast an
        // eine geschlossene WebView wirft.
        _webReady = false;
        _scanProgress?.Stop();

        FolderScanSession? session = _scan;
        _scan = null;
        session?.Cancel();

        // Ein noch laufender Lauf wird von der wartenden Fortsetzung abgeräumt,
        // sobald seine Threads durch sind; vorher darf die Abbruchmarke nicht weg.
        if (session?.Result is not null)
            session.Dispose();

        base.OnClosed(e);
    }

    /// <summary>
    /// Startet einen Ordner-Scan. Die Sitzung liegt im Fenster und nicht im
    /// Anwendungsrumpf: die Handlung ist fensterbezogen, vom Benutzer ausgelöst,
    /// und ihre gesamte Ausgabe geht in diese eine WebView.
    /// </summary>
    private async void StartFolderScan(string requested)
    {
        if (ValidateRoot(requested) is not { } root)
        {
            if (_webReady)
            {
                Web.CoreWebView2.PostWebMessageAsJson(WebBridge.BuildScanStatusPayload(
                    _scanId, "error", $"„{requested}“ ist kein Laufwerk, das sich durchsuchen lässt."));
            }

            return;
        }

        // Ein zweiter Lauf storniert den ersten; auf dessen Ende zu warten hieße,
        // den UI-Thread zu blockieren. Das alte Ergebnis erkennt sich beim
        // Eintreffen am Sitzungsvergleich als überholt — dieselbe Sicherung wie
        // bei der Systemübersicht in App.RefreshSystemInfo.
        _scan?.Cancel();

        var session = new FolderScanSession(root, ++_scanId);
        _scan = session;
        StartProgressTimer();

        try
        {
            FolderScanResult result = await session.RunAsync();
            if (!ReferenceEquals(_scan, session))
                return;

            _scanProgress?.Stop();
            if (_webReady)
                Web.CoreWebView2.PostWebMessageAsJson(WebBridge.BuildScanPayload(result, session.ScanId));
        }
        catch (Exception ex)
        {
            DiagnosticLog.Report("Ordner-Scan", ex, $"„{root}“ ließ sich nicht durchsuchen");
            if (!ReferenceEquals(_scan, session))
                return;

            _scanProgress?.Stop();
            if (_webReady)
            {
                Web.CoreWebView2.PostWebMessageAsJson(
                    WebBridge.BuildScanStatusPayload(session.ScanId, "error", ex.Message));
            }
        }
        finally
        {
            // Wurde die Sitzung überholt oder das Fenster geschlossen, hält sie
            // niemand mehr — und ihre Threads sind jetzt sicher durch.
            if (!ReferenceEquals(_scan, session))
                session.Dispose();
        }
    }

    /// <summary>
    /// Nimmt ausschließlich Laufwerkswurzeln an. Damit kann die Seite den Host
    /// nicht dazu bringen, einen beliebigen Pfad abzulaufen; alles Weitere läuft
    /// über ganzzahlige Kennungen in einen Baum, den der Host selbst gebaut hat.
    /// Netzlaufwerke bleiben draußen — ein Scan über eine langsame Leitung ist
    /// eine Falle, keine Funktion.
    /// </summary>
    private static string? ValidateRoot(string requested)
    {
        if (string.IsNullOrWhiteSpace(requested))
            return null;

        // Ohne abschließenden Trennstrich vergleichen: die Systemübersicht führt
        // die Laufwerke als „C:" (SystemInfoProvider schneidet ihn ab), während
        // DriveInfo sie als „C:\" nennt. Die Seite reicht weiter, was sie von
        // dort bekommen hat.
        string wanted = requested.TrimEnd('\\', '/');

        try
        {
            foreach (DriveInfo drive in DriveInfo.GetDrives())
            {
                if (!drive.IsReady || drive.DriveType is not (DriveType.Fixed or DriveType.Removable))
                    continue;

                if (string.Equals(drive.Name.TrimEnd('\\'), wanted, StringComparison.OrdinalIgnoreCase))
                    return drive.RootDirectory.FullName;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            DiagnosticLog.Report("Ordner-Scan", ex, "Die Laufwerksliste ließ sich nicht lesen");
        }

        return null;
    }

    /// <summary>Reicht die Kinder eines Knotens nach, die im Auszug fehlten.</summary>
    private void PushFolderChildren(int scanId, int node)
    {
        if (CurrentResult(scanId) is not { } result || !_webReady)
            return;

        Web.CoreWebView2.PostWebMessageAsJson(WebBridge.BuildScanChildrenPayload(result, scanId, node));
    }

    private string? ResolveScanPath(int scanId, int node)
    {
        if (CurrentResult(scanId) is not { } result || !result.IsKnown(node))
            return null;

        return result.PathOf(node);
    }

    /// <summary>
    /// Das Ergebnis des benannten Laufs — oder nichts. Die Kennung ist die
    /// Sicherung dagegen, dass ein Nachschlag aus einem überholten Scan in einen
    /// anderen Baum zeigt.
    /// </summary>
    private FolderScanResult? CurrentResult(int scanId)
        => _scan is { } session && session.ScanId == scanId ? session.Result : null;

    private void StartProgressTimer()
    {
        // Vier Meldungen je Sekunde: genug, dass der Fortschritt lebt, wenig
        // genug, dass er nichts kostet. Der Fortschritt wird geholt und nicht
        // geschickt — ein Rückruf je Ordner wären zweihunderttausend Aufrufe
        // durch den Synchronisierungskontext.
        _scanProgress ??= CreateProgressTimer();
        _scanProgress.Start();
    }

    private DispatcherTimer CreateProgressTimer()
    {
        // DispatcherTimer mit Absicht: die Nachricht muss ohnehin auf den
        // UI-Thread, er endet mit dem Fenster, und beim Anhalten gibt es kein
        // Wettrennen mit einem noch laufenden Rückruf.
        var timer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(250),
        };

        timer.Tick += (_, _) =>
        {
            if (_scan is not { } session || !_webReady)
                return;

            Web.CoreWebView2.PostWebMessageAsJson(WebBridge.BuildScanProgressPayload(
                session.ScanId, session.Directories, session.Files, session.Bytes, session.CurrentPath));
        };

        return timer;
    }

    /// <summary>
    /// Übernimmt eine Änderung von der Einstellungsseite. Liefert false, wenn das
    /// Kommando keine Einstellung war.
    /// </summary>
    private bool ApplySetting(WebCommand? command)
    {
        switch (command?.Cmd)
        {
            case "setOpacity" when command.Value is { } opacity:
                _settings.Overlay.Opacity = Math.Clamp(opacity, 0.2, 1.0);
                return true;

            case "setScale" when command.Value is { } scale:
                _settings.Overlay.Scale = Math.Clamp(scale, 0.8, 2.5);
                return true;

            case "setClickThrough" when command.On is { } clickThrough:
                _settings.Overlay.ClickThrough = clickThrough;
                return true;

            // Unbekannte Schlüssel werden verworfen, statt das Schema auf einen
            // Namen zu setzen, für den es keine Farben gibt.
            case "setTheme" when WindowTheme.IsKnown(command.Key):
                _settings.Theme = command.Key!.ToLowerInvariant();
                return true;

            case "setOverlayRow" when command.On is { } on:
                return SetRow(_settings.Visible, command.Key, on);

            case "setChartRow" when command.On is { } on:
                return SetChart(_settings.Chart, command.Key, on);

            default:
                return false;
        }
    }

    private static bool SetRow(VisibilitySettings visible, string? key, bool on)
    {
        switch (key)
        {
            case "cpu": visible.Cpu = on; return true;
            case "gpu": visible.Gpu = on; return true;
            case "ram": visible.Ram = on; return true;
            case "net": visible.Net = on; return true;
            case "disk": visible.Disk = on; return true;
            case "temps": visible.Temps = on; return true;
            default: return false;
        }
    }

    private static bool SetChart(ChartSettings chart, string? key, bool on)
    {
        switch (key)
        {
            case "cpu": chart.Cpu = on; return true;
            case "gpu": chart.Gpu = on; return true;
            case "ram": chart.Ram = on; return true;
            case "net": chart.Net = on; return true;
            case "disk": chart.Disk = on; return true;
            default: return false;
        }
    }
}

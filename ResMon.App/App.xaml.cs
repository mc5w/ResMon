using System.Security;
using System.Security.Principal;
using System.Windows;
using ResMon.App.Bridge;
using ResMon.Core;
// WPF und WinForms sind beide referenziert (Tray-Icon); Application und
// MessageBox gibt es in beiden Namensräumen.
using Application = System.Windows.Application;
using ResMon.Core.Config;
using ResMon.Core.Inventory;
using ResMon.Core.Model;

namespace ResMon.App;

/// <summary>
/// Einstiegspunkt. Hält Collector, Overlay, Detailfenster und Tray-Icon zusammen
/// und verteilt die Snapshots auf den UI-Thread.
/// </summary>
public partial class App : Application
{
    private const string MutexName = @"Global\ResMon.SingleInstance";

    /// <summary>
    /// Über dieses Ereignis holt eine zweite Instanz das Detailfenster der
    /// ersten nach vorn. Sitzungslokal statt global: die Anwendung läuft zwar
    /// erhöht, aber das Anlegen globaler Kernelobjekte setzt ein Recht voraus,
    /// das ein unerhöhter Lauf nicht hat — und dann bliebe der Start hängen.
    /// </summary>
    private const string ShowDetailEventName = @"Local\ResMon.ShowDetail";

    private Mutex? _singleInstance;
    private EventWaitHandle? _showDetailSignal;
    private RegisteredWaitHandle? _showDetailRegistration;
    /// <summary>
    /// Ob der Prozess erhöht läuft. Ändert sich zur Laufzeit nicht und wird
    /// deshalb einmal ermittelt; ohne Adminrechte fehlen Sensoren und ETW.
    /// </summary>
    private static readonly bool IsElevated = ReadElevation();

    private AppSettings _settings = new();
    private Collector? _collector;
    private OverlayWindow? _overlay;
    private DetailWindow? _detail;
    private TrayIcon? _tray;
    private bool _clickThroughAnnounced;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // Zweite Instanz beendet sich sofort (DESIGN.md §14) — aber nicht
        // wortlos: Wer die Anwendung ein zweites Mal startet, will sie sehen. Das
        // Overlay hält sich aus Taskleiste und Alt-Tab heraus, ein stiller
        // Abbruch sähe deshalb aus, als sei gar nichts passiert.
        _singleInstance = new Mutex(initiallyOwned: true, MutexName, out bool isFirstInstance);
        if (!isFirstInstance)
        {
            SignalRunningInstance();
            Shutdown();
            return;
        }

        ListenForShowDetail();

        _settings = AppSettings.Load();
        _collector = new Collector(_settings);
        _collector.SnapshotReady += OnSnapshotReady;

        _overlay = new OverlayWindow(_settings);
        _overlay.DetailRequested += ShowDetailWindow;
        _overlay.CloseRequested += Quit;
        _overlay.SettingsChanged += OnSettingsChanged;
        _overlay.Show();

        _tray = new TrayIcon(_settings);
        _tray.DetailRequested += ShowDetailWindow;
        _tray.ExitRequested += Quit;
        _tray.SettingsChanged += OnSettingsChanged;

        _collector.Start();

        if (_collector.HardwareError is { } error)
        {
            _tray.Notify(
                "Sensoren nicht verfügbar",
                $"Temperaturen und Taktraten fehlen: {error}");
        }

        // Die Einstellung überlebt den Neustart. Wer sie eingeschaltet und
        // vergessen hat, steht sonst vor einem Overlay, das auf nichts reagiert.
        if (_settings.Overlay.ClickThrough)
        {
            AnnounceClickThrough();
            _clickThroughAnnounced = true;
        }
    }

    private static bool ReadElevation()
    {
        try
        {
            using WindowsIdentity identity = WindowsIdentity.GetCurrent();
            return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
        }
        catch (Exception ex) when (ex is SecurityException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>Weckt die laufende Instanz, damit sie ihr Detailfenster zeigt.</summary>
    private static void SignalRunningInstance()
    {
        try
        {
            if (EventWaitHandle.TryOpenExisting(ShowDetailEventName, out EventWaitHandle? running))
            {
                using (running)
                    running.Set();
            }
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or WaitHandleCannotBeOpenedException)
        {
            // Die laufende Instanz gehört einem anderen Benutzer oder ist gerade
            // beim Beenden. Dann bleibt es beim stillen Abbruch.
        }
    }

    /// <summary>
    /// Wartet im Hintergrund auf das Signal einer zweiten Instanz. Der Rückruf
    /// läuft auf einem Threadpool-Thread und muss deshalb über den Dispatcher.
    /// </summary>
    private void ListenForShowDetail()
    {
        try
        {
            _showDetailSignal = new EventWaitHandle(false, EventResetMode.AutoReset, ShowDetailEventName);
            _showDetailRegistration = ThreadPool.RegisterWaitForSingleObject(
                _showDetailSignal,
                (_, _) => Dispatcher.BeginInvoke(ShowDetailWindow),
                state: null,
                Timeout.Infinite,
                executeOnlyOnce: false);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.IO.IOException)
        {
            // Ohne das Ereignis läuft alles weiter; ein zweiter Start beendet
            // sich dann wieder wortlos.
        }
    }

    private void AnnounceClickThrough()
        => _tray?.Notify(
            "Overlay ist klick-durchlässig",
            "Klicks gehen hindurch. Zum Bedienen Strg+Umschalt gedrückt halten — " +
            "oder hier im Tray-Menü wieder ausschalten.");

    /// <summary>
    /// Läuft im Timer-Thread des Collectors — die Weitergabe an die Fenster muss
    /// deshalb über den Dispatcher gehen.
    /// </summary>
    private void OnSnapshotReady(SystemSnapshot snapshot)
    {
        Dispatcher.BeginInvoke(() =>
        {
            AggregateSample[] history = _collector!.History.ToArray();
            _overlay?.Push(snapshot, history);
            _detail?.Push(snapshot, history, new HostDiagnostics(
                _collector.CpuSensorsBlocked,
                GpuCountersMissing: !_collector.GpuCountersAvailable,
                NetworkCountersMissing: !_collector.NetworkCountersAvailable,
                DiskCountersMissing: !_collector.DiskCountersAvailable,
                ProcessCountersMissing: !_collector.ProcessCountersAvailable,
                _collector.UsesLegacyProcessCounters,
                _collector.NetworkTraceError)
            {
                BoardSensorsMissing = _collector.BoardSensorsMissing,
                SensorDriverError = _collector.HardwareError,
                Elevated = IsElevated,
            });
        });
    }

    /// <summary>
    /// Eine Einstellung hat sich geändert — gleich woher. Alle drei Oberflächen
    /// lesen denselben Stand, also bekommen auch alle drei die Änderung; das
    /// Tray-Menü zeigt sonst noch die alten Haken.
    /// </summary>
    private void OnSettingsChanged()
    {
        _overlay?.ApplySettings();
        _detail?.PushSettings();
        _tray?.Sync();
        _settings.Save();

        // Beim Einschalten einmal sagen, wie man wieder herauskommt. Die
        // Einstellung überlebt den Neustart, und ein Overlay, das keine Klicks
        // mehr annimmt, führt sonst niemanden mehr zu seiner eigenen Einstellung.
        if (_settings.Overlay.ClickThrough && !_clickThroughAnnounced)
            AnnounceClickThrough();

        _clickThroughAnnounced = _settings.Overlay.ClickThrough;
    }

    private void ShowDetailWindow()
    {
        if (_detail is null)
        {
            var detail = new DetailWindow(_settings);
            _detail = detail;

            detail.KillRequested += (pid, name) => ProcessTerminator.RequestKill(detail, pid, name);
            detail.SettingsChanged += OnSettingsChanged;
            detail.SystemInfoRefreshRequested += () => RefreshSystemInfo(detail);
            detail.Closed += (_, _) =>
            {
                _detail = null;
                // Prozess-Enumeration ist der teuerste Teil der Erfassung und
                // läuft nur bei geöffnetem Detailfenster (DESIGN.md §9).
                if (_collector is not null)
                    _collector.ProcessSamplingEnabled = false;
            };

            if (_collector is not null)
            {
                _collector.ProcessSamplingEnabled = true;

                // Beim ersten Öffnen läuft die WMI-Abfrage womöglich noch; das
                // Fenster holt sie sich nach, sobald sie fertig ist.
                _ = _collector.SystemInfoReady.ContinueWith(
                    task => Dispatcher.BeginInvoke(() => detail.PushSystemInfo(task.Result)),
                    TaskContinuationOptions.OnlyOnRanToCompletion);
            }

            detail.Show();
            return;
        }

        if (_detail.WindowState == WindowState.Minimized)
            _detail.WindowState = WindowState.Normal;
        _detail.Activate();
    }

    /// <summary>
    /// Erhebt die Systemübersicht neu. WMI braucht dafür mehrere hundert
    /// Millisekunden und darf den UI-Thread nicht blockieren.
    /// </summary>
    private void RefreshSystemInfo(DetailWindow detail)
        => _ = Task.Run(SystemInfoProvider.Collect).ContinueWith(
            task => Dispatcher.BeginInvoke(() =>
            {
                if (ReferenceEquals(_detail, detail))
                    detail.PushSystemInfo(task.Result);
            }),
            TaskContinuationOptions.OnlyOnRanToCompletion);

    /// <summary>
    /// Beenden mit Wachhund: das Entladen des Sensor-Treibers und das Stoppen der
    /// ETW-Sitzung können hängen bleiben. Ein Monitor, der sich nicht schließen
    /// lässt, ist schlimmer als einer, der hart aussteigt.
    /// </summary>
    private void Quit()
    {
        _ = Task.Delay(TimeSpan.FromSeconds(5)).ContinueWith(_ => Environment.Exit(0));
        Shutdown();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _tray?.Dispose();
        _collector?.Dispose();
        _settings.Save();

        _showDetailRegistration?.Unregister(null);
        _showDetailSignal?.Dispose();

        if (_singleInstance is not null)
        {
            try
            {
                _singleInstance.ReleaseMutex();
            }
            catch (ApplicationException)
            {
                // Nicht der besitzende Thread — beim Herunterfahren belanglos.
            }

            _singleInstance.Dispose();
        }

        base.OnExit(e);
    }
}

using System.Windows;
using ResMon.Core;
// WPF und WinForms sind beide referenziert (Tray-Icon); Application und
// MessageBox gibt es in beiden Namensräumen.
using Application = System.Windows.Application;
using ResMon.Core.Config;
using ResMon.Core.Model;

namespace ResMon.App;

/// <summary>
/// Einstiegspunkt. Hält Collector, Overlay, Detailfenster und Tray-Icon zusammen
/// und verteilt die Snapshots auf den UI-Thread.
/// </summary>
public partial class App : Application
{
    private const string MutexName = @"Global\ResMon.SingleInstance";

    private Mutex? _singleInstance;
    private AppSettings _settings = new();
    private Collector? _collector;
    private OverlayWindow? _overlay;
    private DetailWindow? _detail;
    private TrayIcon? _tray;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // Zweite Instanz beendet sich sofort (DESIGN.md §14).
        _singleInstance = new Mutex(initiallyOwned: true, MutexName, out bool isFirstInstance);
        if (!isFirstInstance)
        {
            Shutdown();
            return;
        }

        _settings = AppSettings.Load();
        _collector = new Collector(_settings);
        _collector.SnapshotReady += OnSnapshotReady;

        _overlay = new OverlayWindow(_settings);
        _overlay.DetailRequested += ShowDetailWindow;
        _overlay.CloseRequested += Shutdown;
        _overlay.Show();

        _tray = new TrayIcon(_settings);
        _tray.DetailRequested += ShowDetailWindow;
        _tray.ExitRequested += Shutdown;
        _tray.OpacityChanged += opacity => _overlay.ApplyOpacity(opacity);
        _tray.ClickThroughChanged += enabled => _overlay.ApplyClickThrough(enabled);

        _collector.Start();

        if (_collector.HardwareError is { } error)
        {
            _tray.Notify(
                "Sensoren nicht verfügbar",
                $"Temperaturen und Taktraten fehlen: {error}");
        }
    }

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
            _detail?.Push(snapshot, history);
        });
    }

    private void ShowDetailWindow()
    {
        if (_detail is null)
        {
            _detail = new DetailWindow();
            _detail.Closed += (_, _) =>
            {
                _detail = null;
                // Prozess-Enumeration ist der teuerste Teil der Erfassung und
                // läuft nur bei geöffnetem Detailfenster (DESIGN.md §9).
                if (_collector is not null)
                    _collector.ProcessSamplingEnabled = false;
            };

            if (_collector is not null)
                _collector.ProcessSamplingEnabled = true;

            _detail.Show();
            return;
        }

        if (_detail.WindowState == WindowState.Minimized)
            _detail.WindowState = WindowState.Normal;
        _detail.Activate();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _tray?.Dispose();
        _collector?.Dispose();
        _settings.Save();

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

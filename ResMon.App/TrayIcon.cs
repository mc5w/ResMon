using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;
using ResMon.Core.Config;

namespace ResMon.App;

/// <summary>
/// Tray-Icon samt Einstellungsmenü (DESIGN.md §4, §14). Die Einstellungen sind
/// bewusst als Menü statt als eigener Dialog umgesetzt — es sind nur Schalter.
/// </summary>
public sealed class TrayIcon : IDisposable
{
    private readonly AppSettings _settings;
    private readonly NotifyIcon _notifyIcon;

    /// <summary>Das geladene Symbol; <c>null</c>, wenn das Systemsymbol einspringt.</summary>
    private readonly Icon? _icon;
    private readonly ToolStripMenuItem _clickThroughItem;
    private readonly ToolStripMenuItem _autostartItem;
    private readonly ToolStripMenuItem _opacityRoot;
    private readonly List<(ToolStripMenuItem Item, Func<bool> IsOn)> _checks = [];

    /// <summary>
    /// True, solange <see cref="Sync"/> die Haken setzt. Ohne diese Sperre würde
    /// jedes nachgezogene Häkchen eine Änderungsmeldung auslösen und die gerade
    /// erst übernommene Einstellung erneut durch alle Fenster schicken.
    /// </summary>
    private bool _syncing;

    public TrayIcon(AppSettings settings)
    {
        _settings = settings;

        // Ohne Symbol bliebe NotifyIcon unsichtbar; dann muss das Systemsymbol
        // herhalten.
        _icon = AppIcon.CreateTrayIcon();

        _clickThroughItem = new ToolStripMenuItem("Klick-durchlässig")
        {
            CheckOnClick = true,
            Checked = settings.Overlay.ClickThrough,
        };
        _clickThroughItem.CheckedChanged += (_, _) =>
        {
            if (_syncing)
                return;

            settings.Overlay.ClickThrough = _clickThroughItem.Checked;
            SettingsChanged?.Invoke();
        };
        _checks.Add((_clickThroughItem, () => _settings.Overlay.ClickThrough));

        _autostartItem = new ToolStripMenuItem("Mit Windows starten")
        {
            CheckOnClick = false,
            Checked = Autostart.IsEnabled(),
        };
        _autostartItem.Click += (_, _) => ToggleAutostart();

        _opacityRoot = BuildOpacityMenu();

        var menu = new ContextMenuStrip();
        menu.Items.Add(new ToolStripMenuItem("Details…", null, (_, _) => DetailRequested?.Invoke()));
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(_opacityRoot);
        menu.Items.Add(BuildVisibilityMenu());
        menu.Items.Add(_clickThroughItem);
        menu.Items.Add(_autostartItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(new ToolStripMenuItem("Beenden", null, (_, _) => ExitRequested?.Invoke()));

        _notifyIcon = new NotifyIcon
        {
            Icon = _icon ?? SystemIcons.Application,
            Text = "ResMon",
            Visible = true,
            ContextMenuStrip = menu,
        };
        _notifyIcon.DoubleClick += (_, _) => DetailRequested?.Invoke();
    }

    public event Action? DetailRequested;
    public event Action? ExitRequested;

    /// <summary>Eine Einstellung im Menü wurde geändert; sie steht bereits in den Settings.</summary>
    public event Action? SettingsChanged;

    /// <summary>
    /// Zieht die Haken auf den aktuellen Einstellungsstand nach — nötig, wenn die
    /// Änderung von der Einstellungsseite im Detailfenster kam.
    /// </summary>
    public void Sync()
    {
        _syncing = true;
        try
        {
            foreach ((ToolStripMenuItem item, Func<bool> isOn) in _checks)
                item.Checked = isOn();

            foreach (ToolStripMenuItem item in _opacityRoot.DropDownItems)
                item.Checked = Math.Abs(_settings.Overlay.Opacity - (int)item.Tag! / 100.0) < 0.005;
        }
        finally
        {
            _syncing = false;
        }
    }

    /// <summary>Zeigt eine Ballon-Meldung, etwa wenn der Sensor-Treiber fehlt.</summary>
    public void Notify(string title, string message)
        => _notifyIcon.ShowBalloonTip(5000, title, message, ToolTipIcon.Warning);

    private ToolStripMenuItem BuildOpacityMenu()
    {
        var root = new ToolStripMenuItem("Deckkraft");
        foreach (int percent in new[] { 40, 60, 80, 90, 100 })
        {
            var item = new ToolStripMenuItem($"{percent} %")
            {
                Tag = percent,
                Checked = Math.Abs(_settings.Overlay.Opacity - percent / 100.0) < 0.005,
            };
            item.Click += (_, _) =>
            {
                foreach (ToolStripMenuItem sibling in root.DropDownItems)
                    sibling.Checked = ReferenceEquals(sibling, item);
                _settings.Overlay.Opacity = percent / 100.0;
                SettingsChanged?.Invoke();
            };
            root.DropDownItems.Add(item);
        }

        return root;
    }

    private ToolStripMenuItem BuildVisibilityMenu()
    {
        var root = new ToolStripMenuItem("Anzeige");
        VisibilitySettings visible = _settings.Visible;

        root.DropDownItems.Add(Toggle("CPU", () => visible.Cpu, v => visible.Cpu = v));
        root.DropDownItems.Add(Toggle("GPU", () => visible.Gpu, v => visible.Gpu = v));
        root.DropDownItems.Add(Toggle("Arbeitsspeicher", () => visible.Ram, v => visible.Ram = v));
        root.DropDownItems.Add(Toggle("Netzwerk", () => visible.Net, v => visible.Net = v));
        root.DropDownItems.Add(Toggle("Datenträger", () => visible.Disk, v => visible.Disk = v));
        root.DropDownItems.Add(Toggle("Temperaturen", () => visible.Temps, v => visible.Temps = v));
        return root;

        ToolStripMenuItem Toggle(string text, Func<bool> isOn, Action<bool> apply)
        {
            var item = new ToolStripMenuItem(text) { CheckOnClick = true, Checked = isOn() };
            item.CheckedChanged += (_, _) =>
            {
                if (_syncing)
                    return;

                apply(item.Checked);
                SettingsChanged?.Invoke();
            };
            _checks.Add((item, isOn));
            return item;
        }
    }

    private void ToggleAutostart()
    {
        bool enable = !_autostartItem.Checked;
        bool ok = enable ? Autostart.Enable(out string? error) : Autostart.Disable(out error);

        if (!ok)
        {
            Notify("Autostart", $"Aufgabe konnte nicht {(enable ? "angelegt" : "entfernt")} werden: {error}");
            return;
        }

        _autostartItem.Checked = enable;
        _settings.Autostart = enable;
        _settings.Save();
    }

    public void Dispose()
    {
        _notifyIcon.Visible = false;
        _notifyIcon.Dispose();

        // Springt das Systemsymbol ein, gehört es uns nicht — es ist prozessweit
        // zwischengespeichert und darf nicht freigegeben werden.
        _icon?.Dispose();
    }
}

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
    private readonly Icon _icon;
    private readonly ToolStripMenuItem _clickThroughItem;
    private readonly ToolStripMenuItem _autostartItem;

    public TrayIcon(AppSettings settings)
    {
        _settings = settings;
        _icon = CreateIcon();

        _clickThroughItem = new ToolStripMenuItem("Klick-durchlässig")
        {
            CheckOnClick = true,
            Checked = settings.Overlay.ClickThrough,
        };
        _clickThroughItem.CheckedChanged += (_, _) =>
        {
            ClickThroughChanged?.Invoke(_clickThroughItem.Checked);
            settings.Save();
        };

        _autostartItem = new ToolStripMenuItem("Mit Windows starten")
        {
            CheckOnClick = false,
            Checked = Autostart.IsEnabled(),
        };
        _autostartItem.Click += (_, _) => ToggleAutostart();

        var menu = new ContextMenuStrip();
        menu.Items.Add(new ToolStripMenuItem("Details…", null, (_, _) => DetailRequested?.Invoke()));
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(BuildOpacityMenu());
        menu.Items.Add(BuildVisibilityMenu());
        menu.Items.Add(_clickThroughItem);
        menu.Items.Add(_autostartItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(new ToolStripMenuItem("Beenden", null, (_, _) => ExitRequested?.Invoke()));

        _notifyIcon = new NotifyIcon
        {
            Icon = _icon,
            Text = "ResMon",
            Visible = true,
            ContextMenuStrip = menu,
        };
        _notifyIcon.DoubleClick += (_, _) => DetailRequested?.Invoke();
    }

    public event Action? DetailRequested;
    public event Action? ExitRequested;
    public event Action<double>? OpacityChanged;
    public event Action<bool>? ClickThroughChanged;

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
                Checked = Math.Abs(_settings.Overlay.Opacity - percent / 100.0) < 0.01,
            };
            item.Click += (_, _) =>
            {
                foreach (ToolStripMenuItem sibling in root.DropDownItems)
                    sibling.Checked = ReferenceEquals(sibling, item);
                OpacityChanged?.Invoke(percent / 100.0);
                _settings.Save();
            };
            root.DropDownItems.Add(item);
        }

        return root;
    }

    private ToolStripMenuItem BuildVisibilityMenu()
    {
        var root = new ToolStripMenuItem("Anzeige");
        VisibilitySettings visible = _settings.Visible;

        root.DropDownItems.Add(Toggle("CPU", visible.Cpu, v => visible.Cpu = v));
        root.DropDownItems.Add(Toggle("GPU", visible.Gpu, v => visible.Gpu = v));
        root.DropDownItems.Add(Toggle("Arbeitsspeicher", visible.Ram, v => visible.Ram = v));
        root.DropDownItems.Add(Toggle("Netzwerk", visible.Net, v => visible.Net = v));
        root.DropDownItems.Add(Toggle("Datenträger", visible.Disk, v => visible.Disk = v));
        root.DropDownItems.Add(Toggle("Temperaturen", visible.Temps, v => visible.Temps = v));
        return root;

        ToolStripMenuItem Toggle(string text, bool initial, Action<bool> apply)
        {
            var item = new ToolStripMenuItem(text) { CheckOnClick = true, Checked = initial };
            item.CheckedChanged += (_, _) =>
            {
                // Das Overlay liest die Sichtbarkeit bei jedem Takt neu — die
                // Änderung greift also spätestens nach einer Sekunde.
                apply(item.Checked);
                _settings.Save();
            };
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

    /// <summary>
    /// Zeichnet das Icon zur Laufzeit — drei Balken in aufsteigender Höhe. Spart
    /// eine Binärressource im Repository.
    /// </summary>
    private static Icon CreateIcon()
    {
        using var bitmap = new Bitmap(32, 32);
        using (var graphics = Graphics.FromImage(bitmap))
        {
            graphics.SmoothingMode = SmoothingMode.AntiAlias;
            graphics.Clear(Color.Transparent);

            (int Height, Color Color)[] bars =
            [
                (10, Color.FromArgb(255, 96, 165, 250)),
                (18, Color.FromArgb(255, 74, 222, 128)),
                (26, Color.FromArgb(255, 251, 146, 60)),
            ];

            for (int i = 0; i < bars.Length; i++)
            {
                using var brush = new SolidBrush(bars[i].Color);
                graphics.FillRectangle(brush, 4 + i * 9, 29 - bars[i].Height, 7, bars[i].Height);
            }
        }

        IntPtr handle = bitmap.GetHicon();
        try
        {
            // Kopieren, damit das Icon das native Handle nicht überlebt.
            using var temporary = Icon.FromHandle(handle);
            return (Icon)temporary.Clone();
        }
        finally
        {
            DestroyIcon(handle);
        }
    }

    [System.Runtime.InteropServices.DllImport("user32.dll", SetLastError = true)]
    private static extern bool DestroyIcon(IntPtr hIcon);

    public void Dispose()
    {
        _notifyIcon.Visible = false;
        _notifyIcon.Dispose();
        _icon.Dispose();
    }
}

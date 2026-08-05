using System.Text.Json;
using System.Text.Json.Serialization;

namespace ResMon.Core.Config;

/// <summary>Position, Deckkraft und Klick-Durchlässigkeit des Overlays.</summary>
public sealed class OverlaySettings
{
    public double X { get; set; } = 40;
    public double Y { get; set; } = 40;
    public double Opacity { get; set; } = 0.9;
    public bool ClickThrough { get; set; }
}

/// <summary>Sampling-Takte in Millisekunden (DESIGN.md §9).</summary>
public sealed class IntervalSettings
{
    public int AggregateMs { get; set; } = 1000;
    public int HardwareMs { get; set; } = 2000;
    public int ProcessMs { get; set; } = 2000;
    public int ServiceMs { get; set; } = 30_000;
}

/// <summary>Welche Zeilen das Overlay anzeigt.</summary>
public sealed class VisibilitySettings
{
    public bool Cpu { get; set; } = true;
    public bool Gpu { get; set; } = true;
    public bool Ram { get; set; } = true;
    public bool Net { get; set; } = true;
    public bool Temps { get; set; } = true;
}

/// <summary>
/// Persistierte Einstellungen aus <c>%AppData%\ResMon\settings.json</c>
/// (DESIGN.md §14). Messwerte werden bewusst nicht gespeichert.
/// </summary>
public sealed class AppSettings
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    };

    public OverlaySettings Overlay { get; set; } = new();
    public IntervalSettings Intervals { get; set; } = new();
    public VisibilitySettings Visible { get; set; } = new();
    public bool Autostart { get; set; }

    [JsonIgnore]
    public static string DefaultPath { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "ResMon",
        "settings.json");

    public static AppSettings Load(string? path = null)
    {
        path ??= DefaultPath;
        try
        {
            if (!File.Exists(path))
                return new AppSettings();

            return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(path), SerializerOptions)
                   ?? new AppSettings();
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            // Beschädigte Datei darf den Start nicht verhindern — mit Standardwerten
            // weiterlaufen und beim nächsten Speichern überschreiben.
            return new AppSettings();
        }
    }

    public void Save(string? path = null)
    {
        path ??= DefaultPath;
        try
        {
            string? directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(directory))
                Directory.CreateDirectory(directory);

            File.WriteAllText(path, JsonSerializer.Serialize(this, SerializerOptions));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }
}

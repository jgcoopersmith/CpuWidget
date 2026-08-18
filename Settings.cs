using System.IO;
using System.Text.Json;

namespace CpuWidget;

/// <summary>Widget position and preferences, persisted to %APPDATA%\CpuWidget\settings.json.</summary>
public sealed class Settings
{
    public double? Left { get; set; }
    public double? Top { get; set; }
    /// <summary>Widget width; wider shows a longer history in each graph.</summary>
    public double? Width { get; set; }
    /// <summary>Widget height; taller gives the graphs more vertical room.</summary>
    public double? Height { get; set; }
    public bool AlwaysOnTop { get; set; } = true;
    public double Opacity { get; set; } = 0.92;
    /// <summary>Show temperatures in °F instead of °C.</summary>
    public bool Fahrenheit { get; set; }

    // Colours, as #RRGGBB or #AARRGGBB. Null means the built-in default.
    public string? TitleColor { get; set; }
    public string? CpuAccent { get; set; }
    public string? GpuAccent { get; set; }
    public string? Background { get; set; }

    // The temperature ramp: which colour each threshold band uses.
    public string? TempCool { get; set; }
    public string? TempWarm { get; set; }
    public string? TempHot { get; set; }
    public string? TempCritical { get; set; }

    /// <summary>
    /// Folder holding settings and the history log. Follows the CPUWIDGET_SETTINGS
    /// override, so a test build keeps its data entirely separate.
    /// </summary>
    public static string Directory0 => Path.GetDirectoryName(Path0)!;

    /// <summary>
    /// Where settings live. CPUWIDGET_SETTINGS overrides the location so a test build can
    /// be run without touching the real widget's saved position, size and colours.
    /// </summary>
    private static string Path0 =>
        Environment.GetEnvironmentVariable("CPUWIDGET_SETTINGS") is { Length: > 0 } custom
            ? custom
            : Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "CpuWidget", "settings.json");

    public static Settings Load()
    {
        try
        {
            if (File.Exists(Path0))
                return JsonSerializer.Deserialize<Settings>(File.ReadAllText(Path0)) ?? new Settings();
        }
        catch { /* corrupt or unreadable — start from defaults */ }
        return new Settings();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path0)!);
            File.WriteAllText(Path0, JsonSerializer.Serialize(this,
                new JsonSerializerOptions { WriteIndented = true }));
        }
        catch { /* not worth bothering the user about */ }
    }
}

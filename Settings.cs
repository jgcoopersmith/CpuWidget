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

    private static string Path0 => Path.Combine(
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

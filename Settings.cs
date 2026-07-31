using System.IO;
using System.Text.Json;

namespace CpuWidget;

/// <summary>Widget position and preferences, persisted to %APPDATA%\CpuWidget\settings.json.</summary>
public sealed class Settings
{
    public double? Left { get; set; }
    public double? Top { get; set; }
    public bool AlwaysOnTop { get; set; } = true;
    public double Opacity { get; set; } = 0.92;

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

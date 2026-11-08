using System.IO;
using System.Text.Json;

namespace MegaSchematicsDownloader;

public enum SortKind
{
    NameAsc = 0,
    NameDesc = 1,
    SizeDesc = 2
}

/// <summary>Small user settings file in %AppData%. Any failure falls back to defaults.</summary>
public sealed class AppSettings
{
    public string? DownloadRoot { get; set; }
    public int Sort { get; set; }
    public bool SearchCurrentFolderOnly { get; set; }

    private static string SettingsPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "MegaSchematicsDownloader", "settings.json");

    public static AppSettings Load()
    {
        try
        {
            var path = SettingsPath;
            if (File.Exists(path))
                return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(path)) ?? new AppSettings();
        }
        catch
        {
            // Corrupt or unreadable settings are ignored.
        }
        return new AppSettings();
    }

    public void Save()
    {
        try
        {
            var path = SettingsPath;
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch
        {
            // Settings are a convenience; never fail because of them.
        }
    }
}

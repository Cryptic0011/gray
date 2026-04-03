using System.Text.Json;
using Gmux.Core.Models;

namespace Gmux.Core.Services;

public class SettingsManager
{
    private readonly string _settingsDir;
    private readonly string _settingsFile;

    public AppSettings Current { get; private set; } = new();
    public event Action<AppSettings>? SettingsChanged;

    public SettingsManager()
    {
        _settingsDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "gray");
        _settingsFile = Path.Combine(_settingsDir, "settings.json");
    }

    public async Task LoadAsync()
    {
        if (!File.Exists(_settingsFile))
            return;

        try
        {
            var json = await File.ReadAllTextAsync(_settingsFile);
            var settings = JsonSerializer.Deserialize<AppSettings>(json);
            if (settings != null)
                Current = settings;
        }
        catch
        {
            Current = new AppSettings();
        }
    }

    public async Task SaveAsync()
    {
        Directory.CreateDirectory(_settingsDir);
        var json = JsonSerializer.Serialize(Current, new JsonSerializerOptions { WriteIndented = true });
        await File.WriteAllTextAsync(_settingsFile, json);
        SettingsChanged?.Invoke(Current);
    }
}

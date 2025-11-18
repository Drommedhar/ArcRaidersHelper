using System;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace OverlayApp.Infrastructure;

internal sealed class UserSettingsStore
{
    private readonly string _filePath;
    private readonly JsonSerializerOptions _serializerOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public UserSettingsStore()
    {
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var folder = Path.Combine(appData, "ArcRaidersHelper");
        Directory.CreateDirectory(folder);
        _filePath = Path.Combine(folder, "settings.json");
    }

    public UserSettings Load()
    {
        try
        {
            if (!File.Exists(_filePath))
            {
                return new UserSettings();
            }

            var json = File.ReadAllText(_filePath);
            var settings = JsonSerializer.Deserialize<UserSettings>(json, _serializerOptions);
            return settings ?? new UserSettings();
        }
        catch
        {
            return new UserSettings();
        }
    }

    public void Save(UserSettings settings)
    {
        if (settings is null)
        {
            return;
        }

        var json = JsonSerializer.Serialize(settings, _serializerOptions);
        File.WriteAllText(_filePath, json);
    }
}

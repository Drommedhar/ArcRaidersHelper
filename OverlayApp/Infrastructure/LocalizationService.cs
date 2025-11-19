using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text.Json;

namespace OverlayApp.Infrastructure;

public class LocalizationService : INotifyPropertyChanged
{
    private static LocalizationService? _instance;
    public static LocalizationService Instance => _instance ??= new LocalizationService();

    private Dictionary<string, string> _strings = new();
    private string _currentLanguage = "en";

    private LocalizationService()
    {
        LoadLanguage("en");
    }

    public string this[string key]
    {
        get
        {
            if (_strings.TryGetValue(key, out var value))
            {
                return value;
            }
            return $"[{key}]"; // Visual indicator for missing keys
        }
    }

    public string CurrentLanguage
    {
        get => _currentLanguage;
        set
        {
            if (_currentLanguage != value)
            {
                _currentLanguage = value;
                LocalizationHelper.CurrentLanguage = value;
                LoadLanguage(value);
                OnPropertyChanged(string.Empty); // Refresh all bindings
            }
        }
    }

    private void LoadLanguage(string languageCode)
    {
        var loaded = false;
        var path = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Resources", "Localization", $"{languageCode}.json");
        
        if (File.Exists(path))
        {
            try
            {
                var json = File.ReadAllText(path);
                var dict = JsonSerializer.Deserialize<Dictionary<string, string>>(json);
                if (dict != null)
                {
                    _strings = dict;
                    loaded = true;
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Failed to load localization file for {languageCode}: {ex.Message}");
            }
        }

        if (!loaded && languageCode != "en")
        {
            // Fallback to English if requested language fails
            LoadLanguage("en");
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}

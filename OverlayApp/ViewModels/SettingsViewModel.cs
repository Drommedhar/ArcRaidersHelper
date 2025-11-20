using CommunityToolkit.Mvvm.ComponentModel;
using OverlayApp.Infrastructure;
using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Windows.Input;

namespace OverlayApp.ViewModels;

public partial class SettingsViewModel : ObservableObject
{
    private readonly UserSettings _workingCopy;

    public SettingsViewModel(UserSettings source)
    {
        _workingCopy = source?.Clone() ?? throw new ArgumentNullException(nameof(source));

        OverlayOpacity = _workingCopy.OverlayOpacity;
        ClickThroughOverlayOpacity = _workingCopy.ClickThroughOverlayOpacity;
        HideOnLaunch = _workingCopy.HideOnLaunch;
        AlwaysOnTop = _workingCopy.AlwaysOnTop;
        ClickThroughEnabled = _workingCopy.ClickThroughEnabled;
        AutoCaptureEnabled = _workingCopy.AutoCaptureEnabled;
        QuestDetectionEnabled = _workingCopy.QuestDetectionEnabled;
        ProjectDetectionEnabled = _workingCopy.ProjectDetectionEnabled;
        HideoutDetectionEnabled = _workingCopy.HideoutDetectionEnabled;
        ToggleHotkeyText = _workingCopy.ToggleHotkey;
        ExitHotkeyText = _workingCopy.ExitHotkey;
        ClickThroughHotkeyText = _workingCopy.ClickThroughHotkey;
        SelectedLanguage = AvailableLanguages.FirstOrDefault(l => l.Code == _workingCopy.Language) ?? AvailableLanguages.First();
    }

    public ObservableCollection<LanguageOption> AvailableLanguages { get; } = new()
    {
        new("English", "en"),
        new("Deutsch", "de"),
        new("Français", "fr"),
        new("Español", "es"),
        new("Português", "pt"),
        new("Italiano", "it"),
        new("Русский", "ru"),
        new("Polski", "pl"),
        new("한국어", "ko"),
        new("日本語", "ja"),
        new("简体中文", "zh-CN"),
        new("繁體中文", "zh-TW"),
        new("Türkçe", "tr")
    };

    public record LanguageOption(string Name, string Code);

    [ObservableProperty]
    private LanguageOption _selectedLanguage;

    [ObservableProperty]
    private bool _hideOnLaunch;

    [ObservableProperty]
    private bool _alwaysOnTop;

    [ObservableProperty]
    private bool _clickThroughEnabled;

    [ObservableProperty]
    private bool _autoCaptureEnabled;

    [ObservableProperty]
    private bool _questDetectionEnabled;

    [ObservableProperty]
    private bool _projectDetectionEnabled;

    [ObservableProperty]
    private bool _hideoutDetectionEnabled;

    [ObservableProperty]
    private double _overlayOpacity = 1.0;

    [ObservableProperty]
    private double _clickThroughOverlayOpacity = 0.6;

    [ObservableProperty]
    private string _toggleHotkeyText = "Ctrl+Alt+O";

    [ObservableProperty]
    private string _exitHotkeyText = "Ctrl+Alt+Shift+O";

    [ObservableProperty]
    private string _clickThroughHotkeyText = "Ctrl+Alt+T";

    public bool ApplyChanges(out UserSettings updated)
    {
        updated = _workingCopy;

        if (!TryValidateHotkeys(out var toggle, out var exit, out var click))
        {
            return false;
        }

        updated.HideOnLaunch = HideOnLaunch;
        updated.AlwaysOnTop = AlwaysOnTop;
        updated.ClickThroughEnabled = ClickThroughEnabled;
        updated.AutoCaptureEnabled = AutoCaptureEnabled;
        updated.QuestDetectionEnabled = QuestDetectionEnabled;
        updated.ProjectDetectionEnabled = ProjectDetectionEnabled;
        updated.HideoutDetectionEnabled = HideoutDetectionEnabled;
        updated.OverlayOpacity = Math.Clamp(OverlayOpacity, 0.2, 1.0);
        updated.ClickThroughOverlayOpacity = Math.Clamp(ClickThroughOverlayOpacity, 0.1, 1.0);
        updated.ToggleHotkey = toggle.ToString();
        updated.ExitHotkey = exit.ToString();
        updated.ClickThroughHotkey = click.ToString();
        updated.Language = SelectedLanguage.Code;
        return true;
    }

    private bool TryValidateHotkeys(out HotkeyDefinition toggle, out HotkeyDefinition exit, out HotkeyDefinition click)
    {
        toggle = HotkeyDefinition.FromStringOrDefault(ToggleHotkeyText, new HotkeyDefinition(ModifierKeys.Control | ModifierKeys.Alt, Key.O));
        exit = HotkeyDefinition.FromStringOrDefault(ExitHotkeyText, new HotkeyDefinition(ModifierKeys.Control | ModifierKeys.Alt | ModifierKeys.Shift, Key.O));
        click = HotkeyDefinition.FromStringOrDefault(ClickThroughHotkeyText, new HotkeyDefinition(ModifierKeys.Control | ModifierKeys.Alt, Key.T));

        var distinct = new[] { toggle.ToString(), exit.ToString(), click.ToString() };
        return distinct.Distinct(StringComparer.OrdinalIgnoreCase).Count() == 3;
    }
}

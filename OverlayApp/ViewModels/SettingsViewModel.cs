using CommunityToolkit.Mvvm.ComponentModel;
using OverlayApp.Infrastructure;
using System;
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
        TrackerUrl = _workingCopy.TrackerUrl;
        ClickThroughEnabled = _workingCopy.ClickThroughEnabled;
        ToggleHotkeyText = _workingCopy.ToggleHotkey;
        ExitHotkeyText = _workingCopy.ExitHotkey;
        ClickThroughHotkeyText = _workingCopy.ClickThroughHotkey;
    }

    [ObservableProperty]
    private bool _hideOnLaunch;

    [ObservableProperty]
    private bool _alwaysOnTop;

    [ObservableProperty]
    private bool _clickThroughEnabled;

    [ObservableProperty]
    private double _overlayOpacity = 1.0;

    [ObservableProperty]
    private double _clickThroughOverlayOpacity = 0.6;

    [ObservableProperty]
    private string _trackerUrl = "https://arctracker.io";

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

        if (!Uri.TryCreate(TrackerUrl, UriKind.Absolute, out _))
        {
            return false;
        }

        updated.HideOnLaunch = HideOnLaunch;
        updated.AlwaysOnTop = AlwaysOnTop;
        updated.ClickThroughEnabled = ClickThroughEnabled;
        updated.OverlayOpacity = Math.Clamp(OverlayOpacity, 0.2, 1.0);
        updated.ClickThroughOverlayOpacity = Math.Clamp(ClickThroughOverlayOpacity, 0.1, 1.0);
        updated.TrackerUrl = TrackerUrl;
        updated.ToggleHotkey = toggle.ToString();
        updated.ExitHotkey = exit.ToString();
        updated.ClickThroughHotkey = click.ToString();
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

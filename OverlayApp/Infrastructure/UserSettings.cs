namespace OverlayApp.Infrastructure;

public sealed class UserSettings
{
    public double Width { get; set; } = 1400;
    public double Height { get; set; } = 900;
    public double Left { get; set; } = double.NaN;
    public double Top { get; set; } = double.NaN;
    public bool IsMaximized { get; set; }
    public bool ClickThroughEnabled { get; set; }
    public bool HideOnLaunch { get; set; } = false;
    public bool AlwaysOnTop { get; set; } = true;
    public double OverlayOpacity { get; set; } = 1.0;
    public double ClickThroughOverlayOpacity { get; set; } = 0.6;
    public string TrackerUrl { get; set; } = "https://arctracker.io";
    public string? LastDownloadedVersion { get; set; }
    public string Language { get; set; } = "en";

    public string ToggleHotkey { get; set; } = "Ctrl+Alt+O";
    public string ExitHotkey { get; set; } = "Ctrl+Alt+Shift+O";
    public string ClickThroughHotkey { get; set; } = "Ctrl+Alt+T";

    public UserSettings Clone()
    {
        return (UserSettings)MemberwiseClone();
    }
}

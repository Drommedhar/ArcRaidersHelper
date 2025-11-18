using Microsoft.Web.WebView2.Core;
using OverlayApp.Infrastructure;
using OverlayApp.Properties;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;

namespace OverlayApp;

public partial class MainWindow : Window
{
    private const int ToggleHotkeyId = 1;
    private const int ExitHotkeyId = 2;
    private const int ClickThroughHotkeyId = 3;
    private const double ResizeBorderThickness = 12d;
    private const double MinimumWindowWidth = 400d;
    private const double MinimumWindowHeight = 300d;
    private const string TrackerHost = "arctracker.io";
    private const string DefaultTrackerUrl = "https://arctracker.io/";
    private static readonly Uri DefaultTrackerUri = new(DefaultTrackerUrl);

    private static readonly HotkeyDefinition DefaultToggleHotkey = new(ModifierKeys.Control | ModifierKeys.Alt, Key.O);
    private static readonly HotkeyDefinition DefaultExitHotkey = new(ModifierKeys.Control | ModifierKeys.Alt | ModifierKeys.Shift, Key.O);
    private static readonly HotkeyDefinition DefaultClickThroughHotkey = new(ModifierKeys.Control | ModifierKeys.Alt, Key.T);

    private readonly UserSettingsStore _settingsStore = new();
    private readonly UserSettings _settings;
    private readonly UpdateService _updateService;
    private readonly ILogger _logger;
    private GlobalHotkeyManager? _hotkeyManager;
    private bool _isOverlayVisible = true;
    private MenuItem? _clickThroughMenuItem;
    private bool _suppressMenuToggleEvents;
    private bool _adFilterInitialized;
    private static readonly IReadOnlyList<string> AdHostKeywords = new[]
    {
        "doubleclick.net",
        "googlesyndication.com",
        "google-analytics.com",
        "adservice.google.com",
        "ads.pubmatic.com",
        "scorecardresearch.com",
        "taboola",
        "adsystem",
        "advertising.com",
        "adroll",
        "quantserve",
        "criteo",
        "openx.net"
    };

    private static readonly IReadOnlyList<string> AdPathKeywords = new[]
    {
        "/ads",
        "/adserver",
        "banner",
        "sponsor",
        "promoted",
        "gampad",
        "doubleclick"
    };
    private IntPtr _windowHandle;
    private HwndSource? _hwndSource;
    private int _baseExtendedStyle;
    private HotkeyDefinition _toggleHotkey = DefaultToggleHotkey;
    private HotkeyDefinition _exitHotkey = DefaultExitHotkey;
    private HotkeyDefinition _clickThroughHotkey = DefaultClickThroughHotkey;

    public MainWindow()
    {
        InitializeComponent();
        _logger = LoggerFactory.CreateDefaultLogger();
        var githubToken = TryLoadGitHubToken();
        _updateService = new UpdateService(githubToken, _logger);
        _settings = _settingsStore.Load();
        if (string.IsNullOrWhiteSpace(_settings.TrackerUrl))
        {
            _settings.TrackerUrl = DefaultTrackerUrl;
        }
    }

    private async void OnWindowLoaded(object sender, RoutedEventArgs e)
    {
        ApplyOverlayAppearance();
        ApplyTopmostSetting();
        UpdateClickThroughMenuState(_settings.ClickThroughEnabled);
        UpdateHeaderVisibility(_settings.ClickThroughEnabled);
        NavigateToTracker();

        await InitializeWebViewAsync();
        await ApplyWebContentStylingAsync();

        if (_settings.HideOnLaunch)
        {
            HideOverlay();
        }
        else
        {
            ShowOverlay();
        }

        _ = CheckForUpdatesAsync();
    }

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        _hwndSource = (HwndSource?)PresentationSource.FromVisual(this);
        if (_hwndSource is null)
        {
            throw new InvalidOperationException("Unable to find the window handle for hotkey registration.");
        }

        ApplyWindowPlacementFromSettings();
        _hwndSource.AddHook(WndProc);
        _windowHandle = _hwndSource.Handle;
        _baseExtendedStyle = NativeWindowMethods.GetWindowLong(_windowHandle, NativeWindowMethods.GWL_EXSTYLE);
        _hotkeyManager = new GlobalHotkeyManager(_hwndSource);
        RegisterHotkeys();
        SetClickThroughMode(_settings.ClickThroughEnabled);
    }

    private void OnWindowClosing(object? sender, CancelEventArgs e)
    {
        PersistWindowPlacementToSettings();
        Settings.Default.Save();
        _settingsStore.Save(_settings);
        if (OverlayView?.CoreWebView2 is not null)
        {
            OverlayView.CoreWebView2.WebResourceRequested -= HandleWebResourceRequested;
            OverlayView.CoreWebView2.SourceChanged -= HandleSourceChanged;
            OverlayView.CoreWebView2.NavigationCompleted -= HandleNavigationCompleted;
        }
        _hotkeyManager?.Dispose();
        _hwndSource?.RemoveHook(WndProc);
        _updateService.Dispose();
    }

    private void OnHeaderMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount == 2)
        {
            ToggleWindowState();
            return;
        }

        DragMove();
    }

    private void OnReloadRequested(object sender, RoutedEventArgs e)
    {
        if (OverlayView.CoreWebView2 != null)
        {
            OverlayView.Reload();
        }
        else
        {
            _ = InitializeWebViewAsync();
        }
    }

    private void OnCloseRequested(object sender, RoutedEventArgs e)
    {
        Close();
    }

    private void OnSettingsRequested(object sender, RoutedEventArgs e)
    {
        var dialog = new SettingsWindow(_settings)
        {
            Owner = this
        };

        if (dialog.ShowDialog() == true && dialog.ResultSettings is not null)
        {
            ApplySettingsFromDialog(dialog.ResultSettings);
        }
    }

    private void OnHeaderMenuRequested(object sender, RoutedEventArgs e)
    {
        if (sender is not Button button)
        {
            return;
        }

        if (button.ContextMenu is null)
        {
            return;
        }

        button.ContextMenu.PlacementTarget = button;
        UpdateClickThroughMenuState(_settings.ClickThroughEnabled);
        button.ContextMenu.IsOpen = true;
    }

    private void OnClickThroughMenuItemClicked(object sender, RoutedEventArgs e)
    {
        if (_suppressMenuToggleEvents)
        {
            return;
        }

        var newState = !_settings.ClickThroughEnabled;
        SetClickThroughMode(newState);
    }

    private void OnWindowSizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (!IsLoaded || !_isOverlayVisible || WindowState != WindowState.Normal)
        {
            return;
        }

        _settings.Width = Width;
        _settings.Height = Height;
    }

    private void OnWindowLocationChanged(object? sender, EventArgs e)
    {
        if (!IsLoaded || !_isOverlayVisible || WindowState != WindowState.Normal)
        {
            return;
        }

        _settings.Left = Left;
        _settings.Top = Top;
    }

    private void OnWindowStateChanged(object? sender, EventArgs e)
    {
        _settings.IsMaximized = WindowState == WindowState.Maximized;
        ApplyTopmostSetting();
    }

    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        base.OnPreviewKeyDown(e);

        if (e.Key == Key.Escape)
        {
            HideOverlay();
            e.Handled = true;
        }
    }

    private async Task InitializeWebViewAsync()
    {
        try
        {
            if (OverlayView.CoreWebView2 is null)
            {
                await OverlayView.EnsureCoreWebView2Async();
            }

            ConfigureWebViewDefaults();
        }
        catch (Exception ex)
        {
            MessageBox.Show(this,
                $"Failed to initialize the embedded browser.\n{ex.Message}",
                "ArcTracker Overlay",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
    }

    private void ConfigureWebViewDefaults()
    {
        if (OverlayView.CoreWebView2 is null)
        {
            return;
        }

        var settings = OverlayView.CoreWebView2.Settings;
        settings.AreDefaultContextMenusEnabled = true;
        settings.AreBrowserAcceleratorKeysEnabled = true;
        settings.AreDevToolsEnabled = true;
        settings.IsStatusBarEnabled = false;

        OverlayView.CoreWebView2.NewWindowRequested -= HandleNewWindowRequested;
        OverlayView.CoreWebView2.NewWindowRequested += HandleNewWindowRequested;
        OverlayView.CoreWebView2.DOMContentLoaded -= HandleDomContentLoaded;
        OverlayView.CoreWebView2.DOMContentLoaded += HandleDomContentLoaded;
        OverlayView.CoreWebView2.SourceChanged -= HandleSourceChanged;
        OverlayView.CoreWebView2.SourceChanged += HandleSourceChanged;
        OverlayView.CoreWebView2.NavigationCompleted -= HandleNavigationCompleted;
        OverlayView.CoreWebView2.NavigationCompleted += HandleNavigationCompleted;
        EnsureAdBlockingFilter();
    }

    private void HandleNewWindowRequested(object? sender, CoreWebView2NewWindowRequestedEventArgs e)
    {
        if (OverlayView.CoreWebView2 is null)
        {
            return;
        }

        e.Handled = true;
        OverlayView.CoreWebView2.Navigate(e.Uri);
    }

    private async void HandleDomContentLoaded(object? sender, CoreWebView2DOMContentLoadedEventArgs e)
    {
        await ApplyWebContentStylingAsync();
    }

    private void ToggleOverlay()
    {
        if (_isOverlayVisible)
        {
            HideOverlay();
        }
        else
        {
            ShowOverlay();
        }
    }

    private void HideOverlay()
    {
        if (!_isOverlayVisible)
        {
            return;
        }

        _isOverlayVisible = false;
        Hide();
    }

    private void ShowOverlay()
    {
        if (_isOverlayVisible)
        {
            return;
        }

        _isOverlayVisible = true;
        Show();
        ApplyTopmostSetting();
        Activate();
    }

    private void CloseFromHotkey()
    {
        Dispatcher.Invoke(Close);
    }

    private void ToggleClickThrough()
    {
        var newState = !_settings.ClickThroughEnabled;
        SetClickThroughMode(newState);
    }

    private void ToggleWindowState()
    {
        WindowState = WindowState == WindowState.Maximized
            ? WindowState.Normal
            : WindowState.Maximized;
    }

    private void RegisterHotkeys()
    {
        if (_hotkeyManager is null)
        {
            return;
        }

        _hotkeyManager.Clear();

        _toggleHotkey = HotkeyDefinition.FromStringOrDefault(_settings.ToggleHotkey, DefaultToggleHotkey);
        _exitHotkey = HotkeyDefinition.FromStringOrDefault(_settings.ExitHotkey, DefaultExitHotkey);
        _clickThroughHotkey = HotkeyDefinition.FromStringOrDefault(_settings.ClickThroughHotkey, DefaultClickThroughHotkey);

        _hotkeyManager.Register(ToggleHotkeyId, _toggleHotkey.Modifiers, _toggleHotkey.Key, ToggleOverlay);
        _hotkeyManager.Register(ExitHotkeyId, _exitHotkey.Modifiers, _exitHotkey.Key, CloseFromHotkey);
        _hotkeyManager.Register(ClickThroughHotkeyId, _clickThroughHotkey.Modifiers, _clickThroughHotkey.Key, ToggleClickThrough);

        _settings.ToggleHotkey = _toggleHotkey.ToString();
        _settings.ExitHotkey = _exitHotkey.ToString();
        _settings.ClickThroughHotkey = _clickThroughHotkey.ToString();
    }

    private void ApplyWindowPlacementFromSettings()
    {
        var windowSettings = Settings.Default;
        var safeBounds = GetSafeBounds(windowSettings.Left, windowSettings.Top, windowSettings.Width, windowSettings.Height);

        WindowStartupLocation = WindowStartupLocation.Manual;
        Width = safeBounds.Width;
        Height = safeBounds.Height;
        Left = safeBounds.Left;
        Top = safeBounds.Top;

        WindowState = windowSettings.Maximized ? WindowState.Maximized : WindowState.Normal;
    }

    private void PersistWindowPlacementToSettings()
    {
        var currentBounds = WindowState == WindowState.Normal
            ? new Rect(Left, Top, Width, Height)
            : RestoreBounds;

        if (currentBounds.Width <= 0 || currentBounds.Height <= 0)
        {
            return;
        }

        var safeBounds = GetSafeBounds(currentBounds.Left, currentBounds.Top, currentBounds.Width, currentBounds.Height);
        var windowSettings = Settings.Default;

        windowSettings.Left = safeBounds.Left;
        windowSettings.Top = safeBounds.Top;
        windowSettings.Width = safeBounds.Width;
        windowSettings.Height = safeBounds.Height;
        windowSettings.Maximized = WindowState == WindowState.Maximized;

        _settings.Left = safeBounds.Left;
        _settings.Top = safeBounds.Top;
        _settings.Width = safeBounds.Width;
        _settings.Height = safeBounds.Height;
        _settings.IsMaximized = windowSettings.Maximized;
    }

    private static Rect GetSafeBounds(double left, double top, double width, double height)
    {
        var virtualLeft = SystemParameters.VirtualScreenLeft;
        var virtualTop = SystemParameters.VirtualScreenTop;
        var virtualRight = virtualLeft + SystemParameters.VirtualScreenWidth;
        var virtualBottom = virtualTop + SystemParameters.VirtualScreenHeight;

        var safeWidth = double.IsNaN(width) || width <= 0
            ? MinimumWindowWidth
            : width;
        safeWidth = Math.Clamp(safeWidth, MinimumWindowWidth, Math.Max(MinimumWindowWidth, SystemParameters.VirtualScreenWidth));

        var safeHeight = double.IsNaN(height) || height <= 0
            ? MinimumWindowHeight
            : height;
        safeHeight = Math.Clamp(safeHeight, MinimumWindowHeight, Math.Max(MinimumWindowHeight, SystemParameters.VirtualScreenHeight));

        var maxLeft = Math.Max(virtualLeft, virtualRight - safeWidth);
        var maxTop = Math.Max(virtualTop, virtualBottom - safeHeight);

        var safeLeft = double.IsNaN(left)
            ? virtualLeft
            : Math.Clamp(left, virtualLeft, maxLeft);
        var safeTop = double.IsNaN(top)
            ? virtualTop
            : Math.Clamp(top, virtualTop, maxTop);

        return new Rect(safeLeft, safeTop, safeWidth, safeHeight);
    }

    private void SetClickThroughMode(bool enabled, bool updateToggle = true)
    {
        EnsureWindowHandle();

        var styles = _baseExtendedStyle | NativeWindowMethods.WS_EX_LAYERED;
        styles = enabled
            ? styles | NativeWindowMethods.WS_EX_TRANSPARENT
            : styles & ~NativeWindowMethods.WS_EX_TRANSPARENT;
        NativeWindowMethods.SetWindowLong(_windowHandle, NativeWindowMethods.GWL_EXSTYLE, styles);

        if (ChromeBorder is not null)
        {
            ChromeBorder.IsHitTestVisible = !enabled;
        }

        if (OverlayView is not null)
        {
            OverlayView.IsHitTestVisible = !enabled;
        }
        _settings.ClickThroughEnabled = enabled;

        ApplyOverlayAppearance(enabled);
        UpdateHeaderVisibility(enabled);
        ApplyTopmostSetting();

        if (updateToggle)
        {
            UpdateClickThroughMenuState(enabled);
        }
    }

    private void ApplyOverlayAppearance()
    {
        ApplyOverlayAppearance(_settings.ClickThroughEnabled);
    }

    private void ApplyOverlayAppearance(bool clickThroughActive)
    {
        if (ChromeBorder is null)
        {
            return;
        }

        var baseOpacity = Math.Clamp(_settings.OverlayOpacity, 0.2, 1.0);
        var clickThroughOpacity = Math.Clamp(_settings.ClickThroughOverlayOpacity, 0.1, 1.0);
        var appliedOpacity = clickThroughActive ? clickThroughOpacity : baseOpacity;
        ChromeBorder.Opacity = appliedOpacity;
        _ = ApplyWebContentStylingAsync(clickThroughActive);
    }

    private void ApplyTopmostSetting()
    {
        Topmost = _settings.AlwaysOnTop || _settings.ClickThroughEnabled;
    }

    private void NavigateToTracker()
    {
        var trackerUri = GetTrackerUri();
        OverlayView.Source = trackerUri;
    }

    private Uri GetTrackerUri()
    {
        if (Uri.TryCreate(_settings.TrackerUrl, UriKind.Absolute, out var uri))
        {
            return uri;
        }

        _settings.TrackerUrl = DefaultTrackerUrl;
        _settingsStore.Save(_settings);
        return DefaultTrackerUri;
    }

    private void ApplySettingsFromDialog(UserSettings updated)
    {
        var previousClickThroughState = _settings.ClickThroughEnabled;
        var previousHideAds = _settings.HideAds;

        _settings.ToggleHotkey = updated.ToggleHotkey;
        _settings.ExitHotkey = updated.ExitHotkey;
        _settings.ClickThroughHotkey = updated.ClickThroughHotkey;
        _settings.HideOnLaunch = updated.HideOnLaunch;
        _settings.AlwaysOnTop = updated.AlwaysOnTop;
        _settings.OverlayOpacity = updated.OverlayOpacity;
        _settings.ClickThroughOverlayOpacity = updated.ClickThroughOverlayOpacity;
        _settings.ClickThroughEnabled = updated.ClickThroughEnabled;
        _settings.HideAds = updated.HideAds;
        _settings.HideAds = updated.HideAds;

        RegisterHotkeys();
        NavigateToTracker();
        if (previousClickThroughState != _settings.ClickThroughEnabled)
        {
            SetClickThroughMode(_settings.ClickThroughEnabled);
        }
        else
        {
            ApplyOverlayAppearance();
            UpdateHeaderVisibility(_settings.ClickThroughEnabled);
        }

        ApplyTopmostSetting();
        UpdateClickThroughMenuState(_settings.ClickThroughEnabled);
        _ = ApplyWebContentStylingAsync();

        if (previousHideAds != _settings.HideAds)
        {
            EnsureAdBlockingFilter();
            OverlayView?.Reload();
        }

        _settingsStore.Save(_settings);
    }

    private void UpdateHeaderVisibility(bool clickThroughActive)
    {
        if (HeaderBar is null)
        {
            return;
        }

        HeaderBar.Visibility = clickThroughActive ? Visibility.Collapsed : Visibility.Visible;
    }

    private void EnsureWindowHandle()
    {
        if (_windowHandle != IntPtr.Zero)
        {
            return;
        }

        var helper = new WindowInteropHelper(this);
        _windowHandle = helper.Handle;
        if (_baseExtendedStyle == 0)
        {
            _baseExtendedStyle = NativeWindowMethods.GetWindowLong(_windowHandle, NativeWindowMethods.GWL_EXSTYLE);
        }
    }

    private double GetOverlayOpacity(bool clickThroughActive)
    {
        return clickThroughActive
            ? Math.Clamp(_settings.ClickThroughOverlayOpacity, 0.1, 1.0)
            : Math.Clamp(_settings.OverlayOpacity, 0.2, 1.0);
    }

    private void UpdateClickThroughMenuState(bool isEnabled)
    {
        var menuItem = GetClickThroughMenuItem();
        if (menuItem is null)
        {
            return;
        }

        _suppressMenuToggleEvents = true;
        menuItem.IsChecked = isEnabled;
        menuItem.Header = isEnabled ? "Click-through (On)" : "Click-through (Off)";
        _suppressMenuToggleEvents = false;
    }

    private MenuItem? GetClickThroughMenuItem()
    {
        if (_clickThroughMenuItem is not null)
        {
            return _clickThroughMenuItem;
        }

        if (HeaderMenuButton?.ContextMenu is not ContextMenu menu)
        {
            return null;
        }

        _clickThroughMenuItem = menu.Items.OfType<MenuItem>().FirstOrDefault(item => item.Name == "MenuClickThroughToggle");
        return _clickThroughMenuItem;
    }

    private Task ApplyWebContentStylingAsync()
    {
        return ApplyWebContentStylingAsync(_settings.ClickThroughEnabled);
    }

    private async Task ApplyWebContentStylingAsync(bool clickThroughActive)
    {
        try
        {
            if (OverlayView?.CoreWebView2 is null)
            {
                return;
            }

            var opacity = GetOverlayOpacity(clickThroughActive);
            var opacityText = opacity.ToString(CultureInfo.InvariantCulture);
            var hideAdsFlag = _settings.HideAds ? "true" : "false";
            var hideAdsCss = @"            iframe[src*=""ads"" i],
            iframe[src*=""doubleclick"" i],
            [class*=""ad-container"" i],
            [class*=""banner-ad"" i],
            [id*=""adslot"" i],
            .adsbygoogle {
                display: none !important;
                visibility: hidden !important;
            }";
            var script = $@"
(function() {{
    try {{
        const styleId = 'arcOverlayStyle';
        let style = document.getElementById(styleId);
        if (!style) {{
            style = document.createElement('style');
            style.id = styleId;
            document.head.appendChild(style);
        }}

        style.textContent = `
            html, body, #root, main {{
                background: transparent !important;
            }}
            body {{
                opacity: {opacityText};
                transition: opacity 0.2s ease;
            }}
        `;

        if ({hideAdsFlag}) {{
            style.textContent += `
{hideAdsCss}
            `;
        }}

        document.documentElement.style.background = 'transparent';
        document.body.style.background = 'transparent';
    }} catch (err) {{
        console.warn('overlay style error', err);
    }}
}})();";

            await OverlayView.ExecuteScriptAsync(script);
        }
        catch
        {
            // Styling injection is best-effort; swallow WebView script errors.
        }
    }

    private void EnsureAdBlockingFilter()
    {
        if (_adFilterInitialized || OverlayView?.CoreWebView2 is null)
        {
            return;
        }

        try
        {
            OverlayView.CoreWebView2.AddWebResourceRequestedFilter("*", CoreWebView2WebResourceContext.All);
            OverlayView.CoreWebView2.WebResourceRequested += HandleWebResourceRequested;
            _adFilterInitialized = true;
        }
        catch
        {
            // Ignore filter failures; ad blocking is optional.
        }
    }

    private void HandleWebResourceRequested(object? sender, CoreWebView2WebResourceRequestedEventArgs e)
    {
        if (!_settings.HideAds)
        {
            return;
        }

        if (!Uri.TryCreate(e.Request.Uri, UriKind.Absolute, out var uri))
        {
            return;
        }

        if (!ShouldBlockRequest(uri))
        {
            return;
        }

        if (OverlayView?.CoreWebView2?.Environment is null)
        {
            return;
        }

        try
        {
            e.Response = OverlayView.CoreWebView2.Environment.CreateWebResourceResponse(Stream.Null, 204, "No Content", "Cache-Control: no-cache");
        }
        catch
        {
            // Swallow failures; worst case the ad loads.
        }
    }

    private static bool ShouldBlockRequest(Uri uri)
    {
        var host = uri.Host.ToLowerInvariant();
        if (AdHostKeywords.Any(host.Contains))
        {
            return true;
        }

        var path = uri.AbsolutePath.ToLowerInvariant();
        if (AdPathKeywords.Any(path.Contains))
        {
            return true;
        }

        var query = uri.Query.ToLowerInvariant();
        return AdPathKeywords.Any(query.Contains);
    }

    private void HandleNavigationCompleted(object? sender, CoreWebView2NavigationCompletedEventArgs e)
    {
        if (!e.IsSuccess || OverlayView?.CoreWebView2 is null)
        {
            return;
        }

        if (!Uri.TryCreate(OverlayView.CoreWebView2.Source, UriKind.Absolute, out var currentUri))
        {
            return;
        }

        TryUpdateTrackerLocale(currentUri);
    }

    private void HandleSourceChanged(object? sender, CoreWebView2SourceChangedEventArgs e)
    {
        var source = OverlayView?.CoreWebView2?.Source;
        if (string.IsNullOrWhiteSpace(source))
        {
            return;
        }

        if (Uri.TryCreate(source, UriKind.Absolute, out var uri))
        {
            TryUpdateTrackerLocale(uri);
        }
    }

    private void TryUpdateTrackerLocale(Uri uri)
    {
        if (!IsTrackerDomain(uri))
        {
            return;
        }

        var hasLanguage = TryGetLanguageSegment(uri, out var segment);
        var normalizedUrl = hasLanguage
            ? $"{DefaultTrackerUrl}{segment}"
            : DefaultTrackerUrl;

        if (string.Equals(_settings.TrackerUrl, normalizedUrl, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        _settings.TrackerUrl = normalizedUrl;
        _settingsStore.Save(_settings);
    }

    private static bool IsTrackerDomain(Uri uri)
    {
        return string.Equals(uri.Host, TrackerHost, StringComparison.OrdinalIgnoreCase);
    }

    private static bool TryGetLanguageSegment(Uri uri, out string segment)
    {
        var firstSegment = uri.AbsolutePath
            .Split('/', StringSplitOptions.RemoveEmptyEntries)
            .FirstOrDefault();

        if (!string.IsNullOrWhiteSpace(firstSegment) && IsLanguageSegment(firstSegment))
        {
            segment = firstSegment.ToLowerInvariant();
            return true;
        }

        segment = string.Empty;
        return false;
    }

    private static bool IsLanguageSegment(string? value)
    {
        return !string.IsNullOrWhiteSpace(value)
            && value.Length == 2
            && value.All(char.IsLetter);
    }

    private async Task CheckForUpdatesAsync()
    {
        try
        {
            var currentVersion = GetCurrentAppVersion();
            var result = await _updateService.CheckForUpdatesAsync(currentVersion, CancellationToken.None).ConfigureAwait(false);

            switch (result.Status)
            {
                case UpdateCheckStatus.Downloaded:
                case UpdateCheckStatus.AlreadyDownloaded:
                    PersistLatestDownloadedVersion(result.LatestVersion);
                    await Dispatcher.InvokeAsync(() => HandleDownloadedUpdate(result));
                    _logger.Log("UpdateCheck", $"Update available: {result.LatestVersion}; file={result.DownloadedFile}");
                    break;
                case UpdateCheckStatus.UpToDate:
                    _logger.Log("UpdateCheck", "No updates available.");
                    break;
                case UpdateCheckStatus.Failed:
                    _logger.Log("UpdateCheck", $"Update check failed: {result.ErrorMessage}");
                    break;
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Update check failed: {ex}");
            _logger.Log("UpdateCheck", $"Unhandled exception: {ex.Message}");
        }
    }

    private void PersistLatestDownloadedVersion(Version? version)
    {
        if (version is null)
        {
            return;
        }

        var versionString = version.ToString();
        if (string.Equals(_settings.LastDownloadedVersion, versionString, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        _settings.LastDownloadedVersion = versionString;
        _settingsStore.Save(_settings);
    }

    private void HandleDownloadedUpdate(UpdateCheckResult result)
    {
        if (string.IsNullOrWhiteSpace(result.DownloadedFile))
        {
            return;
        }

        var extractionPath = TryExtractUpdatePackage(result.DownloadedFile, result.LatestVersion);
        if (extractionPath is null)
        {
            ShowManualUpdateMessage(result);
            return;
        }

        var versionLabel = result.LatestVersion?.ToString() ?? "latest";
        _logger.Log("UpdateCheck", $"Update {versionLabel} downloaded; scheduling automatic install.");
        MessageBox.Show(this,
            $"ArcRaidersHelper will close briefly to apply update {versionLabel}. It will reopen automatically when finished.",
            "Applying ArcRaidersHelper update",
            MessageBoxButton.OK,
            MessageBoxImage.Information);
        StartUpdateInstallerProcess(extractionPath, result.DownloadedFile, versionLabel);
    }

    private string? TryExtractUpdatePackage(string packagePath, Version? version)
    {
        try
        {
            var versionLabel = version?.ToString() ?? "latest";
            var extractionRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ArcRaidersHelper", "updates", "extracted");
            Directory.CreateDirectory(extractionRoot);
            var destination = Path.Combine(extractionRoot, versionLabel);

            if (Directory.Exists(destination))
            {
                Directory.Delete(destination, recursive: true);
            }

            ZipFile.ExtractToDirectory(packagePath, destination);
            return destination;
        }
        catch (Exception ex)
        {
            _logger.Log("UpdateCheck", $"Failed to extract update package: {ex.Message}");
            return null;
        }
    }

    private void ShowManualUpdateMessage(UpdateCheckResult result)
    {
        var versionLabel = result.LatestVersion?.ToString() ?? "new";
        var path = result.DownloadedFile ?? "<unknown>";
        var message =
            $"A newer version of ArcRaidersHelper ({versionLabel}) has been downloaded to:\n{path}\n\nClose the overlay and run the downloaded package to finish installing the update.";

        MessageBox.Show(this,
            message,
            "ArcRaidersHelper update ready",
            MessageBoxButton.OK,
            MessageBoxImage.Information);
    }

    private void StartUpdateInstallerProcess(string extractionPath, string packagePath, string? versionLabel)
    {
        try
        {
            var launchInfo = GetSelfLaunchInfo();
            var targetDirectory = AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var options = new UpdateScriptOptions
            {
                SourceDirectory = extractionPath,
                TargetDirectory = targetDirectory,
                ParentProcessId = Environment.ProcessId,
                LauncherPath = launchInfo.FileName,
                LauncherArguments = launchInfo.Arguments,
                LogFilePath = LoggerFactory.GetLogFilePath()
            };

            var scriptPath = UpdateInstaller.LaunchUpdateScript(options);
            _logger.Log("UpdateCheck", $"Started update installer script at {scriptPath} for version {versionLabel ?? "unknown"}.");
            Application.Current.Dispatcher.Invoke(Application.Current.Shutdown);
        }
        catch (Exception ex)
        {
            _logger.Log("UpdateCheck", $"Failed to start update installer: {ex.Message}");
            ShowManualUpdateMessage(new UpdateCheckResult(UpdateCheckStatus.Downloaded, null, packagePath, ex.Message));
        }
    }

    private static Version GetCurrentAppVersion()
    {
        var assembly = Assembly.GetEntryAssembly() ?? Assembly.GetExecutingAssembly();
        return ParseVersionString(assembly?.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion)
            ?? assembly?.GetName().Version
            ?? new Version(0, 0, 0, 0);
    }

    private static Version? ParseVersionString(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var normalized = value.Split('+')[0].Trim();
        if (normalized.StartsWith('v') || normalized.StartsWith('V'))
        {
            normalized = normalized[1..];
        }

        return Version.TryParse(normalized, out var parsed)
            ? parsed
            : null;
    }

    private static string? TryLoadGitHubToken()
    {
        try
        {
            var baseDirectory = AppContext.BaseDirectory;
            var candidatePath = Path.Combine(baseDirectory, "github_token.txt");
            if (!File.Exists(candidatePath))
            {
                return null;
            }

            foreach (var line in File.ReadLines(candidatePath))
            {
                var trimmed = line.Trim();
                if (string.IsNullOrWhiteSpace(trimmed) || trimmed.StartsWith('#'))
                {
                    continue;
                }

                return trimmed;
            }

            return null;
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static ProcessLaunchInfo GetSelfLaunchInfo()
    {
        var processPath = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(processPath))
        {
            throw new InvalidOperationException("Unable to determine the current process path.");
        }

        if (processPath.EndsWith("dotnet.exe", StringComparison.OrdinalIgnoreCase))
        {
            var entryAssembly = Assembly.GetEntryAssembly()?.Location;
            if (string.IsNullOrWhiteSpace(entryAssembly))
            {
                throw new InvalidOperationException("Unable to determine the ArcRaidersHelper assembly path.");
            }

            return new ProcessLaunchInfo(processPath, $"\"{entryAssembly}\"");
        }

        return new ProcessLaunchInfo(processPath, string.Empty);
    }

    private sealed record ProcessLaunchInfo(string FileName, string Arguments);

    private static bool IsDevelopmentEnvironment()
    {
        var baseDirectory = AppContext.BaseDirectory ?? string.Empty;
        return baseDirectory.Contains(Path.Combine("bin", "Debug"), StringComparison.OrdinalIgnoreCase)
            || baseDirectory.Contains(Path.Combine("bin", "Release"), StringComparison.OrdinalIgnoreCase);
    }


    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == NativeWindowMethods.WM_NCHITTEST)
        {
            return HandleHitTest(lParam, ref handled);
        }

        return IntPtr.Zero;
    }

    private IntPtr HandleHitTest(IntPtr lParam, ref bool handled)
    {
        if (_settings.ClickThroughEnabled)
        {
            handled = true;
            return new IntPtr(NativeWindowMethods.HTTRANSPARENT);
        }

        if (WindowState == WindowState.Maximized)
        {
            return IntPtr.Zero;
        }

        var screenPoint = GetPointFromLParam(lParam);
        var relative = PointFromScreen(screenPoint);
        var width = ActualWidth;
        var height = ActualHeight;

        if (width <= 0 || height <= 0)
        {
            return IntPtr.Zero;
        }

        var border = ResizeBorderThickness;
        var onLeft = relative.X <= border;
        var onRight = relative.X >= width - border;
        var onTop = relative.Y <= border;
        var onBottom = relative.Y >= height - border;

        IntPtr result = IntPtr.Zero;
        if (onTop && onLeft)
        {
            result = new IntPtr(NativeWindowMethods.HTTOPLEFT);
        }
        else if (onTop && onRight)
        {
            result = new IntPtr(NativeWindowMethods.HTTOPRIGHT);
        }
        else if (onBottom && onLeft)
        {
            result = new IntPtr(NativeWindowMethods.HTBOTTOMLEFT);
        }
        else if (onBottom && onRight)
        {
            result = new IntPtr(NativeWindowMethods.HTBOTTOMRIGHT);
        }
        else if (onLeft)
        {
            result = new IntPtr(NativeWindowMethods.HTLEFT);
        }
        else if (onRight)
        {
            result = new IntPtr(NativeWindowMethods.HTRIGHT);
        }
        else if (onTop)
        {
            result = new IntPtr(NativeWindowMethods.HTTOP);
        }
        else if (onBottom)
        {
            result = new IntPtr(NativeWindowMethods.HTBOTTOM);
        }

        if (result != IntPtr.Zero)
        {
            handled = true;
        }

        return result;
    }

    private Point GetPointFromLParam(IntPtr lParam)
    {
        var value = unchecked((long)lParam);
        var x = (short)(value & 0xFFFF);
        var y = (short)((value >> 16) & 0xFFFF);
        return new Point(x, y);
    }

    private static class NativeWindowMethods
    {
        internal const int GWL_EXSTYLE = -20;
        internal const int WS_EX_TRANSPARENT = 0x00000020;
        internal const int WS_EX_LAYERED = 0x00080000;
        internal const int WM_NCHITTEST = 0x0084;
        internal const int HTTRANSPARENT = -1;
        internal const int HTLEFT = 10;
        internal const int HTRIGHT = 11;
        internal const int HTTOP = 12;
        internal const int HTTOPLEFT = 13;
        internal const int HTTOPRIGHT = 14;
        internal const int HTBOTTOM = 15;
        internal const int HTBOTTOMLEFT = 16;
        internal const int HTBOTTOMRIGHT = 17;

        [DllImport("user32.dll")]
        internal static extern int GetWindowLong(IntPtr hWnd, int nIndex);

        [DllImport("user32.dll")]
        internal static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);
    }
}
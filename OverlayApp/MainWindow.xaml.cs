using Microsoft.Web.WebView2.Core;
using OverlayApp.Infrastructure;
using OverlayApp.Properties;
using System;
using System.ComponentModel;
using System.Globalization;
using System.Linq;
using System.Runtime.InteropServices;
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

    private static readonly HotkeyDefinition DefaultToggleHotkey = new(ModifierKeys.Control | ModifierKeys.Alt, Key.O);
    private static readonly HotkeyDefinition DefaultExitHotkey = new(ModifierKeys.Control | ModifierKeys.Alt | ModifierKeys.Shift, Key.O);
    private static readonly HotkeyDefinition DefaultClickThroughHotkey = new(ModifierKeys.Control | ModifierKeys.Alt, Key.T);

    private readonly UserSettingsStore _settingsStore = new();
    private readonly UserSettings _settings;
    private GlobalHotkeyManager? _hotkeyManager;
    private bool _isOverlayVisible = true;
    private MenuItem? _clickThroughMenuItem;
    private bool _suppressMenuToggleEvents;
    private IntPtr _windowHandle;
    private HwndSource? _hwndSource;
    private int _baseExtendedStyle;
    private HotkeyDefinition _toggleHotkey = DefaultToggleHotkey;
    private HotkeyDefinition _exitHotkey = DefaultExitHotkey;
    private HotkeyDefinition _clickThroughHotkey = DefaultClickThroughHotkey;

    public MainWindow()
    {
        InitializeComponent();
        _settings = _settingsStore.Load();
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
        _hotkeyManager?.Dispose();
        _hwndSource?.RemoveHook(WndProc);
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
        if (string.IsNullOrWhiteSpace(_settings.TrackerUrl))
        {
            _settings.TrackerUrl = "https://arctracker.io";
        }

        if (Uri.TryCreate(_settings.TrackerUrl, UriKind.Absolute, out var uri))
        {
            OverlayView.Source = uri;
        }
    }

    private void ApplySettingsFromDialog(UserSettings updated)
    {
        var previousClickThroughState = _settings.ClickThroughEnabled;

        _settings.ToggleHotkey = updated.ToggleHotkey;
        _settings.ExitHotkey = updated.ExitHotkey;
        _settings.ClickThroughHotkey = updated.ClickThroughHotkey;
        _settings.HideOnLaunch = updated.HideOnLaunch;
        _settings.AlwaysOnTop = updated.AlwaysOnTop;
        _settings.OverlayOpacity = updated.OverlayOpacity;
        _settings.ClickThroughOverlayOpacity = updated.ClickThroughOverlayOpacity;
        _settings.TrackerUrl = updated.TrackerUrl;
        _settings.ClickThroughEnabled = updated.ClickThroughEnabled;

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
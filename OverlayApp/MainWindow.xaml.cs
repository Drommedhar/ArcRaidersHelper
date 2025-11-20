using OverlayApp.Data;
using OverlayApp.Infrastructure;
using OverlayApp.Progress;
using OverlayApp.Properties;
using OverlayApp.Services;
using OverlayApp.ViewModels;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
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
using Application = System.Windows.Application;
using Button = System.Windows.Controls.Button;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;
using MessageBox = System.Windows.MessageBox;
using Point = System.Windows.Point;

namespace OverlayApp;

public partial class MainWindow : Window
{
    private const int ToggleHotkeyId = 1;
    private const int ExitHotkeyId = 2;
    private const int ClickThroughHotkeyId = 3;
    private const double ResizeBorderThickness = 12d;
    private const double MinimumWindowWidth = 400d;
    private const double MinimumWindowHeight = 300d;
    private const double NavigationWidth = 240d;

    private static readonly HotkeyDefinition DefaultToggleHotkey = new(ModifierKeys.Control | ModifierKeys.Alt, Key.O);
    private static readonly HotkeyDefinition DefaultExitHotkey = new(ModifierKeys.Control | ModifierKeys.Alt | ModifierKeys.Shift, Key.O);
    private static readonly HotkeyDefinition DefaultClickThroughHotkey = new(ModifierKeys.Control | ModifierKeys.Alt, Key.T);

    private readonly UserSettingsStore _settingsStore = new();
    private readonly UserSettings _settings;
    private readonly UpdateService _updateService;
    private readonly ILogger _logger;
    private readonly ArcDataSyncService _dataSyncService;
    private readonly CancellationTokenSource _dataSyncCts = new();
    private readonly UserProgressStore _progressStore;
    private readonly ProgressCalculator _progressCalculator = new();
    private readonly MainViewModel _viewModel;
    private readonly GameCaptureService _gameCaptureService;
    private readonly QuestDetectionService _questDetectionService;
    private readonly SemaphoreSlim _questDetectionGate = new(1, 1);
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
    private ArcDataSnapshot? _arcData;
    private UserProgressState? _userProgress;
    private ProgressReport? _progressReport;
    private bool _firstCaptureFrameLogged;
    private bool _captureExclusionApplied;
    private bool _autoCaptureActive;

    public MainWindow()
    {
        InitializeComponent();
        _logger = LoggerFactory.CreateDefaultLogger();
        var githubToken = TryLoadGitHubToken();
        _updateService = new UpdateService(githubToken, _logger);
        _dataSyncService = new ArcDataSyncService(githubToken, _logger);
        _progressStore = new UserProgressStore(_logger);
        _progressStore.ProgressChanged += OnProgressChanged;
        _settings = _settingsStore.Load();
        _viewModel = new MainViewModel(_progressStore, _logger);
        _gameCaptureService = new GameCaptureService(_logger);
        _gameCaptureService.FrameCaptured += OnGameFrameCaptured;
        _questDetectionService = new QuestDetectionService(_gameCaptureService, _logger);
        _questDetectionService.QuestsDetected += OnQuestsDetected;
        DataContext = _viewModel;
    }

    private void OnProgressChanged(object? sender, UserProgressState newState)
    {
        // This event is invoked from a background thread by UserProgressStore.
        // We perform the heavy calculation here, off the UI thread.
        if (_arcData == null)
        {
            return;
        }

        try
        {
            var report = _progressCalculator.Calculate(newState, _arcData);

            // Only dispatch the lightweight UI update
            Dispatcher.Invoke(() =>
            {
                _userProgress = newState;
                _progressReport = report;
                _viewModel.UpdateData(_arcData, _userProgress, _progressReport);
                _logger.Log("Progress", "Progress updated from file change.");
            });
        }
        catch (Exception ex)
        {
            _logger.Log("Progress", $"Failed to recalculate progress: {ex.Message}");
        }
    }

    private void OnGameFrameCaptured(object? sender, GameFrameCapturedEventArgs e)
    {
        if (_firstCaptureFrameLogged)
        {
            return;
        }

        _firstCaptureFrameLogged = true;
        _logger.Log("GameCapture", $"Receiving frames at {e.Frame.Width}x{e.Frame.Height}; debug dump: {_gameCaptureService.LatestFramePath}");
    }

    private void OnQuestsDetected(object? sender, QuestDetectionEventArgs e)
    {
        _logger.Log("QuestDetection", $"Detection event with {e.Matches.Count} stabilized match(es).");
        _ = HandleQuestDetectionsAsync(e.Matches);
    }

    private async Task HandleQuestDetectionsAsync(IReadOnlyList<QuestDetectionMatch> matches)
    {
        if (matches == null || matches.Count == 0)
        {
            return;
        }

        if (_arcData?.Quests is null || _userProgress is null)
        {
            return;
        }

        await _questDetectionGate.WaitAsync().ConfigureAwait(false);
        try
        {
            var stateChanges = await Dispatcher.InvokeAsync(() => ApplyAutoCompletion(matches));
            if (stateChanges == null || stateChanges.Count == 0)
            {
                _logger.Log("QuestDetection", "No quest state changes were required for this detection event.");
                return;
            }

            await _progressStore.SaveAsync(_userProgress, CancellationToken.None).ConfigureAwait(false);
            var report = _progressCalculator.Calculate(_userProgress, _arcData);
            _progressReport = report;

            _logger.Log("QuestDetection", $"Persisted quest state updates for {stateChanges.Count} quest entry/entries.");

            await Dispatcher.InvokeAsync(() =>
            {
                _viewModel.UpdateData(_arcData, _userProgress, _progressReport);
            });
        }
        catch (Exception ex)
        {
            _logger.Log("QuestDetection", $"Failed to apply quest detection: {ex.Message}");
        }
        finally
        {
            _questDetectionGate.Release();
        }
    }

    private List<QuestStateChange> ApplyAutoCompletion(IReadOnlyList<QuestDetectionMatch> matches)
    {
        var updated = new List<QuestStateChange>();
        if (_arcData?.Quests is null || _userProgress is null)
        {
            return updated;
        }

        foreach (var match in matches)
        {
            _logger.Log("QuestDetection", $"Detected quest '{match.DisplayName}' ({match.QuestId}) on screen (confidence {match.Confidence:F2}).");
            MarkQuestProgressFromDetection(match.QuestId, updated);
        }

        MarkImplicitCompletions(matches, updated);

        return updated;
    }

    private void MarkQuestProgressFromDetection(string questId, List<QuestStateChange> updatedEntries)
    {
        if (_arcData?.Quests is null || _userProgress is null)
        {
            return;
        }

        var stack = new Stack<(string QuestId, bool IsDetected)>();
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        stack.Push((questId, true));

        while (stack.Count > 0)
        {
            var (current, isDetectedQuest) = stack.Pop();
            if (!visited.Add(current))
            {
                continue;
            }

            if (!_arcData.Quests.TryGetValue(current, out var definition))
            {
                continue;
            }

            var questState = _userProgress.Quests.FirstOrDefault(q => q.QuestId.Equals(current, StringComparison.OrdinalIgnoreCase));
            if (questState is null)
            {
                questState = new QuestProgressState { QuestId = current };
                _userProgress.Quests.Add(questState);
            }

            if (!isDetectedQuest && questState.Status != QuestProgressStatus.Completed)
            {
                questState.Status = QuestProgressStatus.Completed;
                updatedEntries.Add(new QuestStateChange(current, QuestProgressStatus.Completed));
                _logger.Log("QuestDetection", $"Marked prerequisite quest '{current}' as completed.");
            }

            if (definition.PreviousQuestIds is null)
            {
                continue;
            }

            foreach (var previous in definition.PreviousQuestIds)
            {
                if (!string.IsNullOrWhiteSpace(previous))
                {
                    stack.Push((previous, false));
                }
            }
        }
    }

    private void MarkImplicitCompletions(IReadOnlyList<QuestDetectionMatch> matches, List<QuestStateChange> updatedEntries)
    {
        if (_arcData?.Quests is null || _userProgress is null)
        {
            return;
        }

        var detectedIds = new HashSet<string>(matches.Select(m => m.QuestId), StringComparer.OrdinalIgnoreCase);
        var completedIds = new HashSet<string>(
            _userProgress.Quests
                .Where(q => q.Status == QuestProgressStatus.Completed)
                .Select(q => q.QuestId),
            StringComparer.OrdinalIgnoreCase);

        // Also include any we just marked in updatedEntries
        foreach (var update in updatedEntries)
        {
            if (update.NewStatus == QuestProgressStatus.Completed)
            {
                completedIds.Add(update.QuestId);
            }
        }

        bool changed = true;
        while (changed)
        {
            changed = false;
            foreach (var kvp in _arcData.Quests)
            {
                var questId = kvp.Key;
                if (completedIds.Contains(questId) || detectedIds.Contains(questId))
                {
                    continue;
                }

                if (AreAllPrerequisitesMet(questId, completedIds))
                {
                    MarkQuestAsCompleted(questId, updatedEntries);
                    completedIds.Add(questId);
                    changed = true;
                }
            }
        }
    }

    private bool AreAllPrerequisitesMet(string questId, HashSet<string> completedIds)
    {
        if (_arcData?.Quests is null || !_arcData.Quests.TryGetValue(questId, out var def))
        {
            return false;
        }

        if (def.PreviousQuestIds is null || def.PreviousQuestIds.Count == 0)
        {
            return true;
        }

        foreach (var prev in def.PreviousQuestIds)
        {
            if (!string.IsNullOrWhiteSpace(prev) && !completedIds.Contains(prev))
            {
                return false;
            }
        }

        return true;
    }

    private void MarkQuestAsCompleted(string questId, List<QuestStateChange> updatedEntries)
    {
        if (_userProgress is null)
        {
            return;
        }

        var questState = _userProgress.Quests.FirstOrDefault(q => q.QuestId.Equals(questId, StringComparison.OrdinalIgnoreCase));
        if (questState is null)
        {
            questState = new QuestProgressState { QuestId = questId };
            _userProgress.Quests.Add(questState);
        }

        if (questState.Status != QuestProgressStatus.Completed)
        {
            questState.Status = QuestProgressStatus.Completed;
            updatedEntries.Add(new QuestStateChange(questId, QuestProgressStatus.Completed));
            _logger.Log("QuestDetection", $"Implicitly marked quest '{questId}' as completed (prereqs met, not on screen).");
        }
    }

    private sealed record QuestStateChange(string QuestId, QuestProgressStatus NewStatus);

    private async void OnWindowLoaded(object sender, RoutedEventArgs e)
    {
        LocalizationService.Instance.CurrentLanguage = _settings.Language;
        ApplyOverlayAppearance();
        ApplyTopmostSetting();
        UpdateClickThroughMenuState(_settings.ClickThroughEnabled);
        UpdateHeaderVisibility(_settings.ClickThroughEnabled);
        _viewModel.SetStatus(LocalizationService.Instance["Status_Syncing"], true);
        try
        {
            await InitializeArcDataAsync(_dataSyncCts.Token);
        }
        catch (OperationCanceledException)
        {
            // Window closing; ignore.
        }

        if (_settings.HideOnLaunch)
        {
            HideOverlay();
        }
        else
        {
            ShowOverlay();
        }

        _ = CheckForUpdatesAsync();
        UpdateAutoCaptureState(_settings.AutoCaptureEnabled);
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
        ApplyCaptureExclusion();
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
        _dataSyncCts.Cancel();
        _dataSyncService.Dispose();
        _dataSyncCts.Dispose();
        _progressStore.Dispose();
        _updateService.Dispose();
        _questDetectionService.QuestsDetected -= OnQuestsDetected;
        _questDetectionService.Dispose();
        UpdateAutoCaptureState(false);
        _gameCaptureService.FrameCaptured -= OnGameFrameCaptured;
        _gameCaptureService.Dispose();
        if (_captureExclusionApplied)
        {
            DisplayAffinityHelper.ClearAffinity(_windowHandle, _logger);
            _captureExclusionApplied = false;
        }
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

    private async void OnReloadRequested(object sender, RoutedEventArgs e)
    {
        try
        {
            await RefreshArcDataAsync(forceDownload: true);
        }
        catch (OperationCanceledException)
        {
            // Window closing; ignore.
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

        if (_settings.ClickThroughEnabled)
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

        if (_settings.ClickThroughEnabled)
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

    private Task InitializeArcDataAsync(CancellationToken cancellationToken)
    {
        return SynchronizeArcDataAsync(initialLoad: true, forceDownload: false, cancellationToken);
    }

    private Task RefreshArcDataAsync(bool forceDownload)
    {
        return SynchronizeArcDataAsync(initialLoad: false, forceDownload, _dataSyncCts.Token);
    }

    private async Task SynchronizeArcDataAsync(bool initialLoad, bool forceDownload, CancellationToken cancellationToken)
    {
        try
        {
            await Dispatcher.InvokeAsync(() => _viewModel.SetStatus(LocalizationService.Instance["Status_Syncing"], true));
            var snapshot = initialLoad
                ? await _dataSyncService.InitializeAsync(cancellationToken).ConfigureAwait(false)
                : await _dataSyncService.RefreshAsync(forceDownload, cancellationToken).ConfigureAwait(false);

            _arcData = snapshot;
            _questDetectionService.UpdateArcData(snapshot);
            if (_autoCaptureActive)
            {
                _questDetectionService.SetEnabled(_settings.QuestDetectionEnabled);
            }
            _logger.Log("DataSync", $"Arc data synchronized ({snapshot.CommitSha ?? "unknown"}); items={snapshot.Items.Count}, projects={snapshot.Projects.Count}.");
            await InitializeProgressAsync(snapshot, cancellationToken).ConfigureAwait(false);

            await Dispatcher.InvokeAsync(() =>
            {
                _viewModel.UpdateData(_arcData, _userProgress, _progressReport);
                var label = snapshot.LastSyncedUtc == DateTimeOffset.MinValue
                    ? LocalizationService.Instance["Status_Synced"]
                    : string.Format(LocalizationService.Instance["Status_SyncedTime"], snapshot.LastSyncedUtc.ToLocalTime().ToString("g"));
                _viewModel.SetStatus(label, false);
            });
        }
        catch (OperationCanceledException)
        {
            _logger.Log("DataSync", "Arc data sync canceled.");
            throw;
        }
        catch (Exception ex)
        {
            _logger.Log("DataSync", $"Arc data sync failed: {ex.Message}");
            await Dispatcher.InvokeAsync(() => _viewModel.SetStatus(LocalizationService.Instance["Status_SyncFailed"], false));
        }
    }

    private async Task InitializeProgressAsync(ArcDataSnapshot snapshot, CancellationToken cancellationToken)
    {
        try
        {
            _userProgress = await _progressStore.LoadAsync(cancellationToken).ConfigureAwait(false);
            _progressReport = _progressCalculator.Calculate(_userProgress, snapshot);
            _logger.Log("Progress", $"Loaded {_progressReport.ActiveQuests.Count} active quests; {_progressReport.NeededItems.Count} item types missing.");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.Log("Progress", $"Failed to load progress: {ex.Message}");
        }
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

        var navigationPanel = FindName("NavigationPanel") as Border;
        var navigationColumn = FindName("NavigationColumn") as ColumnDefinition;

        if (navigationPanel is not null && navigationColumn is not null)
        {
            if (enabled && navigationPanel.Visibility == Visibility.Visible)
            {
                navigationPanel.Visibility = Visibility.Collapsed;
                navigationColumn.Width = new GridLength(0);
                Left += NavigationWidth;
                Width -= NavigationWidth;
            }
            else if (!enabled && navigationPanel.Visibility == Visibility.Collapsed)
            {
                navigationPanel.Visibility = Visibility.Visible;
                navigationColumn.Width = new GridLength(NavigationWidth);
                Left -= NavigationWidth;
                Width += NavigationWidth;
            }
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
    }

    private void ApplyTopmostSetting()
    {
        Topmost = _settings.AlwaysOnTop || _settings.ClickThroughEnabled;
    }

    private void ApplySettingsFromDialog(UserSettings updated)
    {
        var previousClickThroughState = _settings.ClickThroughEnabled;
        var previousLanguage = _settings.Language;
        var previousAutoCaptureState = _settings.AutoCaptureEnabled;

        _settings.ToggleHotkey = updated.ToggleHotkey;
        _settings.ExitHotkey = updated.ExitHotkey;
        _settings.ClickThroughHotkey = updated.ClickThroughHotkey;
        _settings.HideOnLaunch = updated.HideOnLaunch;
        _settings.AlwaysOnTop = updated.AlwaysOnTop;
        _settings.OverlayOpacity = updated.OverlayOpacity;
        _settings.ClickThroughOverlayOpacity = updated.ClickThroughOverlayOpacity;
        _settings.ClickThroughEnabled = updated.ClickThroughEnabled;
        _settings.AutoCaptureEnabled = updated.AutoCaptureEnabled;
        _settings.QuestDetectionEnabled = updated.QuestDetectionEnabled;
        _settings.ProjectDetectionEnabled = updated.ProjectDetectionEnabled;
        _settings.HideoutDetectionEnabled = updated.HideoutDetectionEnabled;
        _settings.Language = updated.Language;

        RegisterHotkeys();
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
        if (previousAutoCaptureState != _settings.AutoCaptureEnabled)
        {
            UpdateAutoCaptureState(_settings.AutoCaptureEnabled);
        }

        _settingsStore.Save(_settings);

        if (!string.Equals(previousLanguage, _settings.Language, StringComparison.OrdinalIgnoreCase))
        {
            LocalizationService.Instance.CurrentLanguage = _settings.Language;
            if (_arcData != null && _userProgress != null)
            {
                _progressReport = _progressCalculator.Calculate(_userProgress, _arcData);
            }
            _viewModel.UpdateData(_arcData, _userProgress, _progressReport);
        }
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

    private void UpdateClickThroughMenuState(bool isEnabled)
    {
        var menuItem = GetClickThroughMenuItem();
        if (menuItem is null)
        {
            return;
        }

        _suppressMenuToggleEvents = true;
        menuItem.IsChecked = isEnabled;
        menuItem.Header = isEnabled 
            ? LocalizationService.Instance["Menu_ClickThroughOn"] 
            : LocalizationService.Instance["Menu_ClickThroughOff"];
        _suppressMenuToggleEvents = false;
    }

    private void UpdateAutoCaptureState(bool enable)
    {
        if (enable)
        {
            if (_autoCaptureActive)
            {
                _questDetectionService.SetEnabled(_arcData is not null);
                return;
            }

            try
            {
                _gameCaptureService.Start();
                _autoCaptureActive = true;
                _logger.Log("GameCapture", "Auto capture enabled.");
                _questDetectionService.SetEnabled(_arcData is not null && _settings.QuestDetectionEnabled);
            }
            catch (Exception ex)
            {
                _logger.Log("GameCapture", $"Failed to start auto capture: {ex.Message}");
                MessageBox.Show(this,
                    LocalizationService.Instance["Settings_AutoCaptureStartFailed"],
                    LocalizationService.Instance["App_Title"],
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
                _settings.AutoCaptureEnabled = false;
                _questDetectionService.SetEnabled(false);
            }
        }
        else
        {
            if (!_autoCaptureActive)
            {
                _questDetectionService.SetEnabled(false);
                return;
            }

            try
            {
                _gameCaptureService.Stop();
            }
            finally
            {
                _autoCaptureActive = false;
                _logger.Log("GameCapture", "Auto capture disabled.");
                _questDetectionService.SetEnabled(false);
            }
        }
    }

    private void ApplyCaptureExclusion()
    {
        if (_windowHandle == IntPtr.Zero || _captureExclusionApplied)
        {
            return;
        }

        _captureExclusionApplied = DisplayAffinityHelper.TryExcludeFromCapture(_windowHandle, _logger);
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
            string.Format(LocalizationService.Instance["Update_Message"], versionLabel),
            LocalizationService.Instance["Update_Applying"],
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
        var message = string.Format(LocalizationService.Instance["Update_ReadyMessage"], versionLabel, path);

        MessageBox.Show(this,
            message,
            LocalizationService.Instance["Update_ReadyTitle"],
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
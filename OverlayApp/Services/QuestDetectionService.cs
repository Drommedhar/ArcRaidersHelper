using OverlayApp.Data;
using OverlayApp.Infrastructure;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Windows.Globalization;
using Windows.Media.Ocr;
using Windows.Graphics.Imaging;

namespace OverlayApp.Services;

internal sealed class QuestDetectionService : IDisposable
{
    private static readonly NormalizedRectangle QuestListRegion = new(0.022, 0.49, 0.26, 0.48);
    private static readonly NormalizedRectangle PlayButtonRegion = new(0.60, 0.70, 0.25, 0.12);
    private static readonly NormalizedRectangle PlayButtonCoreRegion = new(0.66, 0.74, 0.16, 0.08);
    private static readonly NormalizedRectangle QuestSidebarHighlightRegion = new(0.03, 0.14, 0.24, 0.24);
    private static readonly NormalizedRectangle QuestListDarkRegion = new(0.03, 0.57, 0.24, 0.28);
    private readonly GameCaptureService _captureService;
    private readonly ILogger _logger;
    private readonly SemaphoreSlim _analysisGate = new(1, 1);
    private readonly Dictionary<string, StableDetection> _stableMatches = new(StringComparer.OrdinalIgnoreCase);
    private readonly TimeSpan _analysisInterval = TimeSpan.FromSeconds(1.2);
    private readonly int _stabilityThreshold = 2;
    private readonly TimeSpan _statusLogInterval = TimeSpan.FromSeconds(10);
    private DateTimeOffset _lastAnalysis = DateTimeOffset.MinValue;
    private DateTimeOffset _lastNotPlayLog = DateTimeOffset.MinValue;
    private DateTimeOffset _lastNoMatchLog = DateTimeOffset.MinValue;
    private DateTimeOffset _lastUnmatchedLog = DateTimeOffset.MinValue;
    private QuestNameMatcher _matcher = QuestNameMatcher.Empty;
    private OcrEngine? _ocrEngine;
    private bool _enabled;
    private bool _disposed;

    public QuestDetectionService(GameCaptureService captureService, ILogger logger)
    {
        _captureService = captureService;
        _logger = logger;
        InitializeOcrEngine();
        _captureService.FrameCaptured += OnFrameCaptured;
    }

    public event EventHandler<QuestDetectionEventArgs>? QuestsDetected;

    public void UpdateArcData(ArcDataSnapshot? snapshot)
    {
        _matcher = QuestNameMatcher.FromSnapshot(snapshot);
        _stableMatches.Clear();
    }

    public void SetEnabled(bool enabled)
    {
        if (_enabled == enabled)
        {
            return;
        }

        _enabled = enabled;
        if (!enabled)
        {
            _stableMatches.Clear();
            _logger.Log("QuestDetection", "Quest detection disabled.");
        }
        else
        {
            _logger.Log("QuestDetection", "Quest detection enabled.");
        }
    }

    private void InitializeOcrEngine()
    {
        try
        {
            _ocrEngine = OcrEngine.TryCreateFromUserProfileLanguages();
            if (_ocrEngine != null)
            {
                _logger.Log("QuestDetection", "OCR engine initialized using user profile languages.");
                return;
            }
        }
        catch (Exception ex)
        {
            _logger.Log("QuestDetection", $"Failed to initialize OCR from user languages: {ex.Message}");
        }

        var fallbackLanguages = new[] { "en-US", "de-DE" };
        foreach (var tag in fallbackLanguages)
        {
            try
            {
                var language = new Language(tag);
                var engine = OcrEngine.TryCreateFromLanguage(language);
                if (engine != null)
                {
                    _ocrEngine = engine;
                    _logger.Log("QuestDetection", $"OCR engine initialized with fallback language {tag}.");
                    return;
                }
            }
            catch (Exception ex)
            {
                _logger.Log("QuestDetection", $"OCR language {tag} unavailable: {ex.Message}");
            }
        }

        _logger.Log("QuestDetection", "Unable to initialize OCR engine. Quest detection disabled.");
    }

    private async void OnFrameCaptured(object? sender, GameFrameCapturedEventArgs e)
    {
        if (_disposed || !_enabled || _ocrEngine is null || _matcher.IsEmpty)
        {
            return;
        }

        var now = DateTimeOffset.UtcNow;
        if (now - _lastAnalysis < _analysisInterval)
        {
            return;
        }

        if (!await _analysisGate.WaitAsync(0).ConfigureAwait(false))
        {
            return;
        }

        try
        {
            _lastAnalysis = now;
            var matches = await AnalyzeFrameAsync(e.Frame).ConfigureAwait(false);
            if (matches is null || matches.Count == 0)
            {
                return;
            }

            var newlyStable = UpdateStability(matches);
            if (newlyStable.Count > 0)
            {
                var detectedNames = string.Join(", ", newlyStable.Select(m => string.IsNullOrWhiteSpace(m.DisplayName) ? m.DetectedName : m.DisplayName));
                _logger.Log("QuestDetection", $"Stable quests detected: {detectedNames}");
                QuestsDetected?.Invoke(this, new QuestDetectionEventArgs(newlyStable, now));
            }
        }
        catch (Exception ex)
        {
            _logger.Log("QuestDetection", $"Analysis failed: {ex.Message}");
        }
        finally
        {
            _analysisGate.Release();
        }
    }

    private List<QuestDetectionMatch> UpdateStability(IReadOnlyList<QuestDetectionMatch> matches)
    {
        var confirmed = new List<QuestDetectionMatch>();
        var currentSet = new HashSet<string>(matches.Select(m => m.QuestId), StringComparer.OrdinalIgnoreCase);

        foreach (var key in _stableMatches.Keys.ToList())
        {
            if (!currentSet.Contains(key))
            {
                _stableMatches.Remove(key);
            }
        }

        foreach (var match in matches)
        {
            if (_stableMatches.TryGetValue(match.QuestId, out var existing))
            {
                existing = existing with { HitCount = Math.Min(existing.HitCount + 1, 5), Match = match };
            }
            else
            {
                existing = new StableDetection(match, 1);
            }

            _stableMatches[match.QuestId] = existing;
            if (existing.HitCount == _stabilityThreshold)
            {
                confirmed.Add(match);
            }
        }

        return confirmed;
    }

    private async Task<IReadOnlyList<QuestDetectionMatch>?> AnalyzeFrameAsync(GameCaptureFrame frame)
    {
        if (!IsLikelyPlayMenu(frame, out var buttonCoverage, out var panelCoverage, out var darkCoverage))
        {
            LogThrottled(ref _lastNotPlayLog, $"Quest detection skipped: Play menu not detected (button={buttonCoverage:P1}, header={panelCoverage:P1}, listDark={darkCoverage:P1}).");
            return null;
        }

        SoftwareBitmap? softwareBitmap = null;
        try
        {
            softwareBitmap = await frame.ExtractSoftwareBitmapAsync(QuestListRegion).ConfigureAwait(false);
            var ocrResult = await _ocrEngine!.RecognizeAsync(softwareBitmap);
            var matches = new List<QuestDetectionMatch>();
            var unmatched = new List<string>();

            foreach (var line in ocrResult.Lines)
            {
                var text = line.Text?.Trim();
                if (string.IsNullOrWhiteSpace(text) || text.Length < 3)
                {
                    continue;
                }

                var match = _matcher.Match(text);
                if (match is not null)
                {
                    matches.Add(match);
                }
                else
                {
                    unmatched.Add(text);
                }
            }

            if (matches.Count == 0)
            {
                var noMatchMessage = unmatched.Count > 0
                    ? $"Quest detection OCR produced no quest matches. Sample text: {string.Join(" | ", unmatched.Take(4))}"
                    : "Quest detection OCR produced no quest matches.";
                LogThrottled(ref _lastNoMatchLog, noMatchMessage);
            }
            else
            {
                _logger.Log("QuestDetection", $"OCR detected {matches.Count} quest candidate(s).");
                if (unmatched.Count > 0)
                {
                    LogThrottled(ref _lastUnmatchedLog, $"Additional OCR lines without quest match: {string.Join(" | ", unmatched.Take(4))}");
                }
            }

            return matches;
        }
        finally
        {
            softwareBitmap?.Dispose();
        }
    }

    private bool IsLikelyPlayMenu(GameCaptureFrame frame, out double buttonCoverage, out double panelCoverage, out double darkPanelCoverage)
    {
        buttonCoverage = frame.GetColorCoverage(PlayButtonCoreRegion, static (b, g, r) =>
        {
            var yellowScore = (r - b) + (g - b);
            return r >= 185 && g >= 160 && yellowScore >= 160;
        });

        panelCoverage = frame.GetColorCoverage(QuestSidebarHighlightRegion, static (b, g, r) =>
        {
            var sum = r + g + b;
            var brightness = sum / 3;
            var max = Math.Max(r, Math.Max(g, b));
            var min = Math.Min(r, Math.Min(g, b));
            var saturation = max - min;
            return brightness >= 150 && saturation <= 60;
        });

        darkPanelCoverage = frame.GetColorCoverage(QuestListDarkRegion, static (b, g, r) =>
        {
            var blueDominant = b - r >= 25 && b - g >= 10;
            return r <= 110 && g <= 120 && b <= 150 && blueDominant;
        });

        if (buttonCoverage >= 0.04 && (panelCoverage >= 0.10 || darkPanelCoverage >= 0.25))
        {
            return true;
        }

        if (buttonCoverage >= 0.03 && panelCoverage >= 0.08 && darkPanelCoverage >= 0.20)
        {
            return true;
        }

        var (r, g, b) = frame.GetAverageColor(PlayButtonRegion);
        var brightness = (r + g + b) / 3.0;
        var yellowScore = (r - b) + (g - b);
        var fallback = brightness > 110 && yellowScore > 100 && (panelCoverage >= 0.06 || darkPanelCoverage >= 0.18);
        if (fallback)
        {
            return true;
        }

        return false;
    }

    private void LogThrottled(ref DateTimeOffset lastLog, string message)
    {
        var now = DateTimeOffset.UtcNow;
        if (now - lastLog < _statusLogInterval)
        {
            return;
        }

        lastLog = now;
        _logger.Log("QuestDetection", message);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _captureService.FrameCaptured -= OnFrameCaptured;
        _analysisGate.Dispose();
    }

    private sealed record StableDetection(QuestDetectionMatch Match, int HitCount);
}

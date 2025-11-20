using OverlayApp.Data;
using OverlayApp.Data.Models;
using OverlayApp.Infrastructure;
using OverlayApp.Progress;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Windows.Globalization;
using Windows.Graphics.Imaging;
using Windows.Media.Ocr;

namespace OverlayApp.Services;

internal sealed class ProjectDetectionService : IDisposable
{
    // Regions based on 1920x1080 estimation
    // "EXPEDITION" title area
    private static readonly NormalizedRectangle ExpeditionTitleRegion = new(0.05, 0.02, 0.15, 0.05);
    
    // Project Name area "HAUPTSYSTEME (2/6)"
    private static readonly NormalizedRectangle ProjectNameRegion = new(0.02, 0.14, 0.25, 0.05);

    // Progress Bar Boxes (1-6)
    // Assuming the bar starts around x=0.025, y=0.10 and has width ~0.15
    // Each box is roughly 0.025 wide.
    private static readonly NormalizedRectangle[] ProgressBoxRegions = new[]
    {
        new NormalizedRectangle(0.025, 0.10, 0.025, 0.03), // 1
        new NormalizedRectangle(0.050, 0.10, 0.025, 0.03), // 2
        new NormalizedRectangle(0.075, 0.10, 0.025, 0.03), // 3
        new NormalizedRectangle(0.100, 0.10, 0.025, 0.03), // 4
        new NormalizedRectangle(0.125, 0.10, 0.025, 0.03), // 5
        new NormalizedRectangle(0.150, 0.10, 0.025, 0.03)  // 6
    };

    private readonly GameCaptureService _captureService;
    private readonly UserProgressStore _progressStore;
    private readonly ILogger _logger;
    private readonly SemaphoreSlim _analysisGate = new(1, 1);
    private readonly TimeSpan _analysisInterval = TimeSpan.FromSeconds(1.5);
    private DateTimeOffset _lastAnalysis = DateTimeOffset.MinValue;
    
    private OcrEngine? _ocrEngine;
    private ArcDataSnapshot? _snapshot;
    private UserProgressState? _lastKnownProgress;
    private bool _enabled;
    private bool _disposed;

    // Stability tracking
    private string? _lastDetectedProjectId;
    private int _lastDetectedPhase = -1;
    private int _stabilityCounter = 0;
    private const int StabilityThreshold = 2;

    public ProjectDetectionService(
        GameCaptureService captureService,
        UserProgressStore progressStore,
        ILogger logger)
    {
        _captureService = captureService;
        _progressStore = progressStore;
        _logger = logger;
        InitializeOcrEngine();
        _captureService.FrameCaptured += OnFrameCaptured;
    }

    public void UpdateArcData(ArcDataSnapshot? snapshot)
    {
        _snapshot = snapshot;
    }

    public void UpdateUserProgress(UserProgressState? progress)
    {
        _lastKnownProgress = progress;
    }

    public void SetEnabled(bool enabled)
    {
        if (_enabled == enabled) return;
        _enabled = enabled;
        if (enabled)
        {
            _logger.Log("ProjectDetection", "Project detection enabled.");
        }
        else
        {
            _logger.Log("ProjectDetection", "Project detection disabled.");
            _stabilityCounter = 0;
            _lastDetectedProjectId = null;
        }
    }

    private void InitializeOcrEngine()
    {
        try
        {
            _ocrEngine = OcrEngine.TryCreateFromUserProfileLanguages();
            if (_ocrEngine != null) return;
        }
        catch (Exception ex)
        {
            _logger.Log("ProjectDetection", $"Failed to initialize OCR from user languages: {ex.Message}");
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
                    return;
                }
            }
            catch { /* Ignore */ }
        }
        
        _logger.Log("ProjectDetection", "Unable to initialize OCR engine.");
    }

    private async void OnFrameCaptured(object? sender, GameFrameCapturedEventArgs e)
    {
        if (_disposed || !_enabled || _ocrEngine is null || _snapshot is null) return;

        var now = DateTimeOffset.UtcNow;
        if (now - _lastAnalysis < _analysisInterval) return;

        if (!await _analysisGate.WaitAsync(0).ConfigureAwait(false)) return;

        try
        {
            _lastAnalysis = now;
            await AnalyzeFrameAsync(e.Frame).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.Log("ProjectDetection", $"Analysis failed: {ex.Message}");
        }
        finally
        {
            _analysisGate.Release();
        }
    }

    private async Task AnalyzeFrameAsync(GameCaptureFrame frame)
    {
        // 1. Check if we are on the Expedition screen and identify the project from the title
        // The user requested to use the top-left title (e.g. "EXPEDITION") to identify the project.
        // This text is likely "EXPEDITION" or "EXPEDITION PROJECT".
        
        var (isScreen, titleText) = await GetScreenTitleAsync(frame);
        if (!isScreen || string.IsNullOrWhiteSpace(titleText))
        {
            return;
        }

        string? detectedProjectId = FindProjectByTitle(titleText);
        
        if (detectedProjectId == null)
        {
            // Fallback: If we have a single tracked project, assume it's that one.
            if (_lastKnownProgress != null)
            {
                var tracked = _lastKnownProgress.Projects.Where(p => p.Tracking).ToList();
                if (tracked.Count == 1)
                {
                    detectedProjectId = tracked[0].ProjectId;
                }
            }
        }

        if (detectedProjectId == null)
        {
            _logger.Log("ProjectDetection", $"Expedition screen detected ('{titleText}'), but project could not be matched.");
            return;
        }

        // 3. Determine progress
        int completedPhases = 0;
        for (int i = 0; i < ProgressBoxRegions.Length; i++)
        {
            var (r, g, b) = frame.GetAverageColor(ProgressBoxRegions[i]);
            if (IsBlue(r, g, b))
            {
                completedPhases = i + 1;
            }
            else
            {
                // Assuming contiguous progress
                break;
            }
        }

        // 4. Update stability and store
        if (detectedProjectId == _lastDetectedProjectId && completedPhases == _lastDetectedPhase)
        {
            _stabilityCounter++;
            if (_stabilityCounter == StabilityThreshold)
            {
                await UpdateProgressAsync(detectedProjectId, completedPhases);
            }
        }
        else
        {
            _lastDetectedProjectId = detectedProjectId;
            _lastDetectedPhase = completedPhases;
            _stabilityCounter = 1;
        }
    }

    private async Task<(bool IsScreen, string? TitleText)> GetScreenTitleAsync(GameCaptureFrame frame)
    {
        SoftwareBitmap? softwareBitmap = null;
        try
        {
            softwareBitmap = await frame.ExtractSoftwareBitmapAsync(ExpeditionTitleRegion).ConfigureAwait(false);
            var ocrResult = await _ocrEngine!.RecognizeAsync(softwareBitmap);
            
            foreach (var line in ocrResult.Lines)
            {
                var text = line.Text?.Trim();
                if (!string.IsNullOrWhiteSpace(text) && text.Length > 3)
                {
                    // Simple check for "EXPEDITION" to confirm we are on the right screen
                    // This works for EN, DE, FR, etc. as most use the word Expedition or similar.
                    if (text.Contains("EXPEDITION", StringComparison.OrdinalIgnoreCase) || 
                        text.Contains("PROJEKT", StringComparison.OrdinalIgnoreCase) ||
                        text.Contains("PROJECT", StringComparison.OrdinalIgnoreCase))
                    {
                        return (true, text);
                    }
                }
            }
        }
        catch (Exception ex)
        {
            _logger.Log("ProjectDetection", $"Title OCR failed: {ex.Message}");
        }
        finally
        {
            softwareBitmap?.Dispose();
        }

        return (false, null);
    }

    private string? FindProjectByTitle(string title)
    {
        if (_snapshot?.Projects == null) return null;

        // Clean title for better matching
        // Remove common words if needed, but "Expedition" is key here.
        var cleanTitle = title.Trim();

        foreach (var project in _snapshot.Projects)
        {
            if (project.Name == null) continue;

            foreach (var kvp in project.Name)
            {
                var projectName = kvp.Value;
                
                // 1. Exact match
                if (string.Equals(projectName, cleanTitle, StringComparison.OrdinalIgnoreCase))
                {
                    return project.Id;
                }

                // 2. Contains match (e.g. "Expedition Project" contains "Expedition")
                // We check if the project name contains the title found on screen, OR
                // if the title found on screen contains the project name.
                if (projectName.Contains(cleanTitle, StringComparison.OrdinalIgnoreCase) || 
                    cleanTitle.Contains(projectName, StringComparison.OrdinalIgnoreCase))
                {
                    return project.Id;
                }

                // 3. Fuzzy match
                if (ComputeLevenshteinDistance(projectName.ToLower(), cleanTitle.ToLower()) <= 3)
                {
                    return project.Id;
                }
            }
        }

        return null;
    }

    private bool IsBlue(double r, double g, double b)
    {
        // Cyan/Light Blue: High B, High G, Low R
        return b > 150 && g > 120 && r < 100;
    }

    private bool IsDark(double r, double g, double b)
    {
        return r < 60 && g < 60 && b < 60;
    }

    private async Task<string?> DetectProjectNameAsync(GameCaptureFrame frame)
    {
        SoftwareBitmap? softwareBitmap = null;
        var candidates = new List<string>();
        try
        {
            softwareBitmap = await frame.ExtractSoftwareBitmapAsync(ProjectNameRegion).ConfigureAwait(false);
            var ocrResult = await _ocrEngine!.RecognizeAsync(softwareBitmap);
            
            foreach (var line in ocrResult.Lines)
            {
                var text = line.Text;
                if (string.IsNullOrWhiteSpace(text)) continue;

                // Clean up text: "HAUPTSYSTEME (2/6)" -> "HAUPTSYSTEME"
                var cleanText = text;
                int parenIndex = cleanText.IndexOf('(');
                if (parenIndex > 0)
                {
                    cleanText = cleanText.Substring(0, parenIndex);
                }
                cleanText = cleanText.Trim();
                candidates.Add(cleanText);

                // Match against projects
                foreach (var project in _snapshot!.Projects)
                {
                    if (project.Name == null) continue;

                    foreach (var kvp in project.Name)
                    {
                        if (string.Equals(kvp.Value, cleanText, StringComparison.OrdinalIgnoreCase))
                        {
                            return project.Id;
                        }
                        
                        // Fuzzy match?
                        if (ComputeLevenshteinDistance(kvp.Value.ToLower(), cleanText.ToLower()) <= 2)
                        {
                            return project.Id;
                        }
                    }
                }
            }
        }
        catch (Exception ex)
        {
            _logger.Log("ProjectDetection", $"OCR failed: {ex.Message}");
        }
        finally
        {
            softwareBitmap?.Dispose();
        }

        if (candidates.Count > 0)
        {
            _logger.Log("ProjectDetection", $"No project match found. Candidates: {string.Join(", ", candidates)}");
        }

        return null;
    }

    private async Task UpdateProgressAsync(string projectId, int phase)
    {
        try
        {
            var state = await _progressStore.LoadAsync(CancellationToken.None);
            var projectState = state.Projects.FirstOrDefault(p => p.ProjectId == projectId);
            
            if (projectState == null)
            {
                projectState = new ProjectProgressState { ProjectId = projectId, Tracking = true };
                state.Projects.Add(projectState);
            }

            if (projectState.HighestPhaseCompleted != phase)
            {
                projectState.HighestPhaseCompleted = phase;
                await _progressStore.SaveAsync(state, CancellationToken.None);
                _logger.Log("ProjectDetection", $"Updated project {projectId} to phase {phase}");
            }
        }
        catch (Exception ex)
        {
            _logger.Log("ProjectDetection", $"Failed to update progress: {ex.Message}");
        }
    }

    private static int ComputeLevenshteinDistance(string s, string t)
    {
        int n = s.Length;
        int m = t.Length;
        int[,] d = new int[n + 1, m + 1];

        if (n == 0) return m;
        if (m == 0) return n;

        for (int i = 0; i <= n; d[i, 0] = i++) { }
        for (int j = 0; j <= m; d[0, j] = j++) { }

        for (int i = 1; i <= n; i++)
        {
            for (int j = 1; j <= m; j++)
            {
                int cost = (t[j - 1] == s[i - 1]) ? 0 : 1;
                d[i, j] = Math.Min(
                    Math.Min(d[i - 1, j] + 1, d[i, j - 1] + 1),
                    d[i - 1, j - 1] + cost);
            }
        }
        return d[n, m];
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _captureService.FrameCaptured -= OnFrameCaptured;
        _analysisGate.Dispose();
    }
}

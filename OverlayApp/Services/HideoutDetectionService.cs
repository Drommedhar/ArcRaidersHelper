using OverlayApp.Data;
using OverlayApp.Data.Models;
using OverlayApp.Infrastructure;
using OverlayApp.Progress;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Windows.Globalization;
using Windows.Graphics.Imaging;
using Windows.Media.Ocr;

namespace OverlayApp.Services;

internal sealed class HideoutDetectionService : IDisposable
{
    // Region for the specific bench title (top-left)
    // e.g. "< SCRAPPY — STUFE 05" or "< WERKBANK — STUFE 01"
    private static readonly NormalizedRectangle SpecificBenchTitleRegion = new(0.02, 0.02, 0.40, 0.10);

    // Region for the overview icons (bottom row)
    // Instead of small boxes, we scan the entire bottom center area.
    // This avoids issues with precise alignment.
    // X: 0.10 to 0.90 (Center 80%)
    // Y: 0.75 to 0.92 (Bottom ~17%) - Exclude bottom menu text
    private static readonly NormalizedRectangle OverviewWideRegion = new(0.10, 0.75, 0.80, 0.17);

    // Region for Stash Capacity (Top Left of Left Panel)
    // Covers "LAGER" title and "239/280" capacity
    // X: 0.05 to 0.20 (Left side)
    // Y: 0.12 to 0.22 (Below header)
    private static readonly NormalizedRectangle StashCapacityRegion = new(0.05, 0.12, 0.15, 0.10);

    // Mapping for the overview icons (indices 1-7).
    // We use English names as keys to look up the actual IDs in the snapshot.
    // The first icon (Scrappy) has no level, so the first Roman numeral we find corresponds to Workbench.
    private static readonly string[] OverviewModuleNames = new[]
    {
        "Workbench",
        "Weapon Bench",
        "Equipment Bench",
        "Medical Station",
        "Refiner",
        "Utility Bench",
        "Explosives Bench"
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

    // Regex for specific bench title: "NAME - STUFE XX"
    // Matches "WERKBANK - STUFE 01", "SCRAPPY - STUFE 05"
    // Also handles "—" (em dash) and potential OCR errors.
    // Updated to make the dash optional, as OCR might split lines or miss the dash.
    private static readonly Regex BenchTitleRegex = new(
        @"(?<name>.+?)\s*(?:[-—]\s*)?(?:STUFE|LEVEL|STAGE)\s*(?<level>\d+)", 
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    // Regex for stash capacity: "239/280"
    // Matches "239/280", "239 / 280", "239|280" (OCR error)
    private static readonly Regex StashCapacityRegex = new(
        @"(?<current>\d+)\s*[\/|I]\s*(?<max>\d+)", 
        RegexOptions.Compiled);

    public HideoutDetectionService(
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
            _logger.Log("HideoutDetection", "Hideout detection enabled.");
        }
        else
        {
            _logger.Log("HideoutDetection", "Hideout detection disabled.");
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
            _logger.Log("HideoutDetection", $"Failed to initialize OCR from user languages: {ex.Message}");
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
        
        _logger.Log("HideoutDetection", "Unable to initialize OCR engine.");
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
            _logger.Log("HideoutDetection", $"Analysis failed: {ex.Message}");
        }
        finally
        {
            _analysisGate.Release();
        }
    }

    private async Task AnalyzeFrameAsync(GameCaptureFrame frame)
    {
        // Strategy:
        // 1. Try to detect specific bench screen (high confidence, specific module).
        // 2. If not found, try to detect overview screen (multiple modules).

        // Debug log to confirm analysis is running (throttled by interval)
        _logger.Log("HideoutDetection", "Analyzing frame for hideout data...");

        bool specificFound = await DetectSpecificBenchAsync(frame);
        if (!specificFound)
        {
            // Check for Stash screen (Inventory)
            bool stashFound = await DetectStashScreenAsync(frame);
            
            if (!stashFound)
            {
                _logger.Log("HideoutDetection", "Specific bench/stash not found. Checking overview screen...");
                await DetectOverviewScreenAsync(frame);
            }
        }
    }

    private async Task<bool> DetectStashScreenAsync(GameCaptureFrame frame)
    {
        SoftwareBitmap? softwareBitmap = null;
        try
        {
            softwareBitmap = await frame.ExtractSoftwareBitmapAsync(StashCapacityRegion).ConfigureAwait(false);
            var ocrResult = await _ocrEngine!.RecognizeAsync(softwareBitmap);
            
            // Combine lines to handle split text
            var allText = string.Join(" ", ocrResult.Lines.Select(l => l.Text));
            if (string.IsNullOrWhiteSpace(allText)) return false;

            // Check for "LAGER" or "STASH" to confirm we are in the right area (optional but safer)
            // OCR might read "LAGER" as "L A G E R" or similar, so we just look for the numbers primarily.
            // But if we find the numbers, it's a strong signal.

            var match = StashCapacityRegex.Match(allText);
            if (match.Success)
            {
                if (int.TryParse(match.Groups["max"].Value, out int maxCapacity))
                {
                    _logger.Log("HideoutDetection", $"Detected Stash Capacity: Max {maxCapacity}");
                    
                    // Find Stash module
                    var stashId = FindModuleByName("Stash");
                    if (stashId == null) stashId = FindModuleByName("Lager"); // Fallback

                    if (stashId != null)
                    {
                        // Find level matching this capacity
                        var level = FindLevelByStashCapacity(stashId, maxCapacity);
                        if (level.HasValue)
                        {
                            await UpdateHideoutProgressAsync(stashId, level.Value);
                            return true;
                        }
                        else
                        {
                            _logger.Log("HideoutDetection", $"Could not find Stash level for capacity {maxCapacity}.");
                        }
                    }
                    else
                    {
                        _logger.Log("HideoutDetection", "Stash module not found in data.");
                    }
                }
            }
        }
        catch (Exception ex)
        {
            _logger.Log("HideoutDetection", $"Stash detection failed: {ex.Message}");
        }
        finally
        {
            softwareBitmap?.Dispose();
        }

        return false;
    }

    private int? FindLevelByStashCapacity(string stashId, int maxCapacity)
    {
        if (_snapshot?.HideoutModules == null) return null;
        if (!_snapshot.HideoutModules.TryGetValue(stashId, out var module)) return null;

        foreach (var level in module.Levels)
        {
            // Check description in ExtensionData
            if (level.ExtensionData != null && level.ExtensionData.TryGetValue("description", out var descElement))
            {
                string? description = null;
                if (descElement.ValueKind == JsonValueKind.Object)
                {
                    // Try English first
                    if (descElement.TryGetProperty("en", out var enProp)) description = enProp.GetString();
                    // Fallback to any other string property if needed, or just check the JSON string
                }
                else if (descElement.ValueKind == JsonValueKind.String)
                {
                    description = descElement.GetString();
                }

                if (!string.IsNullOrEmpty(description))
                {
                    // Parse description for max size
                    // "64 slots" or "+24 slots (88 total)"
                    
                    // Check for "(88 total)"
                    var matchTotal = Regex.Match(description, @"\((\d+)\s*total\)");
                    if (matchTotal.Success && int.TryParse(matchTotal.Groups[1].Value, out int total))
                    {
                        if (total == maxCapacity) return level.Level;
                    }
                    
                    // Check for "64 slots" (usually Level 1 or 0)
                    var matchSlots = Regex.Match(description, @"^(\d+)\s*slots");
                    if (matchSlots.Success && int.TryParse(matchSlots.Groups[1].Value, out int slots))
                    {
                        if (slots == maxCapacity) return level.Level;
                    }
                }
            }
        }

        return null;
    }

    private async Task<bool> DetectSpecificBenchAsync(GameCaptureFrame frame)
    {
        SoftwareBitmap? softwareBitmap = null;
        try
        {
            softwareBitmap = await frame.ExtractSoftwareBitmapAsync(SpecificBenchTitleRegion).ConfigureAwait(false);
            var ocrResult = await _ocrEngine!.RecognizeAsync(softwareBitmap);
            
            // Combine all lines to handle cases where OCR splits the title (e.g. "WERKBANK" \n "STUFE 01")
            var allText = string.Join(" ", ocrResult.Lines.Select(l => l.Text));
            if (string.IsNullOrWhiteSpace(allText)) return false;

            _logger.Log("HideoutDetection", $"Title Candidate: '{allText}'");

            var match = BenchTitleRegex.Match(allText);
            if (match.Success)
            {
                var namePart = match.Groups["name"].Value.Trim();
                // Remove leading "<" if present (back button)
                namePart = namePart.TrimStart('<', ' ').Trim();
                
                var levelPart = match.Groups["level"].Value;
                if (int.TryParse(levelPart, out int level))
                {
                    var moduleId = FindModuleByName(namePart);
                    if (moduleId != null)
                    {
                        await UpdateHideoutProgressAsync(moduleId, level);
                        return true;
                    }
                    else 
                    {
                        _logger.Log("HideoutDetection", $"Matched bench '{namePart}' (Level {level}) but found no corresponding module ID.");
                    }
                }
            }
        }
        catch (Exception ex)
        {
            _logger.Log("HideoutDetection", $"Specific bench OCR failed: {ex.Message}");
        }
        finally
        {
            softwareBitmap?.Dispose();
        }

        return false;
    }

    private async Task DetectOverviewScreenAsync(GameCaptureFrame frame)
    {
        // Scan the wide region for all text
        SoftwareBitmap? softwareBitmap = null;
        try
        {
            softwareBitmap = await frame.ExtractSoftwareBitmapAsync(OverviewWideRegion).ConfigureAwait(false);
            var ocrResult = await _ocrEngine!.RecognizeAsync(softwareBitmap);
            
            if (ocrResult.Lines.Count == 0)
            {
                _logger.Log("HideoutDetection", "Overview OCR found no text in region.");
                return;
            }

            var foundLevels = new List<(double X, int Level)>();

            foreach (var line in ocrResult.Lines)
            {
                _logger.Log("HideoutDetection", $"Overview Raw Line: '{line.Text}'");
                foreach (var word in line.Words)
                {
                    var text = word.Text?.Trim().ToUpperInvariant();
                    if (string.IsNullOrWhiteSpace(text)) continue;

                    int val = ParseRomanNumeral(text);
                    if (val > 0)
                    {
                        // Store the X coordinate (center of the word) to sort them left-to-right
                        var centerX = word.BoundingRect.X + (word.BoundingRect.Width / 2.0);
                        foundLevels.Add((centerX, val));
                        _logger.Log("HideoutDetection", $"Found Roman numeral '{text}' ({val}) at X={centerX:F1}");
                    }
                }
            }

            if (foundLevels.Count == 0)
            {
                return;
            }

            // Sort by X coordinate
            foundLevels.Sort((a, b) => a.X.CompareTo(b.X));

            // Scrappy (the hideout base) does not have a level indicator on the overview,
            // but if we are seeing the overview, it exists and is at least Level 1.
            // We update it here to ensure it's tracked.
            var scrappyId = FindModuleByName("Scrappy");
            if (scrappyId != null)
            {
                await UpdateHideoutProgressAsync(scrappyId, 1);
            }

            // Map to modules
            // We expect up to 7 levels.
            // We'll try to match them to the OverviewModuleNames list.
            // Since we might miss some (OCR failure), this is a best-effort mapping.
            // However, if we find a sequence, we can assume they are contiguous or try to fit them.
            // For now, let's assume if we find N levels, they correspond to the first N modules in the list
            // OR we could try to be smarter if we knew the exact X positions.
            // Given the user said "The order after that is...", let's assume left-to-right mapping.
            
            // If we find fewer than expected, we might be mis-assigning. 
            // But without fixed positions, left-to-right is the best we can do.
            // We can log the count to help debug.
            
            _logger.Log("HideoutDetection", $"Found {foundLevels.Count} levels on overview.");

            for (int i = 0; i < foundLevels.Count; i++)
            {
                if (i >= OverviewModuleNames.Length) break;

                var moduleName = OverviewModuleNames[i];
                var level = foundLevels[i].Level;
                
                var moduleId = FindModuleByName(moduleName);
                if (moduleId != null)
                {
                    await UpdateHideoutProgressAsync(moduleId, level);
                }
                else
                {
                    _logger.Log("HideoutDetection", $"Could not resolve ID for '{moduleName}' (Level {level}).");
                }
            }
        }
        catch (Exception ex)
        {
            _logger.Log("HideoutDetection", $"Overview detection failed: {ex.Message}");
        }
        finally
        {
            softwareBitmap?.Dispose();
        }
    }

    // Removed ReadRomanNumeralAsync as it is no longer used.
    /*
    private async Task<int> ReadRomanNumeralAsync(GameCaptureFrame frame, NormalizedRectangle region)
    {
        // ...
    }
    */

    private int ParseRomanNumeral(string text)
    {
        // Handle potential OCR noise
        // Sometimes 'I' is read as 'l', '1', '|', 'i', '!', ']'
        text = text.Replace("l", "I")
                   .Replace("1", "I")
                   .Replace("|", "I")
                   .Replace("i", "I")
                   .Replace("!", "I")
                   .Replace("]", "I")
                   .Replace("[", "I")
                   .Replace(".", "")
                   .Replace("'", "")
                   .Replace("’", "")
                   .Trim();

        return text switch
        {
            "I" => 1,
            "II" => 2,
            "III" => 3,
            "IV" => 4,
            "V" => 5,
            "VI" => 6,
            "VII" => 7,
            "VIII" => 8,
            _ => 0
        };
    }

    private string? FindModuleByName(string name)
    {
        if (_snapshot?.HideoutModules == null) return null;

        var cleanName = name.Trim();

        foreach (var module in _snapshot.HideoutModules.Values)
        {
            if (module.Name == null || module.Id == null) continue;

            foreach (var kvp in module.Name)
            {
                if (string.Equals(kvp.Value, cleanName, StringComparison.OrdinalIgnoreCase))
                {
                    return module.Id;
                }
                
                // Fuzzy match
                if (ComputeLevenshteinDistance(kvp.Value.ToLower(), cleanName.ToLower()) <= 2)
                {
                    return module.Id;
                }
            }
        }

        // Fallback: Handle specific overrides like "WERKBANK" -> "Workbench" if the data is missing the German translation.
        if (cleanName.Equals("WERKBANK", StringComparison.OrdinalIgnoreCase))
        {
            // Avoid infinite recursion if "Workbench" is also not found (though unlikely)
            if (!cleanName.Equals("Workbench", StringComparison.OrdinalIgnoreCase))
            {
                return FindModuleByName("Workbench");
            }
        }

        return null;
    }

    private async Task UpdateHideoutProgressAsync(string moduleId, int level)
    {
        try
        {
            var state = await _progressStore.LoadAsync(CancellationToken.None);
            var moduleState = state.HideoutModules.FirstOrDefault(m => m.ModuleId == moduleId);
            
            if (moduleState == null)
            {
                moduleState = new HideoutProgressState { ModuleId = moduleId, Tracking = true };
                state.HideoutModules.Add(moduleState);
            }

            if (moduleState.CurrentLevel != level)
            {
                moduleState.CurrentLevel = level;
                await _progressStore.SaveAsync(state, CancellationToken.None);
                _logger.Log("HideoutDetection", $"Updated module {moduleId} to level {level}");
            }
        }
        catch (Exception ex)
        {
            _logger.Log("HideoutDetection", $"Failed to update progress: {ex.Message}");
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

using OpenCvSharp;
using OverlayApp.Data;
using OverlayApp.Infrastructure;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using Rect = OpenCvSharp.Rect;
using Point = OpenCvSharp.Point;

namespace OverlayApp.Services;

internal class ItemSlotDetectionService : IDisposable
{
    private readonly GameCaptureService _captureService;
    private readonly ILogger _logger;
    private readonly List<(Mat Template, Mat Mask, Mat SmallTemplate, Mat SmallMask)> _templates = new();
    private readonly List<(string Name, Mat Template, Mat SmallTemplate)> _itemTemplates = new();
    private const double ConfidentMatchThreshold = 0.6;
    private bool _enabled;
    private bool _disposed;
    private readonly SemaphoreSlim _processingGate = new(1, 1);
    private readonly List<TrackedSlot> _trackedSlots = new();
    private const int MaxStability = 10;
    private const int StabilityThreshold = 1; 
    private readonly TimeSpan RemovalTimeout = TimeSpan.FromMilliseconds(100);

    /// <summary>
    /// Confidence threshold for detecting a slot (0.0 to 1.0).
    /// </summary>
    public double SlotDetectionThreshold { get; set; } = 0.7;

    /// <summary>
    /// Confidence threshold for identifying an item within a slot (0.0 to 1.0).
    /// </summary>
    public double ItemIdentificationThreshold { get; set; } = 0.0;

    // Event now passes a list of (Rect, IsOccupied, ItemName, Confidence, Candidates) tuples
    public event EventHandler<List<(System.Windows.Rect Rect, bool IsOccupied, string? ItemName, double Confidence, List<(string Name, double Score)> Candidates)>>? SlotsDetected;

    private class TrackedSlot : IDisposable
    {
        public Rect Rect { get; set; }
        public int Stability { get; set; }
        public DateTime LastSeen { get; set; }
        public bool IsOccupied { get; set; }
        public bool IsVisible { get; set; }
        public string? ItemName { get; set; }
        public double Confidence { get; set; }
        public List<(string Name, double Score)> Candidates { get; set; } = new();
        public Mat? LastImage { get; set; }

        public void Dispose()
        {
            LastImage?.Dispose();
            LastImage = null;
        }
    }

    public ItemSlotDetectionService(GameCaptureService captureService, ILogger logger)
    {
        _captureService = captureService;
        _logger = logger;
        _captureService.FrameCaptured += OnFrameCaptured;
        LoadReferenceImages();
        LoadItemTemplates();
    }

    private void LoadItemTemplates()
    {
        var repoItemsPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ArcRaidersHelper", "arcdata", "repo", "images", "items");
        var repoItemsInGamePath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ArcRaidersHelper", "arcdata", "repo", "images", "items_ingame");

        var searchDirectories = new List<string>();
        if (!string.IsNullOrWhiteSpace(repoItemsInGamePath) && Directory.Exists(repoItemsInGamePath))
        {
            searchDirectories.Add(repoItemsInGamePath);
        }

        if (Directory.Exists(repoItemsPath))
        {
            searchDirectories.Add(repoItemsPath);
        }
        else
        {
            _logger.Log("ItemSlotDetection", $"Item images directory not found at {repoItemsPath}");
        }

        if (searchDirectories.Count == 0)
        {
            _logger.Log("ItemSlotDetection", "No item image directories available for template loading.");
            return;
        }

        foreach (var t in _itemTemplates)
        {
            t.Template.Dispose();
            t.SmallTemplate.Dispose();
        }
        _itemTemplates.Clear();

        var seenNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        int loadedCount = 0;

        foreach (var directory in searchDirectories)
        {
            string[] files;
            try
            {
                files = Directory.GetFiles(directory, "*.png", SearchOption.AllDirectories);
            }
            catch (Exception ex)
            {
                _logger.Log("ItemSlotDetection", $"Failed to enumerate item images in {directory}: {ex.Message}");
                continue;
            }

            foreach (var file in files)
            {
                var name = Path.GetFileNameWithoutExtension(file);
                if (string.IsNullOrWhiteSpace(name) || !seenNames.Add(name))
                {
                    continue;
                }

                try
                {
                    var mat = Cv2.ImRead(file, ImreadModes.Color);
                    if (mat.Empty())
                    {
                        mat.Dispose();
                        continue;
                    }

                    var small = new Mat();
                    Cv2.Resize(mat, small, new OpenCvSharp.Size(0, 0), 0.5, 0.5, InterpolationFlags.Linear);

                    _itemTemplates.Add((name, mat, small));
                    loadedCount++;
                }
                catch (Exception ex)
                {
                    _logger.Log("ItemSlotDetection", $"Failed to load item image {file}: {ex.Message}");
                }
            }
        }

        _logger.Log("ItemSlotDetection", $"Loaded {loadedCount} item templates from {string.Join(", ", searchDirectories)}.");
    }

    private void LoadReferenceImages()
    {
        var referencePath = Path.Combine(AppContext.BaseDirectory, "Resources", "ReferenceImages");
        if (!Directory.Exists(referencePath))
        {
            Directory.CreateDirectory(referencePath);
            _logger.Log("ItemSlotDetection", $"Created reference images directory at {referencePath}. Please place reference images there.");
            return;
        }

        var files = Directory.GetFiles(referencePath, "*.png");
        
        // Clear existing templates
        foreach (var t in _templates)
        {
            t.Template.Dispose();
            t.Mask.Dispose();
            t.SmallTemplate.Dispose();
            t.SmallMask.Dispose();
        }
        _templates.Clear();
        
        foreach (var file in files)
        {
            try
            {
                // Load as Color
                var mat = Cv2.ImRead(file, ImreadModes.Color);
                if (!mat.Empty())
                {
                    var mask = CreateBorderMask(mat);

                    // Pre-process for detection (Grayscale)
                    using var grayTemplate = new Mat();
                    Cv2.CvtColor(mat, grayTemplate, ColorConversionCodes.BGR2GRAY);

                    using var blurredTemplate = new Mat();
                    Cv2.GaussianBlur(grayTemplate, blurredTemplate, new OpenCvSharp.Size(3, 3), 0);

                    var smallTemplate = new Mat();
                    Cv2.Resize(blurredTemplate, smallTemplate, new OpenCvSharp.Size(0, 0), 0.5, 0.5, InterpolationFlags.Linear);

                    var smallMask = new Mat();
                    Cv2.Resize(mask, smallMask, new OpenCvSharp.Size(0, 0), 0.5, 0.5, InterpolationFlags.Nearest);

                    _templates.Add((mat, mask, smallTemplate, smallMask));
                    _logger.Log("ItemSlotDetection", $"Loaded reference image: {Path.GetFileName(file)}");
                }
            }
            catch (Exception ex)
            {
                _logger.Log("ItemSlotDetection", $"Failed to load reference image {file}: {ex.Message}");
            }
        }
    }

    private Mat CreateBorderMask(Mat template)
    {
        int w = template.Width;
        int h = template.Height;
        
        // 1. Geometric Mask: White border, Black center
        var mask = new Mat(template.Size(), MatType.CV_8UC1, Scalar.White);

        // Calculate border thickness. 
        // Use a thinner border (1/16) to ensure we catch the border but avoid the item.
        int borderThickness = Math.Max(1, Math.Min(w, h) / 16); 

        // Mask out the center geometrically
        var centerRect = new Rect(borderThickness, borderThickness, w - 2 * borderThickness, h - 2 * borderThickness);
        Cv2.Rectangle(mask, centerRect, Scalar.Black, -1);
        
        return mask;
    }

    public void SetEnabled(bool enabled)
    {
        _enabled = enabled;
        if (enabled)
        {
            LoadReferenceImages();
        }
    }

    private async void OnFrameCaptured(object? sender, GameFrameCapturedEventArgs e)
    {
        if (!_enabled || _disposed || _templates.Count == 0)
        {
            return;
        }

        if (!await _processingGate.WaitAsync(0))
        {
            return;
        }

        try
        {
            var rawDetections = await Task.Run(() => ProcessFrame(e.Frame, _trackedSlots));
            // _logger.Log("ItemSlotDetection", $"ProcessFrame returned {rawDetections.Count} candidates.");

            var stableSlots = UpdateTrackedSlots(rawDetections, e.Frame.ScreenLeft, e.Frame.ScreenTop);
            // _logger.Log("ItemSlotDetection", $"UpdateTrackedSlots returned {stableSlots.Count} stable slots.");
            
            // Always invoke to ensure we clear the overlay when slots are lost
            SlotsDetected?.Invoke(this, stableSlots);
        }
        catch (Exception ex)
        {
            _logger.Log("ItemSlotDetection", $"Error processing frame: {ex.Message}");
        }
        finally
        {
            _processingGate.Release();
        }
    }

    private List<(System.Windows.Rect Rect, bool IsOccupied, string? ItemName, double Confidence, List<(string Name, double Score)> Candidates)> UpdateTrackedSlots(List<(Rect Rect, bool IsOccupied, string? ItemName, double Confidence, List<(string Name, double Score)> Candidates, Mat? SlotImage)> currentDetections, int screenLeft, int screenTop)
    {
        var now = DateTime.UtcNow;

        // Match current detections to tracked slots
        foreach (var tracked in _trackedSlots)
        {
            var bestMatchIndex = -1;
            double minDistance = double.MaxValue;

            for (int i = 0; i < currentDetections.Count; i++)
            {
                var detection = currentDetections[i];
                var dist = Distance(tracked.Rect, detection.Rect);
                
                if (dist < 20 && dist < minDistance) // 20px tolerance
                {
                    minDistance = dist;
                    bestMatchIndex = i;
                }
            }

            if (bestMatchIndex != -1)
            {
                // Update tracked slot
                var match = currentDetections[bestMatchIndex];
                
                // Smooth position (Exponential Moving Average)
                double alpha = 0.3;
                int newX = (int)(tracked.Rect.X * (1 - alpha) + match.Rect.X * alpha);
                int newY = (int)(tracked.Rect.Y * (1 - alpha) + match.Rect.Y * alpha);
                tracked.Rect = new Rect(newX, newY, match.Rect.Width, match.Rect.Height);

                tracked.IsOccupied = match.IsOccupied;
                if (match.IsOccupied)
                {
                    tracked.ItemName = match.ItemName;
                    tracked.Confidence = match.Confidence;
                    tracked.Candidates = match.Candidates;
                    
                    if (match.SlotImage != null)
                    {
                        tracked.LastImage?.Dispose();
                        tracked.LastImage = match.SlotImage;
                    }
                }
                else
                {
                    tracked.ItemName = null;
                    tracked.Confidence = 0;
                    tracked.Candidates.Clear();
                    tracked.LastImage?.Dispose();
                    tracked.LastImage = null;
                }

                tracked.Stability = Math.Min(tracked.Stability + 1, MaxStability);
                tracked.LastSeen = now;
                
                if (tracked.Stability >= StabilityThreshold)
                {
                    tracked.IsVisible = true;
                }
                
                // Remove from current detections so it's not matched again or added as new
                // But first dispose the image if we didn't use it (shouldn't happen if we matched, but good practice)
                // Actually we moved ownership to tracked.LastImage if match.SlotImage != null.
                // If we didn't use it (e.g. not occupied?), we should dispose it.
                if (!match.IsOccupied && match.SlotImage != null)
                {
                    match.SlotImage.Dispose();
                }

                currentDetections.RemoveAt(bestMatchIndex);
            }
        }

        // Add new detections
        foreach (var detection in currentDetections)
        {
            _trackedSlots.Add(new TrackedSlot
            {
                Rect = detection.Rect,
                IsOccupied = detection.IsOccupied,
                ItemName = detection.ItemName,
                Confidence = detection.Confidence,
                Candidates = detection.Candidates,
                Stability = 1,
                LastSeen = now,
                IsVisible = false, // Wait for stability threshold
                LastImage = detection.SlotImage
            });
        }

        // Remove lost slots
        for (int i = _trackedSlots.Count - 1; i >= 0; i--)
        {
            var s = _trackedSlots[i];
            if ((now - s.LastSeen) > RemovalTimeout)
            {
                s.Dispose();
                _trackedSlots.RemoveAt(i);
            }
        }

        // Return stable slots
        return _trackedSlots
            .Where(s => s.IsVisible)
            .Select(s => (new System.Windows.Rect(s.Rect.X + screenLeft, s.Rect.Y + screenTop, s.Rect.Width, s.Rect.Height), s.IsOccupied, s.Confidence >= ConfidentMatchThreshold ? s.ItemName : null, s.Confidence, s.Candidates))
            .ToList();
    }

    private double Distance(Rect r1, Rect r2)
    {
        var c1 = new Point(r1.X + r1.Width / 2, r1.Y + r1.Height / 2);
        var c2 = new Point(r2.X + r2.Width / 2, r2.Y + r2.Height / 2);
        return Math.Sqrt(Math.Pow(c1.X - c2.X, 2) + Math.Pow(c1.Y - c2.Y, 2));
    }

    private List<(Rect Rect, bool IsOccupied, string? ItemName, double Confidence, List<(string Name, double Score)> Candidates, Mat? SlotImage)> ProcessFrame(GameCaptureFrame frame, List<TrackedSlot> trackedSlots)
    {
        // Use ConcurrentBag for thread-safe parallel processing
        var allCandidates = new ConcurrentBag<(Rect Rect, double Score, bool IsOccupied, List<(string Name, double Score)> Candidates, Mat? SlotImage)>();

        try
        {
            using var matBgra = new Mat(frame.Height, frame.Width, MatType.CV_8UC4);
            var length = frame.Height * frame.Stride;
            
            if (MemoryMarshal.TryGetArray(frame.PixelBuffer, out var segment) && segment.Array != null)
            {
                 Marshal.Copy(segment.Array, segment.Offset, matBgra.Data, length);
            }
            else
            {
                 Marshal.Copy(frame.PixelBuffer.ToArray(), 0, matBgra.Data, length);
            }
            
            using var matBgr = new Mat();
            Cv2.CvtColor(matBgra, matBgr, ColorConversionCodes.BGRA2BGR);
            
            // Convert to Grayscale for faster matching
            using var matGray = new Mat();
            Cv2.CvtColor(matBgr, matGray, ColorConversionCodes.BGR2GRAY);

            // Pre-process frame with blur to reduce noise/flicker
            using var blurredFrame = new Mat();
            Cv2.GaussianBlur(matGray, blurredFrame, new OpenCvSharp.Size(3, 3), 0);

            // Downscale for faster matching (0.5x)
            using var smallFrame = new Mat();
            Cv2.Resize(blurredFrame, smallFrame, new OpenCvSharp.Size(0, 0), 0.5, 0.5, InterpolationFlags.Linear);

            Parallel.ForEach(_templates, templateData =>
            {
                var (template, mask, smallTemplate, smallMask) = templateData;
                try
                {
                    if (template.Width > matBgr.Width || template.Height > matBgr.Height)
                        return;

                    // Templates are pre-processed in LoadReferenceImages

                    using var result = new Mat();
                    
                    // Use Color Texture Matching (CCoeffNormed) WITH Blur.
                    Cv2.MatchTemplate(smallFrame, smallTemplate, result, TemplateMatchModes.CCoeffNormed, smallMask);

                    // Threshold: Lowered to ensure we catch all items.
                    double threshold = SlotDetectionThreshold; 
                    int matchCount = 0;
                    const int MaxMatchesPerTemplate = 50;

                    while (true)
                    {
                        if (matchCount >= MaxMatchesPerTemplate)
                        {
                            break;
                        }

                        Cv2.MinMaxLoc(result, out _, out double maxVal, out _, out Point maxLoc);
                        
                        if (maxVal >= threshold)
                        {
                            // Scale coordinates back up by 2
                            var rect = new Rect(maxLoc.X * 2, maxLoc.Y * 2, template.Width, template.Height);
                            
                            // Check if occupied using Edge Density on the ROI
                            bool isOccupied = CheckIfOccupiedByEdges(blurredFrame, rect);

                            // Only track occupied slots
                            if (isOccupied)
                            {
                                // Optimization: Check if we already know this slot and the image hasn't changed
                                bool cacheHit = false;
                                List<(string Name, double Score)> candidates = new();
                                Mat? slotImage = null;

                                // Extract ROI for identification/caching
                                // Ensure rect is within bounds
                                var roiRect = new Rect(rect.X, rect.Y, rect.Width, rect.Height);
                                if (roiRect.X < 0) roiRect.X = 0;
                                if (roiRect.Y < 0) roiRect.Y = 0;
                                if (roiRect.Right > matBgr.Width) roiRect.Width = matBgr.Width - roiRect.X;
                                if (roiRect.Bottom > matBgr.Height) roiRect.Height = matBgr.Height - roiRect.Y;

                                if (roiRect.Width > 0 && roiRect.Height > 0)
                                {
                                    // We must clone because matBgr will be disposed
                                    slotImage = new Mat(matBgr, roiRect).Clone();

                                    // Find matching tracked slot
                                    // Note: trackedSlots is accessed from multiple threads here, but it's read-only during this phase
                                    // We need to be careful not to modify it.
                                    TrackedSlot? bestMatch = null;
                                    double minDist = double.MaxValue;

                                    foreach (var t in trackedSlots)
                                    {
                                        var d = Distance(t.Rect, rect);
                                        if (d < 20 && d < minDist)
                                        {
                                            minDist = d;
                                            bestMatch = t;
                                        }
                                    }

                                    if (bestMatch != null && bestMatch.IsOccupied && bestMatch.LastImage != null && !bestMatch.LastImage.IsDisposed)
                                    {
                                        // Compare images
                                        using var cmp = new Mat();
                                        if (slotImage.Size() != bestMatch.LastImage.Size())
                                        {
                                            Cv2.Resize(bestMatch.LastImage, cmp, slotImage.Size());
                                        }
                                        else
                                        {
                                            bestMatch.LastImage.CopyTo(cmp);
                                        }

                                        using var diff = new Mat();
                                        Cv2.Absdiff(slotImage, cmp, diff);
                                        var mean = Cv2.Mean(diff);
                                        
                                        // Threshold for "same image". 
                                        // 10 is a small average pixel difference.
                                        if (mean.Val0 < 10 && mean.Val1 < 10 && mean.Val2 < 10)
                                        {
                                            candidates = bestMatch.Candidates.ToList(); // Clone list
                                            cacheHit = true;
                                            
                                            // If cache hit, we don't strictly need to return the new image unless we want to update the reference.
                                            // Updating reference handles slow lighting changes.
                                            // Let's return it.
                                        }
                                    }
                                }

                                if (!cacheHit)
                                {
                                    candidates = IdentifyItem(matBgr, rect);
                                }
                                else
                                {
                                    // If cache hit, we might want to skip IdentifyItem but we still need to return the result
                                }

                                allCandidates.Add((rect, maxVal, isOccupied, candidates, slotImage));
                            }

                            // Mask out the detected area in the result to find the next one
                            int maskW = (int)(smallTemplate.Width * 0.6);
                            int maskH = (int)(smallTemplate.Height * 0.6);
                            int maskX = maxLoc.X - maskW / 2;
                            int maskY = maxLoc.Y - maskH / 2;
                            
                            maskX = Math.Max(0, maskX);
                            maskY = Math.Max(0, maskY);
                            maskW = Math.Min(maskW, result.Width - maskX);
                            maskH = Math.Min(maskH, result.Height - maskY);

                            if (maskW > 0 && maskH > 0)
                            {
                                Cv2.Rectangle(result, new Rect(maskX, maskY, maskW, maskH), Scalar.Black, -1);
                            }
                            matchCount++;
                        }
                        else
                        {
                            break;
                        }
                    }
                }
                catch (Exception ex)
                {
                    _logger.Log("ItemSlotDetection", $"Error processing template: {ex.Message}");
                }
            });
        }
        catch (Exception ex)
        {
            _logger.Log("ItemSlotDetection", $"OpenCV error: {ex.Message}");
        }

        // Apply NMS (Non-Maximum Suppression) on all candidates
        var detectedSlots = new List<(Rect, bool, string?, double, List<(string Name, double Score)>, Mat?)>();
        var sortedCandidates = allCandidates.OrderByDescending(c => c.Score).ToList();

        foreach (var candidate in sortedCandidates)
        {
            bool overlap = false;
            foreach (var existing in detectedSlots)
            {
                if (GetIntersectionOverUnion(existing.Item1, candidate.Rect) > 0.5)
                {
                    overlap = true;
                    break;
                }
            }

            if (!overlap)
            {
                var best = candidate.Candidates.FirstOrDefault();
                detectedSlots.Add((candidate.Rect, candidate.IsOccupied, best.Name, best.Score, candidate.Candidates, candidate.SlotImage));
            }
            else
            {
                // Dispose unused image
                candidate.SlotImage?.Dispose();
            }
        }

        return detectedSlots;
    }

    private double GetIntersectionOverUnion(Rect r1, Rect r2)
    {
        var intersection = r1.Intersect(r2);
        if (intersection.Width <= 0 || intersection.Height <= 0) return 0;
        double areaI = intersection.Width * intersection.Height;
        double areaU = (r1.Width * r1.Height) + (r2.Width * r2.Height) - areaI;
        return areaI / areaU;
    }

    private bool CheckIfOccupiedByEdges(Mat frame, Rect slotRect)
    {
        // Define center region (e.g., inner 60%)
        int w = slotRect.Width;
        int h = slotRect.Height;
        int marginX = w / 5;
        int marginY = h / 5;
        
        var centerRect = new Rect(marginX, marginY, w - 2 * marginX, h - 2 * marginY);
        
        // Ensure rect is within bounds
        var frameRect = new Rect(slotRect.X + centerRect.X, slotRect.Y + centerRect.Y, centerRect.Width, centerRect.Height);
        
        if (frameRect.X < 0 || frameRect.Y < 0 || frameRect.Right > frame.Width || frameRect.Bottom > frame.Height)
            return false;

        // Extract ROI from the blurred frame
        using var roi = new Mat(frame, frameRect);
        
        // Run Canny on the ROI only
        using var edges = new Mat();
        Cv2.Canny(roi, edges, 50, 150);

        int nonZero = Cv2.CountNonZero(edges);
        double density = (double)nonZero / (frameRect.Width * frameRect.Height);
        
        // Items usually have high edge density. Empty slots (even with animated background) usually have low edge density.
        // Threshold needs tuning. 0.05 means 5% of pixels are edges.
        return density > 0.05; 
    }

    private List<(string Name, double Score)> IdentifyItem(Mat frame, Rect slotRect)
    {
        var candidates = new ConcurrentBag<(string Name, double Score)>();
        if (_itemTemplates.Count == 0) return candidates.ToList();

        // Extract ROI from the frame (which is now Color BGR)
        // Ensure rect is within bounds
        var roiRect = new Rect(slotRect.X, slotRect.Y, slotRect.Width, slotRect.Height);
        if (roiRect.X < 0) roiRect.X = 0;
        if (roiRect.Y < 0) roiRect.Y = 0;
        if (roiRect.Right > frame.Width) roiRect.Width = frame.Width - roiRect.X;
        if (roiRect.Bottom > frame.Height) roiRect.Height = frame.Height - roiRect.Y;

        if (roiRect.Width <= 0 || roiRect.Height <= 0) return candidates.ToList();

        using var roi = new Mat(frame, roiRect);
        using var smallRoi = new Mat();
        Cv2.Resize(roi, smallRoi, new OpenCvSharp.Size(0, 0), 0.5, 0.5, InterpolationFlags.Linear);

        // First pass: Small templates
        var potentialCandidates = new ConcurrentBag<(string Name, Mat Template, double Score)>();

        Parallel.ForEach(_itemTemplates, item =>
        {
            var (name, template, smallTemplate) = item;
            if (smallTemplate.Width > smallRoi.Width || smallTemplate.Height > smallRoi.Height) return;

            using var result = new Mat();
            Cv2.MatchTemplate(smallRoi, smallTemplate, result, TemplateMatchModes.CCoeffNormed);
            Cv2.MinMaxLoc(result, out _, out double maxVal, out _, out _);

            // Use a slightly lower threshold for coarse search
            if (maxVal > ItemIdentificationThreshold * 0.9)
            {
                potentialCandidates.Add((name, template, maxVal));
            }
        });

        // Take top 10 from coarse search
        var topPotential = potentialCandidates.OrderByDescending(x => x.Score).Take(10).ToList();

        // Second pass: Full templates on top candidates
        Parallel.ForEach(topPotential, item =>
        {
            var (name, template, _) = item;
            if (template.Width > roi.Width || template.Height > roi.Height) return;

            using var result = new Mat();
            Cv2.MatchTemplate(roi, template, result, TemplateMatchModes.CCoeffNormed);
            Cv2.MinMaxLoc(result, out _, out double maxVal, out _, out _);

            if (maxVal > ItemIdentificationThreshold)
            {
                candidates.Add((name, maxVal));
            }
        });

        var sorted = candidates.OrderByDescending(x => x.Score).Take(5).ToList();

        if (sorted.Count == 0)
        {
            return sorted;
        }

        var best = sorted[0];

        // Only keep multiple candidates when we are below the confidence threshold
        if (best.Score >= ConfidentMatchThreshold)
        {
            sorted = new List<(string Name, double Score)> { best };
            return sorted;
        }

        return sorted;
    }

    public void UpdateArcData(ArcDataSnapshot data)
    {
        _ = data;
    }

    public void Dispose()
    {
        _disposed = true;
        _captureService.FrameCaptured -= OnFrameCaptured;
        foreach (var t in _templates)
        {
            t.Template.Dispose();
            t.Mask.Dispose();
            t.SmallTemplate.Dispose();
            t.SmallMask.Dispose();
        }
        _templates.Clear();

        foreach (var t in _itemTemplates)
        {
            t.Template.Dispose();
            t.SmallTemplate.Dispose();
        }
        _itemTemplates.Clear();
        
        foreach (var t in _trackedSlots)
        {
            t.Dispose();
        }
        _trackedSlots.Clear();
        
        _processingGate.Dispose();
    }
}

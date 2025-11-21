using OverlayApp.Infrastructure;
using System;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;

namespace OverlayApp.Services;

internal sealed class GameCaptureService : IDisposable
{
    private readonly ILogger _logger;
    private readonly TimeSpan _captureInterval;
    private readonly TimeSpan _retryDelay = TimeSpan.FromMilliseconds(500);
    private readonly TimeSpan _debugDumpInterval;
    private readonly object _stateGate = new();
    private CancellationTokenSource? _cts;
    private readonly object _frameGate = new();
    private readonly string _captureDirectory;
    private readonly string _latestFramePath;

    private Task? _captureTask;
    private bool _disposed;
    private GameCaptureFrame? _latestFrame;
    private DateTimeOffset _lastDebugDump = DateTimeOffset.MinValue;

    public event EventHandler<GameFrameCapturedEventArgs>? FrameCaptured;

    public GameCaptureService(
        ILogger logger,
        TimeSpan? captureInterval = null,
        TimeSpan? debugDumpInterval = null,
        string? captureDirectory = null)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        // Capture faster (e.g. 33ms ~ 30 FPS) to improve responsiveness and tracking during scrolling
        _captureInterval = captureInterval ?? TimeSpan.FromMilliseconds(500);
        _debugDumpInterval = debugDumpInterval ?? TimeSpan.FromSeconds(1);
        _captureDirectory = captureDirectory ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "ArcRaidersHelper",
            "captures");
        Directory.CreateDirectory(_captureDirectory);
        _latestFramePath = Path.Combine(_captureDirectory, "latest-frame.png");
    }

    public string LatestFramePath => _latestFramePath;

    public void Start()
    {
        ThrowIfDisposed();
        lock (_stateGate)
        {
            if (_cts is not null)
            {
                return;
            }

            _cts = new CancellationTokenSource();
            _captureTask = Task.Run(() => CaptureLoopAsync(_cts.Token));
        }
    }

    public void Stop()
    {
        CancellationTokenSource? cts;
        Task? captureTask;

        lock (_stateGate)
        {
            if (_cts is null)
            {
                return;
            }

            cts = _cts;
            captureTask = _captureTask;
            _cts = null;
            _captureTask = null;
        }

        if (cts is null)
        {
            return;
        }

        cts.Cancel();
        try
        {
            captureTask?.Wait();
        }
        catch (AggregateException ex)
        {
            if (ex.InnerException is not OperationCanceledException and not TaskCanceledException)
            {
                _logger.Log("GameCapture", $"Capture loop terminated unexpectedly: {ex.InnerException?.Message ?? ex.Message}");
            }
        }
        catch (TaskCanceledException)
        {
            // Expected during shutdown.
        }
        finally
        {
            cts.Dispose();
        }
    }

    public async Task<string?> DumpLatestFrameAsync(CancellationToken cancellationToken)
    {
        GameCaptureFrame? snapshot;
        lock (_frameGate)
        {
            snapshot = _latestFrame;
        }

        if (snapshot is null)
        {
            _logger.Log("GameCapture", "No frame is available to dump.");
            return null;
        }

        var fileName = $"frame-{snapshot.Timestamp:yyyyMMdd_HHmmssfff}.png";
        var path = Path.Combine(_captureDirectory, fileName);
        await Task.Run(() => snapshot.SaveAsPng(path), cancellationToken).ConfigureAwait(false);
        _logger.Log("GameCapture", $"Saved debug frame to '{path}'.");
        return path;
    }

    private async Task CaptureLoopAsync(CancellationToken cancellationToken)
    {
        _logger.Log("GameCapture", "Starting desktop capture loop.");
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                var frame = CaptureGameWindow();
                if (frame is null)
                {
                    await Task.Delay(_retryDelay, cancellationToken).ConfigureAwait(false);
                    continue;
                }

                lock (_frameGate)
                {
                    _latestFrame = frame;
                }

                FrameCaptured?.Invoke(this, new GameFrameCapturedEventArgs(frame));
                //MaybeDumpDebugFrame(frame);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.Log("GameCapture", $"Frame capture failed: {ex.Message}");
                try
                {
                    await Task.Delay(_retryDelay, cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                continue;
            }

            try
            {
                await Task.Delay(_captureInterval, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }

        _logger.Log("GameCapture", "Capture loop stopped.");
    }

    private void MaybeDumpDebugFrame(GameCaptureFrame frame)
    {
        var now = DateTimeOffset.UtcNow;
        if (now - _lastDebugDump < _debugDumpInterval)
        {
            return;
        }

        _lastDebugDump = now;
        _ = Task.Run(() =>
        {
            try
            {
                frame.SaveAsPng(_latestFramePath);
            }
            catch (Exception ex)
            {
                _logger.Log("GameCapture", $"Failed to write debug frame: {ex.Message}");
            }
        });
    }

    private IntPtr _gameWindowHandle = IntPtr.Zero;

    private GameCaptureFrame? CaptureGameWindow()
    {
        if (_gameWindowHandle == IntPtr.Zero || !NativeMethods.IsWindow(_gameWindowHandle))
        {
            _gameWindowHandle = FindGameWindow();
        }

        if (_gameWindowHandle == IntPtr.Zero)
        {
            _logger.Log("GameCapture", "ARC Raiders window not found (checked process 'PioneerGame').");
            return null;
        }

        var hWnd = _gameWindowHandle;

        if (NativeMethods.IsIconic(hWnd))
        {
            _logger.Log("GameCapture", "ARC Raiders window is minimized.");
            return null;
        }

        if (!NativeMethods.GetClientRect(hWnd, out var clientRect))
        {
            _logger.Log("GameCapture", "Failed to get client rect.");
            return null;
        }

        var topLeft = new NativeMethods.POINT { X = clientRect.Left, Y = clientRect.Top };
        var bottomRight = new NativeMethods.POINT { X = clientRect.Right, Y = clientRect.Bottom };

        if (!NativeMethods.ClientToScreen(hWnd, ref topLeft) || !NativeMethods.ClientToScreen(hWnd, ref bottomRight))
        {
            _logger.Log("GameCapture", "Failed to convert client coordinates to screen coordinates.");
            return null;
        }

        var width = bottomRight.X - topLeft.X;
        var height = bottomRight.Y - topLeft.Y;

        if (width <= 0 || height <= 0)
        {
            _logger.Log("GameCapture", $"Invalid window dimensions: {width}x{height}.");
            return null;
        }

        var bounds = new Rectangle(topLeft.X, topLeft.Y, width, height);
        using var bitmap = new Bitmap(bounds.Width, bounds.Height, PixelFormat.Format32bppArgb);
        using var graphics = Graphics.FromImage(bitmap);
        graphics.CopyFromScreen(bounds.Left, bounds.Top, 0, 0, bounds.Size, CopyPixelOperation.SourceCopy);
        return GameCaptureFrame.FromBitmap(bitmap, DateTimeOffset.UtcNow, bounds.Left, bounds.Top);
    }

    private IntPtr FindGameWindow()
    {
        var processes = Process.GetProcessesByName("PioneerGame");
        try
        {
            foreach (var process in processes)
            {
                if (process.MainWindowHandle != IntPtr.Zero)
                {
                    return process.MainWindowHandle;
                }
            }
        }
        finally
        {
            foreach (var process in processes)
            {
                process.Dispose();
            }
        }

        return IntPtr.Zero;
    }

    private void ThrowIfDisposed()
    {
        if (_disposed)
        {
            throw new ObjectDisposedException(nameof(GameCaptureService));
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        Stop();
    }
}

internal static class NativeMethods
{
    internal const int SM_CXSCREEN = 0;
    internal const int SM_CYSCREEN = 1;

    [DllImport("user32.dll")]
    internal static extern int GetSystemMetrics(int nIndex);

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    internal static extern IntPtr FindWindow(string? lpClassName, string? lpWindowName);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool GetClientRect(IntPtr hWnd, out RECT lpRect);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool ClientToScreen(IntPtr hWnd, ref POINT lpPoint);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool IsIconic(IntPtr hWnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool IsWindow(IntPtr hWnd);

    [StructLayout(LayoutKind.Sequential)]
    internal struct RECT
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct POINT
    {
        public int X;
        public int Y;
    }
}

internal sealed class GameFrameCapturedEventArgs : EventArgs
{
    public GameFrameCapturedEventArgs(GameCaptureFrame frame)
    {
        Frame = frame ?? throw new ArgumentNullException(nameof(frame));
    }

    public GameCaptureFrame Frame { get; }
}

internal sealed class GameCaptureFrame
{
    private readonly byte[] _pixelBuffer;

    private GameCaptureFrame(
        DateTimeOffset timestamp,
        int width,
        int height,
        int stride,
        int screenLeft,
        int screenTop,
        byte[] pixelBuffer)
    {
        Timestamp = timestamp;
        Width = width;
        Height = height;
        Stride = stride;
        ScreenLeft = screenLeft;
        ScreenTop = screenTop;
        _pixelBuffer = pixelBuffer;
    }

    public DateTimeOffset Timestamp { get; }
    public int Width { get; }
    public int Height { get; }
    public int Stride { get; }
    public int ScreenLeft { get; }
    public int ScreenTop { get; }
    public ReadOnlyMemory<byte> PixelBuffer => _pixelBuffer;

    public Rectangle GetPixelRectangle(NormalizedRectangle region)
    {
        return region.ToPixelRectangle(Width, Height);
    }

    public (double R, double G, double B) GetAverageColor(NormalizedRectangle region)
    {
        var rect = region.ToPixelRectangle(Width, Height);
        if (rect.Width <= 0 || rect.Height <= 0)
        {
            return (0, 0, 0);
        }

        long sumR = 0;
        long sumG = 0;
        long sumB = 0;

        for (var y = rect.Top; y < rect.Bottom; y++)
        {
            var rowStart = y * Stride;
            for (var x = rect.Left; x < rect.Right; x++)
            {
                var pixelIndex = rowStart + x * 4;
                sumB += _pixelBuffer[pixelIndex];
                sumG += _pixelBuffer[pixelIndex + 1];
                sumR += _pixelBuffer[pixelIndex + 2];
            }
        }

        var count = rect.Width * rect.Height;
        if (count == 0)
        {
            return (0, 0, 0);
        }

        return (sumR / (double)count, sumG / (double)count, sumB / (double)count);
    }

    public double GetColorCoverage(NormalizedRectangle region, Func<byte, byte, byte, bool> predicate)
    {
        if (predicate is null)
        {
            throw new ArgumentNullException(nameof(predicate));
        }

        var rect = region.ToPixelRectangle(Width, Height);
        if (rect.Width <= 0 || rect.Height <= 0)
        {
            return 0;
        }

        var matches = 0;
        var total = rect.Width * rect.Height;

        for (var y = rect.Top; y < rect.Bottom; y++)
        {
            var rowStart = y * Stride;
            for (var x = rect.Left; x < rect.Right; x++)
            {
                var pixelIndex = rowStart + x * 4;
                var b = _pixelBuffer[pixelIndex];
                var g = _pixelBuffer[pixelIndex + 1];
                var r = _pixelBuffer[pixelIndex + 2];
                if (predicate(b, g, r))
                {
                    matches++;
                }
            }
        }

        if (total == 0)
        {
            return 0;
        }

        return matches / (double)total;
    }

    public Bitmap ExtractBitmap(NormalizedRectangle region)
    {
        var rect = region.ToPixelRectangle(Width, Height);
        if (rect.Width <= 0 || rect.Height <= 0)
        {
            throw new InvalidOperationException("Region is outside of the captured frame.");
        }

        var bitmap = new Bitmap(rect.Width, rect.Height, PixelFormat.Format32bppArgb);
        var destRect = new Rectangle(0, 0, rect.Width, rect.Height);
        var destData = bitmap.LockBits(destRect, ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
        try
        {
            for (var y = 0; y < rect.Height; y++)
            {
                var sourceIndex = (rect.Top + y) * Stride + rect.Left * 4;
                var destination = destData.Scan0 + y * destData.Stride;
                Marshal.Copy(_pixelBuffer, sourceIndex, destination, rect.Width * 4);
            }
        }
        finally
        {
            bitmap.UnlockBits(destData);
        }

        return bitmap;
    }

    public async Task<SoftwareBitmap> ExtractSoftwareBitmapAsync(NormalizedRectangle region, BitmapPixelFormat pixelFormat = BitmapPixelFormat.Bgra8)
    {
        using var bitmap = ExtractBitmap(region);
        using var randomAccessStream = new InMemoryRandomAccessStream();
        var stream = randomAccessStream.AsStreamForWrite();
        try
        {
            bitmap.Save(stream, ImageFormat.Png);
            await stream.FlushAsync().ConfigureAwait(false);
            randomAccessStream.Seek(0);
            var decoder = await BitmapDecoder.CreateAsync(randomAccessStream);
            var softwareBitmap = await decoder.GetSoftwareBitmapAsync(pixelFormat, BitmapAlphaMode.Premultiplied);
            return softwareBitmap;
        }
        finally
        {
            stream.Dispose();
        }
    }

    public static GameCaptureFrame FromBitmap(Bitmap bitmap, DateTimeOffset timestamp, int screenLeft, int screenTop)
    {
        var rect = new Rectangle(0, 0, bitmap.Width, bitmap.Height);
        var data = bitmap.LockBits(rect, ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        try
        {
            var buffer = new byte[data.Height * data.Stride];
            Marshal.Copy(data.Scan0, buffer, 0, buffer.Length);
            return new GameCaptureFrame(timestamp, bitmap.Width, bitmap.Height, data.Stride, screenLeft, screenTop, buffer);
        }
        finally
        {
            bitmap.UnlockBits(data);
        }
    }

    public void SaveAsPng(string path)
    {
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        using var bitmap = new Bitmap(Width, Height, PixelFormat.Format32bppArgb);
        var rect = new Rectangle(0, 0, Width, Height);
        var data = bitmap.LockBits(rect, ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
        try
        {
            Marshal.Copy(_pixelBuffer, 0, data.Scan0, _pixelBuffer.Length);
        }
        finally
        {
            bitmap.UnlockBits(data);
        }

        bitmap.Save(path, ImageFormat.Png);
    }
}

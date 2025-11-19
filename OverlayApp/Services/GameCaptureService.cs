using OverlayApp.Infrastructure;
using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

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
        _captureInterval = captureInterval ?? TimeSpan.FromMilliseconds(250);
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
                var frame = CapturePrimaryMonitor();
                lock (_frameGate)
                {
                    _latestFrame = frame;
                }

                FrameCaptured?.Invoke(this, new GameFrameCapturedEventArgs(frame));
                MaybeDumpDebugFrame(frame);
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

    private static GameCaptureFrame CapturePrimaryMonitor()
    {
        var width = NativeMethods.GetSystemMetrics(NativeMethods.SM_CXSCREEN);
        var height = NativeMethods.GetSystemMetrics(NativeMethods.SM_CYSCREEN);
        if (width <= 0 || height <= 0)
        {
            throw new InvalidOperationException("Unable to determine the primary monitor size.");
        }

        var bounds = new Rectangle(0, 0, width, height);
        using var bitmap = new Bitmap(bounds.Width, bounds.Height, PixelFormat.Format32bppArgb);
        using var graphics = Graphics.FromImage(bitmap);
        graphics.CopyFromScreen(bounds.Left, bounds.Top, 0, 0, bounds.Size, CopyPixelOperation.SourceCopy);
        return GameCaptureFrame.FromBitmap(bitmap, DateTimeOffset.UtcNow, bounds.Left, bounds.Top);
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

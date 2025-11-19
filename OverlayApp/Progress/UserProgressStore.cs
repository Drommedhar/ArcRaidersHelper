using OverlayApp.Data;
using OverlayApp.Infrastructure;
using System;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace OverlayApp.Progress;

internal sealed class UserProgressStore : IDisposable
{
    private readonly ArcDataPaths _paths = new();
    private readonly ILogger _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly JsonSerializerOptions _serializerOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
        AllowTrailingCommas = true,
        ReadCommentHandling = JsonCommentHandling.Skip
    };
    private FileSystemWatcher? _watcher;
    private CancellationTokenSource? _debounceCts;
    private readonly object _debounceLock = new();

    private bool _disposed;

    public event EventHandler<UserProgressState>? ProgressChanged;

    public UserProgressStore(ILogger logger)
    {
        _logger = logger;
        _paths.EnsureBaseDirectories();
        InitializeWatcher();
    }

    private void InitializeWatcher()
    {
        try
        {
            var directory = Path.GetDirectoryName(_paths.ProgressFilePath);
            if (string.IsNullOrEmpty(directory)) return;

            _watcher = new FileSystemWatcher(directory, Path.GetFileName(_paths.ProgressFilePath))
            {
                NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.CreationTime | NotifyFilters.Size,
                EnableRaisingEvents = true
            };

            _watcher.Changed += OnFileChanged;
            _watcher.Created += OnFileChanged;
        }
        catch (Exception ex)
        {
            _logger.Log("ProgressStore", $"Failed to initialize file watcher: {ex.Message}");
        }
    }

    private async void OnFileChanged(object sender, FileSystemEventArgs e)
    {
        CancellationToken token;
        lock (_debounceLock)
        {
            _debounceCts?.Cancel();
            _debounceCts?.Dispose();
            _debounceCts = new CancellationTokenSource();
            token = _debounceCts.Token;
        }

        try
        {
            // Debounce: wait for file writes to settle
            await Task.Delay(500, token);
            if (token.IsCancellationRequested) return;

            var state = await LoadAsync(token);
            
            if (!token.IsCancellationRequested)
            {
                ProgressChanged?.Invoke(this, state);
            }
        }
        catch (OperationCanceledException)
        {
            // Ignore
        }
        catch (Exception ex)
        {
            _logger.Log("ProgressStore", $"Error handling file change: {ex.Message}");
        }
    }

    public async Task<UserProgressState> LoadAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!File.Exists(_paths.ProgressFilePath))
            {
                return UserProgressState.CreateDefault();
            }

            await using var stream = File.OpenRead(_paths.ProgressFilePath);
            var state = await JsonSerializer.DeserializeAsync<UserProgressState>(stream, _serializerOptions, cancellationToken).ConfigureAwait(false);
            return state ?? UserProgressState.CreateDefault();
        }
        catch (JsonException ex)
        {
            _logger.Log("Progress", $"Failed to parse progress file: {ex.Message}");
            return UserProgressState.CreateDefault();
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task SaveAsync(UserProgressState state, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            state.LastUpdatedUtc = DateTimeOffset.UtcNow;
            await using var stream = File.Create(_paths.ProgressFilePath);
            await JsonSerializer.SerializeAsync(stream, state, _serializerOptions, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _watcher?.Dispose();
        _debounceCts?.Cancel();
        _debounceCts?.Dispose();
        _gate.Dispose();
        GC.SuppressFinalize(this);
    }
}

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

    private bool _disposed;

    public UserProgressStore(ILogger logger)
    {
        _logger = logger;
        _paths.EnsureBaseDirectories();
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
        _gate.Dispose();
        GC.SuppressFinalize(this);
    }
}

using OverlayApp.Data.Models;
using OverlayApp.Infrastructure;
using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace OverlayApp.Data;

internal sealed class ArcDataSyncService : IDisposable
{
    private const string RepoOwner = "RaidTheory";
    private const string RepoName = "arcraiders-data";
    private const string RepoBranch = "main";
    private static readonly Uri CommitEndpoint = new($"https://api.github.com/repos/{RepoOwner}/{RepoName}/commits/{RepoBranch}");
    private static readonly Uri ArchiveEndpoint = new($"https://codeload.github.com/{RepoOwner}/{RepoName}/zip/refs/heads/{RepoBranch}");

    private readonly HttpClient _httpClient;
    private readonly ILogger _logger;
    private readonly ArcDataPaths _paths = new();
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly JsonSerializerOptions _serializerOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true
    };

    private ArcDataSnapshot? _cachedSnapshot;
    private ArcDataMetadata? _metadata;
    private bool _disposed;

    public ArcDataSyncService(string? githubToken, ILogger logger)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _paths.EnsureBaseDirectories();

        _httpClient = new HttpClient();
        _httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("ArcRaidersHelper/1.0");
        _httpClient.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        if (!string.IsNullOrWhiteSpace(githubToken))
        {
            _httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", githubToken.Trim());
        }
    }

    public async Task<ArcDataSnapshot> InitializeAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await EnsureLatestDataAsync(forceDownload: false, cancellationToken).ConfigureAwait(false);
            _cachedSnapshot ??= await LoadSnapshotAsync(cancellationToken).ConfigureAwait(false);
            return _cachedSnapshot;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<ArcDataSnapshot> RefreshAsync(bool forceDownload, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await EnsureLatestDataAsync(forceDownload, cancellationToken).ConfigureAwait(false);
            _cachedSnapshot = await LoadSnapshotAsync(cancellationToken).ConfigureAwait(false);
            return _cachedSnapshot;
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task EnsureLatestDataAsync(bool forceDownload, CancellationToken cancellationToken)
    {
        _metadata ??= await LoadMetadataAsync(cancellationToken).ConfigureAwait(false);

        string? remoteCommit = null;
        try
        {
            remoteCommit = await GetRemoteCommitShaAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.Log("DataSync", $"Failed to query GitHub for latest data: {ex.Message}");
        }

        if (remoteCommit is null && _metadata?.CommitSha is null)
        {
            throw new InvalidOperationException("Arc data cache is empty and GitHub could not be reached.");
        }

        if (remoteCommit is null)
        {
            return;
        }

        var hasLocalData = Directory.Exists(_paths.RepositoryDirectory);
        var isUpToDate = !forceDownload
            && hasLocalData
            && string.Equals(_metadata?.CommitSha, remoteCommit, StringComparison.OrdinalIgnoreCase);

        if (isUpToDate)
        {
            if (_metadata is not null)
            {
                _metadata.LastCheckedUtc = DateTimeOffset.UtcNow;
                await SaveMetadataAsync(_metadata, cancellationToken).ConfigureAwait(false);
            }

            return;
        }

        await DownloadAndExtractAsync(remoteCommit, cancellationToken).ConfigureAwait(false);
        _metadata = ArcDataMetadata.Create(remoteCommit);
        _metadata.DataSizeBytes = CalculateDirectorySize(_paths.RepositoryDirectory);
        await SaveMetadataAsync(_metadata, cancellationToken).ConfigureAwait(false);
        _cachedSnapshot = null;
        _logger.Log("DataSync", $"Synchronized arc data at commit {remoteCommit}.");
    }

    private async Task<string?> GetRemoteCommitShaAsync(CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, CommitEndpoint);
        using var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            _logger.Log("DataSync", $"GitHub commit lookup failed ({(int)response.StatusCode} {response.ReasonPhrase}).");
            return null;
        }

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);
        return document.RootElement.TryGetProperty("sha", out var shaElement)
            ? shaElement.GetString()
            : null;
    }

    private async Task DownloadAndExtractAsync(string commitSha, CancellationToken cancellationToken)
    {
        var archivePath = Path.Combine(_paths.DownloadsDirectory, $"arcdata_{commitSha}.zip");
        await DownloadArchiveAsync(archivePath, cancellationToken).ConfigureAwait(false);
        ExtractArchive(archivePath);
    }

    private async Task DownloadArchiveAsync(string destinationFile, CancellationToken cancellationToken)
    {
        using var response = await _httpClient.GetAsync(ArchiveEndpoint, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        await using var httpStream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        await using var fileStream = File.Create(destinationFile);
        await httpStream.CopyToAsync(fileStream, cancellationToken).ConfigureAwait(false);
    }

    private void ExtractArchive(string archivePath)
    {
        var tempExtractRoot = Path.Combine(_paths.TempDirectory, $"extract_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempExtractRoot);

        try
        {
            ZipFile.ExtractToDirectory(archivePath, tempExtractRoot);
            var extractedRoot = Directory.EnumerateDirectories(tempExtractRoot).FirstOrDefault();
            if (string.IsNullOrWhiteSpace(extractedRoot))
            {
                throw new InvalidOperationException("Archive extraction failed: root folder not found.");
            }

            if (Directory.Exists(_paths.RepositoryDirectory))
            {
                Directory.Delete(_paths.RepositoryDirectory, recursive: true);
            }

            Directory.Move(extractedRoot, _paths.RepositoryDirectory);
        }
        finally
        {
            TryDeleteDirectory(tempExtractRoot);
            TryDeleteFile(archivePath);
        }
    }

    private async Task<ArcDataSnapshot> LoadSnapshotAsync(CancellationToken cancellationToken)
    {
        if (!Directory.Exists(_paths.RepositoryDirectory))
        {
            throw new DirectoryNotFoundException($"Arc data repository not found at {_paths.RepositoryDirectory}.");
        }

        var projectsPath = Path.Combine(_paths.RepositoryDirectory, "projects.json");
        var projects = await DeserializeFileAsync<List<ArcProject>>(projectsPath, cancellationToken).ConfigureAwait(false)
            ?? new List<ArcProject>();

        var itemsDirectory = Path.Combine(_paths.RepositoryDirectory, "items");
        var items = await DeserializeDirectoryAsync<ArcItem>(itemsDirectory, cancellationToken).ConfigureAwait(false);

        var hideoutDirectory = Path.Combine(_paths.RepositoryDirectory, "hideout");
        var hideoutModules = await DeserializeDirectoryAsync<HideoutModule>(hideoutDirectory, cancellationToken).ConfigureAwait(false);

        var questsDirectory = Path.Combine(_paths.RepositoryDirectory, "quests");
        var quests = await DeserializeDirectoryAsync<ArcQuest>(questsDirectory, cancellationToken).ConfigureAwait(false);

        var commitSha = _metadata?.CommitSha;
        var lastSynced = _metadata?.LastSyncedUtc ?? DateTimeOffset.MinValue;

        return new ArcDataSnapshot(commitSha, lastSynced, projects, items, hideoutModules, quests);
    }

    private async Task<ArcDataMetadata?> LoadMetadataAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(_paths.MetadataFilePath))
        {
            return null;
        }

        try
        {
            await using var stream = File.OpenRead(_paths.MetadataFilePath);
            return await JsonSerializer.DeserializeAsync<ArcDataMetadata>(stream, _serializerOptions, cancellationToken).ConfigureAwait(false);
        }
        catch (JsonException ex)
        {
            _logger.Log("DataSync", $"Failed to read metadata: {ex.Message}");
            return null;
        }
    }

    private async Task SaveMetadataAsync(ArcDataMetadata metadata, CancellationToken cancellationToken)
    {
        await using var stream = File.Create(_paths.MetadataFilePath);
        await JsonSerializer.SerializeAsync(stream, metadata, _serializerOptions, cancellationToken).ConfigureAwait(false);
    }

    private async Task<IReadOnlyDictionary<string, T>> DeserializeDirectoryAsync<T>(string directoryPath, CancellationToken cancellationToken)
        where T : class, IArcEntity
    {
        var results = new Dictionary<string, T>(StringComparer.OrdinalIgnoreCase);
        if (!Directory.Exists(directoryPath))
        {
            return results;
        }

        foreach (var file in Directory.EnumerateFiles(directoryPath, "*.json", SearchOption.TopDirectoryOnly))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var entity = await DeserializeFileAsync<T>(file, cancellationToken).ConfigureAwait(false);
            if (entity is null)
            {
                _logger.Log("DataSync", $"Skipping malformed file {Path.GetFileName(file)} in {directoryPath}.");
                continue;
            }

            var key = !string.IsNullOrWhiteSpace(entity.Id)
                ? entity.Id
                : Path.GetFileNameWithoutExtension(file);
            results[key] = entity;
        }

        return results;
    }

    private async Task<T?> DeserializeFileAsync<T>(string filePath, CancellationToken cancellationToken)
    {
        if (!File.Exists(filePath))
        {
            return default;
        }

        try
        {
            await using var stream = File.OpenRead(filePath);
            return await JsonSerializer.DeserializeAsync<T>(stream, _serializerOptions, cancellationToken).ConfigureAwait(false);
        }
        catch (JsonException ex)
        {
            _logger.Log("DataSync", $"Failed to parse {Path.GetFileName(filePath)}: {ex.Message}");
            return default;
        }
    }

    private static long CalculateDirectorySize(string directoryPath)
    {
        if (!Directory.Exists(directoryPath))
        {
            return 0;
        }

        return Directory.EnumerateFiles(directoryPath, "*", SearchOption.AllDirectories)
            .Select(path => new FileInfo(path).Length)
            .Sum();
    }

    private static void TryDeleteDirectory(string directoryPath)
    {
        try
        {
            if (Directory.Exists(directoryPath))
            {
                Directory.Delete(directoryPath, recursive: true);
            }
        }
        catch
        {
            // Best-effort cleanup.
        }
    }

    private static void TryDeleteFile(string filePath)
    {
        try
        {
            if (File.Exists(filePath))
            {
                File.Delete(filePath);
            }
        }
        catch
        {
            // Best-effort cleanup.
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _httpClient.Dispose();
        _gate.Dispose();
        GC.SuppressFinalize(this);
    }
}

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace OverlayApp.Infrastructure;

internal sealed class UpdateService : IDisposable
{
    private const string Owner = "Drommedhar";
    private const string Repository = "ArcRaidersHelper";
    private const string UserAgent = "ArcRaidersHelper/1.0";
    private static readonly string LatestReleaseUrl = $"https://github.com/{Owner}/{Repository}/releases/latest";
    private static readonly Uri LatestReleaseApiUri = new($"https://api.github.com/repos/{Owner}/{Repository}/releases/latest");

    private readonly HttpClient _redirectClient;
    private readonly HttpClient _apiClient;
    private readonly string? _authToken;
    private readonly ILogger _logger;

    public UpdateService(string? authToken, ILogger logger)
    {
        _authToken = string.IsNullOrWhiteSpace(authToken) ? null : authToken.Trim();
        _logger = logger;
        var redirectHandler = new HttpClientHandler
        {
            AllowAutoRedirect = false
        };

        _redirectClient = new HttpClient(redirectHandler, disposeHandler: true)
        {
            Timeout = TimeSpan.FromSeconds(10)
        };
        _redirectClient.DefaultRequestHeaders.UserAgent.ParseAdd(UserAgent);

        _apiClient = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(30)
        };
        _apiClient.DefaultRequestHeaders.UserAgent.ParseAdd(UserAgent);
        _apiClient.DefaultRequestHeaders.Accept.Clear();
        _apiClient.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");

        if (_authToken is not null)
        {
            var header = new AuthenticationHeaderValue("token", _authToken);
            _apiClient.DefaultRequestHeaders.Authorization = header;
            _redirectClient.DefaultRequestHeaders.Authorization = header;
        }
    }

    public async Task<UpdateCheckResult> CheckForUpdatesAsync(Version currentVersion, CancellationToken cancellationToken)
    {
        var release = await TryFetchLatestReleaseAsync(cancellationToken).ConfigureAwait(false);
        if (release is null || release.Version is null)
        {
            _logger.Log("UpdateService", "Unable to determine the latest release from GitHub.");
            return UpdateCheckResult.Failed("Unable to determine the latest release.");
        }

        if (release.Version <= currentVersion)
        {
            _logger.Log("UpdateService", $"Application is up to date (current={currentVersion}, latest={release.Version}).");
            return UpdateCheckResult.UpToDate(release.Version);
        }

        var asset = release.PrimaryAsset;
        if (asset is null)
        {
            _logger.Log("UpdateService", "Latest release does not include a downloadable asset.");
            return UpdateCheckResult.Failed("Latest release does not include a downloadable asset.", release.Version);
        }

        var downloadDirectory = EnsureDownloadDirectory();
        var fileName = SanitizeFileName(asset.Name);
        var destinationPath = Path.Combine(downloadDirectory, fileName);

        if (File.Exists(destinationPath) && new FileInfo(destinationPath).Length > 0)
        {
            _logger.Log("UpdateService", $"Latest release already downloaded at {destinationPath}.");
            return UpdateCheckResult.AlreadyDownloaded(release.Version, destinationPath);
        }

        _logger.Log("UpdateService", $"Downloading release asset '{asset.Name}' to {destinationPath}.");
        return await DownloadAssetAsync(asset.DownloadUrl, destinationPath, release.Version, cancellationToken).ConfigureAwait(false);
    }

    public void Dispose()
    {
        _apiClient.Dispose();
        _redirectClient.Dispose();
    }

    private static string EnsureDownloadDirectory()
    {
        var directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ArcRaidersHelper", "updates");
        Directory.CreateDirectory(directory);
        return directory;
    }

    private static string SanitizeFileName(string candidate)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var sanitizedChars = candidate.Select(ch => invalid.Contains(ch) ? '_' : ch).ToArray();
        var sanitized = new string(sanitizedChars);
        return string.IsNullOrWhiteSpace(sanitized) ? $"ArcRaidersHelper-update-{DateTime.UtcNow:yyyyMMddHHmmss}.bin" : sanitized;
    }

    private async Task<UpdateCheckResult> DownloadAssetAsync(Uri downloadUri, string destinationPath, Version version, CancellationToken token)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, downloadUri);
            request.Headers.Accept.Clear();
            request.Headers.Accept.ParseAdd("application/octet-stream");
            if (_authToken is not null)
            {
                request.Headers.Authorization = new AuthenticationHeaderValue("token", _authToken);
            }

            using var response = await _apiClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                _logger.Log("UpdateService", $"Download failed with status {(int)response.StatusCode} ({response.StatusCode}).");
                return UpdateCheckResult.Failed($"Unable to download release asset ({(int)response.StatusCode}).", version);
            }

            var tempFile = Path.GetTempFileName();
            try
            {
                await using (var targetStream = File.Open(tempFile, FileMode.Create, FileAccess.Write, FileShare.None))
                {
                    await response.Content.CopyToAsync(targetStream, token).ConfigureAwait(false);
                }

                File.Move(tempFile, destinationPath, overwrite: true);
            }
            finally
            {
                if (File.Exists(tempFile))
                {
                    File.Delete(tempFile);
                }
            }

            return UpdateCheckResult.Downloaded(version, destinationPath);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException)
        {
            _logger.Log("UpdateService", $"Exception during download: {ex.Message}");
            return UpdateCheckResult.Failed("Downloading the newest release failed.", version);
        }
    }

    private async Task<GitHubRelease?> TryFetchLatestReleaseAsync(CancellationToken token)
    {
        var release = await TryFetchReleaseAsync(LatestReleaseApiUri, token).ConfigureAwait(false);
        if (release is not null)
        {
            _logger.Log("UpdateService", "Successfully resolved latest release via GitHub API.");
            return release;
        }

        var tag = await TryResolveLatestTagAsync(token).ConfigureAwait(false);
        if (!string.IsNullOrWhiteSpace(tag))
        {
            var releaseByTag = await TryFetchReleaseByTagAsync(tag!, token).ConfigureAwait(false);
            if (releaseByTag is not null)
            {
                return releaseByTag;
            }
        }

        _logger.Log("UpdateService", "Unable to resolve latest release via API or redirect.");
        return null;
    }

    private async Task<string?> TryResolveLatestTagAsync(CancellationToken token)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Head, LatestReleaseUrl);
            if (_authToken is not null)
            {
                request.Headers.Authorization = new AuthenticationHeaderValue("token", _authToken);
            }
            using var response = await _redirectClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);

            if (IsRedirect(response.StatusCode) && response.Headers.Location is not null)
            {
                var redirectUri = response.Headers.Location.IsAbsoluteUri
                    ? response.Headers.Location
                    : new Uri(new Uri(LatestReleaseUrl), response.Headers.Location);

                var tag = redirectUri.Segments.LastOrDefault()?.Trim('/');
                if (!string.IsNullOrWhiteSpace(tag) && !tag.Equals("latest", StringComparison.OrdinalIgnoreCase))
                {
                    return tag;
                }
            }
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            _logger.Log("UpdateService", $"Failed to resolve latest tag: {ex.Message}");
        }

        return null;
    }

    private Task<GitHubRelease?> TryFetchReleaseByTagAsync(string tag, CancellationToken token)
    {
        var uri = new Uri($"https://api.github.com/repos/{Owner}/{Repository}/releases/tags/{Uri.EscapeDataString(tag)}");
        return TryFetchReleaseAsync(uri, token);
    }

    private async Task<GitHubRelease?> TryFetchReleaseAsync(Uri uri, CancellationToken token)
    {
        try
        {
            using var response = await _apiClient.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                return null;
            }

            await using var payload = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
            using var document = await JsonDocument.ParseAsync(payload, cancellationToken: token).ConfigureAwait(false);
            return ParseRelease(document.RootElement);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            _logger.Log("UpdateService", $"Failed to fetch release data from {uri}: {ex.Message}");
            return null;
        }
    }

    private static GitHubRelease? ParseRelease(JsonElement root)
    {
        if (!root.TryGetProperty("tag_name", out var tagProperty))
        {
            return null;
        }

        var tagValue = tagProperty.GetString();
        if (string.IsNullOrWhiteSpace(tagValue))
        {
            return null;
        }

        var name = root.TryGetProperty("name", out var nameProperty) ? nameProperty.GetString() : null;
        var version = ParseSemanticVersion(tagValue, name);

        var assets = new List<GitHubAsset>();
        if (root.TryGetProperty("assets", out var assetsProperty))
        {
            foreach (var assetElement in assetsProperty.EnumerateArray())
            {
                var assetName = assetElement.TryGetProperty("name", out var assetNameProperty) ? assetNameProperty.GetString() : null;
                var downloadUrl = assetElement.TryGetProperty("url", out var apiUrlProperty) ? apiUrlProperty.GetString() : null;
                if (string.IsNullOrWhiteSpace(downloadUrl) && assetElement.TryGetProperty("browser_download_url", out var browserUrlProperty))
                {
                    downloadUrl = browserUrlProperty.GetString();
                }

                if (string.IsNullOrWhiteSpace(assetName) || string.IsNullOrWhiteSpace(downloadUrl))
                {
                    continue;
                }

                if (!Uri.TryCreate(downloadUrl, UriKind.Absolute, out var assetUri))
                {
                    continue;
                }

                assets.Add(new GitHubAsset(assetName, assetUri));
            }
        }

        return new GitHubRelease(tagValue!, version, name, assets);
    }

    private static Version? ParseSemanticVersion(string? tag, string? name)
    {
        foreach (var candidate in new[] { tag, name })
        {
            if (string.IsNullOrWhiteSpace(candidate))
            {
                continue;
            }

            var normalized = candidate.Trim();
            if (normalized.StartsWith("v", StringComparison.OrdinalIgnoreCase))
            {
                normalized = normalized[1..];
            }

            var plusIndex = normalized.IndexOf('+');
            if (plusIndex > 0)
            {
                normalized = normalized[..plusIndex];
            }

            var dashIndex = normalized.IndexOf('-');
            if (dashIndex > 0)
            {
                normalized = normalized[..dashIndex];
            }

            if (Version.TryParse(normalized, out var parsed))
            {
                return parsed;
            }
        }

        return null;
    }

    private static bool IsRedirect(HttpStatusCode statusCode)
    {
        var code = (int)statusCode;
        return code is >= 300 and < 400;
    }

    private sealed record GitHubAsset(string Name, Uri DownloadUrl);

    private sealed class GitHubRelease
    {
        public GitHubRelease(string tag, Version? version, string? name, IReadOnlyList<GitHubAsset> assets)
        {
            Tag = tag;
            Version = version;
            Name = name;
            Assets = assets;
        }

        public string Tag { get; }
        public Version? Version { get; }
        public string? Name { get; }
        public IReadOnlyList<GitHubAsset> Assets { get; }

        public GitHubAsset? PrimaryAsset => SelectPreferredAsset(Assets);

        private static GitHubAsset? SelectPreferredAsset(IReadOnlyList<GitHubAsset> assets)
        {
            var filtered = assets
                .Where(asset => !asset.Name.Contains("source code", StringComparison.OrdinalIgnoreCase))
                .Select(asset => new { Asset = asset, Score = GetAssetPriority(asset.Name) })
                .OrderBy(entry => entry.Score)
                .ThenBy(entry => entry.Asset.Name, StringComparer.OrdinalIgnoreCase);

            return filtered.FirstOrDefault()?.Asset;
        }

        private static int GetAssetPriority(string name)
        {
            var extension = Path.GetExtension(name).ToLowerInvariant();
            return extension switch
            {
                ".msi" => 0,
                ".exe" => 1,
                ".zip" => 2,
                ".7z" => 3,
                _ => 10
            };
        }
    }
}

internal enum UpdateCheckStatus
{
    UpToDate,
    Downloaded,
    AlreadyDownloaded,
    Failed
}

internal sealed record UpdateCheckResult(UpdateCheckStatus Status, Version? LatestVersion, string? DownloadedFile, string? ErrorMessage)
{
    public static UpdateCheckResult UpToDate(Version version) => new(UpdateCheckStatus.UpToDate, version, null, null);

    public static UpdateCheckResult Downloaded(Version version, string path) => new(UpdateCheckStatus.Downloaded, version, path, null);

    public static UpdateCheckResult AlreadyDownloaded(Version version, string path) => new(UpdateCheckStatus.AlreadyDownloaded, version, path, null);

    public static UpdateCheckResult Failed(string message, Version? version = null) => new(UpdateCheckStatus.Failed, version, null, message);
}

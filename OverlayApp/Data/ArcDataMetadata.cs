using System;
using System.Text.Json.Serialization;

namespace OverlayApp.Data;

internal sealed class ArcDataMetadata
{
    [JsonPropertyName("commitSha")]
    public string? CommitSha { get; set; }

    [JsonPropertyName("lastSyncedUtc")]
    public DateTimeOffset LastSyncedUtc { get; set; }

    [JsonPropertyName("lastCheckedUtc")]
    public DateTimeOffset LastCheckedUtc { get; set; }

    [JsonPropertyName("dataSizeBytes")]
    public long DataSizeBytes { get; set; }

    public static ArcDataMetadata Create(string? commitSha)
    {
        var timestamp = DateTimeOffset.UtcNow;
        return new ArcDataMetadata
        {
            CommitSha = commitSha,
            LastSyncedUtc = timestamp,
            LastCheckedUtc = timestamp
        };
    }
}

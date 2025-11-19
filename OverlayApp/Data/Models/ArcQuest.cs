using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace OverlayApp.Data.Models;

internal sealed class ArcQuest : IArcEntity
{
    [JsonPropertyName("id")]
    public string? Id { get; set; }

    [JsonPropertyName("name")]
    public Dictionary<string, string>? Name { get; set; }

    [JsonPropertyName("description")]
    public Dictionary<string, string>? Description { get; set; }

    [JsonPropertyName("trader")]
    public string? Trader { get; set; }

    [JsonPropertyName("videoUrl")]
    public string? VideoUrl { get; set; }

    [JsonPropertyName("updatedAt")]
    public string? UpdatedAt { get; set; }

    [JsonPropertyName("xp")]
    public int Xp { get; set; }

    [JsonPropertyName("objectives")]
    public List<Dictionary<string, JsonElement>> Objectives { get; set; } = new();

    [JsonPropertyName("requiredItemIds")]
    public List<ProjectPhaseItemRequirement>? RequiredItems { get; set; }

    [JsonPropertyName("rewardItemIds")]
    public List<ProjectPhaseItemRequirement>? RewardItems { get; set; }

    [JsonPropertyName("previousQuestIds")]
    public List<string>? PreviousQuestIds { get; set; }

    [JsonPropertyName("nextQuestIds")]
    public List<string>? NextQuestIds { get; set; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? ExtensionData { get; set; }
}

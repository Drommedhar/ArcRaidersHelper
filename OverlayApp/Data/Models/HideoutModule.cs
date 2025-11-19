using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace OverlayApp.Data.Models;

internal sealed class HideoutModule : IArcEntity
{
    [JsonPropertyName("id")]
    public string? Id { get; set; }

    [JsonPropertyName("name")]
    public Dictionary<string, string>? Name { get; set; }

    [JsonPropertyName("maxLevel")]
    public int MaxLevel { get; set; }

    [JsonPropertyName("levels")]
    public List<HideoutLevel> Levels { get; set; } = new();

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? ExtensionData { get; set; }
}

internal sealed class HideoutLevel
{
    [JsonPropertyName("level")]
    public int Level { get; set; }

    [JsonPropertyName("requirementItemIds")]
    public List<ProjectPhaseItemRequirement>? RequirementItems { get; set; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? ExtensionData { get; set; }
}

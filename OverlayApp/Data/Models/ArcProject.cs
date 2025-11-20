using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace OverlayApp.Data.Models;

internal sealed class ArcProject : IArcEntity
{
    [JsonPropertyName("id")]
    public string? Id { get; set; }

    [JsonPropertyName("name")]
    public Dictionary<string, string>? Name { get; set; }

    [JsonPropertyName("description")]
    public Dictionary<string, string>? Description { get; set; }

    [JsonPropertyName("phases")]
    public List<ProjectPhase> Phases { get; set; } = new();

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? ExtensionData { get; set; }
}

internal sealed class ProjectPhase
{
    [JsonPropertyName("phase")]
    public int Phase { get; set; }

    [JsonPropertyName("name")]
    public Dictionary<string, string>? Name { get; set; }

    [JsonPropertyName("description")]
    public Dictionary<string, string>? Description { get; set; }

    [JsonPropertyName("requirementItemIds")]
    public List<ProjectPhaseItemRequirement>? RequirementItems { get; set; }

    [JsonPropertyName("requirementCategories")]
    public List<ProjectPhaseCategoryRequirement>? RequirementCategories { get; set; }
}

internal sealed class ProjectPhaseItemRequirement
{
    [JsonPropertyName("itemId")]
    public string? ItemId { get; set; }

    [JsonPropertyName("quantity")]
    public int Quantity { get; set; }
}

internal sealed class ProjectPhaseCategoryRequirement
{
    [JsonPropertyName("category")]
    public string? Category { get; set; }

    [JsonPropertyName("valueRequired")]
    public double ValueRequired { get; set; }
}

using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace OverlayApp.Data.Models;

internal sealed class ArcItem : IArcEntity
{
    [JsonPropertyName("id")]
    public string? Id { get; set; }

    [JsonPropertyName("name")]
    public Dictionary<string, string>? Name { get; set; }

    [JsonPropertyName("description")]
    public Dictionary<string, string>? Description { get; set; }

    [JsonPropertyName("type")]
    public string? Type { get; set; }

    [JsonPropertyName("rarity")]
    public string? Rarity { get; set; }

    [JsonPropertyName("value")]
    public int? Value { get; set; }

    [JsonPropertyName("recyclesInto")]
    public Dictionary<string, int>? RecyclesInto { get; set; }

    [JsonPropertyName("salvagesInto")]
    public Dictionary<string, int>? SalvagesInto { get; set; }

    [JsonPropertyName("weightKg")]
    public double? WeightKg { get; set; }

    [JsonPropertyName("stackSize")]
    public int? StackSize { get; set; }

    [JsonPropertyName("foundIn")]
    public string? FoundIn { get; set; }

    [JsonPropertyName("imageFilename")]
    public string? ImageFilename { get; set; }

    [JsonPropertyName("updatedAt")]
    public string? UpdatedAt { get; set; }

    [JsonPropertyName("effects")]
    public Dictionary<string, Dictionary<string, JsonElement>>? Effects { get; set; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? ExtensionData { get; set; }
}

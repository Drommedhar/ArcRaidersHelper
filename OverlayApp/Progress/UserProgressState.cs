using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace OverlayApp.Progress;

internal sealed class UserProgressState
{
    [JsonPropertyName("quests")]
    public List<QuestProgressState> Quests { get; set; } = new();

    [JsonPropertyName("projects")]
    public List<ProjectProgressState> Projects { get; set; } = new();

    [JsonPropertyName("hideout")]
    public List<HideoutProgressState> HideoutModules { get; set; } = new();

    [JsonPropertyName("inventory")]
    public List<InventoryItemState> Inventory { get; set; } = new();

    [JsonPropertyName("customNeeds")]
    public List<ItemNeedOverride> CustomNeeds { get; set; } = new();

    [JsonPropertyName("lastUpdatedUtc")]
    public DateTimeOffset LastUpdatedUtc { get; set; } = DateTimeOffset.UtcNow;

    public static UserProgressState CreateDefault() => new();
}

internal sealed class QuestProgressState
{
    [JsonPropertyName("questId")]
    public string QuestId { get; set; } = string.Empty;

    [JsonPropertyName("status")]
    public QuestProgressStatus Status { get; set; } = QuestProgressStatus.NotStarted;

    [JsonPropertyName("tracked")]
    public bool Tracked { get; set; }

    [JsonPropertyName("completedObjectives")]
    public List<string> CompletedObjectiveIds { get; set; } = new();

    [JsonPropertyName("notes")]
    public string? Notes { get; set; }
}

internal sealed class ProjectProgressState
{
    [JsonPropertyName("projectId")]
    public string ProjectId { get; set; } = string.Empty;

    [JsonPropertyName("highestPhaseCompleted")]
    public int HighestPhaseCompleted { get; set; }

    [JsonPropertyName("tracking")]
    public bool Tracking { get; set; }
}

internal sealed class HideoutProgressState
{
    [JsonPropertyName("moduleId")]
    public string ModuleId { get; set; } = string.Empty;

    [JsonPropertyName("currentLevel")]
    public int CurrentLevel { get; set; }

    [JsonPropertyName("tracking")]
    public bool Tracking { get; set; }
}

internal sealed class InventoryItemState
{
    [JsonPropertyName("itemId")]
    public string ItemId { get; set; } = string.Empty;

    [JsonPropertyName("quantity")]
    public int Quantity { get; set; }
}

internal sealed class ItemNeedOverride
{
    [JsonPropertyName("itemId")]
    public string ItemId { get; set; } = string.Empty;

    [JsonPropertyName("quantity")]
    public int Quantity { get; set; }

    [JsonPropertyName("reason")]
    public string? Reason { get; set; }
}

internal enum QuestProgressStatus
{
    NotStarted = 0,
    InProgress = 1,
    Completed = 2,
    Abandoned = 3
}

using System;
using System.Collections.Generic;

namespace OverlayApp.Progress;

internal sealed class ProgressReport
{
    public ProgressReport(
        IReadOnlyList<ActiveQuestSummary> activeQuests,
        IReadOnlyList<NeededItemSummary> neededItems,
        IReadOnlyList<RequirementGroup> groupedRequirements,
        ProgressCompletionMetrics completion)
    {
        ActiveQuests = activeQuests;
        NeededItems = neededItems;
        GroupedRequirements = groupedRequirements;
        Completion = completion;
    }

    public IReadOnlyList<ActiveQuestSummary> ActiveQuests { get; }

    public IReadOnlyList<NeededItemSummary> NeededItems { get; }

    public IReadOnlyList<RequirementGroup> GroupedRequirements { get; }

    public ProgressCompletionMetrics Completion { get; }
}

internal sealed class RequirementGroup
{
    public string Category { get; init; } = string.Empty;
    public List<RequirementSource> Sources { get; init; } = new();
}

internal sealed class RequirementSource
{
    public string Name { get; init; } = string.Empty;
    public string Subtitle { get; init; } = string.Empty;
    public List<NeededItemSummary> Items { get; init; } = new();
}

internal sealed class ActiveQuestSummary
{
    public string QuestId { get; init; } = string.Empty;

    public string? DisplayName { get; init; }

    public int CompletedObjectives { get; init; }

    public int TotalObjectives { get; init; }

    public double CompletionPercent => TotalObjectives == 0 ? 0 : Math.Round((double)CompletedObjectives / TotalObjectives * 100, 1);
}

internal sealed class NeededItemSummary
{
    public string ItemId { get; init; } = string.Empty;

    public string? DisplayName { get; init; }

    public string? ImageFilename { get; init; }

    public string? Rarity { get; init; }

    public int OwnedQuantity { get; init; }

    public int RequiredQuantity { get; init; }

    public int MissingQuantity { get; init; }

    public List<string> Sources { get; init; } = new();
}

internal sealed class ProgressCompletionMetrics
{
    public int TotalTrackedQuests { get; init; }

    public int CompletedQuests { get; init; }

    public double QuestCompletionPercent { get; init; }

    public int TotalTrackedProjects { get; init; }

    public double ProjectCompletionPercent { get; init; }

    public int TotalTrackedHideoutModules { get; init; }

    public double HideoutCompletionPercent { get; init; }

    public int DistinctItemsNeeded { get; init; }
}

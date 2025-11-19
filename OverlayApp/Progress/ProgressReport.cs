using System;
using System.Collections.Generic;

namespace OverlayApp.Progress;

internal sealed class ProgressReport
{
    public ProgressReport(
        IReadOnlyList<ActiveQuestSummary> activeQuests,
        IReadOnlyList<NeededItemSummary> neededItems,
        ProgressCompletionMetrics completion)
    {
        ActiveQuests = activeQuests;
        NeededItems = neededItems;
        Completion = completion;
    }

    public IReadOnlyList<ActiveQuestSummary> ActiveQuests { get; }

    public IReadOnlyList<NeededItemSummary> NeededItems { get; }

    public ProgressCompletionMetrics Completion { get; }
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

    public int OwnedQuantity { get; init; }

    public int RequiredQuantity { get; init; }

    public int MissingQuantity { get; init; }
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

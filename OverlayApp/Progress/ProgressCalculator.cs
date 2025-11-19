using OverlayApp.Data;
using OverlayApp.Data.Models;
using System;
using System.Collections.Generic;
using System.Linq;

namespace OverlayApp.Progress;

internal sealed class ProgressCalculator
{
    public ProgressReport Calculate(UserProgressState state, ArcDataSnapshot? snapshot)
    {
        snapshot ??= new ArcDataSnapshot(null, DateTimeOffset.MinValue, Array.Empty<ArcProject>(), new Dictionary<string, ArcItem>(), new Dictionary<string, HideoutModule>(), new Dictionary<string, ArcQuest>());
        var activeQuests = BuildActiveQuestSummaries(state, snapshot);
        var neededItems = BuildNeededItemSummaries(state, snapshot);
        var completion = BuildCompletionMetrics(state, snapshot, neededItems.Count);
        return new ProgressReport(activeQuests, neededItems, completion);
    }

    private static List<ActiveQuestSummary> BuildActiveQuestSummaries(UserProgressState state, ArcDataSnapshot snapshot)
    {
        var results = new List<ActiveQuestSummary>();
        foreach (var quest in state.Quests)
        {
            if (quest.Status != QuestProgressStatus.InProgress && !(quest.Tracked && quest.Status != QuestProgressStatus.Completed))
            {
                continue;
            }

            var def = snapshot.Quests.TryGetValue(quest.QuestId, out var found) ? found : null;
            var totalObjectives = def?.Objectives.Count ?? 0;
            var completed = quest.CompletedObjectiveIds.Count;
            if (totalObjectives == 0 && completed > 0)
            {
                totalObjectives = completed;
            }

            results.Add(new ActiveQuestSummary
            {
                QuestId = quest.QuestId,
                DisplayName = ResolveName(def?.Name) ?? quest.QuestId,
                CompletedObjectives = completed,
                TotalObjectives = totalObjectives
            });
        }

        return results;
    }

    private static List<NeededItemSummary> BuildNeededItemSummaries(UserProgressState state, ArcDataSnapshot snapshot)
    {
        var inventory = state.Inventory.ToDictionary(i => i.ItemId, i => i.Quantity, StringComparer.OrdinalIgnoreCase);
        var requirements = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        void AddRequirement(string? itemId, int quantity)
        {
            if (string.IsNullOrWhiteSpace(itemId) || quantity <= 0)
            {
                return;
            }

            requirements[itemId] = requirements.TryGetValue(itemId, out var current)
                ? current + quantity
                : quantity;
        }

        foreach (var project in state.Projects.Where(p => p.Tracking))
        {
            var definition = snapshot.Projects.FirstOrDefault(p => string.Equals(p.Id, project.ProjectId, StringComparison.OrdinalIgnoreCase));
            if (definition is null)
            {
                continue;
            }

            var targetPhase = project.HighestPhaseCompleted + 1;
            var phase = definition.Phases.FirstOrDefault(p => p.Phase == targetPhase);
            if (phase?.RequirementItems is null)
            {
                continue;
            }

            foreach (var requirement in phase.RequirementItems)
            {
                AddRequirement(requirement.ItemId, requirement.Quantity);
            }
        }

        foreach (var module in state.HideoutModules.Where(m => m.Tracking))
        {
            if (!snapshot.HideoutModules.TryGetValue(module.ModuleId, out var definition))
            {
                continue;
            }

            var targetLevel = module.CurrentLevel + 1;
            var level = definition.Levels.FirstOrDefault(l => l.Level == targetLevel);
            if (level?.RequirementItems is null)
            {
                continue;
            }

            foreach (var requirement in level.RequirementItems)
            {
                AddRequirement(requirement.ItemId, requirement.Quantity);
            }
        }

        foreach (var customNeed in state.CustomNeeds)
        {
            AddRequirement(customNeed.ItemId, customNeed.Quantity);
        }

        var summaries = new List<NeededItemSummary>();
        foreach (var pair in requirements)
        {
            var owned = inventory.TryGetValue(pair.Key, out var have) ? have : 0;
            var missing = Math.Max(0, pair.Value - owned);
            if (missing <= 0)
            {
                continue;
            }

            var itemName = snapshot.Items.TryGetValue(pair.Key, out var item)
                ? ResolveName(item.Name)
                : null;

            summaries.Add(new NeededItemSummary
            {
                ItemId = pair.Key,
                DisplayName = itemName ?? pair.Key,
                OwnedQuantity = owned,
                RequiredQuantity = pair.Value,
                MissingQuantity = missing
            });
        }

        return summaries
            .OrderByDescending(s => s.MissingQuantity)
            .ThenBy(s => s.ItemId, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static ProgressCompletionMetrics BuildCompletionMetrics(UserProgressState state, ArcDataSnapshot snapshot, int distinctItemsNeeded)
    {
        var totalTrackedQuests = state.Quests.Count;
        var completedQuests = state.Quests.Count(q => q.Status == QuestProgressStatus.Completed);
        var questPercent = totalTrackedQuests == 0 ? 0 : Math.Round((double)completedQuests / totalTrackedQuests * 100, 1);

        var trackedProjects = state.Projects.Where(p => p.Tracking).ToList();
        var projectPercent = trackedProjects.Count == 0
            ? 0
            : Math.Round(trackedProjects
                .Select(p => CalculateProjectPercent(p, snapshot))
                .DefaultIfEmpty(0)
                .Average(), 1);

        var trackedHideout = state.HideoutModules.Where(h => h.Tracking).ToList();
        var hideoutPercent = trackedHideout.Count == 0
            ? 0
            : Math.Round(trackedHideout
                .Select(h => CalculateHideoutPercent(h, snapshot))
                .DefaultIfEmpty(0)
                .Average(), 1);

        return new ProgressCompletionMetrics
        {
            TotalTrackedQuests = totalTrackedQuests,
            CompletedQuests = completedQuests,
            QuestCompletionPercent = questPercent,
            TotalTrackedProjects = trackedProjects.Count,
            ProjectCompletionPercent = projectPercent,
            TotalTrackedHideoutModules = trackedHideout.Count,
            HideoutCompletionPercent = hideoutPercent,
            DistinctItemsNeeded = distinctItemsNeeded
        };
    }

    private static double CalculateProjectPercent(ProjectProgressState progress, ArcDataSnapshot snapshot)
    {
        var definition = snapshot.Projects.FirstOrDefault(p => string.Equals(p.Id, progress.ProjectId, StringComparison.OrdinalIgnoreCase));
        if (definition?.Phases is null || definition.Phases.Count == 0)
        {
            return 0;
        }

        return Math.Clamp((double)progress.HighestPhaseCompleted / definition.Phases.Count * 100, 0, 100);
    }

    private static double CalculateHideoutPercent(HideoutProgressState progress, ArcDataSnapshot snapshot)
    {
        if (!snapshot.HideoutModules.TryGetValue(progress.ModuleId, out var definition) || definition.MaxLevel <= 0)
        {
            return 0;
        }

        return Math.Clamp((double)progress.CurrentLevel / definition.MaxLevel * 100, 0, 100);
    }

    private static string? ResolveName(Dictionary<string, string>? localizedValues)
    {
        if (localizedValues is null || localizedValues.Count == 0)
        {
            return null;
        }

        return localizedValues.TryGetValue("en", out var english) && !string.IsNullOrWhiteSpace(english)
            ? english
            : localizedValues.Values.FirstOrDefault(v => !string.IsNullOrWhiteSpace(v));
    }
}

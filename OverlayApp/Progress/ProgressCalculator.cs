using OverlayApp.Data;
using OverlayApp.Data.Models;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace OverlayApp.Progress;

internal sealed class ProgressCalculator
{
    private static readonly Regex[] ObjectivePatterns = new[]
    {
        new Regex(@"^Deliver\s+(\d+)\s+(.+?)\s+to", RegexOptions.Compiled | RegexOptions.IgnoreCase),
        new Regex(@"^Stash\s+(\d+)\s+(.+?)\s+in", RegexOptions.Compiled | RegexOptions.IgnoreCase),
        new Regex(@"^Deposit\s+(\d+)\s+(.+?)\s+in", RegexOptions.Compiled | RegexOptions.IgnoreCase),
        new Regex(@"^Hand over\s+(\d+)\s+(.+?)\s+to", RegexOptions.Compiled | RegexOptions.IgnoreCase)
    };

    public ProgressReport Calculate(UserProgressState state, ArcDataSnapshot? snapshot)
    {
        snapshot ??= new ArcDataSnapshot(null, DateTimeOffset.MinValue, Array.Empty<ArcProject>(), new Dictionary<string, ArcItem>(), new Dictionary<string, HideoutModule>(), new Dictionary<string, ArcQuest>());
        
        var activeQuests = BuildActiveQuestSummaries(state, snapshot);
        var (neededItems, groupedRequirements) = BuildNeededItemSummaries(state, snapshot);
        var completion = BuildCompletionMetrics(state, snapshot, neededItems.Count);
        return new ProgressReport(activeQuests, neededItems, groupedRequirements, completion);
    }

    private static Dictionary<string, string> BuildItemNameMap(ArcDataSnapshot snapshot)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var pair in snapshot.Items)
        {
            var id = pair.Key;
            var nameDict = pair.Value.Name;
            if (nameDict != null && nameDict.TryGetValue("en", out var name) && !string.IsNullOrWhiteSpace(name))
            {
                map[name] = id;
                // Handle simple pluralization
                if (!name.EndsWith("s", StringComparison.OrdinalIgnoreCase))
                {
                    map[name + "s"] = id;
                }
                else
                {
                    // Handle singularization (e.g. "Scanners" -> "Scanner")
                    map[name[..^1]] = id;
                }
                
                // Handle "parts" vs "part" - specific override if needed, though the above covers it mostly
                if (name.EndsWith(" Parts", StringComparison.OrdinalIgnoreCase))
                {
                     map[name[..^1]] = id; 
                }
            }
        }
        return map;
    }

    private static List<ActiveQuestSummary> BuildActiveQuestSummaries(UserProgressState state, ArcDataSnapshot snapshot)
    {
        var results = new List<ActiveQuestSummary>();
        var userQuests = state.Quests.ToDictionary(q => q.QuestId, StringComparer.OrdinalIgnoreCase);
        var completedQuestIds = userQuests.Values
            .Where(q => q.Status == QuestProgressStatus.Completed)
            .Select(q => q.QuestId)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var pair in snapshot.Quests)
        {
            var questId = pair.Key;
            var definition = pair.Value;

            userQuests.TryGetValue(questId, out var userQuest);
            var status = userQuest?.Status ?? QuestProgressStatus.NotStarted;

            if (status == QuestProgressStatus.Completed || status == QuestProgressStatus.Abandoned)
            {
                continue;
            }

            // Removed prerequisite check to ensure all future quests are visible
            // if (status == QuestProgressStatus.NotStarted) ...

            var totalObjectives = definition.Objectives.Count;
            var completed = userQuest?.CompletedObjectiveIds.Count ?? 0;
            if (totalObjectives == 0 && completed > 0)
            {
                totalObjectives = completed;
            }

            results.Add(new ActiveQuestSummary
            {
                QuestId = questId,
                DisplayName = ResolveName(definition.Name) ?? questId,
                CompletedObjectives = completed,
                TotalObjectives = totalObjectives
            });
        }

        return results;
    }

    private static (List<NeededItemSummary> Aggregated, List<RequirementGroup> Grouped) BuildNeededItemSummaries(
        UserProgressState state, 
        ArcDataSnapshot snapshot)
    {
        var inventory = state.Inventory.ToDictionary(i => i.ItemId, i => i.Quantity, StringComparer.OrdinalIgnoreCase);
        var requirements = new Dictionary<string, (int Quantity, HashSet<string> Sources)>(StringComparer.OrdinalIgnoreCase);
        
        var projectGroup = new RequirementGroup { Category = "Projects" };
        var hideoutGroup = new RequirementGroup { Category = "Hideout" };
        var questGroup = new RequirementGroup { Category = "Quests" };
        var customGroup = new RequirementGroup { Category = "Custom" };

        void AddRequirement(string? itemId, int quantity, string source)
        {
            if (string.IsNullOrWhiteSpace(itemId) || quantity <= 0)
            {
                return;
            }

            if (!requirements.TryGetValue(itemId, out var current))
            {
                current = (0, new HashSet<string>(StringComparer.Ordinal));
            }

            current.Quantity += quantity;
            current.Sources.Add(source);
            requirements[itemId] = current;
        }

        NeededItemSummary CreateItemSummary(string itemId, int quantity)
        {
            var owned = inventory.TryGetValue(itemId, out var have) ? have : 0;
            var missing = Math.Max(0, quantity - owned);
            
            string? itemName = null;
            string? imageFilename = null;
            string? rarity = null;

            if (snapshot.Items.TryGetValue(itemId, out var item))
            {
                itemName = ResolveName(item.Name);
                imageFilename = item.ImageFilename;
                rarity = item.Rarity;
            }

            return new NeededItemSummary
            {
                ItemId = itemId,
                DisplayName = itemName ?? itemId,
                ImageFilename = imageFilename,
                Rarity = rarity,
                OwnedQuantity = owned,
                RequiredQuantity = quantity,
                MissingQuantity = missing
            };
        }

        foreach (var definition in snapshot.Projects)
        {
            var projectId = definition.Id;
            var userProject = state.Projects.FirstOrDefault(p => string.Equals(p.ProjectId, projectId, StringComparison.OrdinalIgnoreCase));
            
            var currentPhase = userProject?.HighestPhaseCompleted ?? 0;
            
            foreach (var phase in definition.Phases.Where(p => p.Phase > currentPhase))
            {
                if (phase.RequirementItems is null)
                {
                    continue;
                }

                var sourceName = ResolveName(definition.Name) ?? projectId ?? "Unknown Project";
                var source = $"Project: {sourceName} (Phase {phase.Phase})";
                
                var reqSource = new RequirementSource { Name = sourceName, Subtitle = $"Phase {phase.Phase}" };
                // projectGroup.Sources.Add(reqSource);

                var itemsAdded = false;
                foreach (var requirement in phase.RequirementItems)
                {
                    if (string.IsNullOrWhiteSpace(requirement.ItemId)) continue;
                    AddRequirement(requirement.ItemId, requirement.Quantity, source);
                    reqSource.Items.Add(CreateItemSummary(requirement.ItemId, requirement.Quantity));
                    itemsAdded = true;
                }

                if (itemsAdded)
                {
                    projectGroup.Sources.Add(reqSource);
                }
            }
        }

        foreach (var pair in snapshot.HideoutModules)
        {
            var moduleId = pair.Key;
            var definition = pair.Value;

            var userModule = state.HideoutModules.FirstOrDefault(m => string.Equals(m.ModuleId, moduleId, StringComparison.OrdinalIgnoreCase));
            var currentLevel = userModule?.CurrentLevel ?? 0;

            if (definition.Levels != null)
            {
                foreach (var level in definition.Levels.Where(l => l.Level > currentLevel))
                {
                    if (level.RequirementItems is null)
                    {
                        continue;
                    }

                    var sourceName = ResolveName(definition.Name) ?? moduleId;
                    var source = $"Hideout: {sourceName} (Level {level.Level})";

                    var reqSource = new RequirementSource { Name = sourceName, Subtitle = $"Level {level.Level}" };
                    // hideoutGroup.Sources.Add(reqSource);

                    var itemsAdded = false;
                    foreach (var requirement in level.RequirementItems)
                    {
                        if (string.IsNullOrWhiteSpace(requirement.ItemId)) continue;
                        AddRequirement(requirement.ItemId, requirement.Quantity, source);
                        reqSource.Items.Add(CreateItemSummary(requirement.ItemId, requirement.Quantity));
                        itemsAdded = true;
                    }

                    if (itemsAdded)
                    {
                        hideoutGroup.Sources.Add(reqSource);
                    }
                }
            }
        }

        foreach (var customNeed in state.CustomNeeds)
        {
            AddRequirement(customNeed.ItemId, customNeed.Quantity, "Custom Tracking");
            // Custom needs grouping is simple
            var reqSource = customGroup.Sources.FirstOrDefault();
            if (reqSource == null)
            {
                reqSource = new RequirementSource { Name = "Custom Tracking" };
                customGroup.Sources.Add(reqSource);
            }
            reqSource.Items.Add(CreateItemSummary(customNeed.ItemId, customNeed.Quantity));
        }

        var userQuests = state.Quests.ToDictionary(q => q.QuestId, StringComparer.OrdinalIgnoreCase);

        foreach (var pair in snapshot.Quests)
        {
            var questId = pair.Key;
            var definition = pair.Value;

            userQuests.TryGetValue(questId, out var userQuest);
            var status = userQuest?.Status ?? QuestProgressStatus.NotStarted;

            if (status == QuestProgressStatus.Completed)
            {
                continue;
            }

            var sourceName = ResolveName(definition.Name) ?? questId;
            var source = $"Quest: {sourceName}";
            
            var reqSource = new RequirementSource { Name = sourceName };
            // Only add if we find items
            var itemsFound = false;

            // Check requiredItemIds
            if (definition.RequiredItems != null && definition.RequiredItems.Count > 0)
            {
                foreach (var req in definition.RequiredItems)
                {
                    if (string.IsNullOrWhiteSpace(req.ItemId)) continue;
                    AddRequirement(req.ItemId, req.Quantity, source);
                    reqSource.Items.Add(CreateItemSummary(req.ItemId, req.Quantity));
                    itemsFound = true;
                }
            }

            if (itemsFound)
            {
                questGroup.Sources.Add(reqSource);
            }
        }

        var summaries = new List<NeededItemSummary>();
        foreach (var pair in requirements)
        {
            var owned = inventory.TryGetValue(pair.Key, out var have) ? have : 0;
            var missing = Math.Max(0, pair.Value.Quantity - owned);
            if (missing <= 0)
            {
                continue;
            }

            string? itemName = null;
            string? imageFilename = null;
            string? rarity = null;

            if (snapshot.Items.TryGetValue(pair.Key, out var item))
            {
                itemName = ResolveName(item.Name);
                imageFilename = item.ImageFilename;
                rarity = item.Rarity;
            }

            summaries.Add(new NeededItemSummary
            {
                ItemId = pair.Key,
                DisplayName = itemName ?? pair.Key,
                ImageFilename = imageFilename,
                Rarity = rarity,
                OwnedQuantity = owned,
                RequiredQuantity = pair.Value.Quantity,
                MissingQuantity = missing,
                Sources = pair.Value.Sources.OrderBy(s => s).ToList()
            });
        }

        var groups = new List<RequirementGroup>();
        if (questGroup.Sources.Count > 0) groups.Add(questGroup);
        if (hideoutGroup.Sources.Count > 0) groups.Add(hideoutGroup);
        if (projectGroup.Sources.Count > 0) groups.Add(projectGroup);
        if (customGroup.Sources.Count > 0) groups.Add(customGroup);

        return (summaries
            .OrderByDescending(s => s.MissingQuantity)
            .ThenBy(s => s.ItemId, StringComparer.OrdinalIgnoreCase)
            .ToList(), groups);
    }

    private static string? GetString(JsonElement element)
    {
        return element.ValueKind == JsonValueKind.String ? element.GetString() : element.ToString();
    }

    private static int GetInt(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Number && element.TryGetInt32(out var val))
        {
            return val;
        }
        if (element.ValueKind == JsonValueKind.String && int.TryParse(element.GetString(), out var parsed))
        {
            return parsed;
        }
        return 1;
    }

    private static ProgressCompletionMetrics BuildCompletionMetrics(UserProgressState state, ArcDataSnapshot snapshot, int distinctItemsNeeded)
    {
        // Quests: Global Completion
        var totalQuests = snapshot.Quests.Count;
        var completedQuests = state.Quests.Count(q => q.Status == QuestProgressStatus.Completed);
        var questPercent = totalQuests == 0 ? 0 : Math.Round((double)completedQuests / totalQuests * 100, 1);

        // Projects: Global Phase Completion
        var totalProjectPhases = snapshot.Projects.Sum(p => p.Phases?.Count ?? 0);
        var completedProjectPhases = 0;
        foreach (var projectDef in snapshot.Projects)
        {
            var userProject = state.Projects.FirstOrDefault(p => string.Equals(p.ProjectId, projectDef.Id, StringComparison.OrdinalIgnoreCase));
            if (userProject != null)
            {
                var max = projectDef.Phases?.Count ?? 0;
                completedProjectPhases += Math.Min(userProject.HighestPhaseCompleted, max);
            }
        }
        var projectPercent = totalProjectPhases == 0 ? 0 : Math.Round((double)completedProjectPhases / totalProjectPhases * 100, 1);

        // Hideout: Global Level Completion
        var totalHideoutLevels = 0;
        var completedHideoutLevels = 0;
        foreach (var moduleDef in snapshot.HideoutModules.Values)
        {
            var max = moduleDef.MaxLevel > 0 ? moduleDef.MaxLevel : (moduleDef.Levels?.Count ?? 0);
            totalHideoutLevels += max;

            var userModule = state.HideoutModules.FirstOrDefault(m => string.Equals(m.ModuleId, moduleDef.Id, StringComparison.OrdinalIgnoreCase));
            if (userModule != null)
            {
                completedHideoutLevels += Math.Min(userModule.CurrentLevel, max);
            }
        }
        var hideoutPercent = totalHideoutLevels == 0 ? 0 : Math.Round((double)completedHideoutLevels / totalHideoutLevels * 100, 1);

        return new ProgressCompletionMetrics
        {
            TotalTrackedQuests = totalQuests,
            CompletedQuests = completedQuests,
            QuestCompletionPercent = questPercent,
            TotalTrackedProjects = snapshot.Projects.Count,
            ProjectCompletionPercent = projectPercent,
            TotalTrackedHideoutModules = snapshot.HideoutModules.Count,
            HideoutCompletionPercent = hideoutPercent,
            DistinctItemsNeeded = distinctItemsNeeded
        };
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

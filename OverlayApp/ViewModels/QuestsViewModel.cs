using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using OverlayApp.Data;
using OverlayApp.Data.Models;
using OverlayApp.Progress;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows.Input;

namespace OverlayApp.ViewModels;

internal sealed partial class QuestsViewModel : NavigationPaneViewModel
{
    private readonly UserProgressStore _progressStore;
    private readonly List<QuestDisplayModel> _allQuests = new();

    public event Action<string>? NavigationRequested;
    public event Action<QuestDisplayModel>? RequestScrollToQuest;

    public QuestsViewModel(UserProgressStore progressStore) : base("Quests", "📜")
    {
        _progressStore = progressStore;
    }

    public ObservableCollection<QuestDisplayModel> Quests { get; } = new();

    [ObservableProperty]
    private string _emptyMessage = "Progress not loaded";

    [ObservableProperty]
    private string _selectedFilter = "Available";

    [RelayCommand]
    private void SetFilter(string filter)
    {
        SelectedFilter = filter;
        ApplyFilter();
    }

    public override void Update(ArcDataSnapshot? snapshot, UserProgressState? progress, ProgressReport? report)
    {
        _allQuests.Clear();
        Quests.Clear();
        if (snapshot?.Quests is null)
        {
            EmptyMessage = "Data not loaded";
            return;
        }

        var userQuests = progress?.Quests?.ToDictionary(q => q.QuestId, StringComparer.OrdinalIgnoreCase)
                         ?? new Dictionary<string, QuestProgressState>(StringComparer.OrdinalIgnoreCase);

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
            var statusStr = status.ToString();

            if (status == QuestProgressStatus.NotStarted)
            {
                // Check prerequisites
                var previousIds = definition.PreviousQuestIds;
                if (previousIds != null && previousIds.Any(pid => !completedQuestIds.Contains(pid)))
                {
                    statusStr = "Locked";
                }
            }

            var totalObjectives = definition.Objectives.Count;
            var completedObjectives = userQuest?.CompletedObjectiveIds.Count ?? 0;

            var progressPercent = totalObjectives == 0
                ? (status == QuestProgressStatus.Completed ? 100 : 0)
                : (double)completedObjectives / totalObjectives * 100;

            var objectives = new List<QuestObjectiveViewModel>();
            foreach (var obj in definition.Objectives)
            {
                var type = GetValueIgnoreCase(obj, "type");
                var typeStr = type?.ValueKind == JsonValueKind.String ? type.Value.GetString() : "Unknown";

                string? descStr = ResolveJsonName(obj);

                if (string.IsNullOrEmpty(descStr))
                {
                    var keysToCheck = new[] { "description", "name", "text" };
                    foreach (var key in keysToCheck)
                    {
                        var elem = GetValueIgnoreCase(obj, key);
                        if (elem.HasValue)
                        {
                            descStr = ResolveJsonElement(elem.Value);
                            if (!string.IsNullOrEmpty(descStr)) break;
                        }
                    }
                }

                objectives.Add(new QuestObjectiveViewModel
                {
                    Type = typeStr ?? "Unknown",
                    Description = descStr ?? ""
                });
            }

            var prereqs = new List<QuestReferenceViewModel>();
            if (definition.PreviousQuestIds != null)
            {
                foreach (var pid in definition.PreviousQuestIds)
                {
                    if (snapshot.Quests.TryGetValue(pid, out var pDef))
                    {
                        userQuests.TryGetValue(pid, out var pProg);
                        var pStatus = pProg?.Status ?? QuestProgressStatus.NotStarted;
                        prereqs.Add(new QuestReferenceViewModel(NavigateToQuest)
                        {
                            QuestId = pid,
                            Name = ResolveName(pDef.Name) ?? pid,
                            Status = pStatus.ToString()
                        });
                    }
                }
            }

            var unlocks = new List<QuestReferenceViewModel>();
            if (definition.NextQuestIds != null)
            {
                foreach (var nid in definition.NextQuestIds)
                {
                    if (snapshot.Quests.TryGetValue(nid, out var nDef))
                    {
                        userQuests.TryGetValue(nid, out var nProg);
                        var nStatus = nProg?.Status ?? QuestProgressStatus.NotStarted;
                        
                        // Check if locked
                        if (nStatus == QuestProgressStatus.NotStarted)
                        {
                            var nPrereqs = nDef.PreviousQuestIds;
                            if (nPrereqs != null && nPrereqs.Any(p => !completedQuestIds.Contains(p)))
                            {
                                nStatus = (QuestProgressStatus)(-1); // Locked marker
                            }
                        }

                        unlocks.Add(new QuestReferenceViewModel(NavigateToQuest)
                        {
                            QuestId = nid,
                            Name = ResolveName(nDef.Name) ?? nid,
                            Status = nStatus == (QuestProgressStatus)(-1) ? "Locked" : nStatus.ToString()
                        });
                    }
                }
            }

            _allQuests.Add(new QuestDisplayModel(_progressStore, progress)
            {
                QuestId = questId,
                Name = ResolveName(definition.Name) ?? questId,
                Description = ResolveName(definition.Description) ?? string.Empty,
                Trader = definition.Trader ?? "Unknown",
                Status = statusStr,
                ProgressText = totalObjectives > 0 ? $"{completedObjectives}/{totalObjectives}" : "-",
                ProgressPercent = progressPercent,
                IsTracked = userQuest?.Tracked ?? false,
                Notes = userQuest?.Notes ?? string.Empty,
                RequiredItems = CreateItemQuantityList(definition.RequiredItems, snapshot.Items, OnNavigate),
                RewardItems = CreateItemQuantityList(definition.RewardItems, snapshot.Items, OnNavigate),
                Objectives = objectives,
                Prerequisites = prereqs,
                Unlocks = unlocks
            });
        }

        ApplyFilter();
    }

    private void NavigateToQuest(string questId)
    {
        var target = _allQuests.FirstOrDefault(q => q.QuestId == questId);
        if (target == null) return;

        if (target.Status == "Completed") SelectedFilter = "Completed";
        else if (target.Status == "Locked") SelectedFilter = "Locked";
        else SelectedFilter = "Available";

        ApplyFilter();

        target.IsExpanded = true;
        RequestScrollToQuest?.Invoke(target);
    }

    private void ApplyFilter()
    {
        Quests.Clear();
        var filtered = _allQuests.AsEnumerable();

        switch (SelectedFilter)
        {
            case "Available":
                filtered = filtered.Where(q => q.Status != "Locked" && q.Status != "Completed");
                break;
            case "Locked":
                filtered = filtered.Where(q => q.Status == "Locked");
                break;
            case "Completed":
                filtered = filtered.Where(q => q.Status == "Completed");
                break;
        }

        var sorted = filtered
            .OrderBy(q => q.Status == "Completed" ? 2 : q.Status == "NotStarted" ? 1 : 0)
            .ThenBy(q => q.Name);

        foreach (var item in sorted)
        {
            Quests.Add(item);
        }

        EmptyMessage = Quests.Count == 0 ? "No quests found" : string.Empty;
    }

    private void OnNavigate(string itemId)
    {
        NavigationRequested?.Invoke(itemId);
    }

    private static string? ResolveName(Dictionary<string, string>? localized)
    {
        if (localized is null)
        {
            return null;
        }

        return localized.TryGetValue("en", out var en) && !string.IsNullOrWhiteSpace(en)
            ? en
            : localized.Values.FirstOrDefault(v => !string.IsNullOrWhiteSpace(v));
    }

    private static string? ResolveJsonName(Dictionary<string, JsonElement>? localized)
    {
        if (localized is null) return null;

        if (localized.TryGetValue("en", out var en) && en.ValueKind == JsonValueKind.String)
        {
            return en.GetString();
        }
        
        foreach (var kvp in localized)
        {
            if (kvp.Key.Equals("type", StringComparison.OrdinalIgnoreCase)) continue;
            if (kvp.Key.Equals("id", StringComparison.OrdinalIgnoreCase)) continue;
            
            if (kvp.Value.ValueKind == JsonValueKind.String)
            {
                return kvp.Value.GetString();
            }
        }
        return null;
    }

    private static string? ResolveJsonElement(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.String) return element.GetString();
        if (element.ValueKind != JsonValueKind.Object) return null;

        if (element.TryGetProperty("en", out var en) && en.ValueKind == JsonValueKind.String)
        {
            return en.GetString();
        }

        foreach (var prop in element.EnumerateObject())
        {
            if (prop.Name.Equals("type", StringComparison.OrdinalIgnoreCase)) continue;
            if (prop.Value.ValueKind == JsonValueKind.String)
            {
                return prop.Value.GetString();
            }
        }
        return null;
    }

    private static JsonElement? GetValueIgnoreCase(Dictionary<string, JsonElement> dict, string key)
    {
        var match = dict.Keys.FirstOrDefault(k => k.Equals(key, StringComparison.OrdinalIgnoreCase));
        return match != null ? dict[match] : null;
    }

    private static JsonElement? GetValueIgnoreCase(JsonElement element, string key)
    {
        if (element.ValueKind != JsonValueKind.Object) return null;
        foreach (var prop in element.EnumerateObject())
        {
            if (prop.Name.Equals(key, StringComparison.OrdinalIgnoreCase))
            {
                return prop.Value;
            }
        }
        return null;
    }

    private static List<ItemQuantityViewModel> CreateItemQuantityList(List<ProjectPhaseItemRequirement>? requirements, IReadOnlyDictionary<string, ArcItem>? allItems, Action<string> navigateAction)
    {
        if (requirements == null || allItems == null) return new();
        
        var list = new List<ItemQuantityViewModel>();
        foreach (var req in requirements)
        {
            if (string.IsNullOrEmpty(req.ItemId)) continue;

            if (allItems.TryGetValue(req.ItemId, out var item))
            {
                list.Add(new ItemQuantityViewModel(navigateAction)
                {
                    ItemId = req.ItemId,
                    Name = ResolveName(item.Name) ?? req.ItemId,
                    ImageFilename = item.ImageFilename ?? "",
                    Quantity = req.Quantity,
                    Rarity = item.Rarity ?? "Common"
                });
            }
            else
            {
                list.Add(new ItemQuantityViewModel(navigateAction)
                {
                    ItemId = req.ItemId,
                    Name = req.ItemId,
                    ImageFilename = "",
                    Quantity = req.Quantity,
                    Rarity = "Common"
                });
            }
        }
        return list;
    }
}

internal partial class QuestDisplayModel : ObservableObject
{
    private readonly UserProgressStore _progressStore;
    private readonly UserProgressState? _progressState;

    public QuestDisplayModel(UserProgressStore progressStore, UserProgressState? progressState)
    {
        _progressStore = progressStore;
        _progressState = progressState;
    }

    public string QuestId { get; set; } = string.Empty;

    public string Name { get; set; } = string.Empty;

    public string Description { get; set; } = string.Empty;

    public string Trader { get; set; } = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanComplete))]
    private string _status = string.Empty;

    public string ProgressText { get; set; } = string.Empty;

    public double ProgressPercent { get; set; }

    public bool IsTracked { get; set; }

    public string Notes { get; set; } = string.Empty;

    public List<ItemQuantityViewModel> RequiredItems { get; set; } = new();

    public List<ItemQuantityViewModel> RewardItems { get; set; } = new();

    public List<QuestObjectiveViewModel> Objectives { get; set; } = new();

    public List<QuestReferenceViewModel> Prerequisites { get; set; } = new();
    public List<QuestReferenceViewModel> Unlocks { get; set; } = new();

    [ObservableProperty]
    private bool _isExpanded;

    public bool CanComplete => Status != "Completed";

    [RelayCommand]
    private void ToggleExpand()
    {
        IsExpanded = !IsExpanded;
    }

    [RelayCommand(CanExecute = nameof(CanComplete))]
    private async Task CompleteQuest()
    {
        if (_progressState == null) return;

        var quest = _progressState.Quests.FirstOrDefault(q => q.QuestId == QuestId);
        if (quest == null)
        {
            quest = new QuestProgressState { QuestId = QuestId };
            _progressState.Quests.Add(quest);
        }

        quest.Status = QuestProgressStatus.Completed;
        Status = QuestProgressStatus.Completed.ToString();
        
        await _progressStore.SaveAsync(_progressState, System.Threading.CancellationToken.None);
    }
}

public class QuestObjectiveViewModel
{
    public string Type { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
}

public class QuestReferenceViewModel
{
    private readonly Action<string> _navigateAction;

    public QuestReferenceViewModel(Action<string> navigateAction)
    {
        _navigateAction = navigateAction;
        NavigateCommand = new RelayCommand(() => _navigateAction(QuestId));
    }

    public string QuestId { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Status { get; set; } = string.Empty;
    public ICommand NavigateCommand { get; }
}

using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using OverlayApp.Data;
using OverlayApp.Data.Models;
using OverlayApp.Infrastructure;
using OverlayApp.Progress;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;

namespace OverlayApp.ViewModels;

internal sealed partial class QuestsViewModel : NavigationPaneViewModel
{
    private readonly UserProgressStore _progressStore;
    private readonly List<QuestDisplayModel> _allQuests = new();

    public event Action<string>? NavigationRequested;
    public event Action<QuestDisplayModel>? RequestScrollToQuest;

    public QuestsViewModel(UserProgressStore progressStore) : base("Nav_Quests", "📜")
    {
        _progressStore = progressStore;
        EmptyMessage = LocalizationService.Instance["Quests_EmptyMessage"];
    }

    public ObservableCollection<QuestDisplayModel> Quests { get; } = new();
    public ObservableCollection<QuestTreeNode> TreeNodes { get; } = new();
    public ObservableCollection<QuestDisplayModel> ActiveQuests { get; } = new();

    [ObservableProperty]
    private Geometry _connectionPathData = Geometry.Empty;

    [ObservableProperty]
    private double _treeWidth = 2000;

    [ObservableProperty]
    private double _treeHeight = 2000;

    [ObservableProperty]
    private string _emptyMessage;

    [ObservableProperty]
    private string _selectedFilter = "Available";

    [ObservableProperty]
    private bool _isTreeView;

    [RelayCommand]
    private void ToggleViewMode()
    {
        IsTreeView = !IsTreeView;
    }

    [RelayCommand]
    private void SetFilter(string filter)
    {
        SelectedFilter = filter;
        ApplyFilter();
    }

    public override void Update(ArcDataSnapshot? snapshot, UserProgressState? progress, ProgressReport? report)
    {
        if (snapshot?.Quests is null)
        {
            foreach (var q in _allQuests) q.PropertyChanged -= OnQuestPropertyChanged;
            _allQuests.Clear();
            Quests.Clear();
            EmptyMessage = LocalizationService.Instance["Quests_EmptyMessage"];
            return;
        }

        var userQuests = progress?.Quests?.ToDictionary(q => q.QuestId, StringComparer.OrdinalIgnoreCase)
                         ?? new Dictionary<string, QuestProgressState>(StringComparer.OrdinalIgnoreCase);

        var completedQuestIds = userQuests.Values
            .Where(q => q.Status == QuestProgressStatus.Completed)
            .Select(q => q.QuestId)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var currentQuestIds = new HashSet<string>();

        foreach (var pair in snapshot.Quests)
        {
            var questId = pair.Key;
            var definition = pair.Value;
            currentQuestIds.Add(questId);

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

                string? descStr = LocalizationHelper.ResolveJsonName(obj);

                if (string.IsNullOrEmpty(descStr))
                {
                    var keysToCheck = new[] { "description", "name", "text" };
                    foreach (var key in keysToCheck)
                    {
                        var elem = GetValueIgnoreCase(obj, key);
                        if (elem.HasValue)
                        {
                            descStr = LocalizationHelper.ResolveJsonElement(elem.Value);
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
                            Name = LocalizationHelper.ResolveName(pDef.Name) ?? pid,
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
                            Name = LocalizationHelper.ResolveName(nDef.Name) ?? nid,
                            Status = nStatus == (QuestProgressStatus)(-1) ? "Locked" : nStatus.ToString()
                        });
                    }
                }
            }

            var existing = _allQuests.FirstOrDefault(q => q.QuestId == questId);
            if (existing != null)
            {
                existing.UpdateState(progress, snapshot.Quests);
                existing.Name = LocalizationHelper.ResolveName(definition.Name) ?? questId;
                existing.Description = LocalizationHelper.ResolveName(definition.Description) ?? string.Empty;
                existing.Trader = definition.Trader ?? "Unknown";
                existing.Status = statusStr;
                existing.ProgressText = totalObjectives > 0 ? $"{completedObjectives}/{totalObjectives}" : "-";
                existing.ProgressPercent = progressPercent;
                existing.IsTracked = userQuest?.Tracked ?? false;
                existing.Notes = userQuest?.Notes ?? string.Empty;
                existing.RequiredItems = CreateItemQuantityList(definition.RequiredItems, snapshot.Items, OnNavigate);
                existing.RewardItems = CreateItemQuantityList(definition.RewardItems, snapshot.Items, OnNavigate);
                existing.Objectives = objectives;
                existing.Prerequisites = prereqs;
                existing.Unlocks = unlocks;
            }
            else
            {
                var model = new QuestDisplayModel(_progressStore, progress, snapshot.Quests)
                {
                    QuestId = questId,
                    Name = LocalizationHelper.ResolveName(definition.Name) ?? questId,
                    Description = LocalizationHelper.ResolveName(definition.Description) ?? string.Empty,
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
                    Unlocks = unlocks,
                    IsExpanded = false
                };
                model.PropertyChanged += OnQuestPropertyChanged;
                _allQuests.Add(model);
            }
        }

        // Remove stale quests
        for (int i = _allQuests.Count - 1; i >= 0; i--)
        {
            if (!currentQuestIds.Contains(_allQuests[i].QuestId))
            {
                _allQuests[i].PropertyChanged -= OnQuestPropertyChanged;
                _allQuests.RemoveAt(i);
            }
        }

        ApplyFilter();
        TreeNodes.Clear();
        BuildTree();
        UpdateActiveQuests();
    }

    private void OnQuestPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(QuestDisplayModel.IsExpanded))
        {
            BuildTree();
        }
    }

    private void UpdateActiveQuests()
    {
        ActiveQuests.Clear();
        // "Active" means available to be worked on.
        var active = _allQuests.Where(q => q.Status != "Locked" && q.Status != "Completed")
                               .OrderBy(q => q.Name);
        foreach (var q in active)
        {
            ActiveQuests.Add(q);
        }
    }

    private void BuildTree()
    {
        if (_allQuests.Count == 0) 
        {
            TreeNodes.Clear();
            ConnectionPathData = Geometry.Empty;
            return;
        }

        var questMap = _allQuests.ToDictionary(q => q.QuestId);
        var depths = new Dictionary<string, int>();
        
        // Calculate depths
        foreach (var quest in _allQuests)
        {
            CalculateDepth(quest.QuestId, questMap, depths, new HashSet<string>());
        }

        if (depths.Count == 0) return;

        var maxDepth = depths.Values.Max();
        var layers = new List<List<QuestDisplayModel>>();
        for (int i = 0; i <= maxDepth; i++) layers.Add(new List<QuestDisplayModel>());

        foreach (var kvp in depths)
        {
            if (questMap.TryGetValue(kvp.Key, out var q))
            {
                layers[kvp.Value].Add(q);
            }
        }

        // Layout
        double cardWidth = 220;
        double xSpacing = 240; 
        double startX = 50;
        double startY = 50;

        // Sort layers to minimize crossings (Barycenter method)
        // Layer 0: Alphabetical
        layers[0].Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.Ordinal));

        // Map QuestId to its horizontal index in the tree
        var questXIndex = new Dictionary<string, int>();
        for (int i = 0; i < layers[0].Count; i++)
        {
            questXIndex[layers[0][i].QuestId] = i;
        }

        for (int d = 1; d <= maxDepth; d++)
        {
            var layer = layers[d];
            
            // Calculate weight for each node based on parent positions
            var weights = new Dictionary<string, double>();
            foreach (var quest in layer)
            {
                double sumX = 0;
                int count = 0;
                foreach (var prereq in quest.Prerequisites)
                {
                    if (questXIndex.TryGetValue(prereq.QuestId, out int px))
                    {
                        sumX += px;
                        count++;
                    }
                }
                
                weights[quest.QuestId] = count > 0 ? sumX / count : double.MaxValue;
            }

            // Sort by weight, then name
            layer.Sort((a, b) =>
            {
                double wa = weights[a.QuestId];
                double wb = weights[b.QuestId];
                if (Math.Abs(wa - wb) < 0.001) return string.Compare(a.Name, b.Name, StringComparison.Ordinal);
                return wa.CompareTo(wb);
            });

            // Update indices for next layer
            for (int i = 0; i < layer.Count; i++)
            {
                questXIndex[layer[i].QuestId] = i;
            }
        }

        // Calculate centering
        var maxLayerCount = layers.Max(l => l.Count);
        var maxLayerWidth = maxLayerCount * xSpacing;

        // Calculate layer heights
        var layerHeights = new Dictionary<int, double>();
        for (int d = 0; d <= maxDepth; d++)
        {
            double maxHeight = 90; // Base height
            foreach (var q in layers[d])
            {
                double h = EstimateNodeHeight(q);
                if (h > maxHeight) maxHeight = h;
            }
            layerHeights[d] = maxHeight;
        }

        double currentY = startY;
        double maxX = 0;
        double maxY = 0;
        
        var geometry = new StreamGeometry();
        using (var ctx = geometry.Open())
        {
            // Prepare for node updates
            var existingNodes = TreeNodes.ToDictionary(n => n.Quest.QuestId);
            var newNodes = new List<QuestTreeNode>();

            for (int d = 0; d <= maxDepth; d++)
            {
                var layer = layers[d];
                var currentLayerWidth = layer.Count * xSpacing;
                var layerOffsetX = (maxLayerWidth - currentLayerWidth) / 2.0;

                for (int i = 0; i < layer.Count; i++)
                {
                    var quest = layer[i];
                    // Top -> Bottom: Y depends on depth (d), X depends on index (i)
                    var x = startX + layerOffsetX + i * xSpacing;
                    var y = currentY;

                    if (x + cardWidth > maxX) maxX = x + cardWidth;
                    if (y + EstimateNodeHeight(quest) > maxY) maxY = y + EstimateNodeHeight(quest);

                    // Update or Create Node
                    if (existingNodes.TryGetValue(quest.QuestId, out var existingNode))
                    {
                        if (Math.Abs(existingNode.X - x) > 0.1) existingNode.X = x;
                        if (Math.Abs(existingNode.Y - y) > 0.1) existingNode.Y = y;
                    }
                    else
                    {
                        var newNode = new QuestTreeNode 
                        { 
                            X = x, 
                            Y = y, 
                            Quest = quest 
                        };
                        newNodes.Add(newNode);
                        existingNodes[quest.QuestId] = newNode; 
                    }

                    // Add connections to prerequisites
                    foreach (var prereq in quest.Prerequisites)
                    {
                        if (existingNodes.TryGetValue(prereq.QuestId, out var pNode))
                        {
                            // Connect from Center of Parent Header to Top Center of Child
                            var startNodeX = pNode.X + cardWidth / 2;
                            var startNodeY = pNode.Y + 45; // Center of header (90/2)
                            
                            var endNodeX = x + cardWidth / 2;
                            var endNodeY = y; // Top of child

                            var distY = endNodeY - startNodeY;
                            var cp1X = startNodeX;
                            var cp1Y = startNodeY + distY / 2;
                            var cp2X = endNodeX;
                            var cp2Y = endNodeY - distY / 2;

                            ctx.BeginFigure(new System.Windows.Point(startNodeX, startNodeY), false, false);
                            ctx.BezierTo(new System.Windows.Point(cp1X, cp1Y), new System.Windows.Point(cp2X, cp2Y), new System.Windows.Point(endNodeX, endNodeY), true, false);
                        }
                    }
                }
                currentY += layerHeights[d] + 50; // Gap
            }

            if (newNodes.Count > 0)
            {
                foreach (var node in newNodes) TreeNodes.Add(node);
            }
        }

        geometry.Freeze();
        ConnectionPathData = geometry;
        TreeWidth = maxX + 100;
        TreeHeight = maxY + 100;
    }

    private double EstimateNodeHeight(QuestDisplayModel q)
    {
        if (!q.IsExpanded) return 90;
        
        double h = 120; // Base expanded height (Header + Status + padding)
        
        if (!string.IsNullOrEmpty(q.Description)) h += 60; // Approx
        
        if (q.Objectives.Count > 0) 
            h += 25 + q.Objectives.Count * 40; 
            
        if (q.RequiredItems.Count > 0)
        {
            h += 25; 
            // Items are in WrapPanel. Width 220. Item width 130?
            // In Tree view, maybe we should make items smaller or list them vertically?
            // The list view uses 130px wide items. 220 width can fit 1 item per row.
            h += q.RequiredItems.Count * 40; 
        }
        
        if (q.RewardItems.Count > 0)
        {
             h += 25 + q.RewardItems.Count * 40;
        }
        
        if (q.Prerequisites.Count > 0) h += 25 + q.Prerequisites.Count * 30;
        if (q.Unlocks.Count > 0) h += 25 + q.Unlocks.Count * 30;
        
        return h;
    }

    private int CalculateDepth(string questId, Dictionary<string, QuestDisplayModel> questMap, Dictionary<string, int> depths, HashSet<string> visited)
    {
        if (depths.TryGetValue(questId, out var d)) return d;
        if (!visited.Add(questId)) return 0; // Cycle detected

        if (!questMap.TryGetValue(questId, out var quest)) return 0;

        int maxPDepth = -1;
        foreach (var p in quest.Prerequisites)
        {
            var pd = CalculateDepth(p.QuestId, questMap, depths, visited);
            if (pd > maxPDepth) maxPDepth = pd;
        }

        var depth = maxPDepth + 1;
        depths[questId] = depth;
        visited.Remove(questId);
        return depth;
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
            .ThenBy(q => q.Name)
            .ToList();

        SyncCollection(Quests, sorted);

        EmptyMessage = Quests.Count == 0 ? LocalizationService.Instance["Quests_EmptyMessage"] : string.Empty;
    }

    private static void SyncCollection<T>(ObservableCollection<T> collection, List<T> target)
    {
        // 1. Remove items not in target
        var targetSet = new HashSet<T>(target);
        for (int i = collection.Count - 1; i >= 0; i--)
        {
            if (!targetSet.Contains(collection[i]))
            {
                collection.RemoveAt(i);
            }
        }

        // 2. Add/Move items
        for (int i = 0; i < target.Count; i++)
        {
            var item = target[i];
            var oldIndex = collection.IndexOf(item);

            if (oldIndex < 0)
            {
                collection.Insert(i, item);
            }
            else if (oldIndex != i)
            {
                collection.Move(oldIndex, i);
            }
        }
    }

    private void OnNavigate(string itemId)
    {
        NavigationRequested?.Invoke(itemId);
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
                    Name = LocalizationHelper.ResolveName(item.Name) ?? req.ItemId,
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
    private UserProgressState? _progressState;
    private IReadOnlyDictionary<string, ArcQuest> _questDefinitions;

    public QuestDisplayModel(UserProgressStore progressStore, UserProgressState? progressState, IReadOnlyDictionary<string, ArcQuest> questDefinitions)
    {
        _progressStore = progressStore;
        _progressState = progressState;
        _questDefinitions = questDefinitions;
    }

    public void UpdateState(UserProgressState? progressState, IReadOnlyDictionary<string, ArcQuest> questDefinitions)
    {
        _progressState = progressState;
        _questDefinitions = questDefinitions;
    }

    public string QuestId { get; set; } = string.Empty;

    public string Name { get; set; } = string.Empty;

    public string Description { get; set; } = string.Empty;

    public string Trader { get; set; } = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanComplete))]
    [NotifyPropertyChangedFor(nameof(CanReset))]
    private string _status = string.Empty;

    [ObservableProperty]
    private string _progressText = string.Empty;

    [ObservableProperty]
    private double _progressPercent;

    [ObservableProperty]
    private bool _isTracked;

    [ObservableProperty]
    private string _notes = string.Empty;

    [ObservableProperty]
    private List<ItemQuantityViewModel> _requiredItems = new();

    [ObservableProperty]
    private List<ItemQuantityViewModel> _rewardItems = new();

    [ObservableProperty]
    private List<QuestObjectiveViewModel> _objectives = new();

    [ObservableProperty]
    private List<QuestReferenceViewModel> _prerequisites = new();

    [ObservableProperty]
    private List<QuestReferenceViewModel> _unlocks = new();

    [ObservableProperty]
    private bool _isExpanded;

    public bool CanComplete => Status != "Completed";
    public bool CanReset => Status == "Completed";

    [RelayCommand]
    private void ToggleExpand()
    {
        IsExpanded = !IsExpanded;
    }

    [RelayCommand(CanExecute = nameof(CanReset))]
    private async Task ResetQuest()
    {
        if (_progressState == null) return;

        var quest = _progressState.Quests.FirstOrDefault(q => q.QuestId == QuestId);
        if (quest != null && quest.Status == QuestProgressStatus.Completed)
        {
            quest.Status = QuestProgressStatus.NotStarted;
            Status = QuestProgressStatus.NotStarted.ToString();
            await _progressStore.SaveAsync(_progressState, System.Threading.CancellationToken.None);
        }
    }

    [RelayCommand(CanExecute = nameof(CanComplete))]
    private async Task CompleteQuest()
    {
        if (_progressState == null) return;

        var toComplete = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var stack = new Stack<string>();
        stack.Push(QuestId);

        while (stack.Count > 0)
        {
            var currentId = stack.Pop();
            if (!toComplete.Add(currentId)) continue;

            if (_questDefinitions.TryGetValue(currentId, out var def) && def.PreviousQuestIds != null)
            {
                foreach (var prevId in def.PreviousQuestIds)
                {
                    if (!string.IsNullOrWhiteSpace(prevId))
                    {
                        var prevQuestState = _progressState.Quests.FirstOrDefault(q => q.QuestId == prevId);
                        if (prevQuestState == null || prevQuestState.Status != QuestProgressStatus.Completed)
                        {
                            stack.Push(prevId);
                        }
                    }
                }
            }
        }

        bool changed = false;
        foreach (var qId in toComplete)
        {
            var quest = _progressState.Quests.FirstOrDefault(q => q.QuestId == qId);
            if (quest == null)
            {
                quest = new QuestProgressState { QuestId = qId };
                _progressState.Quests.Add(quest);
            }

            if (quest.Status != QuestProgressStatus.Completed)
            {
                quest.Status = QuestProgressStatus.Completed;
                changed = true;
            }
        }

        if (changed)
        {
            Status = QuestProgressStatus.Completed.ToString();
            await _progressStore.SaveAsync(_progressState, System.Threading.CancellationToken.None);
        }
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

internal partial class QuestTreeNode : ObservableObject
{
    [ObservableProperty]
    private double _x;

    [ObservableProperty]
    private double _y;

    public QuestDisplayModel Quest { get; set; } = null!;
}


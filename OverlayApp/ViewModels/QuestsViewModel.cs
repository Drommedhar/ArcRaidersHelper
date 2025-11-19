using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using OverlayApp.Data;
using OverlayApp.Data.Models;
using OverlayApp.Progress;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;

namespace OverlayApp.ViewModels;

internal sealed partial class QuestsViewModel : NavigationPaneViewModel
{
    private readonly UserProgressStore _progressStore;

    public QuestsViewModel(UserProgressStore progressStore) : base("Quests", "📜")
    {
        _progressStore = progressStore;
    }

    public ObservableCollection<QuestDisplayModel> Quests { get; } = new();

    [ObservableProperty]
    private string _emptyMessage = "Progress not loaded";

    public override void Update(ArcDataSnapshot? snapshot, UserProgressState? progress, ProgressReport? report)
    {
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

        var displayList = new List<QuestDisplayModel>();

        foreach (var pair in snapshot.Quests)
        {
            var questId = pair.Key;
            var definition = pair.Value;

            userQuests.TryGetValue(questId, out var userQuest);
            var status = userQuest?.Status ?? QuestProgressStatus.NotStarted;

            if (status == QuestProgressStatus.NotStarted)
            {
                // Check prerequisites
                var prereqs = definition.PreviousQuestIds;
                if (prereqs != null && prereqs.Any(pid => !completedQuestIds.Contains(pid)))
                {
                    continue;
                }
            }

            var totalObjectives = definition.Objectives.Count;
            var completedObjectives = userQuest?.CompletedObjectiveIds.Count ?? 0;

            var progressPercent = totalObjectives == 0
                ? (status == QuestProgressStatus.Completed ? 100 : 0)
                : (double)completedObjectives / totalObjectives * 100;

            displayList.Add(new QuestDisplayModel(_progressStore, progress)
            {
                QuestId = questId,
                Name = ResolveName(definition.Name) ?? questId,
                Trader = definition.Trader ?? "Unknown",
                Status = status.ToString(),
                ProgressText = totalObjectives > 0 ? $"{completedObjectives}/{totalObjectives}" : "-",
                ProgressPercent = progressPercent,
                IsTracked = userQuest?.Tracked ?? false,
                Notes = userQuest?.Notes ?? string.Empty
            });
        }

        var sorted = displayList
            .OrderBy(q => q.Status == "Completed" ? 2 : q.Status == "NotStarted" ? 1 : 0)
            .ThenBy(q => q.Name);

        foreach (var item in sorted)
        {
            Quests.Add(item);
        }

        EmptyMessage = Quests.Count == 0 ? "No quests available" : string.Empty;
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

    public string Trader { get; set; } = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanComplete))]
    private string _status = string.Empty;

    public string ProgressText { get; set; } = string.Empty;

    public double ProgressPercent { get; set; }

    public bool IsTracked { get; set; }

    public string Notes { get; set; } = string.Empty;

    public bool CanComplete => Status != "Completed";

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

using CommunityToolkit.Mvvm.ComponentModel;
using OverlayApp.Data;
using OverlayApp.Infrastructure;
using OverlayApp.Progress;
using System.Collections.ObjectModel;
using System.Linq;

namespace OverlayApp.ViewModels;

internal sealed partial class DashboardViewModel : NavigationPaneViewModel
{
    public DashboardViewModel() : base("Nav_Dashboard", "🏠")
    {
        LastUpdatedLabel = LocalizationService.Instance["Dashboard_Never"];
    }

    public ObservableCollection<DashboardQuestSummary> HighlightQuests { get; } = new();

    public ObservableCollection<DashboardNeedSummary> TopNeededItems { get; } = new();

    [ObservableProperty]
    private int _activeQuests;

    [ObservableProperty]
    private int _itemsMissing;

    [ObservableProperty]
    private double _questCompletionPercent;

    [ObservableProperty]
    private double _projectCompletionPercent;

    [ObservableProperty]
    private double _hideoutCompletionPercent;

    [ObservableProperty]
    private string _lastUpdatedLabel;

    public override void Update(ArcDataSnapshot? snapshot, UserProgressState? progress, ProgressReport? report)
    {
        ActiveQuests = report?.ActiveQuests.Count ?? 0;
        ItemsMissing = report?.NeededItems.Count ?? 0;
        QuestCompletionPercent = report?.Completion.QuestCompletionPercent ?? 0;
        ProjectCompletionPercent = report?.Completion.ProjectCompletionPercent ?? 0;
        HideoutCompletionPercent = report?.Completion.HideoutCompletionPercent ?? 0;
        LastUpdatedLabel = progress?.LastUpdatedUtc.ToLocalTime().ToString("g") ?? LocalizationService.Instance["Dashboard_Never"];

        HighlightQuests.Clear();
        if (report?.ActiveQuests is not null)
        {
            foreach (var quest in report.ActiveQuests
                         .OrderByDescending(q => q.CompletionPercent)
                         .Take(4))
            {
                HighlightQuests.Add(new DashboardQuestSummary
                {
                    QuestId = quest.QuestId,
                    Name = quest.DisplayName ?? quest.QuestId,
                    ProgressPercent = quest.CompletionPercent,
                    ProgressText = quest.TotalObjectives > 0
                        ? $"{quest.CompletedObjectives}/{quest.TotalObjectives}"
                        : "-"
                });
            }
        }

        TopNeededItems.Clear();
        if (report?.NeededItems is not null)
        {
            foreach (var need in report.NeededItems
                         .OrderByDescending(n => n.MissingQuantity)
                         .Take(4))
            {
                TopNeededItems.Add(new DashboardNeedSummary
                {
                    ItemId = need.ItemId,
                    Name = need.DisplayName ?? need.ItemId,
                    Missing = need.MissingQuantity,
                    MissingText = string.Format(LocalizationService.Instance["Dashboard_MissingFormat"], need.MissingQuantity),
                    ProgressPercent = need.RequiredQuantity == 0
                        ? 100
                        : (double)need.OwnedQuantity / need.RequiredQuantity * 100
                });
            }
        }
    }
}

internal sealed class DashboardQuestSummary
{
    public string QuestId { get; set; } = string.Empty;

    public string Name { get; set; } = string.Empty;

    public double ProgressPercent { get; set; }

    public string ProgressText { get; set; } = string.Empty;
}

internal sealed class DashboardNeedSummary
{
    public string ItemId { get; set; } = string.Empty;

    public string Name { get; set; } = string.Empty;

    public int Missing { get; set; }

    public string MissingText { get; set; } = string.Empty;

    public double ProgressPercent { get; set; }
}

using CommunityToolkit.Mvvm.ComponentModel;
using OverlayApp.Data;
using OverlayApp.Progress;
using System.Collections.ObjectModel;
using System.Linq;

namespace OverlayApp.ViewModels;

internal sealed partial class NeededItemsViewModel : NavigationPaneViewModel
{
    public NeededItemsViewModel() : base("Needed Items", "📦")
    {
    }

    public ObservableCollection<NeededItemDisplayModel> Items { get; } = new();

    [ObservableProperty]
    private string _emptyMessage = "Progress not loaded";

    public override void Update(ArcDataSnapshot? snapshot, UserProgressState? progress, ProgressReport? report)
    {
        Items.Clear();
        if (report?.NeededItems is null)
        {
            EmptyMessage = "Progress not loaded";
            return;
        }

        foreach (var item in report.NeededItems.OrderByDescending(i => i.MissingQuantity))
        {
            Items.Add(new NeededItemDisplayModel
            {
                ItemId = item.ItemId,
                Name = item.DisplayName ?? item.ItemId,
                Owned = item.OwnedQuantity,
                Required = item.RequiredQuantity,
                Missing = item.MissingQuantity,
                ProgressPercent = item.RequiredQuantity == 0
                    ? 100
                    : (double)item.OwnedQuantity / item.RequiredQuantity * 100
            });
        }

        EmptyMessage = Items.Count == 0 ? "All tracked requirements satisfied" : string.Empty;
    }
}

internal sealed class NeededItemDisplayModel
{
    public string ItemId { get; set; } = string.Empty;

    public string Name { get; set; } = string.Empty;

    public int Owned { get; set; }

    public int Required { get; set; }

    public int Missing { get; set; }

    public double ProgressPercent { get; set; }
}

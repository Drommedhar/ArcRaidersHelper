using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using OverlayApp.Data;
using OverlayApp.Infrastructure;
using OverlayApp.Progress;
using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Windows.Input;

namespace OverlayApp.ViewModels;

internal sealed partial class NeededItemsViewModel : NavigationPaneViewModel
{
    public event Action<string>? NavigationRequested;

    public NeededItemsViewModel() : base("Nav_NeededItems", "📦")
    {
        EmptyMessage = LocalizationService.Instance["NeededItems_EmptyMessage"];
    }

    public ObservableCollection<RequirementGroupDisplayModel> Groups { get; } = new();

    [ObservableProperty]
    private string _emptyMessage;

    public override void Update(ArcDataSnapshot? snapshot, UserProgressState? progress, ProgressReport? report)
    {
        Groups.Clear();
        if (report?.GroupedRequirements is null)
        {
            EmptyMessage = LocalizationService.Instance["NeededItems_EmptyMessage"];
            return;
        }

        foreach (var group in report.GroupedRequirements)
        {
            var groupModel = new RequirementGroupDisplayModel { Category = group.Category };
            foreach (var source in group.Sources)
            {
                var sourceModel = new RequirementSourceDisplayModel 
                { 
                    Name = source.Name,
                    Subtitle = source.Subtitle
                };
                
                foreach (var item in source.Items)
                {
                    sourceModel.Items.Add(new NeededItemDisplayModel(OnNavigate)
                    {
                        ItemId = item.ItemId,
                        Name = item.DisplayName ?? item.ItemId,
                        ImageFilename = item.ImageFilename,
                        Rarity = item.Rarity,
                        Owned = item.OwnedQuantity,
                        Required = item.RequiredQuantity,
                        Missing = item.MissingQuantity,
                        NeedText = string.Format(LocalizationService.Instance["NeededItems_NeedFormat"], item.MissingQuantity),
                        HaveText = string.Format(LocalizationService.Instance["NeededItems_HaveFormat"], item.OwnedQuantity),
                        ProgressPercent = item.RequiredQuantity == 0
                            ? 100
                            : (double)item.OwnedQuantity / item.RequiredQuantity * 100
                    });
                }
                groupModel.Sources.Add(sourceModel);
            }
            Groups.Add(groupModel);
        }

        EmptyMessage = Groups.Count == 0 ? LocalizationService.Instance["NeededItems_AllSatisfied"] : string.Empty;
    }

    private void OnNavigate(string itemId)
    {
        NavigationRequested?.Invoke(itemId);
    }
}

internal sealed partial class RequirementGroupDisplayModel : ObservableObject
{
    private string _category = string.Empty;
    public string Category 
    { 
        get => _category;
        set
        {
            _category = value;
            // Try to localize category
            var key = $"Category_{value}";
            var localized = LocalizationService.Instance[key];
            // If localization returns the key itself (or if we want to be safe), check if it exists.
            // But LocalizationService usually returns the key if missing.
            // Here we assume if it starts with "Category_", it might be missing.
            // But simpler: just use the value if the key is not found or same as key.
            // Actually, let's just try to look it up.
            if (localized != key)
            {
                DisplayCategory = localized;
            }
            else
            {
                DisplayCategory = value;
            }
        }
    }

    public string DisplayCategory { get; private set; } = string.Empty;

    public ObservableCollection<RequirementSourceDisplayModel> Sources { get; } = new();

    [ObservableProperty]
    private bool _isExpanded;

    [RelayCommand]
    private void ToggleExpand()
    {
        IsExpanded = !IsExpanded;
    }
}

internal sealed partial class RequirementSourceDisplayModel : ObservableObject
{
    public string Name { get; set; } = string.Empty;
    public string Subtitle { get; set; } = string.Empty;
    public ObservableCollection<NeededItemDisplayModel> Items { get; } = new();

    [ObservableProperty]
    private bool _isExpanded;

    [RelayCommand]
    private void ToggleExpand()
    {
        IsExpanded = !IsExpanded;
    }
}

internal sealed class NeededItemDisplayModel
{
    private readonly Action<string> _navigateAction;

    public NeededItemDisplayModel(Action<string> navigateAction)
    {
        _navigateAction = navigateAction;
        NavigateCommand = new RelayCommand(() => _navigateAction(ItemId));
    }

    public string ItemId { get; set; } = string.Empty;

    public string Name { get; set; } = string.Empty;

    public string? ImageFilename { get; set; }

    public string? Rarity { get; set; }

    public int Owned { get; set; }

    public int Required { get; set; }

    public int Missing { get; set; }

    public string NeedText { get; set; } = string.Empty;
    public string HaveText { get; set; } = string.Empty;

    public double ProgressPercent { get; set; }

    public string SourcesText { get; set; } = string.Empty;

    public ICommand NavigateCommand { get; }
}

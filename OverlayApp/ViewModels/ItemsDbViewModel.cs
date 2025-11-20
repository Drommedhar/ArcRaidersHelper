using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using OverlayApp.Data;
using OverlayApp.Data.Models;
using OverlayApp.Infrastructure;
using OverlayApp.Progress;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Text.Json;

namespace OverlayApp.ViewModels;

internal sealed partial class ItemsDbViewModel : NavigationPaneViewModel
{
    private readonly List<ItemEntryViewModel> _allItems = new();

    public ItemsDbViewModel() : base("Nav_ItemsDb", "📚")
    {
        EmptyMessage = LocalizationService.Instance["ItemsDb_EmptyMessage"];
    }

    public ObservableCollection<ItemEntryViewModel> Items { get; } = new();
    public ObservableCollection<string> AvailableTypes { get; } = new();
    public ObservableCollection<string> AvailableRarities { get; } = new();

    [ObservableProperty]
    private string _searchText = string.Empty;

    [ObservableProperty]
    private string? _selectedType;

    [ObservableProperty]
    private string? _selectedRarity;

    [ObservableProperty]
    private string _emptyMessage;

    [ObservableProperty]
    private ItemEntryViewModel? _scrollToItem;

    public event Action<ItemEntryViewModel>? RequestScrollToItem;

    public override void Update(ArcDataSnapshot? snapshot, UserProgressState? progress, ProgressReport? report)
    {
        var currentType = SelectedType;
        var currentRarity = SelectedRarity;
        var expandedIds = _allItems.Where(i => i.IsExpanded).Select(i => i.ItemId).ToHashSet();

        _allItems.Clear();
        Items.Clear();
        AvailableTypes.Clear();
        AvailableRarities.Clear();

        if (snapshot?.Items is null)
        {
            EmptyMessage = LocalizationService.Instance["ItemsDb_DataNotLoaded"];
            return;
        }

        var inventory = progress?.Inventory?.ToDictionary(i => i.ItemId, i => i.Quantity, StringComparer.OrdinalIgnoreCase)
                        ?? new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        var types = new HashSet<string>();
        var rarities = new HashSet<string>();

        foreach (var pair in snapshot.Items.OrderBy(p => p.Key, StringComparer.OrdinalIgnoreCase))
        {
            var entry = pair.Value;
            var type = entry.Type ?? "Unknown";
            var rarity = entry.Rarity ?? "Unknown";

            types.Add(type);
            rarities.Add(rarity);

            var effects = new List<ItemEffectViewModel>();
            if (entry.Effects != null)
            {
                foreach (var effectPair in entry.Effects)
                {
                    if (effectPair.Value is null)
                    {
                        continue;
                    }

                    var effectName = LocalizationHelper.ResolveName(effectPair.Value.ToDictionary(k => k.Key, v => v.Value.ValueKind == JsonValueKind.String ? v.Value.GetString() ?? "" : v.Value.ToString())) ?? effectPair.Key;
                    var effectValue = effectPair.Value.TryGetValue("value", out var val) ? (val.ValueKind == JsonValueKind.String ? val.GetString() : val.ToString()) : "";
                    
                    if (!string.IsNullOrEmpty(effectName))
                    {
                        effects.Add(new ItemEffectViewModel { Name = effectName, Value = effectValue ?? "" });
                    }
                }
            }

            var newItem = new ItemEntryViewModel
            {
                ItemId = pair.Key,
                Name = LocalizationHelper.ResolveName(entry.Name) ?? pair.Key,
                Description = LocalizationHelper.ResolveName(entry.Description) ?? string.Empty,
                ImageFilename = entry.ImageFilename ?? string.Empty,
                Type = GetLocalizedType(type),
                Rarity = GetLocalizedRarity(rarity),
                Value = entry.Value ?? 0,
                Owned = inventory.TryGetValue(pair.Key, out var owned) ? owned : 0,
                StackSize = entry.StackSize ?? 1,
                Weight = entry.WeightKg ?? 0,
                FoundIn = entry.FoundIn ?? string.Empty,
                UpdatedAt = entry.UpdatedAt ?? string.Empty,
                OwnedText = string.Format(LocalizationService.Instance["ItemsDb_Owned"], owned),
                LastUpdatedText = string.Format(LocalizationService.Instance["ItemsDb_LastUpdated"], entry.UpdatedAt ?? string.Empty),
                Effects = effects,
                RecyclesInto = CreateItemQuantityList(entry.RecyclesInto, snapshot.Items, NavigateToItem),
                SalvagesInto = CreateItemQuantityList(entry.SalvagesInto, snapshot.Items, NavigateToItem)
            };

            if (expandedIds.Contains(pair.Key))
            {
                newItem.IsExpanded = true;
            }

            _allItems.Add(newItem);
        }

        var allString = LocalizationService.Instance["ItemsDb_Filter_All"];

        foreach (var t in types.OrderBy(t => t)) AvailableTypes.Add(GetLocalizedType(t));
        AvailableTypes.Insert(0, allString);
        
        foreach (var r in rarities.OrderBy(r => r)) AvailableRarities.Add(GetLocalizedRarity(r));
        AvailableRarities.Insert(0, allString);

        SelectedType = !string.IsNullOrEmpty(currentType) && AvailableTypes.Contains(currentType) ? currentType : allString;
        SelectedRarity = !string.IsNullOrEmpty(currentRarity) && AvailableRarities.Contains(currentRarity) ? currentRarity : allString;

        ApplyFilter();
        EmptyMessage = Items.Count == 0 ? LocalizationService.Instance["ItemsDb_NoMatch"] : string.Empty;
    }

    private string GetLocalizedType(string type)
    {
        var key = $"Type_{type.Replace(" ", "")}";
        var localized = LocalizationService.Instance[key];
        return localized != key ? localized : type;
    }

    private string GetLocalizedRarity(string rarity)
    {
        var key = $"Rarity_{rarity.Replace(" ", "")}";
        var localized = LocalizationService.Instance[key];
        return localized != key ? localized : rarity;
    }

    partial void OnSearchTextChanged(string value) => ApplyFilter();
    partial void OnSelectedTypeChanged(string? value) => ApplyFilter();
    partial void OnSelectedRarityChanged(string? value) => ApplyFilter();

    private void ApplyFilter()
    {
        Items.Clear();
        var query = SearchText?.Trim() ?? string.Empty;
        var allString = LocalizationService.Instance["ItemsDb_Filter_All"];
        
        var filtered = _allItems.AsEnumerable();

        if (!string.IsNullOrWhiteSpace(query))
        {
            filtered = filtered.Where(i => i.Name.Contains(query, StringComparison.OrdinalIgnoreCase) || i.ItemId.Contains(query, StringComparison.OrdinalIgnoreCase));
        }

        if (!string.IsNullOrEmpty(SelectedType) && !string.Equals(SelectedType, allString, StringComparison.OrdinalIgnoreCase))
        {
            filtered = filtered.Where(i => i.Type.Equals(SelectedType, StringComparison.OrdinalIgnoreCase));
        }

        if (!string.IsNullOrEmpty(SelectedRarity) && !string.Equals(SelectedRarity, allString, StringComparison.OrdinalIgnoreCase))
        {
            filtered = filtered.Where(i => i.Rarity.Equals(SelectedRarity, StringComparison.OrdinalIgnoreCase));
        }

        foreach (var item in filtered)
        {
            Items.Add(item);
        }
    }

    public void NavigateToItem(string itemId)
    {
        var target = _allItems.FirstOrDefault(i => i.ItemId.Equals(itemId, StringComparison.OrdinalIgnoreCase));
        if (target is null)
        {
            return;
        }

        var allString = LocalizationService.Instance["ItemsDb_Filter_All"];

        // Clear filters if the item is hidden
        var matchesType = string.Equals(SelectedType, allString, StringComparison.OrdinalIgnoreCase) || string.Equals(target.Type, SelectedType, StringComparison.OrdinalIgnoreCase);
        var matchesRarity = string.Equals(SelectedRarity, allString, StringComparison.OrdinalIgnoreCase) || string.Equals(target.Rarity, SelectedRarity, StringComparison.OrdinalIgnoreCase);
        var matchesSearch = string.IsNullOrWhiteSpace(SearchText) || target.Name.Contains(SearchText, StringComparison.OrdinalIgnoreCase) || target.ItemId.Contains(SearchText, StringComparison.OrdinalIgnoreCase);

        if (!matchesType) SelectedType = allString;
        if (!matchesRarity) SelectedRarity = allString;
        if (!matchesSearch) SearchText = string.Empty;

        // Expand the target item
        target.IsExpanded = true;

        ScrollToItem = target;
        RequestScrollToItem?.Invoke(target);
    }

    private static List<ItemQuantityViewModel> CreateItemQuantityList(Dictionary<string, int>? source, IReadOnlyDictionary<string, ArcItem> allItems, Action<string> navigateAction)
    {
        if (source == null) return new();
        var list = new List<ItemQuantityViewModel>();
        foreach (var kvp in source)
        {
            var itemId = kvp.Key;
            var qty = kvp.Value;
            if (allItems.TryGetValue(itemId, out var item))
            {
                list.Add(new ItemQuantityViewModel(navigateAction)
                {
                    ItemId = itemId,
                    Name = LocalizationHelper.ResolveName(item.Name) ?? itemId,
                    ImageFilename = item.ImageFilename ?? "",
                    Quantity = qty,
                    Rarity = GetLocalizedRarityStatic(item.Rarity ?? "Common")
                });
            }
            else
            {
                list.Add(new ItemQuantityViewModel(navigateAction)
                {
                    ItemId = itemId,
                    Name = itemId,
                    ImageFilename = "",
                    Quantity = qty,
                    Rarity = GetLocalizedRarityStatic("Common")
                });
            }
        }
        return list;
    }

    private static string GetLocalizedRarityStatic(string rarity)
    {
        var key = $"Rarity_{rarity.Replace(" ", "")}";
        var localized = LocalizationService.Instance[key];
        return localized != key ? localized : rarity;
    }
}

internal sealed partial class ItemEntryViewModel : ObservableObject
{
    public string ItemId { get; set; } = string.Empty;

    public string Name { get; set; } = string.Empty;

    public string Description { get; set; } = string.Empty;

    public string ImageFilename { get; set; } = string.Empty;

    public string Type { get; set; } = string.Empty;

    public string Rarity { get; set; } = string.Empty;

    public double Value { get; set; }

    public int Owned { get; set; }

    public int StackSize { get; set; }

    public double Weight { get; set; }

    public string FoundIn { get; set; } = string.Empty;

    public string UpdatedAt { get; set; } = string.Empty;

    public string OwnedText { get; set; } = string.Empty;
    public string LastUpdatedText { get; set; } = string.Empty;

    public List<ItemEffectViewModel> Effects { get; set; } = new();

    public List<ItemQuantityViewModel> RecyclesInto { get; set; } = new();

    public List<ItemQuantityViewModel> SalvagesInto { get; set; } = new();

    [ObservableProperty]
    private bool _isExpanded;

    [RelayCommand]
    private void ToggleExpand()
    {
        IsExpanded = !IsExpanded;
    }
}

public class ItemEffectViewModel
{
    public string Name { get; set; } = string.Empty;
    public string Value { get; set; } = string.Empty;
}

public partial class ItemQuantityViewModel : ObservableObject
{
    private readonly Action<string> _navigateAction;

    public ItemQuantityViewModel(Action<string> navigateAction)
    {
        _navigateAction = navigateAction;
    }

    public string ItemId { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string ImageFilename { get; set; } = string.Empty;
    public int Quantity { get; set; }
    public string Rarity { get; set; } = string.Empty;

    [RelayCommand]
    private void Navigate()
    {
        _navigateAction(ItemId);
    }
}

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
using System.Threading.Tasks;

namespace OverlayApp.ViewModels;

internal sealed partial class HideoutViewModel : NavigationPaneViewModel
{
    private readonly UserProgressStore _progressStore;
    private readonly ILogger _logger;

    private static readonly Dictionary<string, string> ModuleImageMap = new(StringComparer.OrdinalIgnoreCase)
    {
        { "equipment_bench", "gearbench.png" },
        { "explosives_bench", "explosivesstation.png" },
        { "med_station", "medicallab.png" },
        { "refiner", "refiner.png" },
        { "utility_bench", "utilitystation.png" },
        { "weapon_bench", "gunsmith.png" }
    };

    public HideoutViewModel(UserProgressStore progressStore, ILogger logger) : base("Hideout", "🏚️")
    {
        _progressStore = progressStore;
        _logger = logger;
    }

    public ObservableCollection<HideoutModuleDisplayModel> Modules { get; } = new();

    [ObservableProperty]
    private string _emptyMessage = "Progress not loaded";

    public override void Update(ArcDataSnapshot? snapshot, UserProgressState? progress, ProgressReport? report)
    {
        try
        {
            Modules.Clear();
            if (snapshot?.HideoutModules is null)
            {
                EmptyMessage = "Data not loaded";
                return;
            }

            var userModules = progress?.HideoutModules?.ToDictionary(m => m.ModuleId, StringComparer.OrdinalIgnoreCase)
                              ?? new Dictionary<string, HideoutProgressState>(StringComparer.OrdinalIgnoreCase);

            foreach (var pair in snapshot.HideoutModules.OrderBy(m => m.Key))
            {
                var moduleId = pair.Key;
                var definition = pair.Value;

                userModules.TryGetValue(moduleId, out var userModule);

                var display = new HideoutModuleDisplayModel(progress, snapshot.Items, _progressStore, _logger)
                {
                    ModuleId = moduleId,
                    Name = ResolveName(definition.Name) ?? moduleId,
                    ImageFilename = ModuleImageMap.TryGetValue(moduleId, out var img) ? img : $"{moduleId}.png",
                    CurrentLevel = userModule?.CurrentLevel ?? 0,
                    MaxLevel = definition.MaxLevel,
                    Tracking = userModule?.Tracking ?? false,
                    Definition = definition
                };

                Modules.Add(display);
            }

            EmptyMessage = Modules.Count == 0 ? "No modules found" : string.Empty;
        }
        catch (Exception ex)
        {
            _logger.Log("HideoutViewModel", $"Error updating hideout view: {ex}");
            EmptyMessage = $"Error loading hideout: {ex.Message}";
        }
    }

    private static string? ResolveName(Dictionary<string, string>? values)
    {
        if (values is null)
        {
            return null;
        }

        return values.TryGetValue("en", out var en) && !string.IsNullOrWhiteSpace(en)
            ? en
            : values.Values.FirstOrDefault(v => !string.IsNullOrWhiteSpace(v));
    }
}

internal sealed partial class HideoutModuleDisplayModel : ObservableObject
{
    private readonly UserProgressState? _progressState;
    private readonly IReadOnlyDictionary<string, ArcItem>? _itemDb;
    private readonly UserProgressStore _progressStore;
    private readonly ILogger _logger;

    public HideoutModuleDisplayModel(UserProgressState? progressState, IReadOnlyDictionary<string, ArcItem>? itemDb, UserProgressStore progressStore, ILogger logger)
    {
        _progressState = progressState;
        _itemDb = itemDb;
        _progressStore = progressStore;
        _logger = logger;
    }

    public string ModuleId { get; set; } = string.Empty;

    public string Name { get; set; } = string.Empty;

    public string ImageFilename { get; set; } = string.Empty;

    public int MaxLevel { get; set; }

    public bool Tracking { get; set; }

    public HideoutModule? Definition { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ProgressPercent))]
    [NotifyPropertyChangedFor(nameof(NextLevelCost))]
    [NotifyCanExecuteChangedFor(nameof(IncrementLevelCommand))]
    [NotifyCanExecuteChangedFor(nameof(DecrementLevelCommand))]
    private int _currentLevel;

    public double ProgressPercent => MaxLevel == 0 ? 0 : (double)CurrentLevel / MaxLevel * 100;

    public string NextLevelCost
    {
        get
        {
            try
            {
                if (Definition == null || CurrentLevel >= MaxLevel) return "Max Level";
                if (Definition.Levels == null) return "Unknown";
                
                var nextLevel = Definition.Levels.FirstOrDefault(l => l.Level == CurrentLevel + 1);
                if (nextLevel == null) return "Unknown";

                if (nextLevel.RequirementItems == null || nextLevel.RequirementItems.Count == 0) return "None";

                var costs = nextLevel.RequirementItems.Select(r =>
                {
                    var name = r.ItemId ?? "Unknown";
                    if (r.ItemId != null && _itemDb != null && _itemDb.TryGetValue(r.ItemId, out var item))
                    {
                        if (item.Name != null && item.Name.TryGetValue("en", out var enName))
                        {
                            name = enName;
                        }
                    }
                    return $"{r.Quantity}x {name}";
                });

                return string.Join(", ", costs);
            }
            catch (Exception ex)
            {
                // Log error but don't crash
                try 
                {
                    _logger?.Log("HideoutModule", $"Error calculating cost for {ModuleId}: {ex}");
                }
                catch { /* Ignore logging errors */ }
                return "Error";
            }
        }
    }

    async partial void OnCurrentLevelChanged(int value)
    {
        try
        {
            if (_progressState == null) return;

            var module = _progressState.HideoutModules.FirstOrDefault(m => m.ModuleId == ModuleId);
            if (module == null)
            {
                module = new HideoutProgressState { ModuleId = ModuleId };
                _progressState.HideoutModules.Add(module);
            }
            module.CurrentLevel = value;
            
            await _progressStore.SaveAsync(_progressState, System.Threading.CancellationToken.None);
        }
        catch (Exception ex)
        {
            _logger.Log("HideoutModule", $"Error saving progress for {ModuleId}: {ex}");
        }
    }

    [RelayCommand(CanExecute = nameof(CanIncrement))]
    private void IncrementLevel()
    {
        if (CurrentLevel < MaxLevel)
        {
            CurrentLevel++;
        }
    }

    private bool CanIncrement() => CurrentLevel < MaxLevel;

    [RelayCommand(CanExecute = nameof(CanDecrement))]
    private void DecrementLevel()
    {
        if (CurrentLevel > 0)
        {
            CurrentLevel--;
        }
    }

    private bool CanDecrement() => CurrentLevel > 0;
}

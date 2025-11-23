using CommunityToolkit.Mvvm.ComponentModel;
using OverlayApp.Data;
using OverlayApp.Infrastructure;
using OverlayApp.Progress;
using System;
using System.Collections.ObjectModel;
using System.Linq;

namespace OverlayApp.ViewModels;

internal sealed partial class MainViewModel : ObservableObject
{
    public MainViewModel(UserProgressStore progressStore, ILogger logger)
    {
        Dashboard = new DashboardViewModel();
        Quests = new QuestsViewModel(progressStore);
        Quests.NavigationRequested += OnNeededItemNavigationRequested;
        NeededItems = new NeededItemsViewModel();
        NeededItems.NavigationRequested += OnNeededItemNavigationRequested;
        Hideout = new HideoutViewModel(progressStore, logger);
        Projects = new ProjectsViewModel(progressStore);
        ItemsDatabase = new ItemsDbViewModel();

        NavigationItems = new ObservableCollection<NavigationPaneViewModel>
        {
            Dashboard,
            Quests,
            NeededItems,
            Hideout,
            Projects,
            ItemsDatabase
        };

        SelectedNavigation = NavigationItems.FirstOrDefault();
        StatusMessage = LocalizationService.Instance["Status_Initializing"];
    }

    public ObservableCollection<NavigationPaneViewModel> NavigationItems { get; }

    [ObservableProperty]
    private NavigationPaneViewModel? _selectedNavigation;

    [ObservableProperty]
    private string _statusMessage = string.Empty;

    [ObservableProperty]
    private bool _isBusy;

    public DashboardViewModel Dashboard { get; }

    public QuestsViewModel Quests { get; }

    public NeededItemsViewModel NeededItems { get; }

    public HideoutViewModel Hideout { get; }

    public ProjectsViewModel Projects { get; }

    public ItemsDbViewModel ItemsDatabase { get; }

    public object? CurrentView => SelectedNavigation;

    public void UpdateData(ArcDataSnapshot? snapshot, UserProgressState? progress, ProgressReport? report)
    {
        foreach (var pane in NavigationItems)
        {
            pane.Update(snapshot, progress, report);
        }

        OnPropertyChanged(nameof(CurrentView));
    }

    public void SetStatus(string message, bool isBusy)
    {
        StatusMessage = message;
        IsBusy = isBusy;
    }

    public void SetClickThrough(bool enabled)
    {
        foreach (var pane in NavigationItems)
        {
            pane.IsClickThrough = enabled;
        }
    }

    private void OnNeededItemNavigationRequested(string itemId)
    {
        SelectedNavigation = ItemsDatabase;
        ItemsDatabase.NavigateToItem(itemId);
    }

    partial void OnSelectedNavigationChanged(NavigationPaneViewModel? value)
    {
        OnPropertyChanged(nameof(CurrentView));
    }
}

internal abstract partial class NavigationPaneViewModel : ObservableObject
{
    private readonly string _titleKey;

    protected NavigationPaneViewModel(string titleKey, string icon)
    {
        _titleKey = titleKey;
        Icon = icon;
        UpdateTitle();
        LocalizationService.Instance.PropertyChanged += (s, e) => UpdateTitle();
    }

    [ObservableProperty]
    private string _title = string.Empty;

    [ObservableProperty]
    private bool _isClickThrough;

    public string Icon { get; }

    public abstract void Update(ArcDataSnapshot? snapshot, UserProgressState? progress, ProgressReport? report);

    protected void UpdateTitle()
    {
        Title = LocalizationService.Instance[_titleKey];
    }
}

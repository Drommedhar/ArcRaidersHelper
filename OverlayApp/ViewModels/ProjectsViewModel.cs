using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using OverlayApp.Data;
using OverlayApp.Data.Models;
using OverlayApp.Progress;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace OverlayApp.ViewModels;

internal sealed partial class ProjectsViewModel : NavigationPaneViewModel
{
    private readonly UserProgressStore _progressStore;
    private UserProgressState? _lastKnownState;

    public ProjectsViewModel(UserProgressStore progressStore) : base("Projects", "🏗️")
    {
        _progressStore = progressStore;
    }

    public ObservableCollection<ProjectDisplayModel> Projects { get; } = new();

    [ObservableProperty]
    private string _emptyMessage = "Progress not loaded";

    public override void Update(ArcDataSnapshot? snapshot, UserProgressState? progress, ProgressReport? report)
    {
        _lastKnownState = progress;
        Projects.Clear();
        if (snapshot?.Projects is null || snapshot.Items is null)
        {
            EmptyMessage = "Data not loaded";
            return;
        }

        var userProjects = new Dictionary<string, ProjectProgressState>(StringComparer.OrdinalIgnoreCase);
        if (progress?.Projects != null)
        {
            foreach (var p in progress.Projects)
            {
                if (!string.IsNullOrEmpty(p.ProjectId))
                {
                    userProjects[p.ProjectId] = p;
                }
            }
        }

        foreach (var definition in snapshot.Projects.OrderBy(p => p.Id))
        {
            var projectId = definition.Id ?? "Unknown";
            userProjects.TryGetValue(projectId, out var userProject);

            var phasesCompleted = userProject?.HighestPhaseCompleted ?? 0;
            var isTracking = userProject?.Tracking ?? false;
            
            var phases = new List<ProjectPhaseDisplayModel>();
            if (definition.Phases != null)
            {
                foreach (var phase in definition.Phases.OrderBy(p => p.Phase))
                {
                    var requirements = new List<string>();
                    if (phase.RequirementItems != null)
                    {
                        foreach (var req in phase.RequirementItems)
                        {
                            var itemName = snapshot.Items.TryGetValue(req.ItemId ?? "", out var item) 
                                ? ResolveName(item.Name) 
                                : req.ItemId;
                            requirements.Add($"{req.Quantity}x {itemName}");
                        }
                    }
                    if (phase.RequirementCategories != null)
                    {
                        foreach (var cat in phase.RequirementCategories)
                        {
                            requirements.Add($"{cat.ValueRequired} {cat.Category}");
                        }
                    }

                    phases.Add(new ProjectPhaseDisplayModel
                    {
                        PhaseNumber = phase.Phase,
                        Name = ResolveName(phase.Name) ?? $"Phase {phase.Phase}",
                        Description = ResolveName(phase.Description) ?? string.Empty,
                        IsCompleted = phase.Phase <= phasesCompleted,
                        Requirements = requirements
                    });
                }
            }

            var currentPhase = phases.FirstOrDefault(p => !p.IsCompleted);

            Projects.Add(new ProjectDisplayModel(CompletePhaseAsync)
            {
                ProjectId = projectId,
                Name = ResolveName(definition.Name) ?? projectId,
                PhasesCompleted = phasesCompleted,
                TotalPhases = definition.Phases?.Count ?? 0,
                Tracking = isTracking,
                Phases = phases,
                CurrentPhase = currentPhase
            });
        }

        EmptyMessage = Projects.Count == 0 ? "No projects found" : string.Empty;
    }

    private async Task CompletePhaseAsync(string projectId)
    {
        if (_lastKnownState == null) return;

        var project = _lastKnownState.Projects.FirstOrDefault(p => p.ProjectId.Equals(projectId, StringComparison.OrdinalIgnoreCase));
        if (project == null)
        {
            project = new ProjectProgressState { ProjectId = projectId, HighestPhaseCompleted = 0 };
            _lastKnownState.Projects.Add(project);
        }

        project.HighestPhaseCompleted++;
        await _progressStore.SaveAsync(_lastKnownState, CancellationToken.None);
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

internal sealed partial class ProjectDisplayModel : ObservableObject
{
    private readonly Func<string, Task> _completePhaseAction;

    public ProjectDisplayModel(Func<string, Task> completePhaseAction)
    {
        _completePhaseAction = completePhaseAction;
    }

    public string ProjectId { get; set; } = string.Empty;

    public string Name { get; set; } = string.Empty;

    public int PhasesCompleted { get; set; }

    public int TotalPhases { get; set; }

    public bool Tracking { get; set; }

    public double ProgressPercent => TotalPhases == 0 ? 0 : (double)PhasesCompleted / TotalPhases * 100;

    public List<ProjectPhaseDisplayModel> Phases { get; set; } = new();

    public ProjectPhaseDisplayModel? CurrentPhase { get; set; }

    [RelayCommand]
    private async Task CompletePhase()
    {
        await _completePhaseAction(ProjectId);
    }
}

internal sealed class ProjectPhaseDisplayModel
{
    public int PhaseNumber { get; set; }
    public string Name { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public bool IsCompleted { get; set; }
    public List<string> Requirements { get; set; } = new();
}

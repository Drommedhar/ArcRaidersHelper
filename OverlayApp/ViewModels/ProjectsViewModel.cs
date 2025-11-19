using CommunityToolkit.Mvvm.ComponentModel;
using OverlayApp.Data;
using OverlayApp.Data.Models;
using OverlayApp.Progress;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;

namespace OverlayApp.ViewModels;

internal sealed partial class ProjectsViewModel : NavigationPaneViewModel
{
    public ProjectsViewModel() : base("Projects", "🏗️")
    {
    }

    public ObservableCollection<ProjectDisplayModel> Projects { get; } = new();

    [ObservableProperty]
    private string _emptyMessage = "Progress not loaded";

    public override void Update(ArcDataSnapshot? snapshot, UserProgressState? progress, ProgressReport? report)
    {
        Projects.Clear();
        if (progress?.Projects is null || snapshot is null)
        {
            EmptyMessage = "Progress not loaded";
            return;
        }

        foreach (var project in progress.Projects.OrderBy(p => p.ProjectId))
        {
            var definition = snapshot.Projects.FirstOrDefault(p => string.Equals(p.Id, project.ProjectId, System.StringComparison.OrdinalIgnoreCase));
            Projects.Add(new ProjectDisplayModel
            {
                ProjectId = project.ProjectId,
                Name = ResolveName(definition?.Name) ?? project.ProjectId,
                PhasesCompleted = project.HighestPhaseCompleted,
                TotalPhases = definition?.Phases.Count ?? 0,
                Tracking = project.Tracking
            });
        }

        EmptyMessage = Projects.Count == 0 ? "No projects tracked" : string.Empty;
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

internal sealed class ProjectDisplayModel
{
    public string ProjectId { get; set; } = string.Empty;

    public string Name { get; set; } = string.Empty;

    public int PhasesCompleted { get; set; }

    public int TotalPhases { get; set; }

    public bool Tracking { get; set; }

    public double ProgressPercent => TotalPhases == 0 ? 0 : (double)PhasesCompleted / TotalPhases * 100;
}

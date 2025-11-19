using System;
using System.Collections.Generic;
using OverlayApp.Data.Models;

namespace OverlayApp.Data;

internal sealed class ArcDataSnapshot
{
    public ArcDataSnapshot(
        string? commitSha,
        DateTimeOffset lastSyncedUtc,
        IReadOnlyList<ArcProject> projects,
        IReadOnlyDictionary<string, ArcItem> items,
        IReadOnlyDictionary<string, HideoutModule> hideoutModules,
        IReadOnlyDictionary<string, ArcQuest> quests)
    {
        CommitSha = commitSha;
        LastSyncedUtc = lastSyncedUtc;
        Projects = projects;
        Items = items;
        HideoutModules = hideoutModules;
        Quests = quests;
    }

    public string? CommitSha { get; }

    public DateTimeOffset LastSyncedUtc { get; }

    public IReadOnlyList<ArcProject> Projects { get; }

    public IReadOnlyDictionary<string, ArcItem> Items { get; }

    public IReadOnlyDictionary<string, HideoutModule> HideoutModules { get; }

    public IReadOnlyDictionary<string, ArcQuest> Quests { get; }
}

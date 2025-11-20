using System;
using System.Collections.Generic;

namespace OverlayApp.Services;

internal sealed record QuestDetectionMatch(string QuestId, string DetectedName, double Confidence, string DisplayName);

internal sealed class QuestDetectionEventArgs : EventArgs
{
    public QuestDetectionEventArgs(IReadOnlyList<QuestDetectionMatch> matches, DateTimeOffset timestamp)
    {
        Matches = matches;
        Timestamp = timestamp;
    }

    public IReadOnlyList<QuestDetectionMatch> Matches { get; }
    public DateTimeOffset Timestamp { get; }
}

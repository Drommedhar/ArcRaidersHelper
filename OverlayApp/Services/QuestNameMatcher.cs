using OverlayApp.Data;
using OverlayApp.Data.Models;
using OverlayApp.Infrastructure;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace OverlayApp.Services;

internal sealed class QuestNameMatcher
{
    private static readonly Regex MultiSpaceRegex = new("\\s+", RegexOptions.Compiled);
    private readonly List<Entry> _entries;

    private QuestNameMatcher(List<Entry> entries)
    {
        _entries = entries;
    }

    public static QuestNameMatcher Empty { get; } = new(new List<Entry>());

    public bool IsEmpty => _entries.Count == 0;

    public static QuestNameMatcher FromSnapshot(ArcDataSnapshot? snapshot)
    {
        if (snapshot?.Quests is null || snapshot.Quests.Count == 0)
        {
            return Empty;
        }

        var entries = new List<Entry>(snapshot.Quests.Count);
        foreach (var entry in snapshot.Quests)
        {
            var questId = entry.Key;
            if (string.IsNullOrWhiteSpace(questId))
            {
                continue;
            }

            var quest = entry.Value;
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                questId
            };

            if (quest.Name != null)
            {
                foreach (var localized in quest.Name.Values)
                {
                    if (!string.IsNullOrWhiteSpace(localized))
                    {
                        names.Add(localized);
                    }
                }
            }

            var normalizedNames = names
                .Select(NormalizeText)
                .Where(n => !string.IsNullOrWhiteSpace(n))
                .Distinct(StringComparer.Ordinal)
                .ToList();

            if (normalizedNames.Count == 0)
            {
                continue;
            }

            var displayName = LocalizationHelper.ResolveName(quest.Name) ?? questId;
            entries.Add(new Entry(questId, displayName, normalizedNames));
        }

        return new QuestNameMatcher(entries);
    }

    public QuestDetectionMatch? Match(string candidate)
    {
        if (string.IsNullOrWhiteSpace(candidate))
        {
            return null;
        }

        var normalizedCandidate = NormalizeText(candidate);
        if (string.IsNullOrWhiteSpace(normalizedCandidate))
        {
            return null;
        }

        Entry? bestEntry = null;
        double bestScore = 0;

        foreach (var entry in _entries)
        {
            foreach (var token in entry.NormalizedNames)
            {
                if (token.Equals(normalizedCandidate, StringComparison.Ordinal))
                {
                    return new QuestDetectionMatch(entry.QuestId, candidate, 1.0, entry.DisplayName);
                }

                var score = Similarity(normalizedCandidate, token);
                if (score > bestScore)
                {
                    bestScore = score;
                    bestEntry = entry;
                }
            }
        }

        if (bestEntry != null && bestScore >= MinimumConfidence)
        {
            return new QuestDetectionMatch(bestEntry.QuestId, candidate, bestScore, bestEntry.DisplayName);
        }

        return null;
    }

    public static string NormalizeText(string input)
    {
        if (string.IsNullOrWhiteSpace(input))
        {
            return string.Empty;
        }

        var trimmed = input.Trim();
        var normalized = trimmed.Normalize(NormalizationForm.FormD);
        var builder = new StringBuilder(capacity: normalized.Length);

        foreach (var ch in normalized)
        {
            var category = CharUnicodeInfo.GetUnicodeCategory(ch);
            if (category == UnicodeCategory.NonSpacingMark)
            {
                continue;
            }

            if (char.IsLetterOrDigit(ch))
            {
                builder.Append(char.ToUpperInvariant(ch));
            }
            else if (char.IsWhiteSpace(ch))
            {
                builder.Append(' ');
            }
        }

        var collapsed = MultiSpaceRegex.Replace(builder.ToString(), " ").Trim();
        return collapsed;
    }

    public const double MinimumConfidence = 0.52;

    private static double Similarity(string a, string b)
    {
        if (a.Length == 0 || b.Length == 0)
        {
            return 0;
        }

        var distance = LevenshteinDistance(a, b);
        var max = Math.Max(a.Length, b.Length);
        if (max == 0)
        {
            return 1;
        }

        return 1d - distance / (double)max;
    }

    private static int LevenshteinDistance(string a, string b)
    {
        var costs = new int[b.Length + 1];
        for (var i = 0; i <= b.Length; i++)
        {
            costs[i] = i;
        }

        for (var i = 1; i <= a.Length; i++)
        {
            var prevCost = costs[0];
            costs[0] = i;

            for (var j = 1; j <= b.Length; j++)
            {
                var currentCost = costs[j];
                if (a[i - 1] == b[j - 1])
                {
                    costs[j] = prevCost;
                }
                else
                {
                    costs[j] = Math.Min(Math.Min(costs[j - 1], currentCost), prevCost) + 1;
                }

                prevCost = currentCost;
            }
        }

        return costs[b.Length];
    }

    private sealed record Entry(string QuestId, string DisplayName, List<string> NormalizedNames);
}

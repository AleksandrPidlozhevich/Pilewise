using Pilewise.Services;
using System;
using System.Collections.Generic;
using System.Linq;

namespace Pilewise.ViewModels;

/// <summary>The viewer's pure list logic: which filters exist and what a filter + search + sort shows.</summary>
internal static class LibraryQuery
{
    public static List<LibraryFilterOption> BuildFilters(IReadOnlyList<LibraryGameItem> all, bool hasCompletion)
    {
        var exported = all.Where(g => !g.IsHidden).ToList();
        var options = new List<LibraryFilterOption> { new("All platforms", LibraryViewModel.AllKey, exported.Count) };
        foreach (var label in PlatformCatalog.All.Select(d => d.Label).Append(Platforms.GamePass))
        {
            var count = exported.Count(g => g.Platforms.Contains(label));
            if (count > 0) options.Add(new LibraryFilterOption(label, label, count));
        }

        var multi = exported.Count(g => g.IsMultiStore);
        if (multi > 0) options.Add(new LibraryFilterOption("Owned on 2+ stores", LibraryViewModel.MultiStoreKey, multi));

        if (hasCompletion)
        {
            options.Add(new LibraryFilterOption("Completed", LibraryViewModel.CompletedKey, exported.Count(g => g.IsCompleted)));
            options.Add(new LibraryFilterOption("Not completed", LibraryViewModel.NotCompletedKey, exported.Count(g => !g.IsCompleted)));
        }

        var recent = exported.Count(g => g.IsNew);
        if (recent > 0)
            options.Add(new LibraryFilterOption($"Added in the last {FirstSeenGames.RecentDays} days", LibraryViewModel.RecentKey, recent));

        var never = exported.Count(g => g.IsNeverPlayed);
        if (never > 0) options.Add(new LibraryFilterOption("Never played", LibraryViewModel.NeverPlayedKey, never));

        var hidden = all.Count - exported.Count;
        if (hidden > 0) options.Add(new LibraryFilterOption("Hidden", LibraryViewModel.HiddenKey, hidden));

        return options;
    }

    // all is already name-ordered and OrderBy is stable, so ties stay alphabetical.
    public static List<LibraryGameItem> Apply(IReadOnlyList<LibraryGameItem> all, string key, string? query, LibrarySort sort)
    {
        var filtered = all
            .Where(g => key == LibraryViewModel.HiddenKey ? g.IsHidden : !g.IsHidden)
            .Where(g => key switch
            {
                LibraryViewModel.AllKey or LibraryViewModel.HiddenKey => true,
                LibraryViewModel.MultiStoreKey => g.IsMultiStore,
                LibraryViewModel.NeverPlayedKey => g.IsNeverPlayed,
                LibraryViewModel.CompletedKey => g.IsCompleted,
                LibraryViewModel.NotCompletedKey => !g.IsCompleted,
                LibraryViewModel.RecentKey => g.IsNew,
                _ => g.Platforms.Contains(key)
            })
            .Where(g => string.IsNullOrEmpty(query) || g.Name.Contains(query, StringComparison.OrdinalIgnoreCase));

        return (sort switch
        {
            LibrarySort.MostPlayed => filtered.OrderByDescending(g => g.PlaytimeMinutes ?? -1),
            LibrarySort.RecentlyPlayed => filtered.OrderByDescending(g => g.LastPlayed ?? DateTimeOffset.MinValue),
            LibrarySort.RecentlyAdded => filtered.OrderByDescending(g => g.AddedOn ?? DateOnly.MinValue),
            _ => filtered
        }).ToList();
    }
}

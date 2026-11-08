using Pilewise.Services;
using System;
using System.Collections.Generic;
using System.Linq;

namespace Pilewise.ViewModels;

public sealed record LibraryGameItem(
    string Name,
    IReadOnlyList<string> Platforms,
    string Id,
    int? PlaytimeMinutes,
    DateTimeOffset? LastPlayed,
    bool IsHidden = false,
    CsvGameExportRow? Row = null,
    bool IsCompleted = false,
    DateOnly? AddedOn = null)
{
    public static LibraryGameItem From(
        CsvGameExportRow row, bool hidden = false, bool completed = false, DateOnly? addedOn = null)
    {
        var platforms = PlatformCatalog.All.Where(d => d.Has(row)).Select(d => d.Label).ToList();
        if (row.IsGamePass) platforms.Add(Services.Platforms.GamePass);
        return new LibraryGameItem(row.Name, platforms, row.IdText, row.PlaytimeMinutes, row.LastPlayed, hidden, row,
            completed, addedOn);
    }

    public bool IsNew => FirstSeenGames.IsRecent(AddedOn, DateOnly.FromDateTime(DateTime.Today));

    public string AddedText => AddedOn is { } day
        ? $"Added {day.ToString("d MMM", System.Globalization.CultureInfo.CurrentCulture)}"
        : "";

    /// <summary>Owned on two or more stores (Game Pass is a tag, not a purchase).</summary>
    public bool IsMultiStore => Platforms.Count(p => p != Services.Platforms.GamePass) > 1;

    /// <summary>Known to be untouched: a store reported 0 minutes and no store reports a session.</summary>
    public bool IsNeverPlayed => PlaytimeMinutes == 0 && LastPlayed == null;

    public string PlaytimeText => PlaytimeMinutes switch
    {
        null => "—",
        0 => "0 h",
        < 6 => "< 0.1 h",
        var m => $"{m / 60.0:0.#} h"
    };

    public string LastPlayedText => LastPlayed?.ToLocalTime().ToString("d") ?? "—";

    public string? StoreUrl => Row?.StoreUrl;

    public bool HasStoreUrl => StoreUrl != null;

    public string HideLabel => IsHidden ? "Unhide" : "Hide";

    public string HideTip => IsHidden
        ? "Include this game in exports again"
        : "Leave this game out of every export (demos, test servers, tools…)";
}

/// <summary>
/// Totals for the library (hidden games excluded). Only Steam and Xbox report playtime, so the
/// played share is out of the games that have playtime data.
/// </summary>
public sealed record LibraryStats(
    int Games, int Tracked, int Played, int NeverPlayed, int MultiStore, int TotalMinutes, LibraryGameItem? MostPlayed,
    int Completed = 0, string? CompletedSource = null, int RecentlyAdded = 0)
{
    public static LibraryStats Compute(
        IEnumerable<LibraryGameItem> games, BacklogSnapshot? backlog = null, string? source = null)
    {
        var list = games.ToList();
        var tracked = list.Where(g => g.PlaytimeMinutes != null || g.LastPlayed != null).ToList();
        return new LibraryStats(
            list.Count,
            tracked.Count,
            tracked.Count(g => !g.IsNeverPlayed),
            tracked.Count(g => g.IsNeverPlayed),
            list.Count(g => g.IsMultiStore),
            list.Sum(g => g.PlaytimeMinutes ?? 0),
            list.Where(g => g.PlaytimeMinutes > 0).MaxBy(g => g.PlaytimeMinutes),
            list.Count(g => g.IsCompleted),
            source ?? backlog?.Target,
            list.Count(g => g.IsNew));
    }

    public bool HasRecentlyAdded => RecentlyAdded > 0;

    public string CompletedValue => $"{CompletedPercent:0}%";

    public string CompletedLabel => $"completed · {Completed} of {Games}";

    public string PlayedValue => $"{PlayedPercent:0}%";

    public string PlayedLabel => $"played · {Played} of {Tracked}";

    public string HoursValue => $"{TotalMinutes / 60.0:#,0}";

    public string GamesValue => $"{Games:#,0}";

    public string RecentlyAddedText => $"{RecentlyAdded} added in the last {FirstSeenGames.RecentDays} days";

    public bool HasCompletion => CompletedSource != null;

    public double CompletedPercent => Games == 0 ? 0 : Math.Round(Completed * 100.0 / Games);

    public string CompletedText => $"Completed {Completed} of {Games} ({CompletedPercent:0}%)";

    public string CompletedTip => CompletedSource == LibraryViewModel.PilewiseSource
        ? "Games marked completed here or ticked in your CSV / Notion / Sheets; every export makes them the same everywhere."
        : $"Games ticked as completed in {CompletedSource}, as of its last export (export again to refresh).";

    public double PlayedPercent => Tracked == 0 ? 0 : Math.Round(Played * 100.0 / Tracked);

    public bool HasPlaytimeData => Tracked > 0;

    public string PlayedText => $"Played {Played} of {Tracked} ({PlayedPercent:0}%)";

    public string TotalText => $"{TotalMinutes / 60.0:#,0} h in total";

    public string NeverPlayedText => $"{NeverPlayed} never played";

    public string MultiStoreText => $"{MultiStore} on 2+ stores";

    public string MostPlayedText => MostPlayed == null ? "" : $"Most played: {MostPlayed.Name} ({MostPlayed.PlaytimeText})";

    public string PlayedTip =>
        $"Only Steam and Xbox report playtime: {Tracked} of {Games} games have it, and the share is out of those.";
}

using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Pilewise.Services;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

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

/// <summary>
/// What the library viewer can do outside itself; each one is optional (tests and the previewer
/// pass none, which hides the matching buttons).
/// </summary>
/// <param name="OpenLink">Opens a store page.</param>
/// <param name="CopyText">Puts text on the clipboard ("Copy list").</param>
/// <param name="RefreshBacklog">Re-reads the completed marks from the tracker (read-only).</param>
/// <param name="BacklogSource">The tracker <paramref name="RefreshBacklog"/> reads ("Notion").</param>
public sealed record LibraryActions(
    Action<string>? OpenLink = null,
    Func<string, Task>? CopyText = null,
    Func<CancellationToken, Task<BacklogSnapshot?>>? RefreshBacklog = null,
    string? BacklogSource = null);

public sealed record LibrarySortOption(string Label, LibrarySort Sort)
{
    public override string ToString() => Label;
}

public enum LibrarySort
{
    Name,
    MostPlayed,
    RecentlyPlayed,
    RecentlyAdded
}

public sealed record LibraryFilterOption(string Label, string Key, int Count)
{
    public override string ToString() => $"{Label} ({Count})";
}

public sealed partial class LibraryViewModel : ViewModelBase
{
    public const string AllKey = "*";
    public const string MultiStoreKey = "multi";
    public const string NeverPlayedKey = "never";
    public const string HiddenKey = "hidden";
    public const string CompletedKey = "completed";
    public const string NotCompletedKey = "not-completed";
    public const string RecentKey = "recent";
    public const string PilewiseSource = "Pilewise";

    private readonly List<LibraryGameItem> _all;
    private readonly IgnoredGames? _ignored;
    private readonly LibraryActions _actions;
    private readonly CompletedGames? _completed;
    private BacklogSnapshot? _backlog;

    [ObservableProperty] private string? searchText;

    [ObservableProperty] private LibraryFilterOption selectedFilter;

    [ObservableProperty] private LibrarySortOption selectedSort;

    [ObservableProperty] private IReadOnlyList<LibraryGameItem> visibleGames = [];

    [ObservableProperty] private IReadOnlyList<LibraryFilterOption> filters = [];

    [ObservableProperty] private LibraryStats stats = LibraryStats.Compute([]);

    [ObservableProperty] private LibraryGameItem? selectedGame;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasPicked), nameof(PickedText), nameof(CanOpenPickedStorePage))]
    private LibraryGameItem? picked;

    [ObservableProperty] private string? notice;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(RefreshLabel))]
    private bool isRefreshing;

    public bool HasPicked => Picked != null;

    public string PickedText => Picked == null ? "" : $"🎲 {Picked.Name} — {Describe(Picked)}";

    private static string Describe(LibraryGameItem game)
    {
        var stores = string.Join("/", game.Platforms);
        var played = game.IsNeverPlayed ? "never played"
            : game.PlaytimeMinutes is > 0 ? $"{game.PlaytimeText} played"
            : "no playtime data";
        return $"{stores} · {played}";
    }

    private readonly Random _random;

    /// <param name="rows">The games that will be exported.</param>
    /// <param name="hidden">Fetched games the user hid (shown under the "Hidden" filter).</param>
    /// <param name="ignored">Where Hide / Unhide is saved; null makes the viewer read-only.</param>
    /// <param name="backlog">Completed marks from the last tracker export (Notion / Sheets), if any.</param>
    /// <param name="actions">Store pages, clipboard and ✓ refresh (see <see cref="LibraryActions"/>).</param>
    /// <param name="completed">Pilewise's completed list: the ✓ marks, set from the list itself (null:
    /// read-only marks from <paramref name="backlog"/>).</param>
    public LibraryViewModel(
        IEnumerable<CsvGameExportRow> rows, IEnumerable<CsvGameExportRow>? hidden = null, IgnoredGames? ignored = null,
        BacklogSnapshot? backlog = null, LibraryActions? actions = null, Random? random = null,
        Func<CsvGameExportRow, DateOnly?>? addedOn = null, CompletedGames? completed = null)
    {
        _actions = actions ?? new LibraryActions();
        _completed = completed;
        _random = random ?? Random.Shared;
        _ignored = ignored;
        _backlog = backlog;

        _all = rows.Where(r => r.HasContent)
            .Select(r => LibraryGameItem.From(r, addedOn: addedOn?.Invoke(r)))
            .Concat((hidden ?? []).Where(r => r.HasContent)
                .Select(r => LibraryGameItem.From(r, true, addedOn: addedOn?.Invoke(r))))
            .OrderBy(g => g.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();

        Sorts =
        [
            new LibrarySortOption("Sort: name", LibrarySort.Name),
            new LibrarySortOption("Sort: most played", LibrarySort.MostPlayed),
            new LibrarySortOption("Sort: recently played", LibrarySort.RecentlyPlayed),
            new LibrarySortOption("Sort: recently added", LibrarySort.RecentlyAdded)
        ];
        selectedSort = Sorts[0];
        filters = BuildFilters();
        selectedFilter = filters[0];
        stats = LibraryStats.Compute([]);
        MarkCompleted();
    }

    public LibraryViewModel()
        : this([
            CsvGameExportService.CreateRow("Steam/GOG", "The Witcher 3", "Steam:292030; GOG:1207664643"),
            CsvGameExportService.CreateRow("Epic", "Alan Wake", "Epic:ns/cat"),
            CsvGameExportService.CreateRow("Steam", "Portal", "Steam:400")
        ])
    {
    }

    public IReadOnlyList<LibrarySortOption> Sorts { get; }

    public bool HasVisibleGames => VisibleGames.Count > 0;

    public bool CanHide => _ignored != null;

    private int ExportedCount => _all.Count(g => !g.IsHidden);

    public string Summary
    {
        get
        {
            var hidden = _all.Count - ExportedCount;
            var suffix = hidden > 0 ? $" · {hidden} hidden" : "";
            return VisibleGames.Count == ExportedCount && SelectedFilter?.Key != HiddenKey
                ? $"{ExportedCount} games{suffix}"
                : $"Showing {VisibleGames.Count} of {ExportedCount} games{suffix}";
        }
    }

    partial void OnSearchTextChanged(string? value) => ApplyFilter();

    partial void OnSelectedFilterChanged(LibraryFilterOption value) => ApplyFilter();

    partial void OnSelectedSortChanged(LibrarySortOption value) => ApplyFilter();

    public bool CanOpenStorePage => _actions.OpenLink != null;

    public bool CanOpenPickedStorePage => CanOpenStorePage && Picked?.HasStoreUrl == true;

    [RelayCommand]
    private void OpenStorePage(LibraryGameItem? item)
    {
        if (item?.StoreUrl is { } url) _actions.OpenLink?.Invoke(url);
    }

    public bool CanRefreshBacklog => _actions.RefreshBacklog != null;

    public string RefreshLabel => IsRefreshing ? "Refreshing…" : "Refresh";

    public string RefreshTip => $"Read the completed ticks from {_actions.BacklogSource} now (nothing is written)";

    public bool CanMarkCompleted => _completed != null;

    public string? BacklogText => _completed != null
        ? "Tick a game when you finish it · exports keep the ticks the same in your CSV, Notion and Sheets" +
          (CanRefreshBacklog ? $" · Refresh reads new ticks from {_actions.BacklogSource}" : "")
        : _backlog is { } b
        ? $"✓ marks from {b.Target}, as of {b.When.ToLocalTime().ToString("d MMM HH:mm", System.Globalization.CultureInfo.CurrentCulture)}"
        : CanRefreshBacklog ? $"No ✓ marks yet: read them from {_actions.BacklogSource}" : null;

    public bool HasBacklogText => BacklogText != null;

    [RelayCommand]
    private async Task RefreshBacklog()
    {
        if (_actions.RefreshBacklog is not { } refresh || IsRefreshing) return;
        IsRefreshing = true;
        Picked = null;
        try
        {
            var snapshot = await refresh(CancellationToken.None);
            if (snapshot == null)
            {
                Notice = $"No completion column found in {_actions.BacklogSource} (a Completed / Done checkbox or a Status).";
                return;
            }

            _backlog = snapshot;
            MarkCompleted();
            Notice = $"✓ marks updated from {snapshot.Target}: {snapshot.Text}.";
        }
        catch (Exception ex)
        {
            Notice = $"⚠ Couldn't read {_actions.BacklogSource}: {ex.Message}";
        }
        finally
        {
            IsRefreshing = false;
        }
    }

    private void MarkCompleted()
    {
        _completed?.Reload();
        var completed = new HashSet<string>(_backlog?.CompletedKeys ?? [], StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < _all.Count; i++)
        {
            var isCompleted = _all[i].Row is { } row && (_completed != null
                ? _completed.IsCompleted(row)
                : completed.Count > 0 && IgnoredGames.KeysOf(row).Any(completed.Contains));
            if (_all[i].IsCompleted != isCompleted) _all[i] = _all[i] with { IsCompleted = isCompleted };
        }

        Rebuild();
        OnPropertyChanged(nameof(BacklogText));
        OnPropertyChanged(nameof(HasBacklogText));
    }

    public bool CanCopy => _actions.CopyText != null;

    [RelayCommand]
    private async Task CopyList()
    {
        if (_actions.CopyText is not { } copy) return;
        Picked = null;
        try
        {
            await copy(ListText(VisibleGames));
            Notice = $"Copied {VisibleGames.Count} games to the clipboard.";
        }
        catch (Exception ex)
        {
            // The clipboard can be held by another app (Windows) or missing (headless Linux).
            Notice = $"⚠ Couldn't copy to the clipboard: {ex.Message}";
        }
    }

    public static string ListText(IEnumerable<LibraryGameItem> games) =>
        string.Join(Environment.NewLine, games.Select(g =>
            string.Join('\t', g.Name, string.Join("/", g.Platforms), g.PlaytimeText, g.LastPlayedText)));

    [RelayCommand]
    private void PickForMe()
    {
        var pick = Pick(VisibleGames, _random, Picked);
        Picked = pick;
        Notice = pick == null ? "Nothing to pick: every game shown is completed or hidden." : null;
        if (pick != null) SelectedGame = pick;
    }

    [RelayCommand]
    private void DismissPick()
    {
        Picked = null;
        Notice = null;
    }

    public static LibraryGameItem? Pick(IEnumerable<LibraryGameItem> shown, Random random, LibraryGameItem? previous = null)
    {
        var pool = shown.Where(g => !g.IsHidden && !g.IsCompleted).ToList();
        if (pool.Count == 0) return null;

        var fresh = pool.Where(g => g.IsNeverPlayed || g.PlaytimeMinutes is null or < 120).ToList();
        var from = fresh.Count > 0 ? fresh : pool;
        if (from.Count > 1 && previous != null) from.Remove(previous);
        return from[random.Next(from.Count)];
    }

    [RelayCommand]
    private void ToggleCompleted(LibraryGameItem? item)
    {
        if (item?.Row == null || _completed == null) return;

        _completed.Set(item.Row, !item.IsCompleted);
        var index = _all.IndexOf(item);
        if (index >= 0) _all[index] = item with { IsCompleted = !item.IsCompleted };
        if (Picked == item) Picked = null;

        Rebuild();
    }

    [RelayCommand]
    private void ToggleHidden(LibraryGameItem? item)
    {
        if (item?.Row == null || _ignored == null) return;

        if (item.IsHidden) _ignored.Unignore(item.Row);
        else _ignored.Ignore(item.Row);

        var index = _all.IndexOf(item);
        if (index >= 0) _all[index] = item with { IsHidden = !item.IsHidden };

        Rebuild();
    }

    private void Rebuild()
    {
        var key = SelectedFilter?.Key ?? AllKey;
        Filters = BuildFilters();
        SelectedFilter = Filters.FirstOrDefault(f => f.Key == key) ?? Filters[0];
        Stats = LibraryStats.Compute(_all.Where(g => !g.IsHidden), _backlog, _completed != null ? PilewiseSource : null);
        ApplyFilter();
    }

    private List<LibraryFilterOption> BuildFilters()
    {
        var exported = _all.Where(g => !g.IsHidden).ToList();
        var options = new List<LibraryFilterOption> { new("All platforms", AllKey, exported.Count) };
        foreach (var label in PlatformCatalog.All.Select(d => d.Label).Append(Services.Platforms.GamePass))
        {
            var count = exported.Count(g => g.Platforms.Contains(label));
            if (count > 0) options.Add(new LibraryFilterOption(label, label, count));
        }

        var multi = exported.Count(g => g.IsMultiStore);
        if (multi > 0) options.Add(new LibraryFilterOption("Owned on 2+ stores", MultiStoreKey, multi));

        if (_backlog != null || _completed != null)
        {
            options.Add(new LibraryFilterOption("Completed", CompletedKey, exported.Count(g => g.IsCompleted)));
            options.Add(new LibraryFilterOption("Not completed", NotCompletedKey, exported.Count(g => !g.IsCompleted)));
        }

        var recent = exported.Count(g => g.IsNew);
        if (recent > 0) options.Add(new LibraryFilterOption($"Added in the last {FirstSeenGames.RecentDays} days", RecentKey, recent));

        var never = exported.Count(g => g.IsNeverPlayed);
        if (never > 0) options.Add(new LibraryFilterOption("Never played", NeverPlayedKey, never));

        var hidden = _all.Count - exported.Count;
        if (hidden > 0) options.Add(new LibraryFilterOption("Hidden", HiddenKey, hidden));

        return options;
    }

    private void ApplyFilter()
    {
        var query = SearchText?.Trim();
        var key = SelectedFilter?.Key ?? AllKey;

        var filtered = _all
            .Where(g => key == HiddenKey ? g.IsHidden : !g.IsHidden)
            .Where(g => key switch
            {
                AllKey or HiddenKey => true,
                MultiStoreKey => g.IsMultiStore,
                NeverPlayedKey => g.IsNeverPlayed,
                CompletedKey => g.IsCompleted,
                NotCompletedKey => !g.IsCompleted,
                RecentKey => g.IsNew,
                _ => g.Platforms.Contains(key)
            })
            .Where(g => string.IsNullOrEmpty(query) ||
                        g.Name.Contains(query, StringComparison.CurrentCultureIgnoreCase));

        // _all is already name-ordered and OrderBy is stable, so ties stay alphabetical.
        VisibleGames = (SelectedSort?.Sort switch
        {
            LibrarySort.MostPlayed => filtered.OrderByDescending(g => g.PlaytimeMinutes ?? -1),
            LibrarySort.RecentlyPlayed => filtered.OrderByDescending(g => g.LastPlayed ?? DateTimeOffset.MinValue),
            LibrarySort.RecentlyAdded => filtered.OrderByDescending(g => g.AddedOn ?? DateOnly.MinValue),
            _ => filtered
        }).ToList();
        OnPropertyChanged(nameof(Summary));
        OnPropertyChanged(nameof(HasVisibleGames));
    }
}

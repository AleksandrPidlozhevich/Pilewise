using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Pilewise.Services;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Pilewise.ViewModels;

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
            CsvFormat.CreateRow("Steam/GOG", "The Witcher 3", "Steam:292030; GOG:1207664643"),
            CsvFormat.CreateRow("Epic", "Alan Wake", "Epic:ns/cat"),
            CsvFormat.CreateRow("Steam", "Portal", "Steam:400")
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


    private List<LibraryFilterOption> BuildFilters() => LibraryQuery.BuildFilters(_all, _backlog != null || _completed != null);

    private void ApplyFilter()
    {
        VisibleGames = LibraryQuery.Apply(_all, SelectedFilter?.Key ?? AllKey, SearchText?.Trim(),
            SelectedSort?.Sort ?? LibrarySort.Name);
        OnPropertyChanged(nameof(Summary));
        OnPropertyChanged(nameof(HasVisibleGames));
    }
}

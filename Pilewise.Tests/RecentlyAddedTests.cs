using System;
using System.Collections.Generic;
using System.Linq;
using Pilewise.Services;
using Pilewise.ViewModels;
using Xunit;
using Pilewise.Tests.Fakes;

namespace Pilewise.Tests;

public class RecentlyAddedTests
{
    private static readonly DateOnly Day1 = new(2026, 9, 1);
    private static readonly DateOnly Day2 = new(2026, 10, 10);

    private static CsvGameExportRow Steam(string name, string id)
    {
        return CsvFormat.CreateRow("Steam", name, "Steam:" + id);
    }

    private static HashSet<string> Stores(params string[] stores)
    {
        return new HashSet<string>(stores, StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public void FirstFetch_IsTheBaseline_SoNothingIsNew()
    {
        var seen = new FirstSeenGames(new MemoryStateStore());
        seen.Record([Steam("Portal", "400")], Stores("Steam"), Day1);

        Assert.Null(seen.AddedOn(Steam("Portal", "400")));
    }

    [Fact]
    public void GameAppearingLater_GetsTheDayItWasFirstSeen()
    {
        var seen = new FirstSeenGames(new MemoryStateStore());
        seen.Record([Steam("Portal", "400")], Stores("Steam"), Day1);
        seen.Record([Steam("Portal", "400"), Steam("Hades", "1145360")], Stores("Steam"), Day2);
        seen.Record([Steam("Hades", "1145360")], Stores("Steam"), Day2.AddDays(5)); // seen again later

        Assert.Equal(Day2, seen.AddedOn(Steam("Hades", "1145360")));
    }

    [Fact]
    public void NewlyConnectedStore_StartsItsOwnBaseline()
    {
        var seen = new FirstSeenGames(new MemoryStateStore());
        seen.Record([Steam("Portal", "400")], Stores("Steam"), Day1);

        // GOG connected for the first time: its whole library was already owned.
        var gwent = CsvFormat.CreateRow("GOG", "Gwent", "GOG:9");
        seen.Record([Steam("Portal", "400"), gwent], Stores("Steam", "GOG"), Day2);

        Assert.Null(seen.AddedOn(gwent));
    }

    [Fact]
    public void GameAlsoBoughtOnAnotherStore_KeepsItsOriginalDate()
    {
        var seen = new FirstSeenGames(new MemoryStateStore());
        seen.Record([], Stores("Steam", "GOG"), Day1);
        seen.Record([Steam("Hades", "1145360")], Stores("Steam", "GOG"), Day2);

        var both = CsvFormat.CreateRow("Steam/GOG", "Hades", "Steam:1145360; GOG:5");
        seen.Record([both], Stores("Steam", "GOG"), Day2.AddDays(20));

        Assert.Equal(Day2, seen.AddedOn(both));
    }

    [Fact]
    public void State_SurvivesAReload()
    {
        var state = new MemoryStateStore();
        new FirstSeenGames(state).Record([], Stores("Steam"), Day1);
        new FirstSeenGames(state).Record([Steam("Hades", "1145360")], Stores("Steam"), Day2);

        Assert.Equal(Day2, new FirstSeenGames(state).AddedOn(Steam("Hades", "1145360")));
    }

    [Theory]
    [InlineData(0, true)]
    [InlineData(30, true)]
    [InlineData(31, false)]
    public void IsRecent_MeansWithin30Days(int daysAgo, bool expected)
    {
        Assert.Equal(expected, FirstSeenGames.IsRecent(Day2.AddDays(-daysAgo), Day2));
    }

    [Fact]
    public void Library_ShowsNewGames_FilterSortAndCount()
    {
        var today = DateOnly.FromDateTime(DateTime.Today);
        var dates = new Dictionary<string, DateOnly?> { ["Hades"] = today.AddDays(-3), ["Doom"] = today.AddDays(-100) };
        var vm = new LibraryViewModel(
            [Steam("Portal", "400"), Steam("Hades", "1145360"), Steam("Doom", "2280")],
            addedOn: r => dates.GetValueOrDefault(r.Name));

        Assert.Equal(1, vm.Stats.RecentlyAdded);
        vm.SelectedFilter = vm.Filters.Single(f => f.Key == LibraryViewModel.RecentKey);
        Assert.Equal(["Hades"], vm.VisibleGames.Select(g => g.Name));

        vm.SelectedFilter = vm.Filters[0];
        vm.SelectedSort = vm.Sorts.Single(s => s.Sort == LibrarySort.RecentlyAdded);
        Assert.Equal(["Hades", "Doom", "Portal"], vm.VisibleGames.Select(g => g.Name));
    }

    [Theory]
    [InlineData("1.1.0", "1.2.0", "✓ Updated to v1.2.0")]
    [InlineData("1.2.0-beta.1", "1.2.0", null)] // same release
    [InlineData("1.2.0", "1.2.0", null)]
    [InlineData("1.3.0", "1.2.0", null)] // downgrade
    [InlineData(null, "1.2.0", null)] // first install
    [InlineData("garbage", "1.2.0", null)]
    public void UpdateNotice_OnlyAfterAnUpgrade(string? lastRun, string current, string? expected)
    {
        Assert.Equal(expected, UpdateNotice.Message(lastRun, current));
    }

    [Fact]
    public void UpdateNotice_LinksTheReleaseNotes()
    {
        Assert.Equal("https://github.com/AleksandrPidlozhevich/Pilewise/releases/tag/v1.2.0",
            UpdateNotice.ReleaseUrl("1.2.0"));
    }
}
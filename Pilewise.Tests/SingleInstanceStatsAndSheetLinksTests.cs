using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Pilewise.Services;
using Pilewise.ViewModels;
using Xunit;

namespace Pilewise.Tests;

public class SingleInstanceTests
{
    // The mutex is thread-owned, so a separate thread stands in for another process.
    private static T OnOtherThread<T>(Func<T> action)
    {
        T result = default!;
        var thread = new Thread(() => result = action());
        thread.Start();
        thread.Join();
        return result;
    }

    private static string UniqueName()
    {
        return SingleInstance.NameFor("test" + Guid.NewGuid().ToString("N")[..12]);
    }

    [Fact]
    public void NameFor_KeepsOnlySafeCharacters()
    {
        Assert.Equal("Pilewise-alex_k-1", SingleInstance.NameFor("alex_k-1"));
        Assert.Equal("Pilewise-alexk1", SingleInstance.NameFor("alex.k 1"));
        Assert.Equal("Pilewise-user", SingleInstance.NameFor("Олег"));
        Assert.Equal("Pilewise-ab", SingleInstance.NameFor("a/b"));
    }

    [Fact]
    public void SecondInstance_IsRefused_AndCanAskTheFirstToShow()
    {
        var name = UniqueName();
        using var first = SingleInstance.TryAcquire(name);
        Assert.NotNull(first);

        using var shown = new ManualResetEventSlim();
        first.ShowRequested += shown.Set;

        var second = OnOtherThread(() => SingleInstance.TryAcquire(name));
        Assert.Null(second);

        Assert.True(SingleInstance.SignalExisting(name, TimeSpan.FromSeconds(5)));
        Assert.True(shown.Wait(TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public void AfterTheFirstCloses_ANewOneCanStart()
    {
        var name = UniqueName();
        SingleInstance.TryAcquire(name)!.Dispose();

        var next = OnOtherThread(() =>
        {
            using var instance = SingleInstance.TryAcquire(name);
            return instance != null;
        });

        Assert.True(next);
    }

    [Fact]
    public void SignalExisting_NothingRunning_ReturnsFalse()
    {
        Assert.False(SingleInstance.SignalExisting(UniqueName(), TimeSpan.FromMilliseconds(200)));
    }

    [Theory]
    [InlineData("(true,)\n", true)]
    [InlineData("(false,)", false)]
    [InlineData("method return time=1.2 sender=org.freedesktop.DBus\n   boolean true\n", true)]
    [InlineData("method return\n   boolean false", false)]
    [InlineData("", null)]
    [InlineData(null, null)]
    [InlineData("Error: no session bus", null)]
    public void TraySupport_ReadsNameHasOwnerReply(string? output, bool? expected)
    {
        Assert.Equal(expected, TraySupport.ParseNameHasOwner(output));
    }
}

public class LibraryStatsTests
{
    private static LibraryGameItem Game(string name, int? minutes, bool lastPlayed = false, string platform = "Steam")
    {
        return new LibraryGameItem(name, platform.Split('/'), "", minutes,
            lastPlayed ? DateTimeOffset.UnixEpoch : null);
    }

    [Fact]
    public void Compute_CountsPlayedShareOutOfGamesWithPlaytimeData()
    {
        var stats = LibraryStats.Compute([
            Game("Portal", 750),
            Game("Unplayed", 0),
            Game("Halo", null, true, "Xbox"),
            Game("Gwent", null, platform: "GOG"),
            Game("Witcher 3", 6000, platform: "Steam/GOG")
        ]);

        Assert.Equal(5, stats.Games);
        Assert.Equal(4, stats.Tracked);
        Assert.Equal(3, stats.Played);
        Assert.Equal(1, stats.NeverPlayed);
        Assert.Equal(1, stats.MultiStore);
        Assert.Equal(75, stats.PlayedPercent);
        Assert.Equal("Played 3 of 4 (75%)", stats.PlayedText);
        Assert.Equal("Most played: Witcher 3 (100 h)", stats.MostPlayedText);
        Assert.Equal(6750, stats.TotalMinutes);
    }

    [Fact]
    public void Compute_NoPlaytimeData_HidesTheShare()
    {
        var stats = LibraryStats.Compute([Game("Gwent", null, platform: "GOG")]);

        Assert.False(stats.HasPlaytimeData);
        Assert.Equal(0, stats.PlayedPercent);
        Assert.Equal("", stats.MostPlayedText);
    }

    [Fact]
    public void ViewModel_StatsLeaveOutHiddenGames()
    {
        var hidden = CsvFormat.CreateRow("Steam", "Test Server", "Steam:1");
        var vm = new LibraryViewModel([CsvFormat.CreateRow("Steam", "Portal", "Steam:400")], [hidden]);

        Assert.Equal(1, vm.Stats.Games);
    }
}

public class SheetLinksAndCoversTests
{
    private static IList<IList<object>> Sheet(params object[][] rows)
    {
        return rows.Select(r => (IList<object>)r.ToList()).ToList();
    }

    private static CsvGameExportRow Steam(string name, string id)
    {
        return CsvFormat.CreateRow("Steam", name, "Steam:" + id);
    }

    [Fact]
    public void LinkAndCoverColumns_AreFoundByHeader_AndFilledWhenEmpty()
    {
        var sheet = Sheet(
            ["Platform", "Name", "ID", "Completed", "Store link", "Cover"],
            ["Steam", "Portal", "Steam:400", false]);

        var plan = GoogleSheetsSyncPlanner.Plan(sheet, [Steam("Portal", "400")]);

        Assert.Equal(4, plan.Columns.StoreLink);
        Assert.Equal(5, plan.Columns.Cover);
        var update = Assert.Single(plan.Updates);
        Assert.Equal("https://store.steampowered.com/app/400/", update.StoreLink);
        Assert.Equal("https://cdn.akamai.steamstatic.com/steam/apps/400/header.jpg", update.CoverUrl);
    }

    [Fact]
    public void FilledCells_AreLeftAlone()
    {
        var sheet = Sheet(
            ["Platform", "Name", "ID", "Completed", "Store link", "Cover"],
            ["Steam", "Portal", "Steam:400", false, "https://my.link", "=IMAGE(\"https://x\")"]);

        var plan = GoogleSheetsSyncPlanner.Plan(sheet, [Steam("Portal", "400")]);

        Assert.Empty(plan.Updates);
    }

    [Fact]
    public void WithoutTheHeaders_NothingIsAdded()
    {
        var sheet = Sheet(
            ["Platform", "Name", "ID", "Completed"],
            ["Steam", "Portal", "Steam:400", false]);

        var plan = GoogleSheetsSyncPlanner.Plan(sheet, [Steam("Portal", "400")]);

        Assert.Empty(plan.Updates);
        Assert.Equal(-1, plan.Columns.StoreLink);
        Assert.Equal(-1, plan.Columns.Cover);
    }

    [Fact]
    public void HiddenGames_GetNoLinks()
    {
        var sheet = Sheet(
            ["Platform", "Name", "ID", "Completed", "Store link"],
            ["Steam", "Test Server", "Steam:1", false]);

        var plan = GoogleSheetsSyncPlanner.Plan(sheet, [], ignored: [Steam("Test Server", "1")]);

        Assert.Empty(plan.Updates);
    }

    [Fact]
    public void CoverFormula_DoublesQuotes()
    {
        Assert.Equal("=IMAGE(\"https://a/b\"\"c.jpg\")", GoogleSheetsSyncPlanner.CoverFormula("https://a/b\"c.jpg"));
    }
}
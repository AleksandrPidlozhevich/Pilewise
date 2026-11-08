using System;
using System.Collections.Generic;
using System.Linq;
using Pilewise.Models;
using Pilewise.Services;
using Pilewise.ViewModels;
using Xunit;

namespace Pilewise.Tests;

public class PlaytimeTests
{
    private static readonly DateTimeOffset May1 = new(2024, 5, 1, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void BuildFromLibraries_MapsSteamPlaytime_AndTreatsZeroLastPlayedAsNever()
    {
        var rows = CsvGameExportService.BuildFromLibraries(
        [
            new SteamGame { Name = "Portal", GameID = 400, PlaytimeMinutes = 750, LastPlayedUnix = May1.ToUnixTimeSeconds() },
            new SteamGame { Name = "Unplayed", GameID = 1, PlaytimeMinutes = 0, LastPlayedUnix = 0 }
        ], epicGames: null);

        var portal = rows.Single(r => r.Name == "Portal");
        Assert.Equal(12.5, portal.PlaytimeHours);
        Assert.Equal("2024-05-01", portal.LastPlayedDate);
        Assert.Null(rows.Single(r => r.Name == "Unplayed").LastPlayed);
    }

    [Fact]
    public void Merge_KeepsLargestPlaytime_AndLatestLastPlayed()
    {
        var steam = new CsvGameExportRow { Name = "Halo", HasSteam = true, PlaytimeMinutes = 60, LastPlayed = May1 };
        var xbox = new CsvGameExportRow { Name = "Halo", HasXbox = true, LastPlayed = May1.AddDays(3) };

        steam.MergeFrom(xbox);

        Assert.Equal(60, steam.PlaytimeMinutes);
        Assert.Equal(May1.AddDays(3), steam.LastPlayed);
    }

    private static LibraryViewModel Library() => new([
        new CsvGameExportRow { Name = "Alpha", HasSteam = true, SteamGameId = 1, PlaytimeMinutes = 0 },
        new CsvGameExportRow { Name = "Beta", HasSteam = true, SteamGameId = 2, PlaytimeMinutes = 600, LastPlayed = May1 },
        new CsvGameExportRow { Name = "Gamma", HasSteam = true, SteamGameId = 3, PlaytimeMinutes = 30, LastPlayed = May1.AddDays(9) },
        new CsvGameExportRow { Name = "Delta", HasGog = true, GogId = 4 } // GOG reports no playtime
    ]);

    [Fact]
    public void Library_NeverPlayedFilter_OnlyKnownZeroPlaytime()
    {
        var vm = Library();

        vm.SelectedFilter = vm.Filters.Single(f => f.Key == LibraryViewModel.NeverPlayedKey);

        Assert.Equal("Alpha", Assert.Single(vm.VisibleGames).Name);
    }

    [Fact]
    public void Library_Sorts_ByPlaytimeAndRecency()
    {
        var vm = Library();

        vm.SelectedSort = vm.Sorts.Single(s => s.Sort == LibrarySort.MostPlayed);
        Assert.Equal(["Beta", "Gamma", "Alpha", "Delta"], vm.VisibleGames.Select(g => g.Name));

        vm.SelectedSort = vm.Sorts.Single(s => s.Sort == LibrarySort.RecentlyPlayed);
        Assert.Equal(["Gamma", "Beta", "Alpha", "Delta"], vm.VisibleGames.Select(g => g.Name));
    }

    [Theory]
    [InlineData(null, "—")]
    [InlineData(0, "0 h")]
    [InlineData(3, "< 0.1 h")]
    [InlineData(90, "1.5 h")]
    public void Library_PlaytimeText(int? minutes, string expected)
    {
        var item = new LibraryGameItem("x", [], "", minutes, null);
        Assert.Equal(expected, item.PlaytimeText.Replace(',', '.')); // decimal separator follows the UI culture
    }

    [Fact]
    public void NotionSchema_DetectsOptionalColumns_AndRejectsWrongType()
    {
        var ok = NotionSchemaCheck.Check(new Dictionary<string, string>
        {
            ["Name"] = "title", ["Platform's"] = "multi_select", ["GameID"] = "rich_text",
            ["Playtime"] = "number", ["Last played"] = "date"
        });
        Assert.Equal(new NotionOptionalColumns(true, true, Completed: "Completed"), ok.Optional);

        var bad = NotionSchemaCheck.Check(new Dictionary<string, string>
        {
            ["Name"] = "title", ["Platform's"] = "multi_select", ["GameID"] = "rich_text",
            ["Playtime"] = "rich_text"
        });
        Assert.False(bad.IsUsable);
        Assert.False(bad.Optional.Playtime);
    }

    [Fact]
    public void Sheets_PlaytimeColumn_UpdatedWhenChanged_AndFilledForNewRows()
    {
        IList<IList<object>> sheet =
        [
            new List<object> { "Platform", "Name", "ID", "Completed", "Playtime", "Last played" },
            new List<object> { "Steam", "Portal", "Steam:400", false, 2.0, "2024-01-01" },
            new List<object> { "GOG", "Gwent", "GOG:9", false }
        ];
        var portal = new CsvGameExportRow
            { Name = "Portal", HasSteam = true, SteamGameId = 400, PlaytimeMinutes = 750, LastPlayed = May1 };
        var gwent = CsvFormat.CreateRow("GOG", "Gwent", "GOG:9"); // no playtime from GOG
        var halfLife = new CsvGameExportRow { Name = "Half-Life", HasSteam = true, SteamGameId = 70, PlaytimeMinutes = 60 };

        var plan = GoogleSheetsSyncPlanner.Plan(sheet, [portal, gwent, halfLife]);

        Assert.Equal(4, plan.Columns.Playtime);
        Assert.Equal(5, plan.Columns.LastPlayed);
        var update = Assert.Single(plan.Updates); // Gwent untouched: nothing fresh to write
        Assert.Equal(new SheetRowUpdate(1, "Steam", "Steam:400", 12.5, "2024-05-01"), update);
        Assert.Equal(1.0, Assert.Single(plan.NewRows).PlaytimeHours);
    }

    [Fact]
    public void Sheets_WithoutPlaytimeColumn_PlaytimeChangeIsNoUpdate()
    {
        IList<IList<object>> sheet =
        [
            new List<object> { "Platform", "Name", "ID", "Completed" },
            new List<object> { "Steam", "Portal", "Steam:400", false }
        ];
        var portal = new CsvGameExportRow { Name = "Portal", HasSteam = true, SteamGameId = 400, PlaytimeMinutes = 750 };

        var plan = GoogleSheetsSyncPlanner.Plan(sheet, [portal]);

        Assert.Empty(plan.Updates);
        Assert.Equal(-1, plan.Columns.Playtime);
    }
}

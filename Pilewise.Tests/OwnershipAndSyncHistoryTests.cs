using System;
using System.Collections.Generic;
using System.Linq;
using Pilewise.Services;
using Xunit;
using Pilewise.Tests.Fakes;

namespace Pilewise.Tests;

public class OwnershipAndSyncHistoryTests
{
    private static readonly HashSet<string> SteamAndGog = [Platforms.Steam, Platforms.Gog];

    [Fact]
    public void Ownership_Matched_IsOwned()
    {
        var row = CsvFormat.CreateRow("Steam", "Portal", "Steam:400");
        Assert.True(CsvGameExportService.Ownership(row, true, SteamAndGog));
    }

    [Fact]
    public void Ownership_MissingFromFetchedStores_IsNotOwned()
    {
        var row = CsvFormat.CreateRow("Steam/GOG", "Witcher", "Steam:1; GOG:2");
        Assert.False(CsvGameExportService.Ownership(row, false, SteamAndGog));
    }

    [Fact]
    public void Ownership_AnyStoreNotFetched_IsUnknown()
    {
        // Epic session expired → Epic wasn't fetched, so a missing Epic game proves nothing.
        var row = CsvFormat.CreateRow("Steam/Epic", "Alan Wake", "Steam:1; Epic:ns/cat");
        Assert.Null(CsvGameExportService.Ownership(row, false, SteamAndGog));
    }

    [Fact]
    public void Ownership_RowWithoutStore_IsUnknown()
    {
        var row = new CsvGameExportRow { Name = "Hand-typed row" };
        Assert.Null(CsvGameExportService.Ownership(row, false, SteamAndGog));
    }

    [Fact]
    public void Sheets_OwnedColumn_UnticksGoneGames_TicksOwned_SkipsUnknown()
    {
        IList<IList<object>> sheet =
        [
            new List<object> { "Platform", "Name", "ID", "Completed", "Owned" },
            new List<object> { "Steam", "Portal", "Steam:400", false, false }, // owned again → tick
            new List<object> { "Steam", "Refunded", "Steam:9", false, true }, // gone → untick
            new List<object> { "Epic", "Alan Wake", "Epic:ns/cat", false, true } // Epic not fetched → keep
        ];

        var plan = GoogleSheetsSyncPlanner.Plan(sheet,
            [CsvFormat.CreateRow("Steam", "Portal", "Steam:400")], SteamAndGog);

        Assert.Equal(
            [(1, (bool?)true), (2, false)],
            plan.Updates.Select(u => (u.RowIndex, u.Owned)));
        Assert.Equal(["Refunded"], plan.NoLongerOwned);
    }

    [Fact]
    public void Sheets_WithoutOwnedColumn_StillReportsGoneGames_ButWritesNothing()
    {
        IList<IList<object>> sheet =
        [
            new List<object> { "Platform", "Name", "ID", "Completed" },
            new List<object> { "Steam", "Refunded", "Steam:9", false }
        ];

        var plan = GoogleSheetsSyncPlanner.Plan(sheet, [], new HashSet<string> { Platforms.Steam });

        Assert.Empty(plan.Updates);
        Assert.Equal(["Refunded"], plan.NoLongerOwned);
    }

    private static readonly DateTimeOffset Now = new(2026, 10, 17, 15, 0, 0, TimeSpan.FromHours(3));

    [Fact]
    public void Describe_TodayYesterdayAndOlder()
    {
        Assert.Null(SyncHistory.Describe(null, Now));
        Assert.Equal("Last sync: today 14:20", SyncHistory.Describe(Now.AddMinutes(-40), Now));
        Assert.Equal("Last sync: yesterday 09:05",
            SyncHistory.Describe(new DateTimeOffset(2026, 10, 16, 9, 5, 0, Now.Offset), Now));
        Assert.StartsWith("Last sync: 3 ",
            SyncHistory.Describe(new DateTimeOffset(2026, 10, 3, 18, 40, 0, Now.Offset), Now));
    }

    [Fact]
    public void Describe_ConvertsToTheCurrentOffset()
    {
        // 23:30 UTC on the 16th is 02:30 on the 17th at UTC+3 → "today".
        var when = new DateTimeOffset(2026, 10, 16, 23, 30, 0, TimeSpan.Zero);
        Assert.Equal("Last sync: today 02:30", SyncHistory.Describe(when, Now));
    }

    [Fact]
    public void Record_PersistsAcrossInstances()
    {
        var store = new MemoryStateStore();
        new SyncHistory(store).Record(SyncHistory.Notion, Now);

        var reloaded = new SyncHistory(store);

        Assert.Equal(Now, reloaded.Get(SyncHistory.Notion));
        Assert.Null(reloaded.Get(SyncHistory.Csv));
    }
}
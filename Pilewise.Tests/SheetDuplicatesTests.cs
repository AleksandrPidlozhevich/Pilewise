using System.Collections.Generic;
using System.Linq;
using Pilewise.Services;
using Pilewise.ViewModels;
using Xunit;

namespace Pilewise.Tests;

public class SheetDuplicatesTests
{
    private static IList<IList<object>> Sheet(params object[][] rows)
    {
        return rows.Select(r => (IList<object>)r.ToList()).ToList();
    }

    private static readonly object[] Header = ["Platform", "Name", "ID", "Completed"];

    [Fact]
    public void Read_ReturnsTheGameRows_WithTheirCompletedTick()
    {
        var contents = GoogleSheetsSyncPlanner.Read(Sheet(
            Header,
            ["Steam", "Portal", "Steam:400", true],
            ["Steam", "Doom", "Steam:2280"]));

        Assert.False(contents.NeedsUpgrade);
        Assert.Equal([(1, "Portal", (bool?)true), (2, "Doom", false)],
            contents.Rows.Select(r => (r.RowIndex, r.Row.Name, r.Completed)));
    }

    [Fact]
    public void Read_OldLayout_NeedsAnUpgradeFirst()
    {
        Assert.True(GoogleSheetsSyncPlanner.Read(Sheet(["Name", "Game ID", "Completed"], ["Portal", "400", false]))
            .NeedsUpgrade);
    }

    [Fact]
    public void Find_KeepsTheTopRow_MergesIds_AndCarriesTheCompletedTick()
    {
        var rows = GoogleSheetsSyncPlanner.Read(Sheet(
            Header,
            ["Steam", "Hades", "Steam:1145360", false],
            ["Steam", "Portal", "Steam:400", false],
            ["Epic", "Hades", "Epic:ns/cat", true])).Rows;

        var group = Assert.Single(SheetDuplicateFinder.Find(rows));

        Assert.Equal(1, group.Keep.RowIndex);
        Assert.Equal([3], group.Remove.Select(r => r.RowIndex));
        Assert.Equal("Steam:1145360; Epic:ns/cat", group.Merged.IdText);
        Assert.True(group.CarriesCompleted);
        Assert.Equal("row 2: Steam/Epic · Steam:1145360; Epic:ns/cat · Completed ✓", group.ResultText);
        Assert.Equal("row 4: Hades (Epic · Epic:ns/cat)", group.RemoveText);
    }

    [Fact]
    public void StillMatches_FailsWhenARowMovedSinceTheSearch()
    {
        var before = Sheet(Header, ["Steam", "Hades", "Steam:1145360", false], ["Epic", "Hades", "Epic:ns/cat", false]);
        var groups = SheetDuplicateFinder.Find(GoogleSheetsSyncPlanner.Read(before).Rows);
        Assert.True(SheetDuplicateFinder.StillMatches(GoogleSheetsSyncPlanner.Read(before), groups));

        // A row was inserted above: every game moved down one row.
        var after = Sheet(Header, ["GOG", "Gwent", "GOG:9", false], ["Steam", "Hades", "Steam:1145360", false],
            ["Epic", "Hades", "Epic:ns/cat", false]);
        Assert.False(SheetDuplicateFinder.StillMatches(GoogleSheetsSyncPlanner.Read(after), groups));
    }

    [Fact]
    public void Dialog_Texts_UseSingularAndPlural()
    {
        var rows = GoogleSheetsSyncPlanner.Read(Sheet(
            Header, ["Steam", "Hades", "Steam:1145360", false], ["Epic", "Hades", "Epic:ns/cat", false])).Rows;
        var one = DuplicatesViewModel.ForSheets(SheetDuplicateFinder.Find(rows));

        Assert.Equal("Google Sheets: duplicate rows", one.Title);
        Assert.StartsWith("1 game has more than one row.", one.Summary);
        Assert.Contains("1 other row is deleted", one.Summary);
        Assert.Equal("Merge 1 game", one.ConfirmLabel);
        Assert.Equal("No duplicate pages found: every game has a single page.",
            DuplicatesViewModel.ForNotion([]).Summary);
    }
}
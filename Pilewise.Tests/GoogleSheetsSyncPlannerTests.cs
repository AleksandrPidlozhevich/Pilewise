using System;
using System.Collections.Generic;
using System.Linq;
using Pilewise.Services;
using Xunit;

namespace Pilewise.Tests;

public class GoogleSheetsSyncPlannerTests
{
    [Fact]
    public void Plan_EmptySheet_WritesFullHeaderAndAppendsAfterIt()
    {
        var plan = GoogleSheetsSyncPlanner.Plan(Sheet(), [Steam("Portal", "400")]);

        Assert.False(plan.InsertPlatformColumn);
        Assert.False(plan.InsertHeaderRow);
        Assert.Equal(new SheetColumns(0, 1, 2, 3), plan.Columns);
        Assert.Equal(["Platform", "Name", "ID", "Completed"], plan.HeaderCells.Select(c => c.Text));
        Assert.Equal(1, plan.FirstNewRowIndex);
        var row = Assert.Single(plan.NewRows);
        Assert.Equal("Steam:400", row.IdText);
        Assert.Empty(plan.Updates);
    }

    [Fact]
    public void Plan_NewRows_IncludeEveryStore_SortedByName()
    {
        var incoming = new[]
        {
            Steam("Zork", "1"),
            CsvGameExportService.CreateRow("GOG", "Gwent", "GOG:2"),
            CsvGameExportService.CreateRow("Epic", "Alan Wake", "Epic:ns|cat")
        };

        var plan = GoogleSheetsSyncPlanner.Plan(Sheet(), incoming);

        Assert.Equal(["Alan Wake", "Gwent", "Zork"], plan.NewRows.Select(r => r.Name));
    }

    [Fact]
    public void Plan_CurrentLayout_UpToDate_DoesNothing()
    {
        var sheet = Sheet(
            ["Platform", "Name", "ID", "Completed"],
            ["Steam", "Portal", "Steam:400", true]);

        var plan = GoogleSheetsSyncPlanner.Plan(sheet, [Steam("Portal", "400")]);

        Assert.Empty(plan.HeaderCells);
        Assert.Empty(plan.Updates);
        Assert.Empty(plan.NewRows);
        Assert.Equal(2, plan.TotalRows);
    }

    [Fact]
    public void Plan_IncomingAddsPlatform_UpdatesRowInPlace()
    {
        var sheet = Sheet(
            ["Platform", "Name", "ID", "Completed"],
            ["Steam", "The Witcher 3", "Steam:292030", false]);

        var plan = GoogleSheetsSyncPlanner.Plan(sheet,
            [CsvGameExportService.CreateRow("GOG", "The Witcher 3", "GOG:99")]);

        Assert.Empty(plan.NewRows);
        var update = Assert.Single(plan.Updates);
        Assert.Equal(1, update.RowIndex);
        Assert.Equal("Steam/GOG", update.Platform);
        Assert.Equal("Steam:292030; GOG:99", update.Id);
    }

    [Fact]
    public void Plan_ReorderedColumnsAndExtraColumns_FoundByHeader()
    {
        var sheet = Sheet(
            ["Notes", "Completed", "Name", "ID", "Platform"],
            ["great", true, "Portal", "Steam:400", "Steam"]);

        var plan = GoogleSheetsSyncPlanner.Plan(sheet, [Steam("Half-Life", "70")]);

        Assert.Equal(new SheetColumns(4, 2, 3, 1), plan.Columns);
        Assert.Empty(plan.Updates);
        Assert.Equal(2, plan.FirstNewRowIndex);
    }

    [Fact]
    public void Plan_LegacyHeader_InsertsPlatformColumnAndNormalizesBareIds()
    {
        // Old exporter layout: Name | Game ID | Completed with bare Steam ids.
        var sheet = Sheet(
            ["Name", "Game ID", "Completed"],
            ["Portal", "400", true]);

        var plan = GoogleSheetsSyncPlanner.Plan(sheet, [Steam("Portal", "400"), Steam("Half-Life", "70")]);

        Assert.True(plan.InsertPlatformColumn);
        Assert.False(plan.InsertHeaderRow);
        Assert.Equal(new SheetColumns(0, 1, 2, 3), plan.Columns);
        Assert.Equal(["Platform"], plan.HeaderCells.Select(c => c.Text));
        var update = Assert.Single(plan.Updates);
        Assert.Equal(new SheetRowUpdate(1, "Steam", "Steam:400"), update);
        Assert.Equal("Half-Life", Assert.Single(plan.NewRows).Name);
    }

    [Fact]
    public void Plan_LegacyHeaderlessSheet_InsertsHeaderRowAndColumn_NoDuplicateOfFirstGame()
    {
        // The old exporter never wrote its header, so the first game sits in row 0.
        var sheet = Sheet(
            ["Portal", "400", false],
            ["Half-Life", "70", true]);

        var plan = GoogleSheetsSyncPlanner.Plan(sheet, [Steam("Portal", "400"), Steam("Half-Life", "70")]);

        Assert.True(plan.InsertPlatformColumn);
        Assert.True(plan.InsertHeaderRow);
        Assert.Equal(4, plan.HeaderCells.Count);
        Assert.Empty(plan.NewRows);
        Assert.Equal([1, 2], plan.Updates.Select(u => u.RowIndex));
        Assert.Equal(3, plan.TotalRows);
    }

    [Fact]
    public void Plan_MissingCompletedColumn_AddedAfterLastHeaderCell()
    {
        var sheet = Sheet(
            ["Platform", "Name", "ID", "Notes"],
            ["Steam", "Portal", "Steam:400", "x"]);

        var plan = GoogleSheetsSyncPlanner.Plan(sheet, []);

        Assert.Equal(4, plan.Columns.Completed);
        Assert.Equal(new SheetHeaderCell(4, "Completed"), Assert.Single(plan.HeaderCells));
    }

    [Fact]
    public void Plan_IncludeLinux_AddsTheColumnAndFillsEveryRow()
    {
        var sheet = Sheet(
            ["Platform", "Name", "ID", "Completed"],
            ["Steam", "Portal", "Steam:400", true],
            ["Xbox", "Halo", "Xbox:123", false]);

        var plan = GoogleSheetsSyncPlanner.Plan(sheet,
            [Steam("Portal", "400"), CsvGameExportService.CreateRow("Xbox", "Halo", "Xbox:123")],
            includeLinux: true, linuxByAppId: new Dictionary<int, string> { [400] = "Native" });

        Assert.Equal(4, plan.Columns.Linux);
        Assert.Contains(plan.HeaderCells, c => c.Column == 4 && c.Text == "Linux");
        Assert.Contains(plan.Updates, u => u.RowIndex == 1 && u.Linux == "Native");
        Assert.Contains(plan.Updates, u => u.RowIndex == 2 && u.Linux == "Unknown");
    }

    [Fact]
    public void Plan_IncludeLinux_AlreadyFilled_DoesNotRewrite()
    {
        var sheet = Sheet(
            ["Platform", "Name", "ID", "Completed", "Linux"],
            ["Steam", "Portal", "Steam:400", true, "Gold"]);

        var plan = GoogleSheetsSyncPlanner.Plan(sheet, [Steam("Portal", "400")],
            includeLinux: true, linuxByAppId: new Dictionary<int, string> { [400] = "Gold" });

        Assert.Empty(plan.HeaderCells);
        Assert.Empty(plan.Updates);
        Assert.Equal(4, plan.Columns.Linux);
    }

    [Fact]
    public void Plan_UnrelatedSheet_Throws()
    {
        var sheet = Sheet(["Budget", "Amount"], ["Rent", "abc"]);

        Assert.Throws<InvalidOperationException>(() => GoogleSheetsSyncPlanner.Plan(sheet, [Steam("Portal", "400")]));
    }

    private static CsvGameExportRow Steam(string name, string id) =>
        CsvGameExportService.CreateRow("Steam", name, $"Steam:{id}");

    private static IList<IList<object>> Sheet(params object[][] rows) =>
        rows.Select(r => (IList<object>)r.ToList()).ToList();
}

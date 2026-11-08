using System;
using System.Collections.Generic;
using System.Linq;
using Pilewise.Models;
using Pilewise.Services;
using Xunit;

namespace Pilewise.Tests;

public class NotionDuplicateFinderTests
{
    private static int _order;

    private static NotionGameRow Page(string id, string platform, string name, string gameId, int ageDays = 0) =>
        new(CsvFormat.CreateRow(platform, name, gameId), id,
            new HashSet<string>(platform.Split('/'), StringComparer.OrdinalIgnoreCase), gameId,
            CreatedTime: new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero).AddDays(-ageDays).AddSeconds(_order++));

    [Fact]
    public void SameStoreId_IsADuplicate_OldestPageKept_AndMergedIdsGoToIt()
    {
        var groups = NotionDuplicateFinder.Find([
            Page("new", "Steam", "The Witcher 3: Wild Hunt", "Steam:292030", ageDays: 1),
            Page("old", "Steam", "Witcher 3", "Steam:292030", ageDays: 30),
            Page("gog", "GOG", "Witcher 3", "GOG:1207664643", ageDays: 2)
        ]);

        var group = Assert.Single(groups);
        Assert.Equal("old", group.Keep.PageId);
        Assert.Equal(["gog", "new"], group.Remove.Select(r => r.PageId).Order());
        Assert.Equal("Steam:292030; GOG:1207664643", group.Merged.IdText);
        Assert.Equal("Witcher 3", group.Name); // the kept page keeps its title
    }

    [Fact]
    public void SameName_DifferentIdsForTheSameStore_AreDifferentGames()
    {
        var groups = NotionDuplicateFinder.Find([
            Page("a", "Steam", "DOOM", "Steam:379720"),
            Page("b", "Steam", "Doom", "Steam:2280")
        ]);

        Assert.Empty(groups);
    }

    [Fact]
    public void SameName_WithoutIds_IsADuplicate()
    {
        var groups = NotionDuplicateFinder.Find([
            Page("a", "", "Hollow Knight™", ""),
            Page("b", "Steam", "Hollow Knight", "Steam:367520")
        ]);

        var group = Assert.Single(groups);
        Assert.Equal("Steam:367520", group.Merged.IdText);
    }

    [Fact]
    public void UniquePages_NoGroups() =>
        Assert.Empty(NotionDuplicateFinder.Find([
            Page("a", "Steam", "Portal", "Steam:400"),
            Page("b", "Steam", "Portal 2", "Steam:620")
        ]));

    [Fact]
    public void Group_DescribesTheResult()
    {
        var group = Assert.Single(NotionDuplicateFinder.Find([
            Page("a", "Steam", "Hades", "Steam:1145360", ageDays: 5),
            Page("b", "Epic", "Hades", "Epic:ns/cat")
        ]));

        Assert.Equal("Steam/Epic · Steam:1145360; Epic:ns/cat", group.ResultText);
        Assert.Equal("Hades (Epic · Epic:ns/cat)", group.RemoveText);
    }
}

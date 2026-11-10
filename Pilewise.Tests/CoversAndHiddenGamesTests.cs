using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;
using Pilewise.Models;
using Pilewise.Services;
using Pilewise.ViewModels;
using Xunit;
using Pilewise.Tests.Fakes;

namespace Pilewise.Tests;

public class CoversAndHiddenGamesTests
{
    [Fact]
    public void SteamRow_LinksComeFromTheAppId()
    {
        var row = CsvFormat.CreateRow("Steam", "Portal", "Steam:400");
        Assert.Equal("https://cdn.akamai.steamstatic.com/steam/apps/400/header.jpg", row.CoverUrl);
        Assert.Equal("https://store.steampowered.com/app/400/", row.StoreUrl);
    }

    [Theory]
    [InlineData("//images-4.gog-statics.com/abc", "https://images-4.gog-statics.com/abc.jpg")]
    [InlineData("https://images.gog-statics.com/abc.png", "https://images.gog-statics.com/abc.png")]
    [InlineData("", null)]
    [InlineData(null, null)]
    public void GogCover(string? image, string? expected)
    {
        Assert.Equal(expected, StoreLinks.GogCover(image));
    }

    [Theory]
    [InlineData("/en/game/the_witcher", null, "https://www.gog.com/en/game/the_witcher")]
    [InlineData(null, "the_witcher", "https://www.gog.com/game/the_witcher")]
    [InlineData(null, null, null)]
    public void GogStore(string? path, string? slug, string? expected)
    {
        Assert.Equal(expected, StoreLinks.GogStore(path, slug));
    }

    [Fact]
    public void EpicCatalogItem_PrefersTheWideImage_AndUsesTheProductSlug()
    {
        var item = JObject.Parse("""
                                 {
                                   "keyImages": [
                                     { "type": "DieselGameBoxTall", "url": "https://cdn1.epicgames.com/tall.jpg" },
                                     { "type": "DieselGameBox", "url": "https://cdn1.epicgames.com/wide.jpg" }
                                   ],
                                   "customAttributes": { "com.epicgames.app.productSlug": { "type": "STRING", "value": "alan-wake/home" } }
                                 }
                                 """);

        Assert.Equal("https://cdn1.epicgames.com/wide.jpg", StoreLinks.EpicCover(item));
        Assert.Equal("https://store.epicgames.com/p/alan-wake", StoreLinks.EpicStore(item));
        Assert.Null(StoreLinks.EpicStore(new JObject()));
    }

    [Fact]
    public void FetchedLinks_SurviveTheMerge_SteamWins()
    {
        var rows = CsvGameExportService.BuildFromLibraries(
            [new SteamGame { Name = "Hades", GameID = 1145360 }],
            null,
            [
                new GogGame { Title = "Hades", Id = 1, Image = "//images.gog-statics.com/h", Url = "/game/hades" },
                new GogGame { Title = "Only GOG", Id = 2, Image = "//images.gog-statics.com/o", Url = "/game/only" }
            ]);

        var hades = rows.Single(r => r.Name == "Hades");
        Assert.Contains("steamstatic", hades.CoverUrl);
        var gogOnly = rows.Single(r => r.Name == "Only GOG");
        Assert.Equal("https://images.gog-statics.com/o.jpg", gogOnly.CoverUrl);
        Assert.Equal("https://www.gog.com/game/only", gogOnly.StoreUrl);
    }

    [Fact]
    public void IgnoredGames_MatchByAnyStoreId_AndPersist()
    {
        var state = new MemoryStateStore();
        var ignored = new IgnoredGames(state);
        ignored.Ignore(CsvFormat.CreateRow("Steam/GOG", "Demo", "Steam:9; GOG:5"));

        var reloaded = new IgnoredGames(state);
        Assert.True(reloaded.IsIgnored(CsvFormat.CreateRow("GOG", "Demo (GOG)", "GOG:5")));
        Assert.False(reloaded.IsIgnored(CsvFormat.CreateRow("Steam", "Demo", "Steam:10")));

        reloaded.Unignore(CsvFormat.CreateRow("Steam/GOG", "Demo", "Steam:9; GOG:5"));
        Assert.Equal(0, new IgnoredGames(state).Count);
    }

    [Fact]
    public void IgnoredGames_RowWithoutIds_IsRememberedByName()
    {
        var ignored = new IgnoredGames();
        ignored.Ignore(new CsvGameExportRow { Name = "Test Server™" });
        Assert.True(ignored.IsIgnored(new CsvGameExportRow { Name = "test server" }));
    }

    [Fact]
    public void SheetsPlan_HiddenGame_IsNotAdded_NorGone()
    {
        IList<IList<object>> values =
        [
            new List<object> { "Platform", "Name", "ID", "Completed", "Owned" },
            new List<object> { "Steam", "Demo", "Steam:9", false, true }
        ];

        var plan = GoogleSheetsSyncPlanner.Plan(values, [], new HashSet<string> { "Steam" },
        [
            CsvFormat.CreateRow("Steam", "Demo", "Steam:9"),
            CsvFormat.CreateRow("Steam", "New Demo", "Steam:10")
        ]);

        Assert.Empty(plan.NewRows);
        Assert.Empty(plan.NoLongerOwned);
    }

    [Fact]
    public void LibraryViewer_HideMovesTheGameToHidden_AndSavesIt()
    {
        var ignored = new IgnoredGames(new MemoryStateStore());
        var vm = new LibraryViewModel(
            [
                CsvFormat.CreateRow("Steam", "Portal", "Steam:400"),
                CsvFormat.CreateRow("Steam", "Demo", "Steam:9")
            ],
            [CsvFormat.CreateRow("Steam", "Old Tool", "Steam:7")],
            ignored);

        Assert.Equal(["Demo", "Portal"], vm.VisibleGames.Select(g => g.Name));
        Assert.Equal("2 games · 1 hidden", vm.Summary);
        Assert.Equal("Hidden (1)", vm.Filters.Last().ToString());

        vm.ToggleHiddenCommand.Execute(vm.VisibleGames.Single(g => g.Name == "Demo"));

        Assert.Equal(["Portal"], vm.VisibleGames.Select(g => g.Name));
        Assert.True(ignored.IsIgnored(CsvFormat.CreateRow("Steam", "Demo", "Steam:9")));
        vm.SelectedFilter = vm.Filters.Single(f => f.Key == LibraryViewModel.HiddenKey);
        Assert.Equal(["Demo", "Old Tool"], vm.VisibleGames.Select(g => g.Name));

        vm.ToggleHiddenCommand.Execute(vm.VisibleGames.Single(g => g.Name == "Old Tool"));
        Assert.Equal(["Demo"], vm.VisibleGames.Select(g => g.Name));
        Assert.False(ignored.IsIgnored(CsvFormat.CreateRow("Steam", "Old Tool", "Steam:7")));
    }
}
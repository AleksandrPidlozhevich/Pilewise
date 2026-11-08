using System.Linq;
using Pilewise.Services;
using Pilewise.ViewModels;
using Xunit;

namespace Pilewise.Tests;

public class LibraryViewModelTests
{
    private static LibraryViewModel Create() => new([
        CsvFormat.CreateRow("Steam/GOG", "The Witcher 3", "Steam:292030; GOG:1207664643"),
        CsvFormat.CreateRow("Epic", "Alan Wake", "Epic:ns/cat"),
        CsvFormat.CreateRow("Steam", "Portal", "Steam:400"),
        CsvFormat.CreateRow("Steam", "Portal 2", "Steam:620")
    ]);

    [Fact]
    public void ShowsAllGames_SortedByName()
    {
        var vm = Create();

        Assert.Equal(["Alan Wake", "Portal", "Portal 2", "The Witcher 3"], vm.VisibleGames.Select(g => g.Name));
        Assert.Equal("4 games", vm.Summary);
    }

    [Fact]
    public void Filters_ListOnlyPlatformsPresent_WithCounts()
    {
        var vm = Create();

        Assert.Equal(
            ["All platforms (4)", "Steam (3)", "Epic (1)", "GOG (1)", "Owned on 2+ stores (1)"],
            vm.Filters.Select(f => f.ToString()));
    }

    [Fact]
    public void PlatformFilter_And_Search_Combine()
    {
        var vm = Create();

        vm.SelectedFilter = vm.Filters.Single(f => f.Key == Platforms.Steam);
        vm.SearchText = "portal";

        Assert.Equal(["Portal", "Portal 2"], vm.VisibleGames.Select(g => g.Name));
        Assert.Equal("Showing 2 of 4 games", vm.Summary);
    }

    [Fact]
    public void MultiStoreFilter_ShowsGamesOwnedTwice()
    {
        var vm = Create();

        vm.SelectedFilter = vm.Filters.Single(f => f.Key == LibraryViewModel.MultiStoreKey);

        var game = Assert.Single(vm.VisibleGames);
        Assert.Equal(["Steam", "GOG"], game.Platforms);
    }

    [Fact]
    public void Search_NoMatch_ReportsNothingVisible()
    {
        var vm = Create();

        vm.SearchText = "zelda";

        Assert.False(vm.HasVisibleGames);
    }
}

using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Pilewise.Services;
using Pilewise.ViewModels;
using Xunit;

namespace Pilewise.Tests;

public class PickForMeTests
{
    private static LibraryGameItem Game(string name, int? minutes, bool completed = false, bool hidden = false)
    {
        return new LibraryGameItem(name, ["Steam"], "", minutes, null, hidden, null, completed);
    }

    [Fact]
    public void Pick_SkipsCompletedAndHidden()
    {
        var pick = LibraryViewModel.Pick(
            [Game("Done", 0, true), Game("Hidden", 0, hidden: true), Game("Open", 6000)], new Random(1));

        Assert.Equal("Open", pick?.Name);
    }

    [Fact]
    public void Pick_PrefersBarelyPlayedGames()
    {
        var shown = new[] { Game("Long", 6000), Game("Fresh", 0), Game("Sunk", 3000) };

        for (var seed = 0; seed < 20; seed++)
            Assert.Equal("Fresh", LibraryViewModel.Pick(shown, new Random(seed))?.Name);
    }

    [Fact]
    public void Pick_Again_AvoidsThePreviousSuggestion()
    {
        var a = Game("A", 0);
        var b = Game("B", 0);

        for (var seed = 0; seed < 20; seed++)
            Assert.Equal("B", LibraryViewModel.Pick([a, b], new Random(seed), a)?.Name);
        Assert.Equal("A", LibraryViewModel.Pick([a], new Random(0), a)?.Name);
    }

    [Fact]
    public void Pick_NothingLeft_ReturnsNull()
    {
        Assert.Null(LibraryViewModel.Pick([Game("Done", 0, true)], new Random(0)));
    }

    [Fact]
    public void ViewModel_PickSelectsTheGame_AndRespectsTheSearch()
    {
        var vm = new LibraryViewModel(
            [CsvFormat.CreateRow("Steam", "Portal", "Steam:400"), CsvFormat.CreateRow("Steam", "Doom", "Steam:2280")],
            random: new Random(3)) { SearchText = "doo" };

        vm.PickForMeCommand.Execute(null);

        Assert.Equal("Doom", vm.Picked?.Name);
        Assert.Same(vm.Picked, vm.SelectedGame);
        Assert.Equal("https://store.steampowered.com/app/2280/", vm.Picked?.StoreUrl);
    }

    [Fact]
    public void ViewModel_OpenStorePage_UsesTheLinkOpener()
    {
        string? opened = null;
        var vm = new LibraryViewModel([CsvFormat.CreateRow("Steam", "Portal", "Steam:400")],
            actions: new LibraryActions(url => opened = url));

        vm.OpenStorePageCommand.Execute(vm.VisibleGames[0]);

        Assert.Equal("https://store.steampowered.com/app/400/", opened);
    }
}

public class CsvUndoTests
{
    [Fact]
    public async Task Restore_PutsTheBackupBack_AndRemovesIt()
    {
        var dir = Directory.CreateTempSubdirectory("pilewise-undo");
        try
        {
            var csv = Path.Combine(dir.FullName, "games.csv");
            await File.WriteAllTextAsync(csv, "new content");
            await CsvBackup.WriteAsync(csv, "old content");

            Assert.True(await CsvBackup.RestoreAsync(csv));
            Assert.Equal("old content", await File.ReadAllTextAsync(csv));
            Assert.False(File.Exists(CsvBackup.PathFor(csv)));
            Assert.False(await CsvBackup.RestoreAsync(csv)); // a second undo has nothing to apply
        }
        finally
        {
            dir.Delete(true);
        }
    }
}
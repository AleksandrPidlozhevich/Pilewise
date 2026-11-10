using System;
using System.Linq;
using System.Threading.Tasks;
using Pilewise.Services;
using Pilewise.ViewModels;
using Xunit;

namespace Pilewise.Tests;

public class LibraryActionsTests
{
    private static CsvGameExportRow Steam(string name, string id) => CsvFormat.CreateRow("Steam", name, "Steam:" + id);

    [Fact]
    public async Task RefreshBacklog_ReMarksTheList_AndSaysWhereFrom()
    {
        var fresh = new BacklogSnapshot("Notion", DateTimeOffset.Now, 1, 2, ["Steam:2280"]);
        var vm = new LibraryViewModel(
            [Steam("Portal", "400"), Steam("Doom", "2280")],
            backlog: new BacklogSnapshot("Notion", DateTimeOffset.Now.AddDays(-2), 1, 2, ["Steam:400"]),
            actions: new LibraryActions(RefreshBacklog: _ => Task.FromResult<BacklogSnapshot?>(fresh), BacklogSource: "Notion"));
        Assert.Equal(["Portal"], vm.VisibleGames.Where(g => g.IsCompleted).Select(g => g.Name));

        await vm.RefreshBacklogCommand.ExecuteAsync(null);

        Assert.Equal(["Doom"], vm.VisibleGames.Where(g => g.IsCompleted).Select(g => g.Name));
        Assert.Equal(1, vm.Stats.Completed);
        Assert.StartsWith("✓ marks updated from Notion", vm.Notice);
        Assert.False(vm.IsRefreshing);
    }

    [Fact]
    public async Task RefreshBacklog_Failure_IsShownNotThrown()
    {
        var vm = new LibraryViewModel([Steam("Portal", "400")],
            actions: new LibraryActions(RefreshBacklog: _ => throw new InvalidOperationException("401 Unauthorized"),
                BacklogSource: "Notion"));

        await vm.RefreshBacklogCommand.ExecuteAsync(null);

        Assert.Equal("⚠ Couldn't read Notion: 401 Unauthorized", vm.Notice);
    }

    [Fact]
    public void WithoutARefresh_NoBacklogLineOrButton()
    {
        var vm = new LibraryViewModel([Steam("Portal", "400")]);

        Assert.False(vm.CanRefreshBacklog);
        Assert.False(vm.HasBacklogText);
        Assert.False(vm.CanCopy);
    }

    [Fact]
    public async Task CopyList_CopiesTheShownGames_AsTabSeparatedLines()
    {
        string? copied = null;
        var portal = Steam("Portal", "400");
        portal.PlaytimeMinutes = 750;
        var vm = new LibraryViewModel([portal, Steam("Doom", "2280")],
            actions: new LibraryActions(CopyText: text => { copied = text; return Task.CompletedTask; }))
        {
            SearchText = "port"
        };

        await vm.CopyListCommand.ExecuteAsync(null);

        Assert.Equal("Portal\tSteam\t12.5 h\t—\t—", copied);
        Assert.Equal("Copied 1 games to the clipboard.", vm.Notice);
    }
}

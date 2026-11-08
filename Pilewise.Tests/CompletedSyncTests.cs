using Pilewise.Services;
using Pilewise.ViewModels;
using System.Collections.Generic;
using System.Linq;
using Xunit;

using Pilewise.Tests.Fakes;

namespace Pilewise.Tests;

public class CompletedSyncTests
{
    private static CsvGameExportRow Game(string name, int steamId) =>
        CsvFormat.CreateRow("Steam", name, $"Steam:{steamId}");

    [Fact]
    public void TickInOneTarget_ReachesTheOthers_AndAnUntickToo()
    {
        var completed = new CompletedGames(new MemoryStateStore());
        var portal = Game("Portal", 400);

        // CSV has the tick, Notion doesn't (first sync of both).
        var csv = completed.Begin("CSV:a", [(portal, true)]);
        Assert.True(csv.IsCompleted(portal));
        csv.Commit([(portal, true)]);

        var notion = completed.Begin("Notion:db", [(portal, false)]);
        Assert.True(notion.IsCompleted(portal)); // unchanged in Notion, so the list wins
        notion.Commit([(portal, true)]);

        // Unticked in Notion: a change there, so it clears the list and then the CSV.
        var untick = completed.Begin("Notion:db", [(portal, false)]);
        Assert.False(untick.IsCompleted(portal));
        untick.Commit([(portal, false)]);

        Assert.False(completed.IsCompleted(portal));
        Assert.False(completed.Begin("CSV:a", [(portal, true)]).IsCompleted(portal));
    }

    [Fact]
    public void LibraryTick_GoesToEveryTarget_AndADryRunSavesNothing()
    {
        var store = new MemoryStateStore();
        var completed = new CompletedGames(store);
        var portal = Game("Portal", 400);
        var hades = Game("Hades", 1145360);

        completed.Set(portal, true);
        Assert.True(completed.Begin("Sheets:x", [(portal, false)]).IsCompleted(portal));

        // A tick found in the sheet, never committed (a dry run): the list doesn't change.
        Assert.True(completed.Begin("Sheets:x", [(hades, true)]).IsCompleted(hades));
        Assert.False(new CompletedGames(store).IsCompleted(hades));
    }

    [Fact]
    public void CommitKeepsALibraryTickMadeDuringTheSync()
    {
        var store = new MemoryStateStore();
        var completed = new CompletedGames(store);
        var portal = Game("Portal", 400);
        var hades = Game("Hades", 1145360);

        var sync = completed.Begin("CSV:a", [(portal, true)]);
        new CompletedGames(store).Set(hades, true); // e.g. the app while --sync runs
        sync.Commit([(portal, true)]);

        completed.Reload();
        Assert.True(completed.IsCompleted(portal));
        Assert.True(completed.IsCompleted(hades));
    }

    [Fact]
    public void SeedOnce_StartsFromTheLastSnapshot_OnlyTheFirstTime()
    {
        var completed = new CompletedGames(new MemoryStateStore());
        var portal = Game("Portal", 400);

        completed.SeedOnce("Notion:db", ["Steam:400"]);
        completed.SeedOnce("Notion:db", ["Steam:1"]);

        Assert.True(completed.IsCompleted(portal));
        Assert.False(completed.IsCompleted(Game("Other", 1)));
        // Seeded as synced with Notion: an untick found there is a change, not something to re-tick.
        Assert.False(completed.Begin("Notion:db", [(portal, false)]).IsCompleted(portal));
    }

    [Fact]
    public void MergeToCsv_KeepsYourColumns_FillsOptIns_AndSyncsCompleted()
    {
        const string existing =
            "Platform,Name,ID,Notes,Completed,Playtime,Owned,Store link\n" +
            "Steam,Portal,Steam:400,great,x,,,\n" +
            "Steam,Gone Game,Steam:7,meh,,,,https://mine\n";
        var completed = new CompletedGames(new MemoryStateStore());
        var hades = Game("Hades", 1145360);
        completed.Set(hades, true);
        var portal = Game("Portal", 400);
        portal.PlaytimeMinutes = 90;

        var result = CsvGameExportService.MergeToCsv(existing, [portal, hades], new CsvMergeOptions(
            FetchedStores: new HashSet<string> { Platforms.Steam }, Completed: completed, Target: "CSV:t"));
        var lines = result.Content.Split('\n', System.StringSplitOptions.RemoveEmptyEntries)
            .Select(l => l.TrimEnd('\r')).ToList();

        Assert.Equal("Platform,Name,ID,Notes,Completed,Playtime,Owned,Store link", lines[0]);
        Assert.Contains("Steam,Gone Game,Steam:7,meh,,,no,https://mine", lines); // your link stays
        Assert.Contains("Steam,Hades,Steam:1145360,,yes,,yes,https://store.steampowered.com/app/1145360/", lines);
        Assert.Contains("Steam,Portal,Steam:400,great,yes,1.5,yes,https://store.steampowered.com/app/400/", lines);

        result.Commit();
        completed.Reload();
        Assert.True(completed.IsCompleted(portal)); // the "x" typed in Excel went into the list
    }

    [Fact]
    public void MergeToCsv_UntickedInTheFile_ClearsTheList()
    {
        var completed = new CompletedGames(new MemoryStateStore());
        var portal = Game("Portal", 400);
        var first = CsvGameExportService.MergeToCsv(null, [portal],
            new CsvMergeOptions(Completed: completed, Target: "CSV:t"));
        first.Commit();
        completed.Set(portal, true);
        var ticked = CsvGameExportService.MergeToCsv(first.Content, [portal],
            new CsvMergeOptions(Completed: completed, Target: "CSV:t"));
        Assert.Contains("Steam,Portal,Steam:400,yes", ticked.Content);
        ticked.Commit();

        var edited = ticked.Content.Replace("Steam:400,yes", "Steam:400,");
        var result = CsvGameExportService.MergeToCsv(edited, [portal],
            new CsvMergeOptions(Completed: completed, Target: "CSV:t"));
        result.Commit();

        completed.Reload();
        Assert.False(completed.IsCompleted(portal));
        Assert.False(result.Changed);
    }

    [Fact]
    public void MergeToCsv_HiddenGameKeepsItsOwnTick()
    {
        const string existing = "Platform,Name,ID,Completed\nSteam,Demo,Steam:9,yes\n";
        var completed = new CompletedGames(new MemoryStateStore());

        var result = CsvGameExportService.MergeToCsv(existing, [], new CsvMergeOptions(
            Ignored: [Game("Demo", 9)], Completed: completed, Target: "CSV:t"));

        Assert.Contains("Steam,Demo,Steam:9,yes", result.Content);
        result.Commit();
        Assert.False(completed.IsCompleted(Game("Demo", 9)));
    }

    [Fact]
    public void SheetsPlan_WritesTheListsTicks_ToExistingAndNewRows()
    {
        var values = new List<IList<object>>
        {
            new List<object> { "Platform", "Name", "ID", "Completed" },
            new List<object> { "Steam", "Portal", "Steam:400", false }
        };
        var completed = new CompletedGames(new MemoryStateStore());
        completed.Set(Game("Portal", 400), true);
        completed.Set(Game("Hades", 1145360), true);

        var plan = GoogleSheetsSyncPlanner.Plan(values, [Game("Portal", 400), Game("Hades", 1145360)],
            completed: completed, target: "Sheets:t");

        Assert.True(Assert.Single(plan.Updates).Completed);
        Assert.True(plan.NewRowCompleted(0));
        Assert.Equal(2, plan.Tracked!.Count(t => t.Completed == true));
    }

    [Fact]
    public void Library_ToggleCompleted_SavesAndCounts()
    {
        var completed = new CompletedGames(new MemoryStateStore());
        var vm = new LibraryViewModel([Game("Portal", 400), Game("Hades", 1145360)], completed: completed);

        vm.ToggleCompletedCommand.Execute(vm.VisibleGames.First(g => g.Name == "Portal"));

        Assert.True(completed.IsCompleted(Game("Portal", 400)));
        Assert.Equal(1, vm.Stats.Completed);
        Assert.True(vm.CanMarkCompleted);
        Assert.Contains(vm.Filters, f => f.Key == LibraryViewModel.CompletedKey && f.Count == 1);
    }

}

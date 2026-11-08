using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;
using Pilewise.Services;
using Pilewise.ViewModels;
using Xunit;

using Pilewise.Tests.Fakes;

namespace Pilewise.Tests;

public class BacklogProgressTests
{
    private static CsvGameExportRow Steam(string name, string id) => CsvFormat.CreateRow("Steam", name, "Steam:" + id);

    [Fact]
    public void Notion_CompletedCheckbox_IsRead()
    {
        var properties = JObject.Parse("""{ "Name": { "type": "title" }, "Completed": { "type": "checkbox", "checkbox": true } }""");
        Assert.True(NotionCompletion.Read(properties));
    }

    [Theory]
    [InlineData("status", "Done", true)]
    [InlineData("status", "In progress", false)]
    [InlineData("select", "Beaten", true)]
    [InlineData("select", null, false)]
    public void Notion_StatusProperty_DoneValuesCount(string type, string? value, bool expected)
    {
        var option = value == null ? "null" : $$"""{ "name": "{{value}}" }""";
        var properties = JObject.Parse($$"""{ "Status": { "type": "{{type}}", "{{type}}": {{option}} } }""");
        Assert.Equal(expected, NotionCompletion.Read(properties));
    }

    [Fact]
    public void Notion_NoCompletionProperty_IsUnknown()
    {
        var properties = JObject.Parse("""{ "Name": { "type": "title" }, "Notes": { "type": "rich_text" } }""");
        Assert.Null(NotionCompletion.Read(properties));
    }

    [Fact]
    public void Notion_CheckboxWinsOverStatus()
    {
        var properties = JObject.Parse(
            """{ "Status": { "type": "status", "status": { "name": "Done" } }, "Done": { "type": "checkbox", "checkbox": false } }""");
        Assert.False(NotionCompletion.Read(properties));
    }

    [Fact]
    public void Snapshot_CountsNewRowsInTheTotal_AndKeepsCompletedKeys()
    {
        var snapshot = BacklogSnapshot.From("Notion", DateTimeOffset.Now,
            [(Steam("Portal", "400"), true), (Steam("Doom", "2280"), false), (CsvFormat.CreateRow("", "Gwent", ""), true)],
            newRows: 1);

        Assert.NotNull(snapshot);
        Assert.Equal(2, snapshot.Completed);
        Assert.Equal(4, snapshot.Total);
        Assert.Equal("Completed 2 of 4 (50%)", snapshot.Text);
        Assert.Equal(["Name:gwent", "Steam:400"], snapshot.CompletedKeys);
    }

    [Fact]
    public void Snapshot_WithoutAnyCompletionColumn_IsNull() =>
        Assert.Null(BacklogSnapshot.From("Notion", DateTimeOffset.Now, [(Steam("Portal", "400"), null)]));

    [Fact]
    public void Store_KeepsTheLatestPerTarget_AndSurvivesARoundTrip()
    {
        var state = new MemoryStateStore();
        var store = new BacklogProgress(state);
        var older = new BacklogSnapshot("Notion", DateTimeOffset.Now.AddDays(-1), 1, 10, ["Steam:400"]);
        var newer = new BacklogSnapshot("Google Sheets", DateTimeOffset.Now, 3, 10, []);
        store.Record(older);
        store.Record(newer);

        var reloaded = new BacklogProgress(state);
        Assert.Equal(1, reloaded.Get("Notion")?.Completed);
        Assert.Equal(["Steam:400"], reloaded.Get("Notion")?.CompletedKeys);
        Assert.Equal("Google Sheets", reloaded.Latest()?.Target);
    }

    [Fact]
    public void Store_BrokenJson_IsIgnored() => Assert.Empty(BacklogProgress.Parse("{not json"));

    [Fact]
    public void Sheets_TrackedRows_CarryTheCompletedCell_HiddenLeftOut()
    {
        IList<IList<object>> sheet =
        [
            new List<object> { "Platform", "Name", "ID", "Completed" },
            new List<object> { "Steam", "Portal", "Steam:400", true },
            new List<object> { "Steam", "Doom", "Steam:2280" },
            new List<object> { "Steam", "Test Server", "Steam:1", true }
        ];

        var plan = GoogleSheetsSyncPlanner.Plan(sheet, [Steam("Portal", "400")], ignored: [Steam("Test Server", "1")]);

        Assert.Equal([("Portal", (bool?)true), ("Doom", false)], plan.Tracked!.Select(t => (t.Row.Name, t.Completed)));
    }

    [Fact]
    public void Sheets_LegacyHeaderlessSheet_ReadsCompletedFromTheThirdColumn()
    {
        IList<IList<object>> sheet = [new List<object> { "Portal", "400", true }];

        var plan = GoogleSheetsSyncPlanner.Plan(sheet, []);

        Assert.True(Assert.Single(plan.Tracked!).Completed);
    }

    [Fact]
    public void Library_MarksCompletedGames_FiltersAndCounts()
    {
        var snapshot = new BacklogSnapshot("Notion", DateTimeOffset.Now, 1, 2, ["Steam:400"]);
        var vm = new LibraryViewModel([Steam("Portal", "400"), Steam("Doom", "2280")], backlog: snapshot);

        Assert.True(vm.Stats.HasCompletion);
        Assert.Equal("Completed 1 of 2 (50%)", vm.Stats.CompletedText);

        vm.SelectedFilter = vm.Filters.Single(f => f.Key == LibraryViewModel.CompletedKey);
        Assert.Equal(["Portal"], vm.VisibleGames.Select(g => g.Name));
        vm.SelectedFilter = vm.Filters.Single(f => f.Key == LibraryViewModel.NotCompletedKey);
        Assert.Equal(["Doom"], vm.VisibleGames.Select(g => g.Name));
    }

    [Fact]
    public void Library_WithoutSnapshot_HasNoCompletionFilters()
    {
        var vm = new LibraryViewModel([Steam("Portal", "400")]);

        Assert.False(vm.Stats.HasCompletion);
        Assert.DoesNotContain(vm.Filters, f => f.Key == LibraryViewModel.CompletedKey);
    }

}

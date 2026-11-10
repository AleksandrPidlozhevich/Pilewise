using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using Pilewise.Models;
using Pilewise.Services;
using Xunit;
using Pilewise.Tests.Fakes;

namespace Pilewise.Tests;

public class NotionGameExporterTests
{
    [Fact]
    public async Task Export_NewGameNotInDatabase_CreatesOneAndUpdatesNone()
    {
        var incoming = CsvFormat.CreateRow("Steam", "New Game", "Steam:1");
        var result = await RunExport(Library(incoming), []);

        Assert.Single(result.Created);
        Assert.Empty(result.Updated);
    }

    [Fact]
    public async Task Export_IncomingAddsPlatformToExistingRow_UpdatesItNotCreates()
    {
        // Existing Steam-only row; incoming GOG row for the same title (matched by name) adds GOG,
        // so the stored Notion page must be updated rather than a duplicate created.
        var existing = Existing("p1",
            CsvFormat.CreateRow("Steam", "The Witcher 3", "Steam:292030"),
            "Steam:292030", "Steam");
        var incoming = CsvFormat.CreateRow("GOG", "The Witcher 3", "GOG:99");

        var result = await RunExport(Library(incoming), [existing]);

        Assert.Empty(result.Created);
        var update = Assert.Single(result.Updated);
        Assert.Equal("p1", update.PageId);
    }

    [Fact]
    public async Task Export_EverythingAlreadyUpToDate_DoesNothing()
    {
        var existing = Existing("p1",
            CsvFormat.CreateRow("Steam", "Portal", "Steam:400"),
            "Steam:400", "Steam");
        var incoming = CsvFormat.CreateRow("Steam", "Portal", "Steam:400");

        var result = await RunExport(Library(incoming), [existing]);

        Assert.Empty(result.Created);
        Assert.Empty(result.Updated);
    }

    [Fact]
    public async Task Export_LegacyBareId_IsNormalizedToPrefixedForm_EvenWithoutIncoming()
    {
        // Stored GameID is a legacy bare number while the row resolves to "Steam:379720"; the
        // export should rewrite it to the prefixed form even though the game isn't in the import.
        var existing = Existing("p1",
            CsvFormat.CreateRow("Steam", "Doom", "Steam:379720"),
            "379720", "Steam");

        var result = await RunExport(Library( /* nothing owned */), [existing]);

        Assert.Empty(result.Created);
        Assert.Single(result.Updated);
    }

    [Fact]
    public async Task Export_DisposesTheNotionClient()
    {
        var result = await RunExport(Library(), []);
        Assert.True(result.Disposed); // the `using` in ExportAsync released the HttpClient-owning client
    }

    [Fact]
    public async Task Plan_IsADryRun_WritesNothingButReportsChanges()
    {
        var existing = Existing("p1",
            CsvFormat.CreateRow("Steam", "Doom", "Steam:379720"),
            "379720", "Steam");
        var (exporter, created, updated) = CreateExporter(
            Library(CsvFormat.CreateRow("Steam", "Portal", "Steam:400")), [existing]);

        var plan = await exporter.PlanAsync("key", "id", "token", "db");

        Assert.Empty(created);
        Assert.Empty(updated);
        Assert.Equal("Portal", Assert.Single(plan.ToCreate).Name);
        var update = Assert.Single(plan.ToUpdate);
        Assert.Equal("Steam · 379720", update.Before);
        Assert.Equal("Steam · Steam:379720", update.After);
        Assert.Equal(0, plan.Unchanged);
    }

    [Fact]
    public async Task Apply_WritesExactlyThePlannedChanges()
    {
        var (exporter, created, updated) = CreateExporter(
            Library(CsvFormat.CreateRow("Steam", "Portal", "Steam:400")), []);

        var plan = await exporter.PlanAsync("key", "id", "token", "db");
        await exporter.ApplyAsync("token", "db", plan);

        Assert.Single(created);
        Assert.Empty(updated);
    }

    [Fact]
    public async Task Plan_UpToDate_IsEmptyAndCountsUnchanged()
    {
        var existing = Existing("p1",
            CsvFormat.CreateRow("Steam", "Portal", "Steam:400"),
            "Steam:400", "Steam");
        var (exporter, _, _) = CreateExporter(
            Library(CsvFormat.CreateRow("Steam", "Portal", "Steam:400")), [existing]);

        var plan = await exporter.PlanAsync("key", "id", "token", "db");

        Assert.True(plan.IsEmpty);
        Assert.Equal(1, plan.Unchanged);
    }

    [Fact]
    public async Task Plan_SyncsCompletedTicks_BothWays()
    {
        var completed = new CompletedGames(new MemoryStateStore());
        completed.Set(CsvFormat.CreateRow("Steam", "Portal", "Steam:400"), true); // ticked in the app
        var portal = Existing("p1", CsvFormat.CreateRow("Steam", "Portal", "Steam:400"),
                "Steam:400", "Steam") with
            {
                Completed = false
            };
        var hades = Existing("p2", CsvFormat.CreateRow("Steam", "Hades", "Steam:1145360"),
                "Steam:1145360", "Steam") with
            {
                Completed = true
            }; // ticked in Notion
        var client = new FakeNotionApiClient();
        var exporter = new NotionGameExporter(
            new FakeGameLibraryService(Library(
                CsvFormat.CreateRow("Steam", "Portal", "Steam:400"),
                CsvFormat.CreateRow("Steam", "Hades", "Steam:1145360"))),
            new FakeNotionGateway(client, new FakeNotionDataFetcher([portal, hades])), new SilentLog(),
            completed: completed);

        var plan = await exporter.PlanAsync("key", "id", "token", "db");
        var update = Assert.Single(plan.ToUpdate);
        Assert.Equal("p1", update.PageId);
        Assert.Contains("✓ completed", update.After);
        Assert.Equal(2, plan.Backlog!.Completed);

        await exporter.ApplyAsync("token", "db", plan);

        completed.Reload();
        Assert.True(completed.IsCompleted(CsvFormat.CreateRow("Steam", "Hades", "Steam:1145360")));
        Assert.Contains(client.Updated, u => u.PageId == "p1" &&
                                             ((Dictionary<string, object>)u.Body.Properties).ContainsKey("Completed"));
    }

    [Fact]
    public void Schema_StatusColumn_IsReadOnly_AndNoCompletedIsAdded()
    {
        var result = NotionSchemaCheck.Check(new Dictionary<string, string>
        {
            ["Name"] = "title", ["Platform's"] = "multi_select", ["GameID"] = "rich_text", ["Store link"] = "url",
            ["Status"] = "status"
        });

        Assert.Null(result.Optional.Completed);
        Assert.Empty(result.ToCreate);
    }


    [Fact]
    public async Task Plan_MissingGameIdProperty_IsPlanned_AndAddedOnlyOnApply()
    {
        var client = new FakeNotionApiClient();
        client.PropertyTypes.Remove("GameID");
        var exporter = new NotionGameExporter(
            new FakeGameLibraryService(Library(CsvFormat.CreateRow("Steam", "Portal", "Steam:400"))),
            new FakeNotionGateway(client, new FakeNotionDataFetcher([])),
            new SilentLog());

        var plan = await exporter.PlanAsync("key", "id", "token", "db");

        Assert.Equal("GameID", Assert.Single(plan.PropertiesToCreate).Name);
        Assert.Empty(client.AddedProperties); // dry run

        await exporter.ApplyAsync("token", "db", plan);

        Assert.Equal("rich_text", Assert.Single(client.AddedProperties)["GameID"]);
        Assert.Single(client.Created);
    }

    [Fact]
    public async Task Plan_WrongTitleName_FailsBeforeFetchingAnything()
    {
        var client = new FakeNotionApiClient();
        client.PropertyTypes.Remove("Name");
        client.PropertyTypes["Title"] = "title";
        var exporter = new NotionGameExporter(
            new FakeGameLibraryService(Library()),
            new FakeNotionGateway(client, new FakeNotionDataFetcher([])),
            new SilentLog());

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            exporter.PlanAsync("key", "id", "token", "db"));

        Assert.Contains("Rename the title column \"Title\" to \"Name\"", ex.Message);
    }

    [Fact]
    public async Task Plan_PlaytimeColumn_UpdatesChangedHours_LeavesOthers()
    {
        var client = new FakeNotionApiClient();
        client.PropertyTypes["Playtime"] = "number";
        var stale = Existing("p1", CsvFormat.CreateRow("Steam", "Portal", "Steam:400"),
                "Steam:400", "Steam") with
            {
                PlaytimeHours = 2.0
            };
        var current = Existing("p2", CsvFormat.CreateRow("Steam", "Doom", "Steam:379720"),
                "Steam:379720", "Steam") with
            {
                PlaytimeHours = 1.0
            };
        var exporter = new NotionGameExporter(
            new FakeGameLibraryService(Library(
                new CsvGameExportRow { Name = "Portal", HasSteam = true, SteamGameId = 400, PlaytimeMinutes = 750 },
                new CsvGameExportRow { Name = "Doom", HasSteam = true, SteamGameId = 379720, PlaytimeMinutes = 60 },
                new CsvGameExportRow { Name = "New", HasSteam = true, SteamGameId = 1, PlaytimeMinutes = 6 })),
            new FakeNotionGateway(client, new FakeNotionDataFetcher([stale, current])),
            new SilentLog());

        var plan = await exporter.PlanAsync("key", "id", "token", "db");

        var update = Assert.Single(plan.ToUpdate);
        Assert.Equal("p1", update.PageId);
        Assert.Equal("Steam · Steam:400 · 2 h", update.Before);
        Assert.Equal("Steam · Steam:400 · 12.5 h", update.After);
        var created = (Dictionary<string, object>)Assert.Single(plan.ToCreate).Properties;
        Assert.True(created.ContainsKey("Playtime"));
    }

    [Fact]
    public async Task Plan_WithoutPlaytimeColumn_NeverWritesPlaytime()
    {
        var exporter = new NotionGameExporter(
            new FakeGameLibraryService(Library(
                new CsvGameExportRow { Name = "New", HasSteam = true, SteamGameId = 1, PlaytimeMinutes = 6 })),
            new FakeNotionGateway(new FakeNotionApiClient(), new FakeNotionDataFetcher([])),
            new SilentLog());

        var plan = await exporter.PlanAsync("key", "id", "token", "db");

        var created = (Dictionary<string, object>)Assert.Single(plan.ToCreate).Properties;
        Assert.False(created.ContainsKey("Playtime"));
    }

    [Fact]
    public async Task Plan_GoneGame_ListedAndUnticked_OnlyWhenItsStoresWereFetched()
    {
        var client = new FakeNotionApiClient();
        client.PropertyTypes["Owned"] = "checkbox";
        var refunded = Existing("p1", CsvFormat.CreateRow("Steam", "Refunded", "Steam:9"),
                "Steam:9", "Steam") with
            {
                Owned = true
            };
        var epicOnly = Existing("p2", CsvFormat.CreateRow("Epic", "Alan Wake", "Epic:ns/cat"),
                "Epic:ns/cat", "Epic") with
            {
                Owned = true
            };
        var library = Library(CsvFormat.CreateRow("Steam", "Portal", "Steam:400"))
            with
            {
                SteamFetched = true
            }; // Epic not connected
        var exporter = new NotionGameExporter(
            new FakeGameLibraryService(library),
            new FakeNotionGateway(client, new FakeNotionDataFetcher([refunded, epicOnly])),
            new SilentLog());

        var plan = await exporter.PlanAsync("key", "id", "token", "db");

        Assert.Equal("Refunded", Assert.Single(plan.NoLongerOwned).Name);
        var update = Assert.Single(plan.ToUpdate);
        Assert.Equal("p1", update.PageId);
        Assert.EndsWith("not owned", update.After);
        var created = (Dictionary<string, object>)Assert.Single(plan.ToCreate).Properties;
        Assert.True(created.ContainsKey("Owned"));
    }

    [Fact]
    public async Task Plan_WithoutOwnedColumn_ReportsGoneButDoesNotUpdate()
    {
        var refunded = Existing("p1", CsvFormat.CreateRow("Steam", "Refunded", "Steam:9"),
            "Steam:9", "Steam");
        var exporter = new NotionGameExporter(
            new FakeGameLibraryService(Library() with { SteamFetched = true }),
            new FakeNotionGateway(new FakeNotionApiClient(), new FakeNotionDataFetcher([refunded])),
            new SilentLog());

        var plan = await exporter.PlanAsync("key", "id", "token", "db");

        Assert.Single(plan.NoLongerOwned);
        Assert.Empty(plan.ToUpdate);
        Assert.False(plan.HasOwnedColumn);
    }

    [Fact]
    public async Task Plan_NewSteamGame_GetsCoverAndStoreLink()
    {
        var (exporter, _, _) = CreateExporter(
            Library(CsvFormat.CreateRow("Steam", "Portal", "Steam:400")), []);

        var plan = await exporter.PlanAsync("key", "id", "token", "db");

        var create = Assert.Single(plan.ToCreate);
        Assert.Equal("https://cdn.akamai.steamstatic.com/steam/apps/400/header.jpg", create.CoverUrl);
        Assert.True(((Dictionary<string, object>)create.Properties).ContainsKey("Store link"));
    }

    [Fact]
    public async Task Plan_PageWithoutCoverOrLink_IsFilled_ButUserValuesAreKept()
    {
        var row = CsvFormat.CreateRow("Steam", "Portal", "Steam:400");
        var bare = new NotionGameRow(row, "p1", new HashSet<string> { "Steam" }, "Steam:400");
        var custom = new NotionGameRow(CsvFormat.CreateRow("Steam", "Doom", "Steam:379720"), "p2",
            new HashSet<string> { "Steam" }, "Steam:379720", HasCover: true, StoreLink: "https://my.link");
        var (exporter, _, _) = CreateExporter(Library(), [bare, custom]);

        var plan = await exporter.PlanAsync("key", "id", "token", "db");

        var update = Assert.Single(plan.ToUpdate);
        Assert.Equal("p1", update.PageId);
        Assert.EndsWith("+ cover · + store link", update.After);
        Assert.NotNull(update.CoverUrl);
    }

    [Fact]
    public async Task Plan_FreshDatabase_CreatesStoreLinkProperty()
    {
        var client = new FakeNotionApiClient();
        client.PropertyTypes.Remove("Store link");
        var exporter = new NotionGameExporter(new FakeGameLibraryService(Library()),
            new FakeNotionGateway(client, new FakeNotionDataFetcher([])), new SilentLog());

        var plan = await exporter.PlanAsync("key", "id", "token", "db");

        Assert.Equal(("Store link", "url"),
            Assert.Single(plan.PropertiesToCreate) is var p ? (p.Name, p.Type) : default);
    }

    [Fact]
    public async Task Plan_HiddenGame_IsNotCreated_AndItsPageIsNotGone()
    {
        var client = new FakeNotionApiClient();
        client.PropertyTypes["Owned"] = "checkbox";
        var demo = CsvFormat.CreateRow("Steam", "Some Demo", "Steam:9");
        var page = Existing("p1", CsvFormat.CreateRow("Steam", "Some Demo", "Steam:9"),
                "Steam:9", "Steam") with
            {
                Owned = true
            };
        var library = Library(CsvFormat.CreateRow("Steam", "Hidden New", "Steam:10")) with
        {
            SteamFetched = true,
            Rows = [],
            IgnoredRows = [demo, CsvFormat.CreateRow("Steam", "Hidden New", "Steam:10")]
        };
        var exporter = new NotionGameExporter(new FakeGameLibraryService(library),
            new FakeNotionGateway(client, new FakeNotionDataFetcher([page])), new SilentLog());

        var plan = await exporter.PlanAsync("key", "id", "token", "db");

        Assert.Empty(plan.ToCreate);
        Assert.Empty(plan.NoLongerOwned);
        Assert.Empty(plan.ToUpdate);
    }

    [Fact]
    public async Task Duplicates_MergeUpdatesTheKeptPage_AndTrashesTheRest()
    {
        var client = new FakeNotionApiClient();
        var steam = Existing("p1", CsvFormat.CreateRow("Steam", "Hades", "Steam:1145360"),
            "Steam:1145360", "Steam");
        var epic = Existing("p2", CsvFormat.CreateRow("Epic", "Hades", "Epic:ns/cat"),
            "Epic:ns/cat", "Epic");
        var exporter = new NotionGameExporter(new FakeGameLibraryService(Library()),
            new FakeNotionGateway(client, new FakeNotionDataFetcher([steam, epic])), new SilentLog());

        var groups = await exporter.FindDuplicatesAsync("token", "db");
        Assert.Empty(client.Updated); // finding is read-only
        await exporter.MergeDuplicatesAsync("token", "db", groups);

        var (pageId, body) = Assert.Single(client.Updated);
        Assert.Equal("p1", pageId);
        Assert.Null(body.CoverUrl);
        var properties = (Dictionary<string, object>)body.Properties;
        Assert.False(properties.ContainsKey("Store link"));
        Assert.Equal(["p2"], client.Trashed);
    }

    [Fact]
    public async Task Backlog_IsReadInThePlan_AndRecordedOnlyOnApply()
    {
        var done = Existing("p1", CsvFormat.CreateRow("Steam", "Portal", "Steam:400"), "Steam:400", "Steam")
            with
            {
                Completed = true
            };
        var open = Existing("p2", CsvFormat.CreateRow("Steam", "Doom", "Steam:379720"), "Steam:379720", "Steam")
            with
            {
                Completed = false
            };
        var hidden = Existing("p3", CsvFormat.CreateRow("Steam", "Test Server", "Steam:1"), "Steam:1", "Steam")
            with
            {
                Completed = true
            };
        var library = Library(
                CsvFormat.CreateRow("Steam", "Portal", "Steam:400"),
                CsvFormat.CreateRow("Steam", "Doom", "Steam:379720"),
                CsvFormat.CreateRow("Steam", "Hades", "Steam:1145360")) with
            {
                IgnoredRows = [CsvFormat.CreateRow("Steam", "Test Server", "Steam:1")]
            };
        var backlog = new BacklogProgress();
        var exporter = new NotionGameExporter(
            new FakeGameLibraryService(library),
            new FakeNotionGateway(new FakeNotionApiClient(), new FakeNotionDataFetcher([done, open, hidden])),
            new SilentLog(), backlog);

        var plan = await exporter.PlanAsync("key", "id", "token", "db");

        // Hades is new (counted, not completed); the hidden Test Server isn't counted.
        Assert.Equal("Completed 1 of 3 (33%)", plan.Backlog?.Text);
        Assert.Equal(["Steam:400"], plan.Backlog?.CompletedKeys);
        Assert.Null(backlog.Get(SyncHistory.Notion));

        await exporter.ApplyAsync("token", "db", plan);
        Assert.Equal(1, backlog.Get(SyncHistory.Notion)?.Completed);
    }

    [Fact]
    public async Task ReadBacklog_OnlyReads_RecordsTheMarks_AndLeavesHiddenGamesOut()
    {
        var done = Existing("p1", CsvFormat.CreateRow("Steam", "Portal", "Steam:400"), "Steam:400", "Steam")
            with
            {
                Completed = true
            };
        var open = Existing("p2", CsvFormat.CreateRow("Steam", "Doom", "Steam:2280"), "Steam:2280", "Steam")
            with
            {
                Completed = false
            };
        var hidden = Existing("p3", CsvFormat.CreateRow("Steam", "Test Server", "Steam:1"), "Steam:1", "Steam")
            with
            {
                Completed = true
            };
        var client = new FakeNotionApiClient();
        var backlog = new BacklogProgress();
        var exporter = new NotionGameExporter(
            new FakeGameLibraryService(Library()),
            new FakeNotionGateway(client, new FakeNotionDataFetcher([done, open, hidden])),
            new SilentLog(), backlog);

        var snapshot = await exporter.ReadBacklogAsync("token", "db", r => r.Name == "Test Server");

        Assert.Equal("Completed 1 of 2 (50%)", snapshot?.Text);
        Assert.Equal(["Steam:400"], backlog.Get(SyncHistory.Notion)?.CompletedKeys);
        Assert.Empty(client.Created);
        Assert.Empty(client.Updated);
    }

    // Returns the fake client's recorded writes (the fake itself is file-local).
    private static (NotionGameExporter Exporter, List<NotionPageBody> Created,
        List<(string PageId, NotionPageBody Body)> Updated)
        CreateExporter(
            GameLibraryResult library, IReadOnlyList<NotionGameRow> existing)
    {
        var client = new FakeNotionApiClient();
        var exporter = new NotionGameExporter(
            new FakeGameLibraryService(library),
            new FakeNotionGateway(client, new FakeNotionDataFetcher(existing.ToList())),
            new SilentLog());
        return (exporter, client.Created, client.Updated);
    }

    private static async Task<(List<NotionPageBody> Created, List<(string PageId, NotionPageBody Body)> Updated, bool
            Disposed)>
        RunExport(GameLibraryResult library, IReadOnlyList<NotionGameRow> existing)
    {
        var client = new FakeNotionApiClient();
        var fetcher = new FakeNotionDataFetcher(existing.ToList());
        var exporter = new NotionGameExporter(
            new FakeGameLibraryService(library),
            new FakeNotionGateway(client, fetcher),
            new SilentLog());

        await exporter.ExportAsync("key", "id", "token", "db");
        return (client.Created, client.Updated, client.Disposed);
    }

    private static GameLibraryResult Library(params CsvGameExportRow[] rows)
    {
        return new GameLibraryResult(rows.ToList(), false, false, false, false, false, false);
    }

    // An existing page that already has a cover and its store link (so only the tested change shows).
    private static NotionGameRow Existing(string pageId, CsvGameExportRow row, string gameId, params string[] platforms)
    {
        return new NotionGameRow(row, pageId, new HashSet<string>(platforms, StringComparer.OrdinalIgnoreCase), gameId,
            HasCover: true, StoreLink: row.StoreUrl ?? "https://example.com");
    }
}

file sealed class FakeGameLibraryService(GameLibraryResult result) : IGameLibraryService
{
    public Task<GameLibraryResult> FetchAsync(
        string? steamApiKey, string? steamId,
        IProgress<StoreFetchProgress>? progress = null, Action<string>? onStatus = null,
        CancellationToken ct = default)
    {
        return Task.FromResult(result);
    }
}

file sealed class FakeNotionGateway(INotionApiClient client, INotionDataFetcher fetcher) : INotionGateway
{
    public INotionApiClient CreateClient(string? token, string? dbId)
    {
        return client;
    }

    public INotionDataFetcher CreateFetcher(INotionApiClient c)
    {
        return fetcher;
    }
}

file sealed class FakeNotionDataFetcher(List<NotionGameRow> rows) : INotionDataFetcher
{
    public Task<List<NotionGameRow>> FetchRowsAsync(CancellationToken ct = default)
    {
        return Task.FromResult(rows);
    }
}

file sealed class FakeNotionApiClient : INotionApiClient
{
    public List<NotionPageBody> Created { get; } = [];
    public List<(string PageId, NotionPageBody Body)> Updated { get; } = [];
    public List<string> Trashed { get; } = [];
    public bool Disposed { get; private set; }

    public Task<List<JObject>> QueryAllPagesAsync(CancellationToken ct = default)
    {
        return Task.FromResult(new List<JObject>());
    }

    // A correctly set-up database by default; tests replace it to simulate a broken schema.
    public Dictionary<string, string> PropertyTypes { get; set; } = new()
    {
        ["Name"] = "title", ["Platform's"] = "multi_select", ["GameID"] = "rich_text", ["Store link"] = "url",
        ["Completed"] = "checkbox"
    };

    public List<IReadOnlyDictionary<string, string>> AddedProperties { get; } = [];

    public Task<IReadOnlyDictionary<string, string>> GetPropertyTypesAsync(CancellationToken ct = default)
    {
        return Task.FromResult<IReadOnlyDictionary<string, string>>(PropertyTypes);
    }

    public Task AddPropertiesAsync(IReadOnlyDictionary<string, string> properties, CancellationToken ct = default)
    {
        AddedProperties.Add(properties);
        return Task.CompletedTask;
    }

    public Task AddPagesToDatabaseParallel(
        IEnumerable<NotionPageBody> pages, Action? onPageDone = null, CancellationToken ct = default)
    {
        foreach (var page in pages)
        {
            Created.Add(page);
            onPageDone?.Invoke();
        }

        return Task.CompletedTask;
    }

    public Task UpdatePagesParallel(
        IEnumerable<(string PageId, NotionPageBody Body)> updates,
        Action? onPageDone = null, CancellationToken ct = default)
    {
        foreach (var update in updates)
        {
            Updated.Add(update);
            onPageDone?.Invoke();
        }

        return Task.CompletedTask;
    }

    public Task TrashPagesParallel(IEnumerable<string> pageIds, Action? onPageDone = null,
        CancellationToken ct = default)
    {
        Trashed.AddRange(pageIds);
        return Task.CompletedTask;
    }

    public void Dispose()
    {
        Disposed = true;
    }
}
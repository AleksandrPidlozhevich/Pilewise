using Pilewise.Models;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Pilewise.Services;

public sealed class NotionGameExporter
{
    private readonly IGameLibraryService _library;
    private readonly INotionGateway _notion;
    private readonly ILogService _log;
    private readonly BacklogProgress? _backlog;
    private readonly SteamLinuxLookup? _linux;
    private readonly CompletedGames? _completed;

    public NotionGameExporter(
        IGameLibraryService library, INotionGateway notion, ILogService log, BacklogProgress? backlog = null,
        SteamLinuxLookup? linux = null, CompletedGames? completed = null)
    {
        _linux = linux;
        _completed = completed;
        _backlog = backlog;
        _library = library;
        _notion = notion;
        _log = log;
    }

    public async Task ExportAsync(
        string? steamApiKey, string? steamId, string? notionToken, string? dbId,
        IProgress<StoreFetchProgress>? progress = null, Action<string>? onStatus = null,
        CancellationToken ct = default)
    {
        var plan = await PlanAsync(steamApiKey, steamId, notionToken, dbId, progress, onStatus, ct);
        await ApplyAsync(notionToken, dbId, plan, progress, onStatus, ct);
    }

    public async Task<NotionExportPlan> PlanAsync(
        string? steamApiKey, string? steamId, string? notionToken, string? dbId,
        IProgress<StoreFetchProgress>? progress = null, Action<string>? onStatus = null,
        CancellationToken ct = default)
    {
        using var notionApiClient = _notion.CreateClient(notionToken, dbId);
        var fetcher = _notion.CreateFetcher(notionApiClient);

        // Check the schema first: a misnamed title or wrong type fails here, before the (slow)
        // library fetch, with a message saying exactly what to fix in Notion.
        onStatus?.Invoke("Checking the Notion database…");
        var includeLinux = _linux is { Enabled: true };
        var schema = NotionSchemaCheck.Check(await notionApiClient.GetPropertyTypesAsync(ct), includeLinux);
        if (!schema.IsUsable)
            throw new InvalidOperationException(
                "The Notion database needs a fix before Pilewise can export to it:\n• " +
                string.Join("\n• ", schema.Problems));

        var libraryTask = _library.FetchAsync(steamApiKey, steamId, progress, onStatus, ct);
        var existingTask = fetcher.FetchRowsAsync(ct);
        await Task.WhenAll(libraryTask, existingTask);

        var library = await libraryTask;
        var existing = await existingTask;

        var optional = schema.Optional;
        // Seed stored Linux so a failed lookup doesn't wipe a known rating on an unrelated update.
        foreach (var page in existing)
            page.Row.Linux = page.Linux;

        IReadOnlyDictionary<int, string>? linuxLabels = null;
        if (includeLinux)
        {
            onStatus?.Invoke("Checking Linux support…");
            var ids = library.Rows.Concat(existing.Select(e => e.Row)).Select(r => r.SteamGameId).OfType<int>();
            linuxLabels = await _linux!.LookupAsync(ids, progress, ct);
            LinuxRating.Apply(library.Rows, linuxLabels);
        }

        var matcher = new CsvGameExportService.RowMatcher(existing.Select(e => e.Row));
        var matched = new HashSet<CsvGameExportRow>(ReferenceEqualityComparer.Instance);
        var hiddenRows = new HashSet<CsvGameExportRow>(ReferenceEqualityComparer.Instance);

        var newRows = new List<CsvGameExportRow>();

        foreach (var row in library.Rows)
        {
            var match = matcher.Match(row);
            if (match == null)
            {
                newRows.Add(row);
                matcher.Register(row);
                continue;
            }

            match.MergeFrom(row);
            matched.Add(match);
        }

        // Hidden games: their pages are left as they are, but they're still owned.
        foreach (var row in library.Ignored)
            if (matcher.Match(row) is { } hidden)
            {
                matched.Add(hidden);
                hiddenRows.Add(hidden);
            }

        if (linuxLabels != null)
            LinuxRating.Apply(existing.Where(e => !hiddenRows.Contains(e.Row)).Select(e => e.Row), linuxLabels);

        // Completed ticks: merged with Pilewise's list, written when the database has a checkbox for them.
        // Hidden pages keep theirs (and their Linux cell), whatever else on the page changes.
        var tracked = existing.Where(e => !hiddenRows.Contains(e.Row)).ToList();
        var completion = _completed?.Begin(TargetOf(dbId), tracked.Select(e => (e.Row, e.Completed)));
        var writesCompleted = optional.Completed != null && completion != null;
        bool? WantedCompleted(NotionGameRow e) =>
            writesCompleted && !hiddenRows.Contains(e.Row) ? completion!.IsCompleted(e.Row) : null;
        NotionOptionalColumns PageColumns(NotionGameRow e) =>
            hiddenRows.Contains(e.Row) ? optional with { Linux = false, Completed = null } : optional;

        var toCreate = newRows.Select(row => new NotionPageCreate(row.Name, row.PlatformText,
                BuildProperties(row, true, optional, true, setLink: true,
                    completed: writesCompleted ? completion!.IsCompleted(row) : null), row.CoverUrl))
            .ToList();

        // Update any existing page whose stored value no longer matches the desired one.
        // This covers merged-in platforms AND normalising legacy bare ids to the prefixed
        // form (e.g. "1920490" → "Steam:1920490") — even for games no longer owned, since
        // the row's own Platform's tag already tells us the platform.
        // Rows matched by nothing, from stores that were all fetched, are no longer owned.
        var fetched = library.FetchedStores;
        var owned = existing.ToDictionary(
            e => e.PageId, e => CsvGameExportService.Ownership(e.Row, matched.Contains(e.Row), fetched));
        var noLongerOwned = existing
            .Where(e => owned[e.PageId] == false)
            .Select(e => new NotionGoneGame(e.Row.Name, string.Join("/", e.Platforms.OrderBy(PlatformOrder))))
            .OrderBy(g => g.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        // With the opt-in Playtime / Last played / Owned columns, a changed value also triggers an update;
        // so does a page without a cover or an empty Store link that we can now fill.
        var toUpdate = existing
            .Where(e => NotionSignature(e.Row, PageColumns(e), owned[e.PageId], WantedCompleted(e)) !=
                        StoredSignature(e, PageColumns(e), owned[e.PageId], WantedCompleted(e) != null) ||
                        NeedsCover(e) || NeedsLink(e))
            .Select(e => new NotionPageUpdate(
                e.PageId,
                e.Row.Name,
                Describe(string.Join("/", e.Platforms.OrderBy(PlatformOrder)), e.GameId,
                    Wanted(optional.Playtime, e.Row.PlaytimeHours, e.PlaytimeHours),
                    Wanted(optional.LastPlayed, e.Row.LastPlayedDate, e.LastPlayed),
                    Wanted(optional.Owned, owned[e.PageId], e.Owned),
                    linux: optional.Linux ? e.Linux : null,
                    completed: WantedCompleted(e) != null ? e.Completed ?? false : null),
                Describe(e.Row.PlatformText, e.Row.IdText,
                    Wanted(optional.Playtime, e.Row.PlaytimeHours, e.Row.PlaytimeHours),
                    Wanted(optional.LastPlayed, e.Row.LastPlayedDate, e.Row.LastPlayedDate),
                    Wanted(optional.Owned, owned[e.PageId], owned[e.PageId]),
                    NeedsCover(e), NeedsLink(e),
                    linux: optional.Linux ? e.Row.Linux : null,
                    completed: WantedCompleted(e)),
                BuildProperties(e.Row, false, PageColumns(e), owned[e.PageId], NeedsLink(e), WantedCompleted(e)),
                NeedsCover(e) ? e.Row.CoverUrl : null))
            .ToList();

        // The ticks the database holds once the plan is applied (what the completion sync records).
        var completionAfter = tracked
            .Where(e => WantedCompleted(e) != null || e.Completed != null)
            .Select(e => (e.Row, WantedCompleted(e) ?? e.Completed!.Value))
            .Concat(newRows.Select(row => (row, writesCompleted && completion!.IsCompleted(row))))
            .ToList();

        return new NotionExportPlan(
            library,
            schema.ToCreate,
            toCreate.OrderBy(c => c.Name, StringComparer.OrdinalIgnoreCase).ToList(),
            toUpdate.OrderBy(u => u.Name, StringComparer.OrdinalIgnoreCase).ToList(),
            existing.Count - toUpdate.Count,
            noLongerOwned,
            optional.Owned,
            BacklogSnapshot.From(SyncHistory.Notion, DateTimeOffset.Now,
                tracked.Select(e => (e.Row, WantedCompleted(e) ?? e.Completed))
                    .Concat(newRows.Select(row => (row, writesCompleted ? completion!.IsCompleted(row) : (bool?)null)))),
            completion,
            completionAfter);
    }

    public static string TargetOf(string? dbId) => $"Notion:{dbId?.Trim()}";

    public async Task ApplyAsync(
        string? notionToken, string? dbId, NotionExportPlan plan,
        IProgress<StoreFetchProgress>? progress = null, Action<string>? onStatus = null,
        CancellationToken ct = default)
    {
        // Read during the plan; recorded only for a real export (a dry run never applies).
        _backlog?.Record(plan.Backlog);

        if (plan.IsEmpty)
        {
            plan.Completion?.Commit(plan.CompletionAfter ?? []);
            _log.WriteInfo("Notion: already up to date.");
            return;
        }

        using var notionApiClient = _notion.CreateClient(notionToken, dbId);

        // Missing properties go in first, so the page writes below can fill them.
        if (plan.PropertiesToCreate.Count > 0)
        {
            onStatus?.Invoke("Adding database properties…");
            await notionApiClient.AddPropertiesAsync(
                plan.PropertiesToCreate.ToDictionary(p => p.Name, p => p.Type), ct);
            _log.WriteInfo(
                $"Notion: added properties {string.Join(", ", plan.PropertiesToCreate.Select(p => p.Name))}.");
        }

        if (plan.ToCreate.Count == 0 && plan.ToUpdate.Count == 0)
        {
            plan.Completion?.Commit(plan.CompletionAfter ?? []);
            return;
        }

        var total = plan.ToCreate.Count + plan.ToUpdate.Count;
        var done = 0;

        void ReportPage()
        {
            progress?.Report(new StoreFetchProgress(Interlocked.Increment(ref done), total, "Exporting to Notion"));
        }

        onStatus?.Invoke("Exporting to Notion…");
        progress?.Report(new StoreFetchProgress(0, total, "Exporting to Notion"));

        if (plan.ToCreate.Count > 0)
            await notionApiClient.AddPagesToDatabaseParallel(
                plan.ToCreate.Select(c => new NotionPageBody(c.Properties, c.CoverUrl)).ToList(), ReportPage, ct);

        if (plan.ToUpdate.Count > 0)
            await notionApiClient.UpdatePagesParallel(
                plan.ToUpdate.Select(u => (u.PageId, new NotionPageBody(u.Properties, u.CoverUrl))).ToList(),
                ReportPage, ct);

        plan.Completion?.Commit(plan.CompletionAfter ?? []);
        _log.WriteInfo($"Notion: created {plan.ToCreate.Count}, updated {plan.ToUpdate.Count} games.");
    }

    public async Task<BacklogSnapshot?> ReadBacklogAsync(
        string? notionToken, string? dbId, Func<CsvGameExportRow, bool>? isHidden = null, CancellationToken ct = default)
    {
        using var notionApiClient = _notion.CreateClient(notionToken, dbId);
        var pages = await _notion.CreateFetcher(notionApiClient).FetchRowsAsync(ct);
        var shown = pages.Where(p => isHidden?.Invoke(p.Row) != true).Select(p => (p.Row, p.Completed)).ToList();
        var snapshot = BacklogSnapshot.From(SyncHistory.Notion, DateTimeOffset.Now, shown);
        _backlog?.Record(snapshot);
        // Ticks changed in Notion go into Pilewise's list; nothing is written to Notion.
        _completed?.Begin(TargetOf(dbId), shown)
            .Commit(shown.Where(p => p.Completed != null).Select(p => (p.Row, p.Completed!.Value)));
        return snapshot;
    }

    public async Task<IReadOnlyList<NotionDuplicateGroup>> FindDuplicatesAsync(
        string? notionToken, string? dbId, CancellationToken ct = default)
    {
        using var notionApiClient = _notion.CreateClient(notionToken, dbId);
        var rows = await _notion.CreateFetcher(notionApiClient).FetchRowsAsync(ct);
        return NotionDuplicateFinder.Find(rows);
    }

    public async Task MergeDuplicatesAsync(
        string? notionToken, string? dbId, IReadOnlyList<NotionDuplicateGroup> groups,
        IProgress<StoreFetchProgress>? progress = null, CancellationToken ct = default)
    {
        if (groups.Count == 0) return;
        using var notionApiClient = _notion.CreateClient(notionToken, dbId);

        var removed = groups.SelectMany(g => g.Remove.Select(r => r.PageId)).ToList();
        var total = groups.Count + removed.Count;
        var done = 0;

        void ReportPage() =>
            progress?.Report(new StoreFetchProgress(Interlocked.Increment(ref done), total, "Merging duplicates"));

        // Tags + ids only: no opt-in columns, and the Store link is left as the user has it.
        var none = new NotionOptionalColumns(false, false);
        await notionApiClient.UpdatePagesParallel(
            groups.Select(g => (g.Keep.PageId, new NotionPageBody(BuildProperties(g.Merged, false, none, null, false))))
                .ToList(),
            ReportPage, ct);
        await notionApiClient.TrashPagesParallel(removed, ReportPage, ct);

        _log.WriteInfo($"Notion: merged {groups.Count} duplicate groups, moved {removed.Count} pages to the trash.");
    }

    // "Steam/GOG · Steam:1; GOG:2 · 12.5 h · last played 2024-05-01" — preview before/after text.
    private static string Describe(
        string platforms, string gameId, double? hours = null, string? lastPlayed = null, bool? owned = null,
        bool addsCover = false, bool addsLink = false, string? linux = null, bool? completed = null) =>
        string.Join(" · ", new[]
        {
            platforms, gameId,
            hours is { } h ? $"{h.ToString(CultureInfo.InvariantCulture)} h" : null,
            lastPlayed != null ? $"last played {lastPlayed}" : null,
            owned switch { true => "owned", false => "not owned", null => null },
            addsCover ? "+ cover" : null,
            addsLink ? "+ store link" : null,
            linux,
            completed == true ? "✓ completed" : null
        }.Where(part => !string.IsNullOrEmpty(part)));

    // A page gets a cover only when it has none (a cover the user picked is never replaced) …
    private static bool NeedsCover(NotionGameRow page) => !page.HasCover && page.Row.CoverUrl != null;

    // … and a Store link only while the cell is empty.
    private static bool NeedsLink(NotionGameRow page) =>
        string.IsNullOrWhiteSpace(page.StoreLink) && page.Row.StoreUrl != null;

    // An opt-in column's value, only when the column exists and fresh data was fetched for the row
    // (games no longer owned / stores without playtime keep whatever the page already holds).
    private static T? Wanted<T>(bool enabled, T? fresh, T? value) => enabled && fresh != null ? value : default;

    private static int PlatformOrder(string label)
    {
        var index = Array.FindIndex(PlatformCatalog.All, d => d.Label == label);
        return index < 0 ? int.MaxValue : index;
    }

    private static string NotionSignature(
        CsvGameExportRow row, NotionOptionalColumns optional, bool? owned, bool? completed)
    {
        var tags = string.Concat(PlatformCatalog.All.Select(d => d.Has(row)));
        return $"{tags}{row.IsGamePass}|{row.IdText}" +
               $"|{Format(Wanted(optional.Playtime, row.PlaytimeHours, row.PlaytimeHours))}" +
               $"|{Wanted(optional.LastPlayed, row.LastPlayedDate, row.LastPlayedDate)}" +
               $"|{Wanted(optional.Owned, owned, owned)}" +
               $"|{(optional.Linux ? row.Linux : null)}" +
               $"|{completed}";
    }

    private static string StoredSignature(
        NotionGameRow info, NotionOptionalColumns optional, bool? owned, bool writesCompleted)
    {
        var tags = string.Concat(PlatformCatalog.All.Select(d => info.Platforms.Contains(d.Label)));
        return $"{tags}{info.Platforms.Contains(Platforms.GamePass)}|{info.GameId}" +
               $"|{Format(Wanted(optional.Playtime, info.Row.PlaytimeHours, info.PlaytimeHours))}" +
               $"|{Wanted(optional.LastPlayed, info.Row.LastPlayedDate, info.LastPlayed)}" +
               // An unticked checkbox reads back as false; an unknown ownership never rewrites it.
               $"|{Wanted(optional.Owned, owned, info.Owned ?? false)}" +
               $"|{(optional.Linux ? info.Linux : null)}" +
               // A Completed column created in this export reads back as nothing yet: unticked.
               $"|{(writesCompleted ? info.Completed ?? false : null)}";
    }

    private static string? Format(double? hours) => hours?.ToString(CultureInfo.InvariantCulture);

    private static object BuildProperties(
        CsvGameExportRow row, bool includeName, NotionOptionalColumns optional, bool? owned, bool setLink,
        bool? completed = null)
    {
        var platforms = new List<object>();
        foreach (var d in PlatformCatalog.All)
            if (d.Has(row))
                platforms.Add(new { name = d.Label });
        if (row.IsGamePass) platforms.Add(new { name = Platforms.GamePass });

        var properties = new Dictionary<string, object>
        {
            [NotionDataFetcher.PlatformProperty] = new { multi_select = platforms },
            [NotionDataFetcher.GameIdProperty] =
                new { rich_text = new[] { new { text = new { content = row.IdText } } } }
        };

        if (includeName)
            properties[NotionDataFetcher.NameProperty] =
                new { title = new[] { new { text = new { content = row.Name } } } };

        if (optional.Playtime && row.PlaytimeHours is { } hours)
            properties[NotionDataFetcher.PlaytimeProperty] = new { number = hours };
        if (optional.LastPlayed && row.LastPlayedDate is { } lastPlayed)
            properties[NotionDataFetcher.LastPlayedProperty] = new { date = new { start = lastPlayed } };
        if (optional.Owned && owned is { } isOwned)
            properties[NotionDataFetcher.OwnedProperty] = new { checkbox = isOwned };
        if (setLink && row.StoreUrl is { } link)
            properties[NotionDataFetcher.StoreLinkProperty] = new { url = link };
        if (optional.Linux)
            properties[NotionDataFetcher.LinuxProperty] =
                new { select = new { name = row.Linux ?? LinuxRating.Unknown } };
        if (optional.Completed is { } completedProperty && completed is { } isCompleted)
            properties[completedProperty] = new { checkbox = isCompleted };

        return properties;
    }
}

public sealed record NotionPageCreate(string Name, string Platforms, object Properties, string? CoverUrl = null);

public sealed record NotionGoneGame(string Name, string Platforms);

public sealed record NotionPageUpdate(
    string PageId, string Name, string Before, string After, object Properties, string? CoverUrl = null);

public sealed record NotionExportPlan(
    GameLibraryResult Library,
    IReadOnlyList<NotionPropertyToCreate> PropertiesToCreate,
    IReadOnlyList<NotionPageCreate> ToCreate,
    IReadOnlyList<NotionPageUpdate> ToUpdate,
    int Unchanged,
    IReadOnlyList<NotionGoneGame> NoLongerOwned,
    bool HasOwnedColumn,
    BacklogSnapshot? Backlog = null,
    CompletionSync? Completion = null,
    IReadOnlyList<(CsvGameExportRow Row, bool Completed)>? CompletionAfter = null)
{
    public bool IsEmpty => PropertiesToCreate.Count == 0 && ToCreate.Count == 0 && ToUpdate.Count == 0;
}

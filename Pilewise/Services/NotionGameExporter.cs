using Pilewise.Models;
using static Pilewise.Services.NotionPages;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Pilewise.Services;

public sealed class NotionGameExporter(
    IGameLibraryService libraryService, INotionGateway notion, ILogService log, BacklogProgress? backlog = null,
    ISteamLinuxLookup? linux = null, CompletedGames? completed = null)
{
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
        using var notionApiClient = notion.CreateClient(notionToken, dbId);
        var fetcher = notion.CreateFetcher(notionApiClient);

        // Schema first: fail before the slow library fetch.
        onStatus?.Invoke("Checking the Notion database…");
        var includeLinux = linux is { Enabled: true };
        var schema = NotionSchemaCheck.Check(await notionApiClient.GetPropertyTypesAsync(ct), includeLinux);
        if (!schema.IsUsable)
            throw new InvalidOperationException(
                "The Notion database needs a fix before Pilewise can export to it:\n• " +
                string.Join("\n• ", schema.Problems));

        var libraryTask = libraryService.FetchAsync(steamApiKey, steamId, progress, onStatus, ct);
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
            linuxLabels = await linux!.LookupAsync(ids, progress, ct);
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
        var completion = completed?.Begin(TargetOf(dbId), tracked.Select(e => (e.Row, e.Completed)));
        var writesCompleted = optional.Completed != null && completion != null;
        var hiddenColumns = optional with { Linux = false, Completed = null };

        var created = newRows
            .Select(row => (Row: row, Completed: writesCompleted ? completion!.IsCompleted(row) : (bool?)null))
            .ToList();
        var toCreate = created.Select(c => new NotionPageCreate(c.Row.Name, c.Row.PlatformText,
                BuildProperties(c.Row, true, optional, true, setLink: true, completed: c.Completed), c.Row.CoverUrl))
            .ToList();

        var fetched = library.FetchedStores;
        var pages = existing.Select(e =>
        {
            var hidden = hiddenRows.Contains(e.Row);
            return (Page: e, Hidden: hidden,
                Owned: CsvGameExportService.Ownership(e.Row, matched.Contains(e.Row), fetched),
                Completed: writesCompleted && !hidden ? completion!.IsCompleted(e.Row) : (bool?)null,
                Columns: hidden ? hiddenColumns : optional,
                Cover: NeedsCover(e), Link: NeedsLink(e));
        }).ToList();

        var noLongerOwned = pages
            .Where(p => p.Owned == false)
            .Select(p => new NotionGoneGame(p.Page.Row.Name, PlatformsText(p.Page)))
            .OrderBy(g => g.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var toUpdate = pages
            .Where(p => Desired(p.Page.Row, p.Columns, p.Owned, p.Completed) !=
                        Stored(p.Page, p.Columns, p.Owned, p.Completed != null) || p.Cover || p.Link)
            .Select(p =>
            {
                var (e, owned) = (p.Page, p.Owned);
                return new NotionPageUpdate(
                    e.PageId,
                    e.Row.Name,
                    Describe(PlatformsText(e), e.GameId,
                        Wanted(optional.Playtime, e.Row.PlaytimeHours, e.PlaytimeHours),
                        Wanted(optional.LastPlayed, e.Row.LastPlayedDate, e.LastPlayed),
                        Wanted(optional.Owned, owned, e.Owned),
                        linux: optional.Linux ? e.Linux : null,
                        completed: p.Completed != null ? e.Completed ?? false : null),
                    Describe(e.Row.PlatformText, e.Row.IdText,
                        Wanted(optional.Playtime, e.Row.PlaytimeHours, e.Row.PlaytimeHours),
                        Wanted(optional.LastPlayed, e.Row.LastPlayedDate, e.Row.LastPlayedDate),
                        Wanted(optional.Owned, owned, owned),
                        p.Cover, p.Link,
                        linux: optional.Linux ? e.Row.Linux : null,
                        completed: p.Completed),
                    BuildProperties(e.Row, false, p.Columns, owned, p.Link, p.Completed),
                    p.Cover ? e.Row.CoverUrl : null);
            })
            .ToList();

        var trackedTicks = pages.Where(p => !p.Hidden).Select(p => (p.Page.Row, Completed: p.Completed ?? p.Page.Completed))
            .ToList();
        var completionAfter = trackedTicks
            .Where(t => t.Completed != null)
            .Select(t => (t.Row, t.Completed!.Value))
            .Concat(created.Select(c => (c.Row, c.Completed ?? false)))
            .ToList();

        return new NotionExportPlan(
            library,
            schema.ToCreate,
            toCreate.OrderBy(c => c.Name, StringComparer.OrdinalIgnoreCase).ToList(),
            toUpdate.OrderBy(u => u.Name, StringComparer.OrdinalIgnoreCase).ToList(),
            existing.Count - toUpdate.Count,
            noLongerOwned,
            optional.Owned,
            BacklogSnapshot.From(SyncHistory.Notion, DateTimeOffset.Now, trackedTicks.Concat(created)),
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
        backlog?.Record(plan.Backlog);

        if (plan.IsEmpty)
        {
            plan.Completion?.Commit(plan.CompletionAfter ?? []);
            log.WriteInfo("Notion: already up to date.");
            return;
        }

        using var notionApiClient = notion.CreateClient(notionToken, dbId);

        // Missing properties go in first, so the page writes below can fill them.
        if (plan.PropertiesToCreate.Count > 0)
        {
            onStatus?.Invoke("Adding database properties…");
            await notionApiClient.AddPropertiesAsync(
                plan.PropertiesToCreate.ToDictionary(p => p.Name, p => p.Type), ct);
            log.WriteInfo(
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
        log.WriteInfo($"Notion: created {plan.ToCreate.Count}, updated {plan.ToUpdate.Count} games.");
    }

    public async Task<BacklogSnapshot?> ReadBacklogAsync(
        string? notionToken, string? dbId, Func<CsvGameExportRow, bool>? isHidden = null, CancellationToken ct = default)
    {
        using var notionApiClient = notion.CreateClient(notionToken, dbId);
        var pages = await notion.CreateFetcher(notionApiClient).FetchRowsAsync(ct);
        var shown = pages.Where(p => isHidden?.Invoke(p.Row) != true).Select(p => (p.Row, p.Completed)).ToList();
        var snapshot = BacklogSnapshot.From(SyncHistory.Notion, DateTimeOffset.Now, shown);
        backlog?.Record(snapshot);
        // Ticks changed in Notion go into Pilewise's list; nothing is written to Notion.
        completed?.Begin(TargetOf(dbId), shown)
            .Commit(shown.Where(p => p.Completed != null).Select(p => (p.Row, p.Completed!.Value)));
        return snapshot;
    }

    public async Task<IReadOnlyList<NotionDuplicateGroup>> FindDuplicatesAsync(
        string? notionToken, string? dbId, CancellationToken ct = default)
    {
        using var notionApiClient = notion.CreateClient(notionToken, dbId);
        var rows = await notion.CreateFetcher(notionApiClient).FetchRowsAsync(ct);
        return NotionDuplicateFinder.Find(rows);
    }

    public async Task MergeDuplicatesAsync(
        string? notionToken, string? dbId, IReadOnlyList<NotionDuplicateGroup> groups,
        IProgress<StoreFetchProgress>? progress = null, CancellationToken ct = default)
    {
        if (groups.Count == 0) return;
        using var notionApiClient = notion.CreateClient(notionToken, dbId);

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

        log.WriteInfo($"Notion: merged {groups.Count} duplicate groups, moved {removed.Count} pages to the trash.");
    }
}

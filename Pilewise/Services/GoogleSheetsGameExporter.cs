using Google.Apis.Drive.v3;
using Google.Apis.Sheets.v4;
using Google.Apis.Sheets.v4.Data;
using Pilewise.Models;
using static Pilewise.Services.GoogleSheetsApi;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Pilewise.Services;

/// <summary>Outcome of a Google Sheets export. <see cref="SpreadsheetUrl"/> is null when the library
/// came back empty and nothing was written.</summary>
public sealed record GoogleSheetsExportResult(
    GameLibraryResult Library,
    int Added,
    int Updated,
    string? SpreadsheetUrl,
    int NoLongerOwned = 0,
    bool DryRun = false);

public sealed class GoogleSheetsGameExporter(
    ISecretStore secrets,
    IGameLibraryService libraryService,
    ILogService log,
    BacklogProgress? backlog = null,
    ISteamLinuxLookup? linux = null,
    CompletedGames? completed = null)
{
    /// <param name="dryRun">Plan only: nothing is written and a missing spreadsheet isn't created
    /// (it's planned as empty). The counts in the result are what a real run would do.</param>
    public async Task<GoogleSheetsExportResult> ExportAsync(
        string? tableName, string? steamApiKey, string? steamId,
        IProgress<StoreFetchProgress>? progress = null, Action<string>? onStatus = null,
        CancellationToken ct = default, bool dryRun = false)
    {
        if (string.IsNullOrWhiteSpace(tableName))
            throw new InvalidOperationException("Enter the Google Sheets table name first.");
        var spreadsheetName = tableName.Trim();

        // Fetch first: fails fast on a bad Steam key before the Google login opens a browser.
        var library = await libraryService.FetchAsync(steamApiKey, steamId, progress, onStatus, ct);
        if (library.Rows.Count == 0)
            return new GoogleSheetsExportResult(library, 0, 0, null);

        onStatus?.Invoke("Connecting to Google…");
        using var client = new GoogleSheetsApiClient(secrets, log);
        if (!await client.ConnectAsync(ct) || client.DriveService == null || client.SheetsService == null)
            throw new InvalidOperationException(
                "Google authorization failed. Please try again; details are in the log.");

        var sheets = client.SheetsService;
        onStatus?.Invoke("Exporting to Google Sheets…");

        var spreadsheetId = await FindSpreadsheetAsync(client.DriveService, spreadsheetName, ct);
        if (dryRun && spreadsheetId == null)
        {
            var planPreview = await PlanAsync(library, null, spreadsheetName, progress, onStatus, ct);
            return new GoogleSheetsExportResult(library, planPreview.NewRows.Count, 0, null, 0, true);
        }

        spreadsheetId ??= await CreateSpreadsheetAsync(client.DriveService, spreadsheetName, ct);

        // The first tab, by id and title: never assume "Sheet1" (it's localised, e.g. "Лист1").
        var getRequest = sheets.Spreadsheets.Get(spreadsheetId);
        getRequest.Fields = "spreadsheetUrl,sheets.properties(sheetId,title,gridProperties(rowCount,columnCount))";
        var spreadsheet = await getRequest.ExecuteAsync(ct);
        var sheet = spreadsheet.Sheets[0].Properties;
        var sheetId = sheet.SheetId ?? 0;
        var sheetRef = QuoteSheetTitle(sheet.Title);

        // FORMULA: plain cells come back unformatted (numbers such as Playtime stay numbers), and a
        // Cover cell returns its =IMAGE(…) formula, so a filled cover is recognised (its value is empty).
        var getValues = sheets.Spreadsheets.Values.Get(spreadsheetId, sheetRef);
        getValues.ValueRenderOption =
            SpreadsheetsResource.ValuesResource.GetRequest.ValueRenderOptionEnum.FORMULA;
        var existing = await getValues.ExecuteAsync(ct);
        var plan = await PlanAsync(library, existing.Values, spreadsheetName, progress, onStatus, ct);
        if (dryRun)
            return new GoogleSheetsExportResult(library, plan.NewRows.Count, plan.Updates.Count,
                spreadsheet.SpreadsheetUrl, plan.NoLongerOwned.Count, true);

        await ApplyStructureAsync(sheets, spreadsheetId, sheetId, sheet.GridProperties, plan, ct);
        await WriteValuesAsync(sheets, spreadsheetId, sheetRef, plan, ct);
        await WriteCoversAsync(sheets, spreadsheetId, sheetRef, plan, ct);
        await ApplyCheckboxesAsync(sheets, spreadsheetId, sheetId, plan, ct);
        backlog?.Record(BacklogSnapshot.From(SyncHistory.GoogleSheets, DateTimeOffset.Now, plan.Tracked ?? []));
        plan.Completion?.Commit((plan.Tracked ?? []).Where(t => t.Completed != null)
            .Select(t => (t.Row, t.Completed!.Value)));

        log.WriteInfo(
            $"Google Sheets: added {plan.NewRows.Count}, updated {plan.Updates.Count} games in \"{spreadsheetName}\".");
        return new GoogleSheetsExportResult(
            library, plan.NewRows.Count, plan.Updates.Count, spreadsheet.SpreadsheetUrl, plan.NoLongerOwned.Count);
    }

    public async Task<BacklogSnapshot?> ReadBacklogAsync(
        string? tableName, Func<CsvGameExportRow, bool>? isHidden = null, CancellationToken ct = default)
    {
        using var sheet = await OpenExistingAsync(tableName, ct);
        var contents = GoogleSheetsSyncPlanner.Read(sheet.Values);
        var shown = contents.Rows.Where(r => isHidden?.Invoke(r.Row) != true).Select(r => (r.Row, r.Completed))
            .ToList();
        var snapshot = BacklogSnapshot.From(SyncHistory.GoogleSheets, DateTimeOffset.Now, shown);
        backlog?.Record(snapshot);
        // Ticks changed in the sheet go into Pilewise's list; nothing is written to the sheet.
        completed?.Begin(TargetOf(tableName), shown)
            .Commit(shown.Where(r => r.Completed != null).Select(r => (r.Row, r.Completed!.Value)));
        return snapshot;
    }

    public async Task<IReadOnlyList<SheetDuplicateGroup>> FindDuplicatesAsync(string? tableName,
        CancellationToken ct = default)
    {
        using var sheet = await OpenExistingAsync(tableName, ct);
        var contents = GoogleSheetsSyncPlanner.Read(sheet.Values);
        if (contents.NeedsUpgrade)
            throw new InvalidOperationException(
                "This sheet still has the old layout. Export to Google Sheets once (it upgrades the sheet), then search again.");
        return SheetDuplicateFinder.Find(contents.Rows);
    }

    /// <summary>
    /// Merges each group into its kept row (store tags, ids, and the Completed tick of any copy) and
    /// deletes the other rows. The sheet is re-read first: if any of those rows moved or changed since
    /// the search, nothing is written. Google Sheets' version history can undo the merge.
    /// </summary>
    public async Task MergeDuplicatesAsync(
        string? tableName, IReadOnlyList<SheetDuplicateGroup> groups, CancellationToken ct = default)
    {
        if (groups.Count == 0) return;

        using var sheet = await OpenExistingAsync(tableName, ct);
        var contents = GoogleSheetsSyncPlanner.Read(sheet.Values);
        if (contents.NeedsUpgrade || !SheetDuplicateFinder.StillMatches(contents, groups))
            throw new InvalidOperationException(
                "The sheet changed since the search (rows were added, moved or edited). Run Find duplicates again.");

        var columns = contents.Columns;
        var data = new List<ValueRange>();
        foreach (var group in groups)
        {
            data.Add(CellValue(sheet.SheetRef, columns.Platform, group.Keep.RowIndex, group.Merged.PlatformText));
            data.Add(CellValue(sheet.SheetRef, columns.Id, group.Keep.RowIndex, group.Merged.IdText));
            if (group.CarriesCompleted)
                data.Add(CellValue(sheet.SheetRef, columns.Completed, group.Keep.RowIndex, true));
        }

        await sheet.Sheets.Spreadsheets.Values
            .BatchUpdate(new BatchUpdateValuesRequest { ValueInputOption = "RAW", Data = data }, sheet.SpreadsheetId)
            .ExecuteAsync(ct);

        // Bottom-up, so deleting a row never shifts one that's still to be deleted.
        var deletes = groups.SelectMany(g => g.Remove).Select(r => r.RowIndex).Distinct().OrderDescending()
            .Select(index => new Request
            {
                DeleteDimension = new DeleteDimensionRequest
                {
                    Range = new DimensionRange
                    {
                        SheetId = sheet.SheetId, Dimension = "ROWS", StartIndex = index, EndIndex = index + 1
                    }
                }
            })
            .ToList();
        await sheet.Sheets.Spreadsheets
            .BatchUpdate(new BatchUpdateSpreadsheetRequest { Requests = deletes }, sheet.SpreadsheetId)
            .ExecuteAsync(ct);

        log.WriteInfo($"Google Sheets: merged {groups.Count} duplicate games, deleted {deletes.Count} rows.");
    }

    public static string TargetOf(string? tableName)
    {
        return $"{SyncHistory.GoogleSheets}:{tableName?.Trim()}";
    }

    private async Task<GoogleSheetsSyncPlan> PlanAsync(
        GameLibraryResult library, IList<IList<object>>? values, string spreadsheetName,
        IProgress<StoreFetchProgress>? progress, Action<string>? onStatus, CancellationToken ct)
    {
        IReadOnlyDictionary<int, string>? labels = null;
        if (linux is { Enabled: true })
        {
            onStatus?.Invoke("Checking Linux support…");
            var ids = library.Rows.Select(r => r.SteamGameId).OfType<int>();
            if (values != null)
                ids = ids.Concat(GoogleSheetsSyncPlanner.Read(values).Rows.Select(r => r.Row.SteamGameId)
                    .OfType<int>());
            labels = await linux.LookupAsync(ids, progress, ct);
        }

        return GoogleSheetsSyncPlanner.Plan(
            values, library.Rows, library.FetchedStores, library.Ignored,
            labels != null, labels,
            completed, TargetOf(spreadsheetName));
    }

    // The first tab of an existing spreadsheet, with its values; throws when it doesn't exist yet.
    // The caller disposes the result (it owns the Google client).
    private async Task<OpenedSheet> OpenExistingAsync(string? tableName, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(tableName))
            throw new InvalidOperationException("Enter the Google Sheets table name first.");
        var name = tableName.Trim();

        var client = new GoogleSheetsApiClient(secrets, log);
        try
        {
            if (!await client.ConnectAsync(ct) || client.DriveService == null || client.SheetsService == null)
                throw new InvalidOperationException(
                    "Google authorization failed. Please try again; details are in the log.");

            var spreadsheetId = await FindSpreadsheetAsync(client.DriveService, name, ct) ??
                                throw new InvalidOperationException(
                                    $"There's no spreadsheet named \"{name}\" in your Google Drive yet. Export to it first.");

            var sheets = client.SheetsService;
            var getRequest = sheets.Spreadsheets.Get(spreadsheetId);
            getRequest.Fields = "spreadsheetUrl,sheets.properties(sheetId,title)";
            var spreadsheet = await getRequest.ExecuteAsync(ct);
            var properties = spreadsheet.Sheets[0].Properties;
            var sheetRef = QuoteSheetTitle(properties.Title);

            var getValues = sheets.Spreadsheets.Values.Get(spreadsheetId, sheetRef);
            getValues.ValueRenderOption =
                SpreadsheetsResource.ValuesResource.GetRequest.ValueRenderOptionEnum.FORMULA;
            var values = await getValues.ExecuteAsync(ct);
            return new OpenedSheet(client, sheets, spreadsheetId, properties.SheetId ?? 0, sheetRef, values.Values);
        }
        catch
        {
            client.Dispose();
            throw;
        }
    }

    private sealed record OpenedSheet(
        GoogleSheetsApiClient Client,
        SheetsService Sheets,
        string SpreadsheetId,
        int SheetId,
        string SheetRef,
        IList<IList<object>>? Values) : IDisposable
    {
        public void Dispose()
        {
            Client.Dispose();
        }
    }
}
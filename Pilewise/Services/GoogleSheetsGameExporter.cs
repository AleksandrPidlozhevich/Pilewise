using Google.Apis.Drive.v3;
using Google.Apis.Sheets.v4;
using Google.Apis.Sheets.v4.Data;
using Pilewise.Models;
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

/// <summary>
/// Syncs the merged Steam/Epic/GOG/Xbox library into a Google Sheet (found by name in Drive, created
/// if missing): new games are appended, existing rows updated in place. The reconcile decisions live
/// in <see cref="GoogleSheetsSyncPlanner"/>; this class only talks to Google. Beta / may be unstable.
/// </summary>
public sealed class GoogleSheetsGameExporter
{
    private readonly ISecretStore _secrets;
    private readonly IGameLibraryService _library;
    private readonly ILogService _log;
    private readonly BacklogProgress? _backlog;

    public GoogleSheetsGameExporter(
        ISecretStore secrets, IGameLibraryService library, ILogService log, BacklogProgress? backlog = null)
    {
        _backlog = backlog;
        _secrets = secrets;
        _library = library;
        _log = log;
    }

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
        var library = await _library.FetchAsync(steamApiKey, steamId, progress, onStatus, ct);
        if (library.Rows.Count == 0)
            return new GoogleSheetsExportResult(library, 0, 0, null);

        onStatus?.Invoke("Connecting to Google…");
        using var client = new GoogleSheetsApiClient(_secrets, _log);
        if (!await client.ConnectAsync(ct) || client.DriveService == null || client.SheetsService == null)
            throw new InvalidOperationException(
                "Google authorization failed. Please try again; details are in the log.");

        var sheets = client.SheetsService;
        onStatus?.Invoke("Exporting to Google Sheets…");

        var spreadsheetId = await FindSpreadsheetAsync(client.DriveService, spreadsheetName, ct);
        if (dryRun && spreadsheetId == null)
        {
            var fresh = GoogleSheetsSyncPlanner.Plan(null, library.Rows, library.FetchedStores);
            return new GoogleSheetsExportResult(library, fresh.NewRows.Count, 0, null, 0, DryRun: true);
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
        var plan = GoogleSheetsSyncPlanner.Plan(existing.Values, library.Rows, library.FetchedStores, library.Ignored);
        if (dryRun)
            return new GoogleSheetsExportResult(library, plan.NewRows.Count, plan.Updates.Count,
                spreadsheet.SpreadsheetUrl, plan.NoLongerOwned.Count, DryRun: true);

        await ApplyStructureAsync(sheets, spreadsheetId, sheetId, sheet.GridProperties, plan, ct);
        await WriteValuesAsync(sheets, spreadsheetId, sheetRef, plan, ct);
        await WriteCoversAsync(sheets, spreadsheetId, sheetRef, plan, ct);
        await ApplyCheckboxesAsync(sheets, spreadsheetId, sheetId, plan, ct);
        _backlog?.Record(BacklogSnapshot.From(
            SyncHistory.GoogleSheets, DateTimeOffset.Now, plan.Tracked ?? [], plan.NewRows.Count));

        _log.WriteInfo(
            $"Google Sheets: added {plan.NewRows.Count}, updated {plan.Updates.Count} games in \"{spreadsheetName}\".");
        return new GoogleSheetsExportResult(
            library, plan.NewRows.Count, plan.Updates.Count, spreadsheet.SpreadsheetUrl, plan.NoLongerOwned.Count);
    }

    // ---- read-only and maintenance operations on an existing sheet ---------------------------

    /// <summary>
    /// Read-only: reads the Completed ticks of the sheet and records them as the Google Sheets backlog
    /// snapshot (the library viewer's ✓ marks), without fetching any store or writing.
    /// </summary>
    /// <param name="isHidden">Games hidden in the library viewer, left out of the counts.</param>
    public async Task<BacklogSnapshot?> ReadBacklogAsync(
        string? tableName, Func<CsvGameExportRow, bool>? isHidden = null, CancellationToken ct = default)
    {
        using var sheet = await OpenExistingAsync(tableName, ct);
        var contents = GoogleSheetsSyncPlanner.Read(sheet.Values);
        var snapshot = BacklogSnapshot.From(SyncHistory.GoogleSheets, DateTimeOffset.Now,
            contents.Rows.Where(r => isHidden?.Invoke(r.Row) != true).Select(r => (r.Row, r.Completed)));
        _backlog?.Record(snapshot);
        return snapshot;
    }

    /// <summary>Read-only: finds rows of the same game in the sheet.</summary>
    public async Task<IReadOnlyList<SheetDuplicateGroup>> FindDuplicatesAsync(string? tableName, CancellationToken ct = default)
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

        _log.WriteInfo($"Google Sheets: merged {groups.Count} duplicate games, deleted {deletes.Count} rows.");
    }

    // The first tab of an existing spreadsheet, with its values; throws when it doesn't exist yet.
    // The caller disposes the result (it owns the Google client).
    private async Task<OpenedSheet> OpenExistingAsync(string? tableName, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(tableName))
            throw new InvalidOperationException("Enter the Google Sheets table name first.");
        var name = tableName.Trim();

        var client = new GoogleSheetsApiClient(_secrets, _log);
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
        GoogleSheetsApiClient Client, SheetsService Sheets, string SpreadsheetId, int SheetId, string SheetRef,
        IList<IList<object>>? Values) : IDisposable
    {
        public void Dispose() => Client.Dispose();
    }

    // The spreadsheet with this name in the user's Drive, or null.
    private static async Task<string?> FindSpreadsheetAsync(DriveService drive, string name, CancellationToken ct)
    {
        var listRequest = drive.Files.List();
        // Drive query string literal: backslash and single quote must be escaped.
        listRequest.Q =
            "mimeType='application/vnd.google-apps.spreadsheet' and " +
            $"name='{name.Replace("\\", "\\\\").Replace("'", "\\'")}' and trashed = false";
        listRequest.Spaces = "drive";
        listRequest.Fields = "files(id, name)";
        var fileList = await listRequest.ExecuteAsync(ct);

        return fileList.Files is { Count: > 0 } ? fileList.Files[0].Id : null;
    }

    private static async Task<string> CreateSpreadsheetAsync(DriveService drive, string name, CancellationToken ct)
    {
        var fileMetadata = new Google.Apis.Drive.v3.Data.File
        {
            Name = name,
            MimeType = "application/vnd.google-apps.spreadsheet"
        };
        var file = await drive.Files.Create(fileMetadata).ExecuteAsync(ct);
        return file.Id;
    }

    // Migrates old layouts (insert Platform column / header row) and grows the grid so the value
    // writes below fit — values.batchUpdate fails with "exceeds grid limits" instead of expanding.
    private static async Task ApplyStructureAsync(
        SheetsService sheets, string spreadsheetId, int sheetId, GridProperties? grid,
        GoogleSheetsSyncPlan plan, CancellationToken ct)
    {
        var requests = new List<Request>();
        if (plan.InsertPlatformColumn)
            requests.Add(Insert(sheetId, "COLUMNS"));
        if (plan.InsertHeaderRow)
            requests.Add(Insert(sheetId, "ROWS"));

        var rowCount = (grid?.RowCount ?? 0) + (plan.InsertHeaderRow ? 1 : 0);
        var columnCount = (grid?.ColumnCount ?? 0) + (plan.InsertPlatformColumn ? 1 : 0);
        if (plan.TotalRows > rowCount)
            requests.Add(Append(sheetId, "ROWS", plan.TotalRows - rowCount));
        if (plan.Columns.Width > columnCount)
            requests.Add(Append(sheetId, "COLUMNS", plan.Columns.Width - columnCount));

        if (requests.Count > 0)
            await sheets.Spreadsheets
                .BatchUpdate(new BatchUpdateSpreadsheetRequest { Requests = requests }, spreadsheetId)
                .ExecuteAsync(ct);

        static Request Insert(int sheetId, string dimension) => new()
        {
            InsertDimension = new InsertDimensionRequest
            {
                Range = new DimensionRange { SheetId = sheetId, Dimension = dimension, StartIndex = 0, EndIndex = 1 }
            }
        };

        static Request Append(int sheetId, string dimension, int length) => new()
        {
            AppendDimension = new AppendDimensionRequest { SheetId = sheetId, Dimension = dimension, Length = length }
        };
    }

    // Header cells, changed Platform/ID cells and the new rows, all in one RAW write (RAW so ids
    // like "Steam:400" and names starting with "=" are stored verbatim).
    private static async Task WriteValuesAsync(
        SheetsService sheets, string spreadsheetId, string sheetRef, GoogleSheetsSyncPlan plan,
        CancellationToken ct)
    {
        var data = new List<ValueRange>();

        foreach (var cell in plan.HeaderCells)
            data.Add(CellValue(sheetRef, cell.Column, 0, cell.Text));

        foreach (var update in plan.Updates)
        {
            data.Add(CellValue(sheetRef, plan.Columns.Platform, update.RowIndex, update.Platform));
            data.Add(CellValue(sheetRef, plan.Columns.Id, update.RowIndex, update.Id));
            if (update.PlaytimeHours is { } hours)
                data.Add(CellValue(sheetRef, plan.Columns.Playtime, update.RowIndex, hours));
            if (update.LastPlayed is { } lastPlayed)
                data.Add(CellValue(sheetRef, plan.Columns.LastPlayed, update.RowIndex, lastPlayed));
            if (update.Owned is { } owned)
                data.Add(CellValue(sheetRef, plan.Columns.Owned, update.RowIndex, owned));
            if (update.StoreLink is { } link)
                data.Add(CellValue(sheetRef, plan.Columns.StoreLink, update.RowIndex, link));
        }

        if (plan.NewRows.Count > 0)
        {
            data.Add(new ValueRange
            {
                Range = $"{sheetRef}!A{plan.FirstNewRowIndex + 1}",
                Values = plan.NewRows.Select(row => BuildRow(plan.Columns, row)).ToList()
            });
        }

        if (data.Count == 0) return;

        await sheets.Spreadsheets.Values
            .BatchUpdate(new BatchUpdateValuesRequest { ValueInputOption = "RAW", Data = data }, spreadsheetId)
            .ExecuteAsync(ct);
    }

    // A full-width row; cells outside the Pilewise columns stay null, which the API skips.
    private static IList<object> BuildRow(SheetColumns columns, CsvGameExportRow row)
    {
        var cells = new object[columns.Width];
        cells[columns.Platform] = row.PlatformText;
        cells[columns.Name] = row.Name;
        cells[columns.Id] = row.IdText;
        cells[columns.Completed] = false;
        if (columns.Playtime >= 0 && row.PlaytimeHours is { } hours) cells[columns.Playtime] = hours;
        if (columns.LastPlayed >= 0 && row.LastPlayedDate is { } lastPlayed) cells[columns.LastPlayed] = lastPlayed;
        if (columns.Owned >= 0) cells[columns.Owned] = true;
        if (columns.StoreLink >= 0 && row.StoreUrl is { } link) cells[columns.StoreLink] = link;
        return cells;
    }

    // Cover images as =IMAGE(…) formulas: a separate USER_ENTERED write, since the RAW write above
    // stores text verbatim (on purpose, for names and ids).
    private static async Task WriteCoversAsync(
        SheetsService sheets, string spreadsheetId, string sheetRef, GoogleSheetsSyncPlan plan, CancellationToken ct)
    {
        if (plan.Columns.Cover < 0) return;

        var data = plan.Updates
            .Where(u => u.CoverUrl != null)
            .Select(u => CellValue(sheetRef, plan.Columns.Cover, u.RowIndex, GoogleSheetsSyncPlanner.CoverFormula(u.CoverUrl!)))
            .Concat(plan.NewRows
                .Select((row, i) => (row.CoverUrl, Index: plan.FirstNewRowIndex + i))
                .Where(c => c.CoverUrl != null)
                .Select(c => CellValue(sheetRef, plan.Columns.Cover, c.Index, GoogleSheetsSyncPlanner.CoverFormula(c.CoverUrl!))))
            .ToList();
        if (data.Count == 0) return;

        await sheets.Spreadsheets.Values
            .BatchUpdate(new BatchUpdateValuesRequest { ValueInputOption = "USER_ENTERED", Data = data }, spreadsheetId)
            .ExecuteAsync(ct);
    }

    // Completed (and the opt-in Owned column) as checkboxes for every data row — idempotent, and
    // also covers a just-added column.
    private static async Task ApplyCheckboxesAsync(
        SheetsService sheets, string spreadsheetId, int sheetId, GoogleSheetsSyncPlan plan, CancellationToken ct)
    {
        if (plan.TotalRows <= 1) return;

        var columns = new[] { plan.Columns.Completed, plan.Columns.Owned }.Where(c => c >= 0);
        var requests = columns.Select(column => new Request
        {
            SetDataValidation = new SetDataValidationRequest
            {
                Range = new GridRange
                {
                    SheetId = sheetId,
                    StartRowIndex = 1,
                    EndRowIndex = plan.TotalRows,
                    StartColumnIndex = column,
                    EndColumnIndex = column + 1
                },
                Rule = new DataValidationRule
                {
                    Condition = new BooleanCondition { Type = "BOOLEAN" },
                    Strict = true,
                    ShowCustomUi = true
                }
            }
        }).ToList();

        await sheets.Spreadsheets
            .BatchUpdate(new BatchUpdateSpreadsheetRequest { Requests = requests }, spreadsheetId)
            .ExecuteAsync(ct);
    }

    private static ValueRange CellValue(string sheetRef, int column, int row, object value) => new()
    {
        Range = $"{sheetRef}!{ColumnLetter(column)}{row + 1}",
        Values = [new List<object> { value }]
    };

    // A1 column letters for a 0-based index: 0 → A, 25 → Z, 26 → AA.
    private static string ColumnLetter(int index)
    {
        var letters = string.Empty;
        for (var n = index + 1; n > 0; n = (n - 1) / 26)
            letters = (char)('A' + (n - 1) % 26) + letters;
        return letters;
    }

    // A1 sheet reference: always quoted, with embedded quotes doubled.
    private static string QuoteSheetTitle(string title) => $"'{title.Replace("'", "''")}'";
}

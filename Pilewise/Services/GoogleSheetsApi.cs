using Google.Apis.Drive.v3;
using Google.Apis.Sheets.v4;
using Google.Apis.Sheets.v4.Data;
using Pilewise.Models;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Pilewise.Services;

/// <summary>The Drive / Sheets calls behind the export: find or create the spreadsheet, grow the grid, write cells.</summary>
internal static class GoogleSheetsApi
{
    internal static async Task<string?> FindSpreadsheetAsync(DriveService drive, string name, CancellationToken ct)
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

    internal static async Task<string> CreateSpreadsheetAsync(DriveService drive, string name, CancellationToken ct)
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
    internal static async Task ApplyStructureAsync(
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

        static Request Insert(int sheetId, string dimension)
        {
            return new Request
            {
                InsertDimension = new InsertDimensionRequest
                {
                    Range = new DimensionRange
                        { SheetId = sheetId, Dimension = dimension, StartIndex = 0, EndIndex = 1 }
                }
            };
        }

        static Request Append(int sheetId, string dimension, int length)
        {
            return new Request
            {
                AppendDimension = new AppendDimensionRequest
                    { SheetId = sheetId, Dimension = dimension, Length = length }
            };
        }
    }

    // Header cells, changed Platform/ID cells and the new rows, all in one RAW write (RAW so ids
    // like "Steam:400" and names starting with "=" are stored verbatim).
    internal static async Task WriteValuesAsync(
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
            if (update.Linux is { } linux)
                data.Add(CellValue(sheetRef, plan.Columns.Linux, update.RowIndex, linux));
            if (update.Completed is { } completed)
                data.Add(CellValue(sheetRef, plan.Columns.Completed, update.RowIndex, completed));
        }

        if (plan.NewRows.Count > 0)
            data.Add(new ValueRange
            {
                Range = $"{sheetRef}!A{plan.FirstNewRowIndex + 1}",
                Values = plan.NewRows.Select((row, i) => BuildRow(plan.Columns, row, plan.NewRowCompleted(i))).ToList()
            });

        if (data.Count == 0) return;

        await sheets.Spreadsheets.Values
            .BatchUpdate(new BatchUpdateValuesRequest { ValueInputOption = "RAW", Data = data }, spreadsheetId)
            .ExecuteAsync(ct);
    }

    // A full-width row; cells outside the Pilewise columns stay null, which the API skips.
    internal static IList<object> BuildRow(SheetColumns columns, CsvGameExportRow row, bool completed)
    {
        var cells = new object[columns.Width];
        cells[columns.Platform] = row.PlatformText;
        cells[columns.Name] = row.Name;
        cells[columns.Id] = row.IdText;
        cells[columns.Completed] = completed;
        if (columns.Playtime >= 0 && row.PlaytimeHours is { } hours) cells[columns.Playtime] = hours;
        if (columns.LastPlayed >= 0 && row.LastPlayedDate is { } lastPlayed) cells[columns.LastPlayed] = lastPlayed;
        if (columns.Owned >= 0) cells[columns.Owned] = true;
        if (columns.StoreLink >= 0 && row.StoreUrl is { } link) cells[columns.StoreLink] = link;
        if (columns.Linux >= 0) cells[columns.Linux] = row.Linux ?? LinuxRating.Unknown;
        return cells;
    }

    // Cover images as =IMAGE(…) formulas: a separate USER_ENTERED write, since the RAW write above
    // stores text verbatim (on purpose, for names and ids).
    internal static async Task WriteCoversAsync(
        SheetsService sheets, string spreadsheetId, string sheetRef, GoogleSheetsSyncPlan plan, CancellationToken ct)
    {
        if (plan.Columns.Cover < 0) return;

        var data = plan.Updates
            .Where(u => u.CoverUrl != null)
            .Select(u => CellValue(sheetRef, plan.Columns.Cover, u.RowIndex,
                GoogleSheetsSyncPlanner.CoverFormula(u.CoverUrl!)))
            .Concat(plan.NewRows
                .Select((row, i) => (row.CoverUrl, Index: plan.FirstNewRowIndex + i))
                .Where(c => c.CoverUrl != null)
                .Select(c => CellValue(sheetRef, plan.Columns.Cover, c.Index,
                    GoogleSheetsSyncPlanner.CoverFormula(c.CoverUrl!))))
            .ToList();
        if (data.Count == 0) return;

        await sheets.Spreadsheets.Values
            .BatchUpdate(new BatchUpdateValuesRequest { ValueInputOption = "USER_ENTERED", Data = data }, spreadsheetId)
            .ExecuteAsync(ct);
    }

    // Completed (and the opt-in Owned column) as checkboxes for every data row — idempotent, and
    // also covers a just-added column.
    internal static async Task ApplyCheckboxesAsync(
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

    internal static ValueRange CellValue(string sheetRef, int column, int row, object value)
    {
        return new ValueRange
        {
            Range = $"{sheetRef}!{ColumnLetter(column)}{row + 1}",
            Values = [new List<object> { value }]
        };
    }

    internal static string ColumnLetter(int index)
    {
        var letters = string.Empty;
        for (var n = index + 1; n > 0; n = (n - 1) / 26)
            letters = (char)('A' + (n - 1) % 26) + letters;
        return letters;
    }

    internal static string QuoteSheetTitle(string title)
    {
        return $"'{title.Replace("'", "''")}'";
    }
}
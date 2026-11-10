using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace Pilewise.Services;

public sealed record SheetColumns(
    int Platform,
    int Name,
    int Id,
    int Completed,
    int Playtime = -1,
    int LastPlayed = -1,
    int Owned = -1,
    int StoreLink = -1,
    int Cover = -1,
    int Linux = -1)
{
    public int Width =>
        new[] { Platform, Name, Id, Completed, Playtime, LastPlayed, Owned, StoreLink, Cover, Linux }.Max() + 1;
}

public sealed record SheetRow(
    int RowIndex,
    CsvGameExportRow Row,
    string StoredPlatform,
    string StoredId,
    bool? Completed);

public sealed record SheetContents(SheetColumns Columns, bool NeedsUpgrade, IReadOnlyList<SheetRow> Rows);

public sealed record SheetHeaderCell(int Column, string Text);

public sealed record SheetRowUpdate(
    int RowIndex,
    string Platform,
    string Id,
    double? PlaytimeHours = null,
    string? LastPlayed = null,
    bool? Owned = null,
    string? StoreLink = null,
    string? CoverUrl = null,
    string? Linux = null,
    bool? Completed = null);

public sealed record GoogleSheetsSyncPlan(
    bool InsertPlatformColumn,
    bool InsertHeaderRow,
    SheetColumns Columns,
    IReadOnlyList<SheetHeaderCell> HeaderCells,
    IReadOnlyList<SheetRowUpdate> Updates,
    IReadOnlyList<CsvGameExportRow> NewRows,
    int FirstNewRowIndex,
    IReadOnlyList<string> NoLongerOwned,
    IReadOnlyList<(CsvGameExportRow Row, bool? Completed)>? Tracked = null,
    IReadOnlyList<bool>? NewRowsCompleted = null,
    CompletionSync? Completion = null)
{
    public bool NewRowCompleted(int index)
    {
        return NewRowsCompleted is { } ticks && index < ticks.Count && ticks[index];
    }

    public int TotalRows => FirstNewRowIndex + NewRows.Count;
}

public static class GoogleSheetsSyncPlanner
{
    public const string PlatformHeader = "Platform";
    public const string NameHeader = "Name";
    public const string IdHeader = "ID";
    public const string CompletedHeader = "Completed";
    public const string PlaytimeHeader = "Playtime";
    public const string LastPlayedHeader = "Last played";
    public const string OwnedHeader = "Owned";
    public const string StoreLinkHeader = "Store link";
    public const string CoverHeader = "Cover";
    public const string LinuxHeader = LinuxRating.Column;

    public static string CoverFormula(string url)
    {
        return $"=IMAGE(\"{url.Replace("\"", "\"\"")}\")";
    }

    private static readonly string[] IdHeaders = [IdHeader, "Game ID", "GameID"];

    public static GoogleSheetsSyncPlan Plan(
        IList<IList<object>>? values, IEnumerable<CsvGameExportRow> incoming,
        IReadOnlySet<string>? fetchedStores = null, IEnumerable<CsvGameExportRow>? ignored = null,
        bool includeLinux = false, IReadOnlyDictionary<int, string>? linuxByAppId = null,
        CompletedGames? completed = null, string? target = null)
    {
        fetchedStores ??= new HashSet<string>();
        values ??= [];
        var layout = DetectLayout(values, includeLinux);
        var rowShift = layout.InsertHeaderRow ? 1 : 0;

        var existing = ReadExistingRows(values, layout, rowShift);
        var matcher = new CsvGameExportService.RowMatcher(existing.Select(e => e.Row));
        var newRows = new List<CsvGameExportRow>();
        var matched = new HashSet<CsvGameExportRow>(ReferenceEqualityComparer.Instance);
        var hiddenRows = new HashSet<CsvGameExportRow>(ReferenceEqualityComparer.Instance);

        if (includeLinux)
            LinuxRating.Apply(incoming, linuxByAppId ?? new Dictionary<int, string>());

        foreach (var row in incoming)
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

        foreach (var row in ignored ?? [])
            if (matcher.Match(row) is { } hidden)
            {
                matched.Add(hidden);
                hiddenRows.Add(hidden);
            }

        if (includeLinux)
            LinuxRating.Apply(
                existing.Where(e => !hiddenRows.Contains(e.Row)).Select(e => e.Row),
                linuxByAppId ?? new Dictionary<int, string>());

        var tracked = existing.Where(e => !hiddenRows.Contains(e.Row)).ToList();
        var completion = completed?.Begin(target ?? SyncHistory.GoogleSheets,
            tracked.Select(e => (e.Row, e.StoredCompleted)));

        var columns = layout.Columns;
        var updates = new List<SheetRowUpdate>();
        var noLongerOwned = new List<string>();
        foreach (var e in existing)
        {
            var ownership = CsvGameExportService.Ownership(e.Row, matched.Contains(e.Row), fetchedStores);
            if (ownership == false) noLongerOwned.Add(e.Row.Name);

            var hours = columns.Playtime >= 0 ? e.Row.PlaytimeHours : null;
            var lastPlayed = columns.LastPlayed >= 0 ? e.Row.LastPlayedDate : null;
            var owned = columns.Owned >= 0 ? ownership : null;
            var fillable = !hiddenRows.Contains(e.Row);
            var link = fillable && columns.StoreLink >= 0 && e.StoredLink.Length == 0 ? e.Row.StoreUrl : null;
            var cover = fillable && columns.Cover >= 0 && e.StoredCover.Length == 0 ? e.Row.CoverUrl : null;
            var linuxWrite = fillable && includeLinux && e.Row.Linux is { } linux && linux != e.StoredLinux
                ? linux
                : null;
            var completedWrite = fillable && completion != null &&
                                 completion.IsCompleted(e.Row) != (e.StoredCompleted ?? false)
                ? completion.IsCompleted(e.Row)
                : (bool?)null;
            var changed = completedWrite != null ||
                          e.Row.PlatformText != e.StoredPlatform || e.Row.IdText != e.StoredId ||
                          (hours != null && hours != e.StoredPlaytime) ||
                          (lastPlayed != null && lastPlayed != e.StoredLastPlayed) ||
                          (owned != null && owned != e.StoredOwned) ||
                          link != null || cover != null || linuxWrite != null;
            if (changed)
                updates.Add(new SheetRowUpdate(
                    e.RowIndex, e.Row.PlatformText, e.Row.IdText, hours, lastPlayed, owned, link, cover, linuxWrite,
                    completedWrite));
        }

        var orderedNew = newRows.OrderBy(r => r.Name, StringComparer.OrdinalIgnoreCase).ToList();
        var newTicks = orderedNew.Select(r => completion?.IsCompleted(r) ?? false).ToList();
        return new GoogleSheetsSyncPlan(
            layout.InsertPlatformColumn,
            layout.InsertHeaderRow,
            layout.Columns,
            layout.HeaderCells,
            updates,
            orderedNew,
            Math.Max(values.Count + rowShift, 1),
            noLongerOwned.Order(StringComparer.OrdinalIgnoreCase).ToList(),
            tracked.Select(e => (e.Row, completion != null ? completion.IsCompleted(e.Row) : e.StoredCompleted))
                .Concat(orderedNew.Select((r, i) => (r, completion != null ? newTicks[i] : (bool?)null)))
                .ToList(),
            newTicks,
            completion);
    }

    public static SheetContents Read(IList<IList<object>>? values)
    {
        values ??= [];
        var layout = DetectLayout(values);
        var rows = ReadExistingRows(values, layout, 0)
            .Select(e => new SheetRow(e.RowIndex, e.Row, e.StoredPlatform, e.StoredId, e.StoredCompleted))
            .ToList();
        return new SheetContents(layout.Columns, layout.InsertPlatformColumn || layout.InsertHeaderRow, rows);
    }

    private sealed record Layout(
        bool InsertPlatformColumn,
        bool InsertHeaderRow,
        int OrigPlatform,
        int OrigName,
        int OrigId,
        int FirstDataRow,
        SheetColumns Columns,
        IReadOnlyList<SheetHeaderCell> HeaderCells,
        int OrigPlaytime = -1,
        int OrigLastPlayed = -1,
        int OrigOwned = -1,
        int OrigStoreLink = -1,
        int OrigCover = -1,
        int OrigCompleted = -1,
        int OrigLinux = -1);

    private static Layout DetectLayout(IList<IList<object>> values, bool includeLinux = false)
    {
        var fresh = FreshColumns(includeLinux);

        if (values.Count == 0)
            return new Layout(false, false, -1, -1, -1, 1, fresh, FullHeader(fresh));

        var header = values[0];
        var name = FindColumn(header, NameHeader);
        var id = FindColumn(header, IdHeaders);

        if (name < 0 || id < 0)
        {
            if (header.Count >= 2 && long.TryParse(Cell(header, 1), out _))
                return new Layout(true, true, -1, 0, 1, 0, fresh, FullHeader(fresh), OrigCompleted: 2);

            throw new InvalidOperationException(
                "This spreadsheet doesn't look like a Pilewise sheet: the first row of its first tab " +
                $"must be a header with \"{NameHeader}\" and \"{IdHeader}\" columns. " +
                "Enter a new table name to create a fresh sheet.");
        }

        var platform = FindColumn(header, PlatformHeader);
        var completed = FindColumn(header, CompletedHeader);
        var playtime = FindColumn(header, PlaytimeHeader);
        var lastPlayed = FindColumn(header, LastPlayedHeader);
        var owned = FindColumn(header, OwnedHeader);
        var storeLink = FindColumn(header, StoreLinkHeader);
        var cover = FindColumn(header, CoverHeader);
        var linuxOrig = includeLinux ? FindColumn(header, LinuxHeader) : -1;
        var insertPlatform = platform < 0;
        var shift = insertPlatform ? 1 : 0;

        var completedCol = completed >= 0 ? completed + shift : header.Count + shift;
        // New Linux column sits after Completed so both inserts don't share an index.
        var linuxCol = -1;
        if (includeLinux)
            linuxCol = linuxOrig >= 0 ? linuxOrig + shift : Math.Max(header.Count + shift, completedCol + 1);

        var columns = new SheetColumns(
            insertPlatform ? 0 : platform,
            name + shift,
            id + shift,
            completedCol,
            playtime >= 0 ? playtime + shift : -1,
            lastPlayed >= 0 ? lastPlayed + shift : -1,
            owned >= 0 ? owned + shift : -1,
            storeLink >= 0 ? storeLink + shift : -1,
            cover >= 0 ? cover + shift : -1,
            linuxCol);

        var headerCells = new List<SheetHeaderCell>();
        if (insertPlatform) headerCells.Add(new SheetHeaderCell(columns.Platform, PlatformHeader));
        if (completed < 0) headerCells.Add(new SheetHeaderCell(columns.Completed, CompletedHeader));
        if (includeLinux && linuxOrig < 0) headerCells.Add(new SheetHeaderCell(linuxCol, LinuxHeader));

        return new Layout(insertPlatform, false, platform, name, id, 1, columns, headerCells, playtime, lastPlayed,
            owned, storeLink, cover, completed, linuxOrig);
    }

    private static SheetColumns FreshColumns(bool includeLinux)
    {
        return includeLinux ? new SheetColumns(0, 1, 2, 3, Linux: 4) : new SheetColumns(0, 1, 2, 3);
    }

    private static List<SheetHeaderCell> FullHeader(SheetColumns c)
    {
        var cells = new List<SheetHeaderCell>
        {
            new(c.Platform, PlatformHeader),
            new(c.Name, NameHeader),
            new(c.Id, IdHeader),
            new(c.Completed, CompletedHeader)
        };
        if (c.Linux >= 0) cells.Add(new SheetHeaderCell(c.Linux, LinuxHeader));
        return cells;
    }

    private sealed record ExistingRow(
        int RowIndex,
        CsvGameExportRow Row,
        string StoredPlatform,
        string StoredId,
        double? StoredPlaytime,
        string StoredLastPlayed,
        bool? StoredOwned,
        string StoredLink,
        string StoredCover,
        bool? StoredCompleted,
        string StoredLinux);

    private static List<ExistingRow> ReadExistingRows(IList<IList<object>> values, Layout layout, int rowShift)
    {
        var rows = new List<ExistingRow>();
        for (var r = layout.FirstDataRow; r < values.Count; r++)
        {
            var cells = values[r];
            var platform = layout.OrigPlatform >= 0 ? Cell(cells, layout.OrigPlatform) : string.Empty;
            var name = Cell(cells, layout.OrigName);
            var id = Cell(cells, layout.OrigId);

            var row = CsvFormat.CreateRow(platform, name, id);
            CsvFormat.ApplyLegacyBareId(row, id);
            if (!row.HasContent) continue;
            row.Linux = Cell(cells, layout.OrigLinux);

            rows.Add(new ExistingRow(r + rowShift, row, platform, id,
                Number(cells, layout.OrigPlaytime), Cell(cells, layout.OrigLastPlayed),
                layout.OrigOwned >= 0 && layout.OrigOwned < cells.Count ? cells[layout.OrigOwned] as bool? : null,
                Cell(cells, layout.OrigStoreLink), Cell(cells, layout.OrigCover),
                // An empty Completed cell is "not completed"; no column at all is "unknown".
                layout.OrigCompleted < 0 ? null : Flag(cells, layout.OrigCompleted) ?? false,
                Cell(cells, layout.OrigLinux)));
        }

        return rows;
    }

    private static int FindColumn(IList<object> header, params string[] names)
    {
        for (var i = 0; i < header.Count; i++)
            if (names.Contains(Cell(header, i), StringComparer.OrdinalIgnoreCase))
                return i;
        return -1;
    }

    // A numeric cell, whether it comes back as a number (UNFORMATTED_VALUE) or as text.
    private static double? Number(IList<object> row, int index)
    {
        if (index < 0 || index >= row.Count) return null;
        return row[index] switch
        {
            double d => d,
            long l => l,
            int i => i,
            string s when double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) => v,
            _ => null
        };
    }

    // A checkbox cell: a boolean, or TRUE / FALSE text in sheets typed by hand.
    private static bool? Flag(IList<object> row, int index)
    {
        if (index < 0 || index >= row.Count) return null;
        return row[index] switch
        {
            bool b => b,
            string s when bool.TryParse(s.Trim(), out var v) => v,
            _ => null
        };
    }

    private static string Cell(IList<object> row, int index)
    {
        return index >= 0 && index < row.Count ? row[index]?.ToString()?.Trim() ?? string.Empty : string.Empty;
    }
}
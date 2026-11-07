using Pilewise.Models;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace Pilewise.Services;

public sealed class CsvGameExportRow
{
    public string Name { get; set; } = string.Empty;

    public bool HasSteam { get; set; }

    public bool HasEpic { get; set; }

    public bool HasGog { get; set; }

    public int? SteamGameId { get; set; }

    public string? CatalogItemId { get; set; }

    public string? Namespace { get; set; }

    public long? GogId { get; set; }

    public bool HasXbox { get; set; }

    public string? XboxTitleId { get; set; }

    /// <summary>Xbox title played via Game Pass (played, not owned). Surfaced as a "Game Pass" tag.</summary>
    public bool IsGamePass { get; set; }

    public int? PlaytimeMinutes { get; set; }

    public DateTimeOffset? LastPlayed { get; set; }

    public string? FetchedCoverUrl { get; set; }

    public string? Linux { get; set; }

    public string? FetchedStoreUrl { get; set; }

    /// <summary>The CSV row's other cells as read (column key → text): Completed, the opt-in columns and
    /// the user's own ones. Written back as they are, except where Pilewise fills a cell.</summary>
    public IReadOnlyDictionary<string, string>? Cells { get; set; }

    public string? CoverUrl => SteamGameId is { } id ? StoreLinks.SteamCover(id) : FetchedCoverUrl;

    public string? StoreUrl => SteamGameId is { } id ? StoreLinks.SteamStore(id) : FetchedStoreUrl;

    public double? PlaytimeHours => PlaytimeMinutes is { } m ? Math.Round(m / 60.0, 1) : null;

    public string? LastPlayedDate => LastPlayed?.UtcDateTime.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    public bool HasContent =>
        !string.IsNullOrWhiteSpace(Name) || PlatformCatalog.All.Any(d => d.HasId(this));

    public string IdText => BuildIdField();

    public string PlatformText => BuildPlatformField();

    public string ToCsvLine()
    {
        // Three columns only: Platform, Name, ID. Platform and all identifiers are each
        // collapsed into a single cell using non-comma separators so nothing spills into
        // neighbouring columns when opened in Excel.
        return string.Join(",",
            EscapeCsvField(BuildPlatformField()),
            EscapeCsvField(Name),
            EscapeCsvField(BuildIdField()));
    }

    private string BuildPlatformField()
    {
        var parts = new List<string>();
        foreach (var d in PlatformCatalog.All)
            if (d.Has(this))
                parts.Add(d.Label);
        if (IsGamePass) parts.Add(Platforms.GamePass);
        return string.Join("/", parts);
    }

    private string BuildIdField()
    {
        var parts = new List<string>();
        foreach (var d in PlatformCatalog.All)
        {
            var id = d.FormatId(this);
            if (id != null) parts.Add($"{d.Label}:{id}");
        }

        return string.Join("; ", parts);
    }

    public void MergeFrom(CsvGameExportRow other)
    {
        foreach (var d in PlatformCatalog.All)
        {
            if (d.Has(other)) d.SetHas(this, true);
            d.MergeId(this, other);
        }

        IsGamePass |= other.IsGamePass;

        // Only Steam reports playtime and Steam/Xbox last-played; keep the most recent / largest.
        if (other.PlaytimeMinutes is { } minutes && minutes > (PlaytimeMinutes ?? -1))
            PlaytimeMinutes = minutes;
        if (other.LastPlayed is { } lastPlayed && (LastPlayed == null || lastPlayed > LastPlayed))
            LastPlayed = lastPlayed;

        FetchedCoverUrl ??= other.FetchedCoverUrl;
        FetchedStoreUrl ??= other.FetchedStoreUrl;
        if (!string.IsNullOrEmpty(other.Linux)) Linux = other.Linux;
        Cells ??= other.Cells;

        if (!string.IsNullOrEmpty(other.Name))
            Name = other.Name;
    }

    internal static string EscapeCsvField(string value)
    {
        if (value.AsSpan().IndexOfAny(",\"\r\n") < 0)
            return value;

        return $"\"{value.Replace("\"", "\"\"")}\"";
    }
}

/// <summary>How <see cref="CsvGameExportService.MergeToCsv"/> fills the optional columns.</summary>
/// <param name="FetchedStores">Stores fetched this run, for the opt-in Owned column (null: left as it is).</param>
/// <param name="Ignored">Games hidden in the library viewer: their rows are kept as they are.</param>
/// <param name="Completed">Syncs the Completed ticks (null: each row keeps its own).</param>
/// <param name="Target">The file's sync id (<see cref="CsvGameExportService.TargetOf"/>).</param>
public sealed record CsvMergeOptions(
    bool IncludeLinux = false,
    IReadOnlySet<string>? FetchedStores = null,
    IEnumerable<CsvGameExportRow>? Ignored = null,
    CompletedGames? Completed = null,
    string? Target = null);

public sealed record CsvMergeResult(
    string Content, int Added, int Updated, int Total, bool Changed,
    CompletionSync? Sync, IReadOnlyList<(CsvGameExportRow Row, bool Completed)> Written)
{
    public void Commit() => Sync?.Commit(Written);
}

public static partial class CsvGameExportService
{
    public static string TargetOf(string path) => $"CSV:{System.IO.Path.GetFullPath(path)}";

    public const string Header = "Platform,Name,ID";

    public static IReadOnlyList<string> GetMissingColumns(string? content)
    {
        var headerFields = ReadHeaderFields(content);
        var missing = new List<string>();

        if (!HasColumn(headerFields, "Platform")) missing.Add("Platform");
        if (!HasColumn(headerFields, "Name")) missing.Add("Name");
        if (!HasColumn(headerFields, "ID") && !HasColumn(headerFields, "Game ID")) missing.Add("ID");

        return missing;
    }

    public static bool HasLinuxColumn(string? content) =>
        HasColumn(ReadHeaderFields(content), LinuxRating.Column);

    private static List<string> ReadHeaderFields(string? content)
    {
        if (string.IsNullOrWhiteSpace(content))
            return [];

        var firstLine = content
            .Split(["\r\n", "\r", "\n"], StringSplitOptions.RemoveEmptyEntries)
            .FirstOrDefault();

        return string.IsNullOrWhiteSpace(firstLine)
            ? []
            : ParseCsvFields(firstLine).Select(field => field.Trim()).ToList();
    }

    private static bool HasColumn(List<string> headerFields, string name)
    {
        return headerFields.Any(header => string.Equals(header, name, StringComparison.OrdinalIgnoreCase));
    }

    public static List<CsvGameExportRow> BuildFromLibraries(
        IEnumerable<SteamGame>? steamGames,
        IEnumerable<EpicGame>? epicGames,
        IEnumerable<GogGame>? gogGames = null,
        IEnumerable<XboxGame>? xboxGames = null)
    {
        var rows = new List<CsvGameExportRow>();
        var indexes = CreateIndexes(rows);

        if (steamGames != null)
            foreach (var game in steamGames)
            {
                var incoming = new CsvGameExportRow
                {
                    Name = game.Name,
                    HasSteam = true,
                    SteamGameId = game.GameID,
                    PlaytimeMinutes = game.PlaytimeMinutes,
                    LastPlayed = game.LastPlayedUnix > 0 ? DateTimeOffset.FromUnixTimeSeconds(game.LastPlayedUnix) : null
                };

                AddOrMerge(rows, indexes, incoming);
            }

        if (epicGames != null)
            foreach (var game in epicGames)
            {
                var incoming = new CsvGameExportRow
                {
                    Name = game.Title ?? game.AppName,
                    HasEpic = true,
                    CatalogItemId = game.CatalogItemId,
                    Namespace = game.Namespace,
                    FetchedCoverUrl = game.CoverUrl,
                    FetchedStoreUrl = game.StoreUrl
                };

                AddOrMerge(rows, indexes, incoming);
            }

        if (gogGames != null)
            foreach (var game in gogGames)
            {
                var incoming = new CsvGameExportRow
                {
                    Name = game.Title,
                    HasGog = true,
                    GogId = game.Id,
                    FetchedCoverUrl = StoreLinks.GogCover(game.Image),
                    FetchedStoreUrl = StoreLinks.GogStore(game.Url, game.Slug)
                };

                AddOrMerge(rows, indexes, incoming);
            }

        if (xboxGames != null)
            foreach (var game in xboxGames)
            {
                var incoming = new CsvGameExportRow
                {
                    Name = game.Name,
                    HasXbox = true,
                    XboxTitleId = game.TitleId,
                    IsGamePass = game.IsGamePass,
                    LastPlayed = game.LastPlayed,
                    FetchedCoverUrl = StoreLinks.XboxCover(game.ImageUrl),
                    FetchedStoreUrl = StoreLinks.XboxStore(game.ProductId)
                };

                AddOrMerge(rows, indexes, incoming);
            }

        return rows;
    }

    public static CsvGameExportRow CreateRow(string? platform, string name, string? idField)
    {
        var row = new CsvGameExportRow { Name = name };
        ApplyIdField(row, idField ?? string.Empty);
        ApplyPlatformFlags(row, platform ?? string.Empty);
        return row;
    }

    /// <summary>
    /// Whether a row already in an export target is still owned: true when the fetch matched it,
    /// false when it's missing and <b>every</b> store it's tagged with was fetched, null (unknown)
    /// otherwise — e.g. its GOG session expired, so its absence proves nothing.
    /// </summary>
    public static bool? Ownership(CsvGameExportRow row, bool matched, IReadOnlySet<string> fetchedStores)
    {
        if (matched) return true;
        var stores = PlatformCatalog.All.Where(d => d.Has(row)).Select(d => d.Label).ToList();
        return stores.Count > 0 && stores.All(fetchedStores.Contains) ? false : null;
    }

    /// <summary>
    /// Back-compat for targets written by older builds: a bare numeric id (no platform prefix)
    /// is attributed to the tagged platform — GOG when only GOG is tagged, otherwise Steam.
    /// </summary>
    public static void ApplyLegacyBareId(CsvGameExportRow row, string? idField)
    {
        if (row.SteamGameId.HasValue || row.GogId.HasValue || !long.TryParse(idField, out var legacyId))
            return;

        if (row.HasGog && !row.HasSteam)
        {
            row.GogId = legacyId;
        }
        else if (legacyId is >= 0 and <= int.MaxValue) // anything larger can't be a Steam app id
        {
            row.SteamGameId = (int)legacyId;
            row.HasSteam = true;
        }
    }

    private static void ApplyPlatformFlags(CsvGameExportRow row, string platform)
    {
        foreach (var d in PlatformCatalog.All)
            d.SetHas(row, PlatformIncludes(platform, d.Label) || d.HasId(row));
        row.IsGamePass = PlatformIncludes(platform, Platforms.GamePass);
    }

    public static List<CsvGameExportRow> Parse(string content)
    {
        var rows = new List<CsvGameExportRow>();
        if (string.IsNullOrWhiteSpace(content))
            return rows;

        var lines = content.Split(["\r\n", "\r", "\n"], StringSplitOptions.RemoveEmptyEntries);
        var header = lines.Length > 0 ? ParseCsvFields(lines[0]) : [];
        var hasHeader = header.Count > 0 &&
                        string.Equals(header[0].Trim(), "Platform", StringComparison.OrdinalIgnoreCase);
        // Headerless files are the old wide layout; a Platform header picks current vs legacy by column names.
        var legacyWide = !hasHeader || IsLegacyWideHeader(header);
        var linuxColumn = hasHeader ? IndexOfHeader(header, LinuxRating.Column) : -1;
        // Current format: every column after Platform, Name, ID (Linux aside) is kept per row.
        var keys = hasHeader && !legacyWide ? ColumnKeys(header) : [];

        foreach (var line in lines.Skip(hasHeader ? 1 : 0))
        {
            var fields = ParseCsvFields(line);
            var row = ParseLine(fields, legacyWide, linuxColumn);
            if (row is not { HasContent: true }) continue;

            if (keys.Count > 3)
            {
                var cells = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                for (var i = 3; i < keys.Count; i++)
                    if (i != linuxColumn) cells[keys[i]] = i < fields.Count ? fields[i] : string.Empty;
                row.Cells = cells;
            }

            rows.Add(row);
        }

        return rows;
    }

    public static string Serialize(
        IEnumerable<CsvGameExportRow> rows, bool includeLinux = false, IReadOnlyList<string>? headers = null,
        Func<CsvGameExportRow, bool>? completed = null, Func<CsvGameExportRow, bool?>? owned = null)
    {
        var columns = (headers ?? [PlatformColumn, NameColumn, IdColumn]).ToList();
        if (!columns.Contains(CompletedColumn, StringComparer.OrdinalIgnoreCase)) columns.Add(CompletedColumn);
        if (includeLinux && !columns.Contains(LinuxRating.Column, StringComparer.OrdinalIgnoreCase))
            columns.Add(LinuxRating.Column);
        var keys = ColumnKeys(columns);
        completed ??= row => ReadCompleted(row) ?? false;

        var builder = new StringBuilder();
        builder.AppendLine(string.Join(",", columns.Select(CsvGameExportRow.EscapeCsvField)));

        foreach (var row in rows.Where(row => row.HasContent)
                     .OrderBy(row => row.Name, StringComparer.OrdinalIgnoreCase))
            builder.AppendLine(string.Join(",", columns.Select((column, i) =>
                CsvGameExportRow.EscapeCsvField(CellText(row, column, keys[i], completed, owned)))));

        return builder.ToString();
    }

    // Pilewise columns are filled the way the Sheets export fills them: playtime / last played only
    // with fresh data, links / covers only into empty cells; any other column is the user's.
    private static string CellText(
        CsvGameExportRow row, string column, string key,
        Func<CsvGameExportRow, bool> completed, Func<CsvGameExportRow, bool?>? owned)
    {
        var cell = row.Cells?.GetValueOrDefault(key) ?? string.Empty;
        return column.Trim().ToLowerInvariant() switch
        {
            "platform" => row.PlatformText,
            "name" => row.Name,
            "id" or "game id" => row.IdText,
            "completed" => completed(row) ? Yes : string.Empty,
            "linux" => row.Linux ?? LinuxRating.Unknown,
            "playtime" => row.PlaytimeHours?.ToString(CultureInfo.InvariantCulture) ?? cell,
            "last played" => row.LastPlayedDate ?? cell,
            "owned" => owned?.Invoke(row) is { } isOwned ? (isOwned ? Yes : "no") : cell,
            "store link" => cell.Length > 0 ? cell : row.StoreUrl ?? string.Empty,
            "cover" => cell.Length > 0 ? cell : row.CoverUrl ?? string.Empty,
            _ => cell
        };
    }

    public static bool? ReadCompleted(CsvGameExportRow row)
    {
        if (row.Cells?.TryGetValue(CompletedColumn, out var cell) != true) return null;
        var text = cell!.Trim();
        return text.Length > 0 && !NotDone.Contains(text, StringComparer.OrdinalIgnoreCase);
    }

    private const string PlatformColumn = "Platform";
    private const string NameColumn = "Name";
    private const string IdColumn = "ID";
    public const string CompletedColumn = "Completed";
    private const string Yes = "yes";
    private static readonly string[] NotDone = ["no", "false", "0", "-", "нет", "ложь", "ні"];

    // A key per column: its header, or header#index for a repeated one (each keeps its own cells).
    private static List<string> ColumnKeys(IReadOnlyList<string> headers)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        return headers.Select((h, i) => seen.Add(h.Trim()) ? h.Trim() : $"{h.Trim()}#{i}").ToList();
    }

    /// <summary>
    /// Merges the fetched games into a CSV's content (empty / null: a new file) and serializes it,
    /// syncing the Completed ticks with <see cref="CompletedGames"/> when the options carry it.
    /// Call <see cref="CsvMergeResult.Commit"/> once the content was written.
    /// </summary>
    public static CsvMergeResult MergeToCsv(
        string? existingContent, IEnumerable<CsvGameExportRow> incomingRows, CsvMergeOptions? options = null)
    {
        options ??= new CsvMergeOptions();
        var header = ReadHeaderFields(existingContent);
        var keepHeader = header.Count > 0 &&
                         string.Equals(header[0], PlatformColumn, StringComparison.OrdinalIgnoreCase) &&
                         !IsLegacyWideHeader(header);

        var fromFile = Parse(existingContent ?? string.Empty).Select(CloneRow).ToList();
        var (rows, added, updated, matched) = MergeCore(fromFile, incomingRows);
        var existing = new HashSet<CsvGameExportRow>(fromFile, ReferenceEqualityComparer.Instance);

        // Hidden games: rows stay as they are (their own tick), and they're still owned.
        var finder = new RowMatcher(rows);
        var hidden = new HashSet<CsvGameExportRow>(ReferenceEqualityComparer.Instance);
        foreach (var row in options.Ignored ?? [])
            if (finder.Match(row) is { } match && existing.Contains(match))
            {
                hidden.Add(match);
                matched.Add(match);
            }

        var sync = options.Completed?.Begin(options.Target ?? "CSV",
            fromFile.Where(r => !hidden.Contains(r)).Select(r => (r, ReadCompleted(r))));
        bool Completed(CsvGameExportRow row) =>
            hidden.Contains(row) || sync == null ? ReadCompleted(row) ?? false : sync.IsCompleted(row);
        bool? Owned(CsvGameExportRow row) =>
            !existing.Contains(row) ? true
            : options.FetchedStores == null ? null
            : Ownership(row, matched.Contains(row), options.FetchedStores);

        var content = Serialize(rows, options.IncludeLinux || HasLinuxColumn(existingContent),
            keepHeader ? header : null, Completed, Owned);
        var written = rows.Where(r => !hidden.Contains(r)).Select(r => (r, Completed(r))).ToList();
        return new CsvMergeResult(content, added, updated, rows.Count,
            !string.Equals(content, existingContent, StringComparison.Ordinal), sync, written);
    }

    public static (List<CsvGameExportRow> Rows, int Added, int Updated) Merge(
        IEnumerable<CsvGameExportRow> existingRows,
        IEnumerable<CsvGameExportRow> incomingRows)
    {
        var (rows, added, updated, _) = MergeCore(existingRows.Select(CloneRow).ToList(), incomingRows);
        return (rows, added, updated);
    }

    // Merges into the given rows (not copied); also returns the ones a fetched game matched.
    private static (List<CsvGameExportRow> Rows, int Added, int Updated, HashSet<CsvGameExportRow> Matched) MergeCore(
        List<CsvGameExportRow> existingRows, IEnumerable<CsvGameExportRow> incomingRows)
    {
        var rows = existingRows.ToList();
        var matched = new HashSet<CsvGameExportRow>(ReferenceEqualityComparer.Instance);
        var indexes = CreateIndexes(rows);
        var added = 0;
        var updated = 0;

        foreach (var incoming in incomingRows)
        {
            var match = FindMatch(indexes, incoming);
            if (match == null)
            {
                rows.Add(CloneRow(incoming));
                RegisterRow(indexes, rows[^1]);
                added++;
                continue;
            }

            match.MergeFrom(incoming);
            RegisterRow(indexes, match);
            matched.Add(match);
            updated++;
        }

        return (rows, added, updated, matched);
    }

    private static bool IsLegacyWideHeader(List<string> header) =>
        header.Count >= 5 &&
        string.Equals(header[2].Trim(), "Game ID", StringComparison.OrdinalIgnoreCase) &&
        string.Equals(header[3].Trim(), "Catalog Item Id", StringComparison.OrdinalIgnoreCase);

    private static int IndexOfHeader(List<string> header, string name)
    {
        for (var i = 0; i < header.Count; i++)
            if (string.Equals(header[i].Trim(), name, StringComparison.OrdinalIgnoreCase))
                return i;
        return -1;
    }

    private static CsvGameExportRow? ParseLine(List<string> fields, bool legacyWide, int linuxColumn)
    {
        if (fields.Count == 0)
            return null;

        // Legacy wide format: Platform, Name, Game ID, Catalog Item Id, Namespace.
        if (legacyWide && fields.Count >= 5)
        {
            var platform = fields[0];
            var gameId = ParseInt(fields[^3]);
            var catalogItemId = NullIfEmpty(fields[^2]);
            var ns = NullIfEmpty(fields[^1]);
            var name = string.Join(",", fields.Skip(1).Take(fields.Count - 4));

            return new CsvGameExportRow
            {
                Name = name,
                HasSteam = PlatformIncludes(platform, Platforms.Steam) || gameId.HasValue,
                HasEpic = PlatformIncludes(platform, Platforms.Epic) ||
                          !string.IsNullOrEmpty(catalogItemId) ||
                          !string.IsNullOrEmpty(ns),
                SteamGameId = gameId,
                CatalogItemId = catalogItemId,
                Namespace = ns
            };
        }

        if (fields.Count >= 3)
        {
            var row = new CsvGameExportRow { Name = fields[1] };
            ApplyIdField(row, fields[2]);
            ApplyPlatformFlags(row, fields[0]);
            ApplyLegacyBareId(row, fields[2]);
            if (linuxColumn >= 0 && linuxColumn < fields.Count) row.Linux = NullIfEmpty(fields[linuxColumn]);
            return row;
        }

        // Oldest format: Name, Game ID.
        if (fields.Count == 2)
            return new CsvGameExportRow
            {
                Name = fields[0],
                HasSteam = true,
                SteamGameId = ParseInt(fields[1])
            };

        return null;
    }

    private static void ApplyIdField(CsvGameExportRow row, string idField)
    {
        if (string.IsNullOrWhiteSpace(idField))
            return;

        foreach (var part in idField.Split(';', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            var prefix = part.IndexOf(':');
            if (prefix <= 0) continue;

            var label = part[..prefix];
            var descriptor = PlatformCatalog.All.FirstOrDefault(
                d => string.Equals(d.Label, label, StringComparison.OrdinalIgnoreCase));
            descriptor?.ApplyId(row, part[(prefix + 1)..]);
        }
    }

    private static List<string> ParseCsvFields(string line)
    {
        var fields = new List<string>();
        var current = new StringBuilder();
        var inQuotes = false;

        for (var i = 0; i < line.Length; i++)
        {
            var ch = line[i];

            if (ch == '"')
            {
                if (inQuotes && i + 1 < line.Length && line[i + 1] == '"')
                {
                    current.Append('"');
                    i++;
                }
                else
                {
                    inQuotes = !inQuotes;
                }

                continue;
            }

            if (ch == ',' && !inQuotes)
            {
                fields.Add(current.ToString());
                current.Clear();
                continue;
            }

            current.Append(ch);
        }

        fields.Add(current.ToString());
        return fields;
    }

    private static void AddOrMerge(
        List<CsvGameExportRow> rows,
        CsvIndexes indexes,
        CsvGameExportRow incoming)
    {
        var match = FindMatch(indexes, incoming);
        if (match == null)
        {
            rows.Add(incoming);
            RegisterRow(indexes, incoming);
            return;
        }

        match.MergeFrom(incoming);
        RegisterRow(indexes, match);
    }

    private static CsvGameExportRow? FindMatch(CsvIndexes indexes, CsvGameExportRow incoming)
    {
        foreach (var d in PlatformCatalog.All)
        {
            var key = d.MatchKey(incoming);
            if (key != null && indexes.TryGetById(d.Label, key, out var match))
                return match;
        }

        var nameKey = NormalizeGameName(incoming.Name);
        if (!string.IsNullOrEmpty(nameKey) &&
            indexes.ByName.TryGetValue(nameKey, out var byName))
            return byName;

        return null;
    }

    private static CsvIndexes CreateIndexes(IEnumerable<CsvGameExportRow> rows)
    {
        var indexes = new CsvIndexes();
        foreach (var row in rows)
            RegisterRow(indexes, row);

        return indexes;
    }

    private static void RegisterRow(CsvIndexes indexes, CsvGameExportRow row)
    {
        foreach (var d in PlatformCatalog.All)
        {
            var key = d.MatchKey(row);
            if (key != null) indexes.SetById(d.Label, key, row);
        }

        var nameKey = NormalizeGameName(row.Name);
        if (!string.IsNullOrEmpty(nameKey))
            indexes.ByName[nameKey] = row;
    }

    private static CsvGameExportRow CloneRow(CsvGameExportRow row)
    {
        return new CsvGameExportRow
        {
            Name = row.Name,
            HasSteam = row.HasSteam,
            HasEpic = row.HasEpic,
            HasGog = row.HasGog,
            HasXbox = row.HasXbox,
            IsGamePass = row.IsGamePass,
            SteamGameId = row.SteamGameId,
            CatalogItemId = row.CatalogItemId,
            Namespace = row.Namespace,
            GogId = row.GogId,
            XboxTitleId = row.XboxTitleId,
            PlaytimeMinutes = row.PlaytimeMinutes,
            LastPlayed = row.LastPlayed,
            FetchedCoverUrl = row.FetchedCoverUrl,
            FetchedStoreUrl = row.FetchedStoreUrl,
            Linux = row.Linux,
            Cells = row.Cells
        };
    }

    private static int? ParseInt(string value)
    {
        return int.TryParse(value.Trim(), out var parsed) ? parsed : null;
    }

    private static string? NullIfEmpty(string value)
    {
        return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }

    private static bool PlatformIncludes(string platform, string token)
    {
        return platform.Split(['/', ','], StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .Any(part => string.Equals(part, token, StringComparison.OrdinalIgnoreCase));
    }

    public static string NormalizeGameName(string name)
    {
        var normalized = TrademarkSigns().Replace(name.Trim().ToLowerInvariant(), "");
        normalized = Punctuation().Replace(normalized, " ");
        return Whitespace().Replace(normalized, " ").Trim();
    }

    [GeneratedRegex("[™®©]")]
    private static partial Regex TrademarkSigns();

    [GeneratedRegex(@"[^\w\s]")]
    private static partial Regex Punctuation();

    [GeneratedRegex(@"\s+")]
    private static partial Regex Whitespace();

    public sealed class RowMatcher
    {
        private readonly CsvIndexes _indexes;

        public RowMatcher(IEnumerable<CsvGameExportRow> rows)
        {
            _indexes = CreateIndexes(rows);
        }

        public CsvGameExportRow? Match(CsvGameExportRow incoming)
        {
            return FindMatch(_indexes, incoming);
        }

        public void Register(CsvGameExportRow row)
        {
            RegisterRow(_indexes, row);
        }
    }

    private sealed class CsvIndexes
    {
        private readonly Dictionary<(string Platform, string Key), CsvGameExportRow> _byId = new();

        public Dictionary<string, CsvGameExportRow> ByName { get; } = new(StringComparer.Ordinal);

        public bool TryGetById(string platform, string key, out CsvGameExportRow row) =>
            _byId.TryGetValue((platform, key), out row!);

        public void SetById(string platform, string key, CsvGameExportRow row) =>
            _byId[(platform, key)] = row;
    }
}

using Pilewise.Models;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace Pilewise.Services;

public static partial class CsvGameExportService
{
    public static string TargetOf(string path)
    {
        return $"CSV:{System.IO.Path.GetFullPath(path)}";
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
                    LastPlayed = game.LastPlayedUnix > 0
                        ? DateTimeOffset.FromUnixTimeSeconds(game.LastPlayedUnix)
                        : null
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
    /// Merges the fetched games into a CSV's content (empty / null: a new file) and serializes it,
    /// syncing the Completed ticks with <see cref="CompletedGames"/> when the options carry it.
    /// Call <see cref="CsvMergeResult.Commit"/> once the content was written.
    /// </summary>
    public static CsvMergeResult MergeToCsv(
        string? existingContent, IEnumerable<CsvGameExportRow> incomingRows, CsvMergeOptions? options = null)
    {
        options ??= new CsvMergeOptions();
        var header = CsvFormat.ReadHeaderFields(existingContent);
        var keepHeader = header.Count > 0 &&
                         string.Equals(header[0], CsvFormat.PlatformColumn, StringComparison.OrdinalIgnoreCase) &&
                         !CsvFormat.IsLegacyWideHeader(header);

        var fromFile = CsvFormat.Parse(existingContent ?? string.Empty);
        var (rows, added, updated, matched) = MergeCore(fromFile, incomingRows);
        var existing = new HashSet<CsvGameExportRow>(fromFile, ReferenceEqualityComparer.Instance);

        var finder = new RowMatcher(rows);
        var hidden = new HashSet<CsvGameExportRow>(ReferenceEqualityComparer.Instance);
        foreach (var row in options.Ignored ?? [])
            if (finder.Match(row) is { } match && existing.Contains(match))
            {
                hidden.Add(match);
                matched.Add(match);
            }

        var sync = options.Completed?.Begin(options.Target ?? "CSV",
            fromFile.Where(r => !hidden.Contains(r)).Select(r => (r, CsvFormat.ReadCompleted(r))));

        bool Completed(CsvGameExportRow row)
        {
            return hidden.Contains(row) || sync == null ? CsvFormat.ReadCompleted(row) ?? false : sync.IsCompleted(row);
        }

        bool? Owned(CsvGameExportRow row)
        {
            return !existing.Contains(row) ? true
                : options.FetchedStores == null ? null
                : Ownership(row, matched.Contains(row), options.FetchedStores);
        }

        var content = CsvFormat.Serialize(rows, options.IncludeLinux || CsvFormat.HasLinuxColumn(existingContent),
            keepHeader ? header : null, Completed, Owned);
        var written = rows.Where(r => !hidden.Contains(r)).Select(r => (r, Completed(r))).ToList();
        return new CsvMergeResult(content, added, updated, rows.Count,
            !string.Equals(content, existingContent, StringComparison.Ordinal), sync, written);
    }

    public static (List<CsvGameExportRow> Rows, int Added, int Updated) Merge(
        IEnumerable<CsvGameExportRow> existingRows,
        IEnumerable<CsvGameExportRow> incomingRows)
    {
        var (rows, added, updated, _) = MergeCore(existingRows.Select(r => r.Clone()).ToList(), incomingRows);
        return (rows, added, updated);
    }

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
                rows.Add(incoming.Clone());
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

        var nameKey = incoming.NameKey;
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

        var nameKey = row.NameKey;
        if (!string.IsNullOrEmpty(nameKey))
            indexes.ByName[nameKey] = row;
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

    public sealed class RowMatcher(IEnumerable<CsvGameExportRow> rows)
    {
        private readonly CsvIndexes _indexes = CreateIndexes(rows);

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

        public bool TryGetById(string platform, string key, out CsvGameExportRow row)
        {
            return _byId.TryGetValue((platform, key), out row!);
        }

        public void SetById(string platform, string key, CsvGameExportRow row)
        {
            _byId[(platform, key)] = row;
        }
    }
}
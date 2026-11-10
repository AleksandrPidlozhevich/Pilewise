using Pilewise.Models;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;

namespace Pilewise.Services;

/// <summary>CSV text ⇄ rows: parsing (current + two legacy layouts), serialization and cell rules.</summary>
public static class CsvFormat
{
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

    public static bool HasLinuxColumn(string? content)
    {
        return HasColumn(ReadHeaderFields(content), LinuxRating.Column);
    }

    internal static List<string> ReadHeaderFields(string? content)
    {
        if (string.IsNullOrWhiteSpace(content))
            return [];

        var text = content.AsSpan().TrimStart("\r\n");
        var end = text.IndexOfAny('\r', '\n');
        var firstLine = (end < 0 ? text : text[..end]).ToString();

        return string.IsNullOrWhiteSpace(firstLine)
            ? []
            : ParseCsvFields(firstLine).Select(field => field.Trim()).ToList();
    }

    private static bool HasColumn(List<string> headerFields, string name)
    {
        return headerFields.Any(header => string.Equals(header, name, StringComparison.OrdinalIgnoreCase));
    }

    public static CsvGameExportRow CreateRow(string? platform, string name, string? idField)
    {
        var row = new CsvGameExportRow { Name = name };
        ApplyIdField(row, idField ?? string.Empty);
        ApplyPlatformFlags(row, platform ?? string.Empty);
        return row;
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
                    if (i != linuxColumn)
                        cells[keys[i]] = i < fields.Count ? fields[i] : string.Empty;
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
            "owned" => owned?.Invoke(row) is { } isOwned ? isOwned ? Yes : "no" : cell,
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

    internal const string PlatformColumn = "Platform";
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

    internal static bool IsLegacyWideHeader(List<string> header)
    {
        return header.Count >= 5 &&
               string.Equals(header[2].Trim(), "Game ID", StringComparison.OrdinalIgnoreCase) &&
               string.Equals(header[3].Trim(), "Catalog Item Id", StringComparison.OrdinalIgnoreCase);
    }

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
            var descriptor =
                PlatformCatalog.All.FirstOrDefault(d =>
                    string.Equals(d.Label, label, StringComparison.OrdinalIgnoreCase));
            descriptor?.ApplyId(row, part[(prefix + 1)..]);
        }
    }

    private static List<string> ParseCsvFields(string line)
    {
        if (!line.Contains('"')) return [.. line.Split(',')];

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
}
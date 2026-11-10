using Newtonsoft.Json.Linq;
using Pilewise.Services;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Pilewise.Models;

public sealed record NotionGameRow(
    CsvGameExportRow Row,
    string PageId,
    IReadOnlySet<string> Platforms,
    string GameId,
    double? PlaytimeHours = null,
    string? LastPlayed = null,
    bool? Owned = null,
    bool HasCover = false,
    string? StoreLink = null,
    DateTimeOffset? CreatedTime = null,
    bool? Completed = null,
    string? Linux = null);

public class NotionDataFetcher : INotionDataFetcher
{
    public const string NameProperty = "Name";
    public const string PlatformProperty = "Platform's";
    public const string GameIdProperty = "GameID";

    public const string PlaytimeProperty = "Playtime";
    public const string LastPlayedProperty = "Last played";
    public const string OwnedProperty = "Owned";

    // Created automatically like Platform's / GameID; filled only while empty (user edits are kept).
    public const string StoreLinkProperty = "Store link";

    public const string LinuxProperty = LinuxRating.Column;

    private readonly INotionApiClient _notionApiClient;
    private readonly ILogService _log;

    public NotionDataFetcher(INotionApiClient notionApiClient, ILogService log)
    {
        _notionApiClient = notionApiClient;
        _log = log;
    }

    public async Task<List<NotionGameRow>> FetchRowsAsync(CancellationToken ct = default)
    {
        try
        {
            var allPages = await _notionApiClient.QueryAllPagesAsync(ct);
            return allPages
                .Select(ExtractRow)
                .Where(row => row != null)
                .Select(row => row!)
                .ToList();
        }
        catch (Exception ex)
        {
            _log.WriteError($"Data Error with Notion: {ex.Message}");
            throw;
        }
    }

    private NotionGameRow? ExtractRow(JObject page)
    {
        try
        {
            var pageId = page["id"]?.Value<string>();
            if (string.IsNullOrEmpty(pageId)) return null;

            var properties = page["properties"];
            var name = ReadTitle(properties?[NameProperty]) ?? string.Empty;
            var platforms = ReadMultiSelect(properties?[PlatformProperty]);
            var idField = ReadGameId(properties?[GameIdProperty]);

            var row = CsvFormat.CreateRow(string.Join("/", platforms), name, idField);

            // Back-compat: a bare numeric GameID (from the old Number column) has no
            // platform prefix — attribute it to the tagged platform, defaulting to Steam.
            CsvFormat.ApplyLegacyBareId(row, idField);

            return row.HasContent
                ? new NotionGameRow(row, pageId, platforms, idField ?? string.Empty,
                    properties?[PlaytimeProperty]?["number"]?.Value<double?>(),
                    ReadDate(properties?[LastPlayedProperty]),
                    properties?[OwnedProperty]?["checkbox"]?.Value<bool?>(),
                    page["cover"] is JObject,
                    properties?[StoreLinkProperty]?["url"]?.Value<string>(),
                    page["created_time"]?.Type == JTokenType.Date
                        ? page["created_time"]!.Value<DateTime>()
                        : DateTimeOffset.TryParse(page["created_time"]?.Value<string>(), out var created)
                            ? created
                            : null,
                    NotionCompletion.Read(properties),
                    properties?[LinuxProperty]?["select"]?["name"]?.Value<string>())
                : null;
        }
        catch (Exception ex)
        {
            _log.WriteError($"Page processing error {ex.Message}");
            return null;
        }
    }

    // A date property's start as yyyy-MM-dd (Notion may append a time part).
    private static string? ReadDate(JToken? property)
    {
        var start = property?["date"]?["start"]?.Value<string>();
        return start is { Length: >= 10 } ? start[..10] : null;
    }

    private static string? ReadTitle(JToken? property)
    {
        return JoinPlainText(property?["title"] as JArray);
    }

    // Reads GameID whether the column is Text (rich_text) or the legacy Number type.
    private static string? ReadGameId(JToken? property)
    {
        if (property == null) return null;

        var text = JoinPlainText(property["rich_text"] as JArray);
        if (text != null) return text;

        return property["number"]?.Value<long?>()?.ToString();
    }

    private static string? JoinPlainText(JArray? array)
    {
        if (array == null || array.Count == 0) return null;

        var text = string.Concat(array.Select(item =>
            item["plain_text"]?.Value<string>()
            ?? item["text"]?["content"]?.Value<string>()
            ?? string.Empty));

        return string.IsNullOrEmpty(text) ? null : text;
    }

    private static HashSet<string> ReadMultiSelect(JToken? property)
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (property?["multi_select"] is JArray array)
            foreach (var item in array)
            {
                var name = item["name"]?.Value<string>();
                if (!string.IsNullOrEmpty(name)) set.Add(name);
            }

        return set;
    }
}
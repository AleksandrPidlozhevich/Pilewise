using Pilewise.Models;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace Pilewise.Services;

/// <summary>What a Notion page should hold for a game: the change signature, preview text and API property bodies.</summary>
internal static class NotionPages
{
    // "Steam/GOG · Steam:1; GOG:2 · 12.5 h · last played 2024-05-01" — preview before/after text.
    internal static string Describe(
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
    internal static bool NeedsCover(NotionGameRow page) => !page.HasCover && page.Row.CoverUrl != null;

    // … and a Store link only while the cell is empty.
    internal static bool NeedsLink(NotionGameRow page) =>
        string.IsNullOrWhiteSpace(page.StoreLink) && page.Row.StoreUrl != null;

    // An opt-in column's value, only when the column exists and fresh data was fetched for the row
    // (games no longer owned / stores without playtime keep whatever the page already holds).
    internal static T? Wanted<T>(bool enabled, T? fresh, T? value) => enabled && fresh != null ? value : default;

    internal static int PlatformOrder(string label)
    {
        var index = Array.FindIndex(PlatformCatalog.All, d => d.Label == label);
        return index < 0 ? int.MaxValue : index;
    }

    internal static string NotionSignature(
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

    internal static string StoredSignature(
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

    internal static string? Format(double? hours) => hours?.ToString(CultureInfo.InvariantCulture);

    internal static object BuildProperties(
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

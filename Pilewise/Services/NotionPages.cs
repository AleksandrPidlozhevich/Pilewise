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
        bool addsCover = false, bool addsLink = false, string? linux = null, bool? completed = null)
    {
        return string.Join(" · ", new[]
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
    }

    // A page gets a cover only when it has none (a cover the user picked is never replaced) …
    internal static bool NeedsCover(NotionGameRow page)
    {
        return !page.HasCover && page.Row.CoverUrl != null;
    }

    // … and a Store link only while the cell is empty.
    internal static bool NeedsLink(NotionGameRow page)
    {
        return string.IsNullOrWhiteSpace(page.StoreLink) && page.Row.StoreUrl != null;
    }

    // An opt-in column's value, only when the column exists and fresh data was fetched for the row
    // (games no longer owned / stores without playtime keep whatever the page already holds).
    internal static T? Wanted<T>(bool enabled, T? fresh, T? value)
    {
        return enabled && fresh != null ? value : default;
    }

    internal static string PlatformsText(NotionGameRow page)
    {
        return string.Join("/", page.Platforms.OrderBy(PlatformOrder));
    }

    private static int PlatformOrder(string label)
    {
        var index = Array.FindIndex(PlatformCatalog.All, d => d.Label == label);
        return index < 0 ? int.MaxValue : index;
    }

    internal readonly record struct PageState(
        int Tags,
        string? GameId,
        double? Hours,
        string? LastPlayed,
        bool? Owned,
        string? Linux,
        bool? Completed);

    internal static PageState Desired(
        CsvGameExportRow row, NotionOptionalColumns optional, bool? owned, bool? completed)
    {
        return new PageState(
            Tags(d => d.Has(row), row.IsGamePass),
            Text(row.IdText),
            Wanted(optional.Playtime, row.PlaytimeHours, row.PlaytimeHours),
            Text(Wanted(optional.LastPlayed, row.LastPlayedDate, row.LastPlayedDate)),
            Wanted(optional.Owned, owned, owned),
            Text(optional.Linux ? row.Linux : null),
            completed);
    }

    internal static PageState Stored(
        NotionGameRow info, NotionOptionalColumns optional, bool? owned, bool writesCompleted)
    {
        return new PageState(
            Tags(d => info.Platforms.Contains(d.Label), info.Platforms.Contains(Platforms.GamePass)),
            Text(info.GameId),
            Wanted(optional.Playtime, info.Row.PlaytimeHours, info.PlaytimeHours),
            Text(Wanted(optional.LastPlayed, info.Row.LastPlayedDate, info.LastPlayed)),
            Wanted(optional.Owned, owned, info.Owned ?? false),
            Text(optional.Linux ? info.Linux : null),
            writesCompleted ? info.Completed ?? false : null);
    }

    private static int Tags(Func<PlatformDescriptor, bool> has, bool gamePass)
    {
        var bits = gamePass ? 1 : 0;
        for (var i = 0; i < PlatformCatalog.All.Length; i++)
            if (has(PlatformCatalog.All[i]))
                bits |= 2 << i;
        return bits;
    }

    private static string? Text(string? value)
    {
        return string.IsNullOrEmpty(value) ? null : value;
    }

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
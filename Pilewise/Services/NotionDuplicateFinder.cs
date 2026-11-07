using Pilewise.Models;
using System;
using System.Collections.Generic;
using System.Linq;

namespace Pilewise.Services;

/// <summary>
/// Pages of the same game found by <see cref="NotionDuplicateFinder"/>: the one to keep (the oldest,
/// most likely the one with the user's notes / status) and the ones to move to Notion's trash.
/// </summary>
public sealed record NotionDuplicateGroup(NotionGameRow Keep, IReadOnlyList<NotionGameRow> Remove, CsvGameExportRow Merged)
    : IDuplicateGroupView
{
    public string Name => Keep.Row.Name;

    public string ResultText => DuplicateFinder.Describe(Merged.PlatformText, Merged.IdText);

    public string RemoveText => string.Join(", ", Remove.Select(r =>
        $"{r.Row.Name} ({DuplicateFinder.Describe(string.Join("/", r.Platforms), r.GameId)})"));
}

public static class NotionDuplicateFinder
{
    public static IReadOnlyList<NotionDuplicateGroup> Find(IReadOnlyList<NotionGameRow> pages)
    {
        // Oldest first, so the kept page is the original one; pages without a date keep their order.
        var ordered = pages
            .Select((page, index) => (page, index))
            .OrderBy(x => x.page.CreatedTime ?? DateTimeOffset.MaxValue)
            .ThenBy(x => x.index)
            .Select(x => x.page);

        return DuplicateFinder.Find(ordered, p => p.Row)
            .Select(m => new NotionDuplicateGroup(m.Keep, m.Remove, m.Merged))
            .ToList();
    }
}

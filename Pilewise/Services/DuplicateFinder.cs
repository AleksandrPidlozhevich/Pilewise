using System;
using System.Collections.Generic;
using System.Linq;

namespace Pilewise.Services;

public interface IDuplicateGroupView
{
    string Name { get; }

    string ResultText { get; }

    string RemoveText { get; }
}

public sealed record DuplicateMatch<T>(T Keep, IReadOnlyList<T> Remove, CsvGameExportRow Merged);

/// <summary>
/// Finds entries of a tracker that are the same game (e.g. added by hand before the first export, or
/// by an older build). Two entries match when they share a store id, or have the same normalized name
/// and no conflicting id for the same store (so "Doom" (1993) and "Doom" (2016), with different Steam
/// ids, stay apart). The first entry of each group (in the order given) is the one kept; the merged
/// row has every store and id but keeps that entry's name. Pure, so it's unit-tested.
/// </summary>
public static class DuplicateFinder
{
    public static IReadOnlyList<DuplicateMatch<T>> Find<T>(IEnumerable<T> itemsInKeepOrder, Func<T, CsvGameExportRow> rowOf)
    {
        var groups = new List<Group<T>>();
        var byId = new Dictionary<(string Platform, string Key), Group<T>>();
        var byName = new Dictionary<string, List<Group<T>>>(StringComparer.Ordinal);

        foreach (var item in itemsInKeepOrder)
        {
            var row = rowOf(item);
            Group<T>? target = null;

            foreach (var d in PlatformCatalog.All)
                if (d.MatchKey(row) is { } key && byId.TryGetValue((d.Label, key), out var byKey))
                {
                    target = byKey;
                    break;
                }

            var name = row.NameKey;
            if (target == null && name.Length > 0 && byName.TryGetValue(name, out var sameName))
                target = sameName.FirstOrDefault(g => !Conflicts(g.Merged, row));

            // Same id for one store but a different id for another: not safe to merge automatically.
            if (target != null && Conflicts(target.Merged, row)) target = null;

            if (target == null)
            {
                target = new Group<T>(CsvFormat.CreateRow(row.PlatformText, row.Name, row.IdText));
                groups.Add(target);
            }
            else
            {
                var keptName = target.Merged.Name;
                target.Merged.MergeFrom(row);
                target.Merged.Name = keptName; // the kept entry keeps its title
            }

            target.Items.Add(item);

            foreach (var d in PlatformCatalog.All)
                if (d.MatchKey(target.Merged) is { } key)
                    byId[(d.Label, key)] = target;
            if (name.Length > 0)
            {
                if (!byName.TryGetValue(name, out var list)) byName[name] = list = [];
                if (!list.Contains(target)) list.Add(target);
            }
        }

        return groups
            .Where(g => g.Items.Count > 1)
            .Select(g => new DuplicateMatch<T>(g.Items[0], g.Items.Skip(1).ToList(), g.Merged))
            .OrderBy(g => rowOf(g.Keep).Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
    }

    public static string Describe(string platforms, string id) =>
        string.Join(" · ", new[] { platforms, id }.Where(s => !string.IsNullOrWhiteSpace(s)));

    private static bool Conflicts(CsvGameExportRow a, CsvGameExportRow b) =>
        PlatformCatalog.All.Any(d => d.MatchKey(a) is { } ka && d.MatchKey(b) is { } kb && ka != kb);

    private sealed class Group<T>(CsvGameExportRow merged)
    {
        public CsvGameExportRow Merged { get; } = merged;
        public List<T> Items { get; } = [];
    }
}

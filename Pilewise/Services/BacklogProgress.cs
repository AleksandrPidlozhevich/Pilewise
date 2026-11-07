using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.Linq;

namespace Pilewise.Services;

public sealed record BacklogSnapshot(
    string Target, DateTimeOffset When, int Completed, int Total, IReadOnlyList<string> CompletedKeys)
{
    public double Percent => Total == 0 ? 0 : Math.Round(Completed * 100.0 / Total);

    public string Text => $"Completed {Completed} of {Total} ({Percent:0}%)";

    /// <summary>Builds a snapshot from the tracker's rows; null when it has no completion column at all.</summary>
    public static BacklogSnapshot? From(
        string target, DateTimeOffset when, IEnumerable<(CsvGameExportRow Row, bool? Completed)> rows, int newRows = 0)
    {
        var list = rows.ToList();
        if (list.All(r => r.Completed == null)) return null;

        var done = list.Where(r => r.Completed == true).Select(r => r.Row).ToList();
        var keys = done.SelectMany(IgnoredGames.KeysOf).Distinct(StringComparer.OrdinalIgnoreCase)
            .Order(StringComparer.OrdinalIgnoreCase).ToList();
        return new BacklogSnapshot(target, when, done.Count, list.Count + newRows, keys);
    }
}

/// <summary>
/// The latest <see cref="BacklogSnapshot"/> per tracker (Notion / Google Sheets), recorded by the
/// exporters after a real (not dry-run) export. Kept as JSON in the state store (key
/// <c>backlog</c>) like the sync history; re-read on every access so the app and <c>--sync</c> agree.
/// </summary>
public sealed class BacklogProgress
{
    private const string StateKey = "backlog";

    private readonly IStateStore? _store;
    private Dictionary<string, BacklogSnapshot> _cache = new(StringComparer.OrdinalIgnoreCase);

    public BacklogProgress(IStateStore? store = null)
    {
        _store = store;
    }

    public BacklogSnapshot? Get(string target)
    {
        Reload();
        return _cache.GetValueOrDefault(target);
    }

    public BacklogSnapshot? Latest()
    {
        Reload();
        return _cache.Values.MaxBy(s => s.When);
    }

    public void Record(BacklogSnapshot? snapshot)
    {
        if (snapshot == null) return;
        Reload();
        _cache[snapshot.Target] = snapshot;
        _store?.Save(StateKey, Serialize(_cache.Values));
    }

    public static string Serialize(IEnumerable<BacklogSnapshot> snapshots) =>
        JsonConvert.SerializeObject(snapshots.ToList());

    public static List<BacklogSnapshot> Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return [];
        try
        {
            return JArray.Parse(json)
                .Select(t => t.ToObject<BacklogSnapshot>())
                .Where(s => s != null && !string.IsNullOrEmpty(s.Target))
                .Select(s => s! with { CompletedKeys = s.CompletedKeys ?? [] })
                .ToList();
        }
        catch (JsonException)
        {
            return [];
        }
    }

    private void Reload()
    {
        if (_store == null) return;
        _cache = Parse(_store.Load(StateKey))
            .GroupBy(s => s.Target, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.MaxBy(s => s.When)!, StringComparer.OrdinalIgnoreCase);
    }
}

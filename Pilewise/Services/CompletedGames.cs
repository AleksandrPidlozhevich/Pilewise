using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.Linq;

namespace Pilewise.Services;

/// <summary>
/// Which games are completed — Pilewise's own list, shared by CSV, Notion and Google Sheets. A game
/// is remembered by <see cref="IgnoredGames.KeysOf"/>. Kept as JSON in the state store (key
/// <c>completed-games</c>) and re-read on every access, so the app and <c>--sync</c> agree.
/// </summary>
/// <remarks>
/// Sync is a three-way merge per target (one CSV file, one Notion database, one sheet): besides the
/// list, each target's ticks as of its last sync are remembered. A tick that differs from that is a
/// change made in the target and goes into the list (ticked or unticked); otherwise the list is what
/// the target gets. So a tick set or cleared anywhere — the library viewer or any tracker — reaches
/// all of them, and a game changed in two places keeps the change of the target synced last.
/// </remarks>
public sealed class CompletedGames
{
    private const string StateKey = "completed-games";

    private readonly IStateStore? _store;
    private State _state = new();

    public CompletedGames(IStateStore? store = null)
    {
        _store = store;
        Reload();
    }

    public bool IsCompleted(CsvGameExportRow row) => IgnoredGames.KeysOf(row).Any(_state.Done.Contains);

    public int Count => _state.Done.Count;

    public void Reload() => _state = Parse(_store?.Load(StateKey));

    /// <summary>
    /// Upgrade from builds without this list: the first time, it starts from the ✓ keys of the last
    /// tracker snapshot (<see cref="BacklogSnapshot.CompletedKeys"/>), as synced with that target, so
    /// the library keeps its marks. Does nothing once the list exists.
    /// </summary>
    public void SeedOnce(string target, IEnumerable<string> keys)
    {
        if (_store == null || _store.Load(StateKey) != null) return;
        var seen = new HashSet<string>(keys, StringComparer.OrdinalIgnoreCase);
        _state = new State();
        foreach (var key in seen) _state.Done.Add(key);
        _state.Seen[target] = seen;
        Save();
    }

    public void Set(CsvGameExportRow row, bool completed)
    {
        Reload();
        Apply(_state.Done, IgnoredGames.KeysOf(row), completed);
        Save();
    }

    /// <summary>
    /// Starts syncing a target with the ticks it holds now (rows without a completion column are
    /// null). Nothing is saved until <see cref="CompletionSync.Commit"/>, so a dry run just drops it.
    /// </summary>
    /// <param name="target">Stable id of the target, e.g. <c>CSV:C:\games.csv</c> or <c>Notion:&lt;db id&gt;</c>.</param>
    public CompletionSync Begin(string target, IEnumerable<(CsvGameExportRow Row, bool? Completed)> rows)
    {
        Reload();
        var seen = _state.Seen.GetValueOrDefault(target) ?? [];
        var changes = new List<(IReadOnlyList<string> Keys, bool Completed)>();
        foreach (var (row, completed) in rows)
        {
            if (completed is not { } now) continue;
            var keys = IgnoredGames.KeysOf(row);
            if (keys.Any(seen.Contains) != now) changes.Add((keys, now));
        }

        var done = new HashSet<string>(_state.Done, StringComparer.OrdinalIgnoreCase);
        foreach (var (keys, completed) in changes) Apply(done, keys, completed);
        return new CompletionSync(this, target, done, changes);
    }

    // Saves what the target holds now, re-applying its changes on top of a fresh read (a tick set in
    // the library viewer meanwhile survives).
    internal void Commit(
        string target, IReadOnlyList<(IReadOnlyList<string> Keys, bool Completed)> changes,
        IEnumerable<(CsvGameExportRow Row, bool Completed)> targetNow)
    {
        Reload();
        foreach (var (keys, completed) in changes) Apply(_state.Done, keys, completed);
        _state.Seen[target] = new HashSet<string>(
            targetNow.Where(r => r.Completed).SelectMany(r => IgnoredGames.KeysOf(r.Row)),
            StringComparer.OrdinalIgnoreCase);
        Save();
    }

    private static void Apply(HashSet<string> done, IEnumerable<string> keys, bool completed)
    {
        foreach (var key in keys)
            if (completed) done.Add(key);
            else done.Remove(key);
    }

    private void Save()
    {
        var json = new StoredState
        {
            Done = _state.Done.Order(StringComparer.OrdinalIgnoreCase).ToList(),
            Seen = _state.Seen.ToDictionary(
                p => p.Key, List<string>? (p) => p.Value.Order(StringComparer.OrdinalIgnoreCase).ToList(),
                StringComparer.OrdinalIgnoreCase)
        };
        _store?.Save(StateKey, JsonConvert.SerializeObject(json));
    }

    private static State Parse(string? json)
    {
        var state = new State();
        if (string.IsNullOrWhiteSpace(json)) return state;
        try
        {
            var stored = JsonConvert.DeserializeObject<StoredState>(json);
            foreach (var key in stored?.Done ?? []) state.Done.Add(key);
            foreach (var (target, keys) in stored?.Seen ?? [])
                state.Seen[target] = new HashSet<string>(keys ?? [], StringComparer.OrdinalIgnoreCase);
        }
        catch (JsonException)
        {
            // A damaged file starts over empty: the trackers still hold their ticks and fill it again.
        }

        return state;
    }

    private sealed class State
    {
        public HashSet<string> Done { get; } = new(StringComparer.OrdinalIgnoreCase);

        public Dictionary<string, HashSet<string>> Seen { get; } = new(StringComparer.OrdinalIgnoreCase);
    }

    private sealed class StoredState
    {
        [JsonProperty("done")] public List<string>? Done { get; set; }

        [JsonProperty("seen")] public Dictionary<string, List<string>?>? Seen { get; set; }
    }
}

public sealed class CompletionSync
{
    private readonly CompletedGames _owner;
    private readonly string _target;
    private readonly HashSet<string> _done;
    private readonly IReadOnlyList<(IReadOnlyList<string> Keys, bool Completed)> _changes;

    internal CompletionSync(
        CompletedGames owner, string target, HashSet<string> done,
        IReadOnlyList<(IReadOnlyList<string> Keys, bool Completed)> changes)
    {
        _owner = owner;
        _target = target;
        _done = done;
        _changes = changes;
    }

    public bool IsCompleted(CsvGameExportRow row) => IgnoredGames.KeysOf(row).Any(_done.Contains);

    /// <summary>Saves the merged list and the ticks the target holds now (what was written, or what it
    /// already had where nothing could be written). Call only after a real write succeeded.</summary>
    public void Commit(IEnumerable<(CsvGameExportRow Row, bool Completed)> targetNow) =>
        _owner.Commit(_target, _changes, targetNow);
}

using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.Linq;

namespace Pilewise.Services;

public sealed class CompletedGames
{
    private const string StateKey = "completed-games";

    private readonly IStateStore? _store;
    private State _state = new();
    private string? _raw;

    public CompletedGames(IStateStore? store = null)
    {
        _store = store;
        Reload();
    }

    public bool IsCompleted(CsvGameExportRow row) => IgnoredGames.KeysOf(row).Any(_state.Done.Contains);

    public int Count => _state.Done.Count;

    public void Reload()
    {
        var json = _store?.Load(StateKey);
        if (json != null && json == _raw) return;
        _raw = json;
        _state = Parse(json);
    }

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
        _raw = JsonConvert.SerializeObject(json);
        _store?.Save(StateKey, _raw);
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

    public void Commit(IEnumerable<(CsvGameExportRow Row, bool Completed)> targetNow) =>
        _owner.Commit(_target, _changes, targetNow);
}

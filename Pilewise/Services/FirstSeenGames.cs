using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace Pilewise.Services;

/// <summary>
/// When Pilewise first saw each game, so the library viewer can show what was added recently (no
/// store reports a purchase date). Every library fetch records the games it got
/// (<see cref="Record"/>); a game is keyed like <see cref="IgnoredGames.KeysOf"/>, so it keeps its
/// date when it later shows up on another store too.
/// </summary>
/// <remarks>
/// The first fetch of a store sets that store's <b>baseline</b>: its games were already owned, so they
/// get no "added" date. Only games that appear after it count. Kept in the state store (key
/// <c>first-seen</c>, JSON); re-read on every access so the app and <c>--sync</c> agree.
/// </remarks>
public sealed class FirstSeenGames
{
    public const int RecentDays = 30;

    private const string NameKeyPrefix = "Name:";

    private const string StateKey = "first-seen";
    private const string DateFormat = "yyyy-MM-dd";

    private readonly IStateStore? _store;
    private State _state = new();

    public FirstSeenGames(IStateStore? store = null)
    {
        _store = store;
        Reload();
    }

    public void Record(IEnumerable<CsvGameExportRow> rows, IReadOnlySet<string> fetchedStores, DateOnly today)
    {
        Reload();
        var changed = false;
        var day = Format(today);

        foreach (var store in fetchedStores)
            changed |= _state.Baselines.TryAdd(store, day);

        foreach (var row in rows)
        {
            var keys = IgnoredGames.KeysOf(row);
            if (keys.Count == 0) continue;

            // A game already known under one key (e.g. Steam) keeps that date for its new keys (GOG).
            var known = keys.Select(k => _state.Keys.GetValueOrDefault(k)).Where(d => d != null).Min();
            foreach (var key in keys)
                changed |= _state.Keys.TryAdd(key, known ?? day);
        }

        if (changed) Save();
    }

    /// <summary>
    /// The day Pilewise first saw this game, when that's after its stores' baselines (so it was really
    /// added then); null for games owned before Pilewise started tracking, or never seen.
    /// </summary>
    public DateOnly? AddedOn(CsvGameExportRow row)
    {
        DateOnly? added = null;
        foreach (var key in IgnoredGames.KeysOf(row))
        {
            if (Parse(_state.Keys.GetValueOrDefault(key)) is not { } seen) continue;
            if (Baseline(key) is not { } baseline || seen <= baseline) return null;
            if (added == null || seen < added) added = seen;
        }

        return added;
    }

    public static bool IsRecent(DateOnly? addedOn, DateOnly today)
    {
        return addedOn is { } day && today.DayNumber - day.DayNumber <= RecentDays;
    }

    private DateOnly? Baseline(string key)
    {
        if (key.StartsWith(NameKeyPrefix, StringComparison.Ordinal))
            return _state.Baselines.Values.Select(Parse).Min();

        var store = key[..Math.Max(key.IndexOf(':'), 0)];
        return Parse(_state.Baselines.GetValueOrDefault(store));
    }

    private static string Format(DateOnly day)
    {
        return day.ToString(DateFormat, CultureInfo.InvariantCulture);
    }

    private static DateOnly? Parse(string? value)
    {
        return DateOnly.TryParseExact(value, DateFormat, CultureInfo.InvariantCulture, DateTimeStyles.None, out var day)
            ? day
            : null;
    }

    private void Reload()
    {
        if (_store == null) return;
        try
        {
            _state = JsonConvert.DeserializeObject<State>(_store.Load(StateKey) ?? "") ?? new State();
        }
        catch (JsonException)
        {
            _state = new State();
        }

        _state.Baselines ??= new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        _state.Keys ??= new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    }

    private void Save()
    {
        _store?.Save(StateKey, JsonConvert.SerializeObject(_state));
    }

    private sealed class State
    {
        public Dictionary<string, string> Baselines { get; set; } = new(StringComparer.OrdinalIgnoreCase);

        public Dictionary<string, string> Keys { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    }
}
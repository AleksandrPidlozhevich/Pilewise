using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace Pilewise.Services;

public sealed class SyncHistory
{
    public const string Csv = "CSV";
    public const string Notion = "Notion";
    public const string GoogleSheets = "Google Sheets";

    private const string StateKey = "last-sync";

    private readonly IStateStore _store;
    private readonly Dictionary<string, DateTimeOffset> _entries;

    public SyncHistory(IStateStore store)
    {
        _store = store;
        _entries = Parse(store.Load(StateKey));
    }

    public static string KeyOf(SyncTarget target) => target switch
    {
        SyncTarget.Csv => Csv,
        SyncTarget.Notion => Notion,
        _ => GoogleSheets
    };

    // Re-read on every access: the in-app auto-sync records through its own instance (via HeadlessSync),
    // so a cached copy would go stale and a later Record would overwrite the newer time.
    public DateTimeOffset? Get(string target)
    {
        Reload();
        return _entries.TryGetValue(target, out var when) ? when : null;
    }

    public void Record(string target, DateTimeOffset when)
    {
        Reload();
        _entries[target] = when;
        _store.Save(StateKey, string.Join("\n",
            _entries.Select(e => $"{e.Key}={e.Value.ToString("O", CultureInfo.InvariantCulture)}")));
    }

    public static string? Describe(DateTimeOffset? when, DateTimeOffset now)
    {
        if (when == null) return null;

        var local = when.Value.ToOffset(now.Offset);
        var days = (now.Date - local.Date).Days;
        var day = days switch
        {
            0 => "today",
            1 => "yesterday",
            _ => local.ToString(local.Year == now.Year ? "d MMM" : "d MMM yyyy", CultureInfo.CurrentCulture)
        };
        return $"Last sync: {day} {local:HH:mm}";
    }

    private void Reload()
    {
        _entries.Clear();
        foreach (var (key, value) in Parse(_store.Load(StateKey))) _entries[key] = value;
    }

    private static Dictionary<string, DateTimeOffset> Parse(string? content)
    {
        var entries = new Dictionary<string, DateTimeOffset>();
        if (string.IsNullOrEmpty(content)) return entries;

        foreach (var line in content.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var separator = line.LastIndexOf('=');
            if (separator > 0 && DateTimeOffset.TryParse(line[(separator + 1)..].Trim(),
                    CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var when))
                entries[line[..separator]] = when;
        }

        return entries;
    }
}

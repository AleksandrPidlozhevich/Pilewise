using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace Pilewise.Services;

public sealed record AutoSyncSettings(int IntervalHours, IReadOnlyList<SyncTarget> Targets)
{
    /// <summary>The intervals offered in the UI, in hours (index = combo box position).</summary>
    public static readonly IReadOnlyList<int> Intervals = [0, 1, 6, 24];

    public static readonly IReadOnlyList<string> IntervalLabels = ["Off", "Hourly", "Every 6h", "Daily"];

    public static AutoSyncSettings Off { get; } = new(0, []);

    public bool IsOn => IntervalHours > 0 && Targets.Count > 0;

    public string? Serialize() =>
        IsOn ? $"{IntervalHours}h {string.Join(", ", Targets.Select(Name))}" : null;

    public static AutoSyncSettings Parse(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return Off;

        var parts = value.Trim().Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length != 2 || !parts[0].EndsWith('h') ||
            !int.TryParse(parts[0][..^1], NumberStyles.None, CultureInfo.InvariantCulture, out var hours) ||
            !Intervals.Contains(hours) || hours == 0)
            return Off;

        var targets = new List<SyncTarget>();
        foreach (var name in parts[1].Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            SyncTarget? target = name.ToLowerInvariant() switch
            {
                "notion" => SyncTarget.Notion,
                "sheets" => SyncTarget.Sheets,
                "csv" => SyncTarget.Csv,
                _ => null
            };
            if (target is { } t && !targets.Contains(t)) targets.Add(t);
        }

        return targets.Count == 0 ? Off : new AutoSyncSettings(hours, targets);
    }

    private static string Name(SyncTarget target) => target switch
    {
        SyncTarget.Notion => "Notion",
        SyncTarget.Sheets => "Sheets",
        _ => "CSV"
    };
}

public static class AutoSyncPlanner
{
    public static readonly TimeSpan RetryAfterFailure = TimeSpan.FromHours(1);
    public static readonly TimeSpan MinCheck = TimeSpan.FromMinutes(1);
    public static readonly TimeSpan MaxCheck = TimeSpan.FromMinutes(30);

    public static TimeSpan NextCheck(
        AutoSyncSettings settings,
        Func<SyncTarget, DateTimeOffset?> lastSuccess,
        IReadOnlyDictionary<SyncTarget, DateTimeOffset> lastFailure,
        Func<SyncTarget, bool> canRun,
        DateTimeOffset now)
    {
        if (!settings.IsOn) return MaxCheck;

        var interval = TimeSpan.FromHours(settings.IntervalHours);
        var next = settings.Targets.Where(canRun).Select(t =>
        {
            var due = lastSuccess(t) is { } last ? last + interval : now;
            return lastFailure.TryGetValue(t, out var failed) && failed + RetryAfterFailure > due
                ? failed + RetryAfterFailure
                : due;
        }).DefaultIfEmpty(now + MaxCheck).Min();
        return TimeSpan.FromTicks(Math.Clamp((next - now).Ticks, MinCheck.Ticks, MaxCheck.Ticks));
    }

    public static IReadOnlyList<SyncTarget> Due(
        AutoSyncSettings settings,
        Func<SyncTarget, DateTimeOffset?> lastSuccess,
        IReadOnlyDictionary<SyncTarget, DateTimeOffset> lastFailure,
        Func<SyncTarget, bool> canRun,
        DateTimeOffset now)
    {
        if (!settings.IsOn) return [];

        var interval = TimeSpan.FromHours(settings.IntervalHours);
        return settings.Targets
            .Where(canRun)
            .Where(t => lastSuccess(t) is not { } last || now - last >= interval)
            .Where(t => !lastFailure.TryGetValue(t, out var failed) || now - failed >= RetryAfterFailure)
            .ToList();
    }
}

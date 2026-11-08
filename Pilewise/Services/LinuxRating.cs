using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;

namespace Pilewise.Services;

/// <summary>
/// Values for the opt-in Linux column. Native comes from Steam; medals from ProtonDB.
/// <see cref="Unknown"/> means we successfully checked and have no useful signal (not "won't run").
/// <see cref="Unavailable"/> means the check failed or was aborted — retry later; it is not a rating.
/// </summary>
public static class LinuxRating
{
    public const string Column = "Linux";
    public const string Unknown = "Unknown";
    public const string Unavailable = "Unavailable";

    /// <summary>
    /// ProtonDB confidence values observed on the summaries API (case-insensitive).
    /// <c>inadequate</c> / <c>low</c> mean too little evidence to treat a medal as authoritative.
    /// </summary>
    public static bool IsLowConfidence(string? confidence) =>
        confidence?.Trim().ToLowerInvariant() is "inadequate" or "low";

    public static bool IsTransient(string? value) =>
        string.Equals(value, Unavailable, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// True for a stored cell that should survive a later transient lookup failure.
    /// </summary>
    public static bool IsDurable(string? value) =>
        !string.IsNullOrWhiteSpace(value) && !IsTransient(value);

    public static string Label(bool? native, string? tier, string? confidence = null)
    {
        if (native == true) return "Native";

        // Steam platform data may be inconclusive (null): still surface a ProtonDB medal when present.
        if (Medal(tier) is not { } medal) return Unknown;

        if (!IsLowConfidence(confidence)) return medal;

        // Thin evidence: never present Borked as a hard "won't run"; mark other medals as provisional.
        return medal == "Borked" ? Unknown : medal + "?";
    }

    /// <summary>
    /// True when the value is known. No Steam id → Unknown. A Steam id absent from
    /// <paramref name="byAppId"/> was not checked — keep whatever the row already has.
    /// Transient results never erase a durable rating already on the row.
    /// </summary>
    public static bool TryGet(CsvGameExportRow row, IReadOnlyDictionary<int, string>? byAppId, out string value)
    {
        if (row.SteamGameId is not int id)
        {
            value = Unknown;
            return true;
        }

        if (byAppId != null && byAppId.TryGetValue(id, out value!))
        {
            if (IsTransient(value) && IsDurable(row.Linux))
            {
                value = row.Linux!;
                return true;
            }

            return true;
        }

        value = string.Empty;
        return false;
    }

    public static void Apply(IEnumerable<CsvGameExportRow> rows, IReadOnlyDictionary<int, string> byAppId)
    {
        foreach (var row in rows)
            if (TryGet(row, byAppId, out var value))
                row.Linux = value;
    }

    public static string? Medal(string? tier) => tier?.Trim().ToLowerInvariant() switch
    {
        "platinum" => "Platinum",
        "gold" => "Gold",
        "silver" => "Silver",
        "bronze" => "Bronze",
        "borked" => "Borked",
        _ => null
    };

    public static bool? ReadNative(string json, int appId)
    {
        var node = JObject.Parse(json)[appId.ToString()];
        if (node?["success"]?.Value<bool>() != true) return null;
        var linux = node["data"]?["platforms"]?["linux"];
        return linux?.Type == JTokenType.Boolean ? linux.Value<bool>() : null;
    }

    /// <summary>
    /// Effective ProtonDB tier + confidence. When <c>tier</c> is <c>pending</c>, falls back to
    /// <c>provisionalTier</c> (ProtonDB summaries API).
    /// </summary>
    public static (string? Tier, string? Confidence) ReadSummary(string json)
    {
        var obj = JObject.Parse(json);
        var tier = obj["tier"]?.Value<string>();
        if (string.Equals(tier, "pending", StringComparison.OrdinalIgnoreCase))
            tier = obj["provisionalTier"]?.Value<string>();
        return (tier, obj["confidence"]?.Value<string>());
    }

    public static string? ReadTier(string json) => Medal(ReadSummary(json).Tier);
}

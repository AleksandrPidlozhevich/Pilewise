using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;

namespace Pilewise.Services;

public static class LinuxRating
{
    public const string Column = "Linux";
    public const string Unknown = "Unknown";
    public const string Unavailable = "Unavailable";

    public static string Label(bool? native, string? tier)
    {
        if (native == true) return "Native";
        if (string.IsNullOrWhiteSpace(tier)) return Unknown;

        var text = tier.Trim();
        var soft = text.EndsWith('?');
        var medal = Medal(soft ? text[..^1] : text);
        return medal == null ? Unknown : soft ? medal + "?" : medal;
    }

    public static bool TryGet(CsvGameExportRow row, IReadOnlyDictionary<int, string>? byAppId, out string value)
    {
        if (row.SteamGameId is not int id)
        {
            value = Unknown;
            return true;
        }

        value = string.Empty;
        return byAppId != null && byAppId.TryGetValue(id, out value!)
            && !(value == Unavailable && !string.IsNullOrEmpty(row.Linux) && row.Linux != Unavailable);
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

    public static string? ReadTier(string json)
    {
        var obj = JObject.Parse(json);
        var tier = obj["tier"]?.Value<string>();
        if (string.Equals(tier, "pending", StringComparison.OrdinalIgnoreCase))
            tier = obj["provisionalTier"]?.Value<string>();

        var medal = Medal(tier);
        if (medal == null) return null;

        var confidence = obj["confidence"]?.Value<string>()?.Trim().ToLowerInvariant();
        if (confidence is "inadequate" or "low")
            return medal == "Borked" ? null : medal + "?";

        return medal;
    }
}

using Newtonsoft.Json.Linq;
using System.Collections.Generic;

namespace Pilewise.Services;

/// <summary>
/// Values for the opt-in Linux column. Native comes from Steam; medals from ProtonDB.
/// A missing report is <see cref="Unknown"/>, not "won't run".
/// </summary>
public static class LinuxRating
{
    public const string Column = "Linux";
    public const string Unknown = "Unknown";

    public static string Label(bool? native, string? tier) =>
        native == true ? "Native"
        : native == false && Medal(tier) is { } medal ? medal
        : Unknown;

    /// <summary>
    /// True when the value is known. No Steam id → Unknown. A Steam id absent from
    /// <paramref name="byAppId"/> was not checked — keep whatever the row already has.
    /// </summary>
    public static bool TryGet(CsvGameExportRow row, IReadOnlyDictionary<int, string>? byAppId, out string value)
    {
        if (row.SteamGameId is not int id)
        {
            value = Unknown;
            return true;
        }

        if (byAppId != null && byAppId.TryGetValue(id, out value!))
            return true;

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

    public static string? ReadTier(string json) => Medal(JObject.Parse(json)["tier"]?.Value<string>());
}

using Newtonsoft.Json;
using System.Collections.Generic;

namespace Pilewise.Models;

public class SteamGamesResponse
{
    public SteamGamesInnerResponse? Response { get; set; }
}

public class SteamGamesInnerResponse
{
    public List<SteamGame>? Games { get; set; }
}

public class SteamGame
{
    public string Name { get; set; } = string.Empty;

    [JsonProperty("appid")] public int GameID { get; set; }

    [JsonProperty("playtime_forever")] public int PlaytimeMinutes { get; set; }

    /// <summary>Unix time of the last session, 0 when never played (or hidden by privacy settings).</summary>
    [JsonProperty("rtime_last_played")] public long LastPlayedUnix { get; set; }
}
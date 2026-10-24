using Newtonsoft.Json;
using Pilewise.Services;
using System;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace Pilewise.Models;

public class SteamApiClient : ISteamClient
{
    private const int MaxRetries = 5;
    private const string ApiBase = "https://api.steampowered.com";
    private static readonly HttpClient _httpClient = new();

    private readonly ILogService _log;

    public SteamApiClient(ILogService log)
    {
        _log = log;
    }

    public async Task<SteamGamesResponse> GetOwnedGames(string? steamId, string? apiKey, CancellationToken ct = default)
    {
        apiKey = apiKey?.Trim();
        steamId = steamId?.Trim();

        if (string.IsNullOrWhiteSpace(apiKey))
            throw new ArgumentException("Steam API key cannot be empty", nameof(apiKey));

        if (string.IsNullOrWhiteSpace(steamId))
            throw new ArgumentException("Steam ID cannot be empty", nameof(steamId));

        steamId = await ResolveSteamIdAsync(steamId, apiKey, ct);

        // include_played_free_games=1 so free games (e.g. demos/prologues) are returned too;
        // without it Steam omits them and they never reach the export.
        var url = $"{ApiBase}/IPlayerService/GetOwnedGames/v0001/?key={Uri.EscapeDataString(apiKey)}" +
                  $"&steamid={Uri.EscapeDataString(steamId)}&format=json&include_appinfo=1&include_played_free_games=1";

        for (var retry = 0;; retry++)
        {
            using var response = await SendAsync(url, ct);

            if (response.StatusCode == HttpStatusCode.TooManyRequests)
            {
                if (retry >= MaxRetries)
                {
                    _log.WriteError("Steam: exceeded max retries due to rate limiting (429).");
                    throw new HttpRequestException(
                        "The attempt limit has been exceeded. Steam API is temporarily unavailable due to request restrictions.",
                        null, HttpStatusCode.TooManyRequests);
                }

                var delay = TimeSpan.FromSeconds(Math.Pow(2, retry));
                _log.WriteWarning($"Steam: 429 Too Many Requests, retrying in {delay.TotalSeconds:F0} s.");
                await Task.Delay(delay, ct);
                continue;
            }

            if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            {
                _log.WriteError($"Steam API key is invalid ({(int)response.StatusCode} {response.StatusCode}).");
                throw new UnauthorizedAccessException("Invalid Steam API key. Check the correctness of the key.");
            }

            if (!response.IsSuccessStatusCode)
                throw new HttpRequestException(
                    $"Steam API answered {(int)response.StatusCode} {response.StatusCode}. " + CheckHint, null,
                    response.StatusCode);

            var result = JsonConvert.DeserializeObject<SteamGamesResponse>(await response.Content.ReadAsStringAsync(ct));
            if (result?.Response?.Games == null)
            {
                _log.WriteError("Steam API returned empty or invalid game list.");
                throw new InvalidOperationException(
                    "Failed to receive a list of games. Check the Steam ID correctness and adjust the privacy of the profile.");
            }

            _log.WriteInfo($"Steam API returned {result.Response.Games.Count} games.");
            return result;
        }
    }

    private const string CheckHint =
        "Check:\n1. The correctness of the API key\n2. Correct Steam ID\n3. Setting the Privacy of Steam Profile";

    // A network failure becomes one readable error; the URL (it carries the API key) is never logged.
    private async Task<HttpResponseMessage> SendAsync(string url, CancellationToken ct)
    {
        try
        {
            return await _httpClient.GetAsync(url, ct);
        }
        catch (HttpRequestException ex)
        {
            _log.WriteException(ex, "Error when contacting Steam API");
            throw new HttpRequestException($"Error when contacting Steam API: {ex.Message}\n{CheckHint}", ex);
        }
    }

    /// <summary>
    /// Turns whatever the user entered (SteamID64, profile link, vanity name, SteamID3…) into a
    /// SteamID64, resolving custom-URL names through <c>ISteamUser/ResolveVanityURL</c>.
    /// </summary>
    private async Task<string> ResolveSteamIdAsync(string input, string apiKey, CancellationToken ct)
    {
        var parsed = SteamIdParser.Parse(input);
        if (parsed.SteamId64 != null) return parsed.SteamId64;
        if (parsed.VanityName == null)
            throw new ArgumentException(
                "Steam ID not recognised. Enter your 17-digit Steam ID or paste your Steam profile link.");

        var url = $"{ApiBase}/ISteamUser/ResolveVanityURL/v0001/" +
                  $"?key={Uri.EscapeDataString(apiKey)}&vanityurl={Uri.EscapeDataString(parsed.VanityName)}";
        using var response = await SendAsync(url, ct);
        if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            throw new UnauthorizedAccessException("Invalid Steam API key. Check the correctness of the key.");
        response.EnsureSuccessStatusCode();

        var json = JsonConvert.DeserializeObject<VanityResponse>(await response.Content.ReadAsStringAsync(ct));
        // success: 1 = resolved, 42 = no profile with that custom URL.
        if (json?.Response?.Success != 1 || string.IsNullOrEmpty(json.Response.SteamId))
            throw new ArgumentException(
                $"No Steam profile found for \"{parsed.VanityName}\". " +
                "Check the name, or paste your profile link / 17-digit Steam ID instead.");

        _log.WriteInfo($"Steam: resolved custom URL \"{parsed.VanityName}\" to a SteamID64.");
        return json.Response.SteamId;
    }

    private sealed class VanityResponse
    {
        [JsonProperty("response")] public VanityResult? Response { get; set; }
    }

    private sealed class VanityResult
    {
        [JsonProperty("success")] public int Success { get; set; }

        [JsonProperty("steamid")] public string? SteamId { get; set; }
    }
}

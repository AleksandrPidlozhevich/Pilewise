using Newtonsoft.Json.Linq;
using Pilewise.Services;
using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Threading;
using System.Threading.Tasks;

namespace Pilewise.Models;

public sealed class XboxApiClient(ISecretStore secrets, ILogService log) : IXboxClient
{
    private const string TitleHistoryUrlTemplate =
        "https://titlehub.xboxlive.com/users/xuid({0})/titles/titlehistory/decoration/detail";

    // Legacy Xbox inventory (owned/purchased). The authoritative source for the exported library;
    // when it returns nothing, nothing is exported.
    private const string InventoryUrl = "https://inventory.xboxlive.com/users/me/inventory";

    private const string LicensingRelyingParty = "http://licensing.xboxlive.com";

    private readonly XboxSession _session = new(secrets, log);

    public Uri LoginRequestUri => _session.LoginRequestUri;

    public Uri LoginRedirectUri => _session.LoginRedirectUri;

    public void SignOut()
    {
        _session.SignOut();
    }

    public void OpenLoginPageInBrowser()
    {
        _session.OpenLoginPageInBrowser();
    }

    public Task<StoreAuthFromCacheStatus> TryAuthenticateFromCacheAsync()
    {
        return _session.TryAuthenticateFromCacheAsync();
    }

    public Task<bool> AuthenticateWithAuthorizationCodeAsync(string authorizationCode)
    {
        return _session.AuthenticateWithAuthorizationCodeAsync(authorizationCode);
    }

    /// <summary>
    /// Fetches the user's owned Xbox library — the purchased/owned titles from the inventory
    /// service. Title history (played / achievements) is used <b>only</b> to resolve display names
    /// for those owned titles; it is never a source of games, because it also surfaces titles
    /// played on other platforms through Xbox network integration. If the library is empty, an
    /// empty list is returned (nothing is exported).
    /// </summary>
    public async Task<List<XboxGame>> GetGamesAsync(
        IProgress<StoreFetchProgress>? progress = null, CancellationToken ct = default)
    {
        EnsureAuthenticated();

        progress?.Report(new StoreFetchProgress(0, 1, "Loading Xbox library"));

        var ownedTitleIds = await TryGetLibraryTitleIdsAsync(ct);
        if (ownedTitleIds.Count == 0)
        {
            log.WriteInfo("Xbox: owned library is empty — nothing to export.");
            progress?.Report(new StoreFetchProgress(1, 1, "Loading Xbox library"));
            return new List<XboxGame>();
        }

        var names = await GetTitleNamesAsync(ct);

        var games = new List<XboxGame>(ownedTitleIds.Count);
        var unnamed = 0;
        foreach (var titleId in ownedTitleIds)
        {
            names.TryGetValue(titleId, out var info);
            if (info.Name == null) unnamed++;

            // Owned games are never Game Pass; the title id is a last-resort name so a library
            // entry is never silently dropped just because no display name could be resolved.
            games.Add(new XboxGame
            {
                TitleId = titleId,
                Name = info.Name ?? titleId,
                LastPlayed = info.LastPlayed,
                ImageUrl = info.Image,
                ProductId = info.ProductId
            });
        }

        progress?.Report(new StoreFetchProgress(1, 1, "Loading Xbox library"));
        log.WriteInfo($"Xbox: fetched {games.Count} owned titles" +
                      (unnamed > 0 ? $" ({unnamed} without a resolved name)." : "."));
        return games;
    }

    private async Task<Dictionary<string, (string? Name, DateTimeOffset? LastPlayed, string? Image, string? ProductId)>>
        GetTitleNamesAsync(CancellationToken ct)
    {
        var map =
            new Dictionary<string, (string?, DateTimeOffset?, string?, string?)>(StringComparer.OrdinalIgnoreCase);
        try
        {
            var url = string.Format(TitleHistoryUrlTemplate, _session.Xuid);
            using var response =
                await _session.SendAuthorizedAsync(() => XboxSession.BuildXboxApiRequest(HttpMethod.Get, url), ct);
            if (!response.IsSuccessStatusCode)
            {
                log.WriteWarning($"Xbox: title-name lookup failed: {response.StatusCode}.");
                return map;
            }

            var body = await response.Content.ReadAsStringAsync(ct);
            var titles = JObject.Parse(body)["titles"] as JArray ?? new JArray();

            foreach (var title in titles)
            {
                var titleId = title["titleId"]?.Value<string>();
                var name = title["name"]?.Value<string>();
                if (string.IsNullOrEmpty(titleId) || string.IsNullOrEmpty(name)) continue;

                DateTimeOffset? lastPlayed = null;
                var lastPlayedRaw = title["titleHistory"]?["lastTimePlayed"]?.Value<string>();
                if (DateTimeOffset.TryParse(lastPlayedRaw, out var parsed)) lastPlayed = parsed;

                var productId = title["productId"]?.Value<string>() ?? title["detail"]?["productId"]?.Value<string>();
                map[titleId] = (name, lastPlayed, title["displayImage"]?.Value<string>(), productId);
            }
        }
        catch (Exception ex)
        {
            log.WriteWarning($"Xbox: title-name lookup failed: {ex.Message}");
        }

        return map;
    }

    private async Task<HashSet<string>> TryGetLibraryTitleIdsAsync(CancellationToken ct)
    {
        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            var xsts = await _session.RequestXstsAsync(LicensingRelyingParty);
            if (xsts == null) return ids;

            using var request = XboxSession.BuildXboxApiRequest(HttpMethod.Get, InventoryUrl);
            request.Headers.Authorization =
                new AuthenticationHeaderValue("XBL3.0", $"x={xsts.Value.UserHash};{xsts.Value.Token}");

            using var response = await XboxSession.Http.SendAsync(request, ct);
            if (!response.IsSuccessStatusCode)
            {
                log.WriteWarning($"Xbox: inventory request failed: {response.StatusCode}.");
                return ids;
            }

            var items = JObject.Parse(await response.Content.ReadAsStringAsync(ct))["items"] as JArray;
            if (items != null)
                foreach (var item in items)
                {
                    var titleId = item["titleId"]?.Value<string>();
                    if (!string.IsNullOrEmpty(titleId)) ids.Add(titleId);
                }
        }
        catch (Exception ex)
        {
            log.WriteWarning($"Xbox: owned inventory lookup failed: {ex.Message}");
        }

        return ids;
    }

    private void EnsureAuthenticated()
    {
        if (!_session.IsAuthenticated)
            throw new InvalidOperationException("Xbox client is not authenticated. Call authenticate first.");
    }
}
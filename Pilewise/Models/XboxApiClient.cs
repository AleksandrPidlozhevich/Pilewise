using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Pilewise.Services;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Pilewise.Models;

/// <summary>
/// Hand-rolled client for the (unofficial) Xbox Live API. Mirrors <see cref="GogApiClient"/>:
/// the user logs in via the browser and pastes the redirect's authorization code; the Microsoft
/// account refresh token is cached (encrypted) and the short-lived XSTS token is rebuilt from it
/// each session.
///
/// Auth chain (all hand-rolled, no third-party auth library):
///   1. MSA OAuth (login.live.com) using the well-known public Minecraft client id with the
///      legacy MBI_SSL scope — code/refresh → access token.
///   2. user.auth.xboxlive.com/user/authenticate → user token.
///   3. xsts.auth.xboxlive.com/xsts/authorize → XSTS token (+ user hash and XUID).
/// Requests then carry <c>Authorization: XBL3.0 x={uhs};{xsts}</c>.
/// </summary>
public class XboxApiClient : IXboxClient
{
    // Well-known public Minecraft launcher client id (no secret). The relying party is chosen at
    // the XSTS step, so a token obtained here works for general Xbox Live services too.
    private const string ClientId = "00000000402b5328";
    private const string Scope = "service::user.auth.xboxlive.com::MBI_SSL";
    private const string RedirectUri = "https://login.live.com/oauth20_desktop.srf";

    // The MBI_SSL scope only supports the implicit flow: tokens come back in the redirect URL
    // fragment (response_type=token), there is no authorization code to exchange.
    private const string AuthorizeUrl =
        "https://login.live.com/oauth20_authorize.srf?client_id=" + ClientId +
        "&response_type=token&display=touch&locale=en" +
        "&scope=service::user.auth.xboxlive.com::MBI_SSL" +
        "&redirect_uri=https%3A%2F%2Flogin.live.com%2Foauth20_desktop.srf";

    private const string TokenUrl = "https://login.live.com/oauth20_token.srf";
    private const string UserAuthUrl = "https://user.auth.xboxlive.com/user/authenticate";
    private const string XstsUrl = "https://xsts.auth.xboxlive.com/xsts/authorize";

    // Recently-played titles (decorated with detail so we get the canonical name + last-played).
    private const string TitleHistoryUrlTemplate =
        "https://titlehub.xboxlive.com/users/xuid({0})/titles/titlehistory/decoration/detail";

    // Legacy Xbox inventory (owned/purchased). The authoritative source for the exported library;
    // when it returns nothing, nothing is exported.
    private const string InventoryUrl = "https://inventory.xboxlive.com/users/me/inventory";

    // Relying parties requested at the XSTS step for each service.
    private const string XboxLiveRelyingParty = "http://xboxlive.com";
    private const string LicensingRelyingParty = "http://licensing.xboxlive.com";

    // Key under which the MSA refresh token is kept in the secret store.
    private const string SecretKey = "xbox";

    private static readonly HttpClient _httpClient = new();

    private readonly ISecretStore _secrets;
    private readonly ILogService _log;

    // Serialises token refreshes so concurrent requests that hit a 401 don't fire several at once.
    private readonly SemaphoreSlim _refreshLock = new(1, 1);

    private string? _refreshToken;
    private string? _userToken; // user.auth token, reusable to mint XSTS for other relying parties
    private string? _xstsToken; // XSTS for http://xboxlive.com
    private string? _userHash;
    private string? _xuid;

    public XboxApiClient(ISecretStore secretStore, ILogService log)
    {
        _secrets = secretStore;
        _log = log;
    }

    public bool IsAuthenticated =>
        !string.IsNullOrEmpty(_xstsToken) && !string.IsNullOrEmpty(_userHash) && !string.IsNullOrEmpty(_xuid);

    public Uri LoginRequestUri => new(AuthorizeUrl);

    public Uri LoginRedirectUri => new(RedirectUri);

    public void SignOut()
    {
        _refreshToken = _userToken = _xstsToken = _userHash = _xuid = null;
        _secrets.Delete(SecretKey);
        _log.WriteInfo("Xbox: signed out (cleared cached session).");
    }

    public async Task<StoreAuthFromCacheStatus> TryAuthenticateFromCacheAsync()
    {
        var raw = _secrets.Load(SecretKey);
        if (string.IsNullOrEmpty(raw)) return StoreAuthFromCacheStatus.NoCachedSession;

        var (access, refresh) = ReadSession(raw);

        try
        {
            // Prefer a refresh token when one exists (renewable); otherwise reuse the stored MSA
            // access token directly until it expires (the MBI_SSL flow returns no refresh token).
            if (!string.IsNullOrEmpty(refresh) && await AuthenticateWithRefreshTokenAsync(refresh))
                return StoreAuthFromCacheStatus.Authenticated;

            if (!string.IsNullOrEmpty(access) && await AuthenticateCoreAsync(access))
            {
                SaveSession(access, refresh);
                return StoreAuthFromCacheStatus.Authenticated;
            }

            return StoreAuthFromCacheStatus.SessionExpired;
        }
        catch (Exception ex)
        {
            _log.WriteWarning($"Xbox: cached session invalid, re-login required. {ex.Message}");
            return StoreAuthFromCacheStatus.SessionExpired;
        }
    }

    // The session is stored as a small JSON blob: the MSA access token (always) plus the refresh
    // token (when the flow provides one). Persisted only after the session is confirmed valid.
    private void SaveSession(string? accessToken, string? refreshToken)
    {
        _secrets.Save(SecretKey,
            JsonConvert.SerializeObject(new { access_token = accessToken, refresh_token = refreshToken }));
    }

    private static (string? Access, string? Refresh) ReadSession(string raw)
    {
        try
        {
            var json = JObject.Parse(raw);
            return (json["access_token"]?.Value<string>(), json["refresh_token"]?.Value<string>());
        }
        catch
        {
            return (null, null);
        }
    }

    public void OpenLoginPageInBrowser()
    {
        SystemBrowser.Open(AuthorizeUrl);
        _log.WriteInfo("Xbox: opened login page in system browser.");
    }

    // The implicit flow returns the access + refresh tokens in the redirect URL fragment, so there
    // is no code to exchange — we parse them straight out of the pasted redirect URL. (Despite the
    // parameter name, this carries the pasted URL; it keeps the shared IStoreClient connect flow.)
    public async Task<bool> AuthenticateWithAuthorizationCodeAsync(string authorizationCode)
    {
        if (string.IsNullOrWhiteSpace(authorizationCode))
            throw new ArgumentException("Redirect URL is empty", nameof(authorizationCode));

        var (accessToken, refreshToken) = ParseImplicitTokens(authorizationCode);

        // Diagnostic only (no token values): tells us whether the redirect carried a refresh token
        // and whether persistence worked, without leaking secrets.
        _log.WriteInfo(
            $"Xbox: implicit redirect parsed — access={(string.IsNullOrEmpty(accessToken) ? "no" : "yes")}, " +
            $"refresh={(string.IsNullOrEmpty(refreshToken) ? "no" : "yes")}, redirectLen={authorizationCode.Length}, " +
            $"refreshInRedirect={authorizationCode.Contains("refresh_token", StringComparison.OrdinalIgnoreCase)}");

        if (string.IsNullOrEmpty(accessToken))
        {
            _log.WriteError("Xbox: redirect has no access_token — sign in again.");
            return false;
        }

        _refreshToken = refreshToken;
        if (!await AuthenticateCoreAsync(accessToken))
            return false;

        SaveSession(accessToken, refreshToken);
        _log.WriteInfo(
            $"Xbox: session persisted — readback={(string.IsNullOrEmpty(_secrets.Load(SecretKey)) ? "FAILED" : "ok")}");
        return true;
    }

    // Parses access_token / refresh_token from a pasted implicit-flow redirect URL. The tokens live
    // in the fragment (…/oauth20_desktop.srf#access_token=…&refresh_token=…); a bare fragment or
    // query string is accepted too.
    private static (string? AccessToken, string? RefreshToken) ParseImplicitTokens(string text)
    {
        text = text.Trim();

        // Take the parameter section: the fragment after '#', else the query after '?', else as-is.
        var hash = text.IndexOf('#');
        var queryStart = hash >= 0 ? hash : text.IndexOf('?');
        var query = queryStart >= 0 ? text[(queryStart + 1)..] : text;

        string? accessToken = null, refreshToken = null;
        foreach (var pair in query.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var eq = pair.IndexOf('=');
            if (eq <= 0) continue;

            var key = pair[..eq];
            var value = Uri.UnescapeDataString(pair[(eq + 1)..]);
            if (key.Equals("access_token", StringComparison.OrdinalIgnoreCase)) accessToken = value;
            else if (key.Equals("refresh_token", StringComparison.OrdinalIgnoreCase)) refreshToken = value;
        }

        return (accessToken, refreshToken);
    }

    public async Task<bool> AuthenticateWithRefreshTokenAsync(string refreshToken)
    {
        var form = new Dictionary<string, string>
        {
            ["client_id"] = ClientId,
            ["grant_type"] = "refresh_token",
            ["refresh_token"] = refreshToken,
            ["redirect_uri"] = RedirectUri,
            ["scope"] = Scope
        };

        var accessToken = await RequestMsaTokenAsync(form);
        if (accessToken == null || !await AuthenticateCoreAsync(accessToken))
            return false;

        SaveSession(accessToken, _refreshToken);
        return true;
    }

    // Redeems a cached refresh token at the MSA token endpoint for a fresh access token, caching
    // the (rotated) refresh token. Returns the access token, or null on failure.
    private async Task<string?> RequestMsaTokenAsync(Dictionary<string, string> form)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, TokenUrl)
        {
            Content = new FormUrlEncodedContent(form)
        };

        using var response = await _httpClient.SendAsync(request);
        var body = await response.Content.ReadAsStringAsync();

        if (!response.IsSuccessStatusCode)
        {
            _log.WriteError($"Xbox MSA token request failed: {response.StatusCode}, {body}");

            // A refresh-token grant rejected with a client error means the cached token is dead.
            if (form.TryGetValue("grant_type", out var grant) && grant == "refresh_token" &&
                (int)response.StatusCode is >= 400 and < 500)
            {
                _log.WriteWarning("Xbox: cached refresh token rejected — clearing token cache.");
                _secrets.Delete(SecretKey);
            }

            return null;
        }

        var json = JObject.Parse(body);
        var accessToken = json["access_token"]?.Value<string>();
        // A rotated refresh token replaces the old one; a response without one keeps the old one
        // (otherwise the saved session would lose it and expire with the access token).
        _refreshToken = json["refresh_token"]?.Value<string>() ?? form.GetValueOrDefault("refresh_token");

        if (string.IsNullOrEmpty(accessToken))
        {
            _log.WriteError("Xbox MSA token response has no access_token.");
            return null;
        }

        return accessToken;
    }

    // Turns an MSA access token into an Xbox session: user token → XSTS (xboxlive relying party).
    private async Task<bool> AuthenticateCoreAsync(string accessToken)
    {
        // For an MBI_SSL token the RpsTicket is the raw access token (no "d=" prefix; that prefix
        // is only for tokens from a custom Azure application).
        var userAuthBody = new JObject
        {
            ["Properties"] = new JObject
            {
                ["AuthMethod"] = "RPS",
                ["SiteName"] = "user.auth.xboxlive.com",
                ["RpsTicket"] = accessToken
            },
            ["RelyingParty"] = "http://auth.xboxlive.com",
            ["TokenType"] = "JWT"
        };

        var userJson = await PostXboxJsonAsync(UserAuthUrl, userAuthBody);
        _userToken = userJson?["Token"]?.Value<string>();
        if (string.IsNullOrEmpty(_userToken))
        {
            _log.WriteError("Xbox: user.auth returned no Token.");
            return false;
        }

        var xsts = await RequestXstsAsync(XboxLiveRelyingParty);
        if (xsts == null) return false;

        _xstsToken = xsts.Value.Token;
        _userHash = xsts.Value.UserHash;
        _xuid = xsts.Value.Xuid;

        _log.WriteInfo($"Xbox: authenticated as XUID {_xuid}.");
        return true;
    }

    // Mints an XSTS token for the given relying party from the current user token.
    private async Task<(string Token, string UserHash, string? Xuid)?> RequestXstsAsync(string relyingParty)
    {
        if (string.IsNullOrEmpty(_userToken)) return null;

        var body = new JObject
        {
            ["Properties"] = new JObject
            {
                ["SandboxId"] = "RETAIL",
                ["UserTokens"] = new JArray(_userToken)
            },
            ["RelyingParty"] = relyingParty,
            ["TokenType"] = "JWT"
        };

        var json = await PostXboxJsonAsync(XstsUrl, body, true);
        if (json == null) return null;

        // XSTS denials come back as a 401 with an XErr code we can explain.
        var xErr = json["XErr"]?.Value<long>();
        if (xErr.HasValue)
        {
            _log.WriteError($"Xbox: XSTS denied ({relyingParty}): {DescribeXErr(xErr.Value)}");
            return null;
        }

        var token = json["Token"]?.Value<string>();
        var xui = json["DisplayClaims"]?["xui"]?.FirstOrDefault();
        var uhs = xui?["uhs"]?.Value<string>();
        var xid = xui?["xid"]?.Value<string>();

        if (string.IsNullOrEmpty(token) || string.IsNullOrEmpty(uhs))
        {
            _log.WriteError($"Xbox: XSTS response missing Token/uhs ({relyingParty}).");
            return null;
        }

        return (token, uhs, xid);
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

        // The owned library is the single source of truth for what gets exported.
        var ownedTitleIds = await TryGetLibraryTitleIdsAsync(ct);
        if (ownedTitleIds.Count == 0)
        {
            _log.WriteInfo("Xbox: owned library is empty — nothing to export.");
            progress?.Report(new StoreFetchProgress(1, 1, "Loading Xbox library"));
            return new List<XboxGame>();
        }

        // Look up display names for the owned titles (title history is a name source only here).
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
        _log.WriteInfo($"Xbox: fetched {games.Count} owned titles" +
                       (unnamed > 0 ? $" ({unnamed} without a resolved name)." : "."));
        return games;
    }

    // Maps owned title ids to a display name (and last-played) from the title-history feed. Used
    // purely as a name lookup for the owned library — never as a list of games to export. Returns
    // an empty map on any failure (owned titles then fall back to their title id as the name).
    private async Task<Dictionary<string, (string? Name, DateTimeOffset? LastPlayed, string? Image, string? ProductId)>>
        GetTitleNamesAsync(CancellationToken ct)
    {
        var map = new Dictionary<string, (string?, DateTimeOffset?, string?, string?)>(StringComparer.OrdinalIgnoreCase);
        try
        {
            var url = string.Format(TitleHistoryUrlTemplate, _xuid);
            using var response = await SendAuthorizedAsync(() => BuildXboxApiRequest(HttpMethod.Get, url), ct);
            if (!response.IsSuccessStatusCode)
            {
                _log.WriteWarning($"Xbox: title-name lookup failed: {response.StatusCode}.");
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
            _log.WriteWarning($"Xbox: title-name lookup failed: {ex.Message}");
        }

        return map;
    }

    // Fetches the owned title ids (the library) from the legacy inventory service. Returns an empty
    // set on any failure or when the account owns nothing — in which case nothing is exported.
    private async Task<HashSet<string>> TryGetLibraryTitleIdsAsync(CancellationToken ct)
    {
        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            var xsts = await RequestXstsAsync(LicensingRelyingParty);
            if (xsts == null) return ids;

            using var request = BuildXboxApiRequest(HttpMethod.Get, InventoryUrl);
            request.Headers.Authorization =
                new AuthenticationHeaderValue("XBL3.0", $"x={xsts.Value.UserHash};{xsts.Value.Token}");

            using var response = await _httpClient.SendAsync(request, ct);
            if (!response.IsSuccessStatusCode)
            {
                _log.WriteWarning($"Xbox: inventory request failed: {response.StatusCode}.");
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
            _log.WriteWarning($"Xbox: owned inventory lookup failed: {ex.Message}");
        }

        return ids;
    }

    private void EnsureAuthenticated()
    {
        if (!IsAuthenticated)
            throw new InvalidOperationException("Xbox client is not authenticated. Call authenticate first.");
    }

    // Sends an XBL3.0-authorized request; on a 401 it rebuilds the session once (from the cached
    // refresh token) and retries. The caller owns/disposes the returned response.
    private async Task<HttpResponseMessage> SendAuthorizedAsync(
        Func<HttpRequestMessage> requestFactory, CancellationToken ct = default)
    {
        var staleToken = _xstsToken;

        var request = requestFactory();
        request.Headers.Authorization = new AuthenticationHeaderValue("XBL3.0", $"x={_userHash};{_xstsToken}");
        var response = await _httpClient.SendAsync(request, ct);

        if (response.StatusCode == HttpStatusCode.Unauthorized && await TryReauthenticateAsync(staleToken))
        {
            response.Dispose();
            request = requestFactory();
            request.Headers.Authorization = new AuthenticationHeaderValue("XBL3.0", $"x={_userHash};{_xstsToken}");
            response = await _httpClient.SendAsync(request, ct);
        }

        return response;
    }

    private async Task<bool> TryReauthenticateAsync(string? staleXstsToken)
    {
        await _refreshLock.WaitAsync();
        try
        {
            // A concurrent request may already have refreshed while we waited on the lock.
            if (!string.IsNullOrEmpty(_xstsToken) && _xstsToken != staleXstsToken)
                return true;

            try
            {
                // Re-derive the session from the cached token (refresh if available, else the
                // stored access token). We already hold the lock, so this won't re-enter it.
                return await TryAuthenticateFromCacheAsync() == StoreAuthFromCacheStatus.Authenticated;
            }
            catch (Exception ex)
            {
                _log.WriteWarning($"Xbox: re-authentication after 401 failed. {ex.Message}");
                return false;
            }
        }
        finally
        {
            _refreshLock.Release();
        }
    }

    private static HttpRequestMessage BuildXboxApiRequest(HttpMethod method, string url)
    {
        var request = new HttpRequestMessage(method, url);
        request.Headers.Add("x-xbl-contract-version", "2");
        request.Headers.AcceptLanguage.ParseAdd("en-US");
        return request;
    }

    // POSTs a JSON body to an Xbox auth endpoint and returns the parsed response. When
    // <paramref name="allowErrorBody"/> is set, a non-success response is still parsed (used for
    // XSTS, whose denials carry an XErr code in the body).
    private async Task<JObject?> PostXboxJsonAsync(string url, JObject body, bool allowErrorBody = false)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new StringContent(body.ToString(Formatting.None), Encoding.UTF8, "application/json")
        };
        request.Headers.Add("x-xbl-contract-version", "1");
        request.Headers.Accept.ParseAdd("application/json");

        using var response = await _httpClient.SendAsync(request);
        var responseBody = await response.Content.ReadAsStringAsync();

        if (!response.IsSuccessStatusCode && !allowErrorBody)
        {
            _log.WriteError($"Xbox auth request to {url} failed: {response.StatusCode}, {responseBody}");
            return null;
        }

        try
        {
            return JObject.Parse(responseBody);
        }
        catch (Exception ex)
        {
            _log.WriteError($"Xbox: failed to parse response from {url}: {ex.Message}");
            return null;
        }
    }

    private static string DescribeXErr(long xErr)
    {
        return xErr switch
        {
            2148916233 => "this Microsoft account has no Xbox account — create one at xbox.com first",
            2148916235 => "Xbox Live is not available in this account's region",
            2148916236 or 2148916237 => "account needs adult verification (South Korea)",
            2148916238 => "this is a child account — it must be added to a family by an adult",
            _ => $"XErr {xErr}"
        };
    }
}
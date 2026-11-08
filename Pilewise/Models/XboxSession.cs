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

/// <summary>The Xbox Live sign-in chain (MSA → user.auth → XSTS), its cached session and authorized requests.</summary>
internal sealed class XboxSession(ISecretStore secrets, ILogService log)
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


    private const string XboxLiveRelyingParty = "http://xboxlive.com";
    private const string SecretKey = "xbox";

    internal static readonly HttpClient Http = new();

    // Serialises token refreshes so concurrent requests that hit a 401 don't fire several at once.
    private readonly SemaphoreSlim _refreshLock = new(1, 1);

    private string? _refreshToken;
    private string? _userToken; // user.auth token, reusable to mint XSTS for other relying parties
    private string? _xstsToken;
    private string? _userHash;
    private string? _xuid;

    public string? Xuid => _xuid;

    public bool IsAuthenticated =>
        !string.IsNullOrEmpty(_xstsToken) && !string.IsNullOrEmpty(_userHash) && !string.IsNullOrEmpty(_xuid);

    public Uri LoginRequestUri => new(AuthorizeUrl);

    public Uri LoginRedirectUri => new(RedirectUri);

    public void SignOut()
    {
        _refreshToken = _userToken = _xstsToken = _userHash = _xuid = null;
        secrets.Delete(SecretKey);
        log.WriteInfo("Xbox: signed out (cleared cached session).");
    }

    public async Task<StoreAuthFromCacheStatus> TryAuthenticateFromCacheAsync()
    {
        var raw = secrets.Load(SecretKey);
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
            log.WriteWarning($"Xbox: cached session invalid, re-login required. {ex.Message}");
            return StoreAuthFromCacheStatus.SessionExpired;
        }
    }

    // The session is stored as a small JSON blob: the MSA access token (always) plus the refresh
    // token (when the flow provides one). Persisted only after the session is confirmed valid.
    private void SaveSession(string? accessToken, string? refreshToken)
    {
        secrets.Save(SecretKey,
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
        log.WriteInfo("Xbox: opened login page in system browser.");
    }

    // The implicit flow returns the access + refresh tokens in the redirect URL fragment, so there
    // is no code to exchange — we parse them straight out of the pasted redirect URL. (Despite the
    // parameter name, this carries the pasted URL; it keeps the shared IStoreClient connect flow.)
    public async Task<bool> AuthenticateWithAuthorizationCodeAsync(string authorizationCode)
    {
        if (string.IsNullOrWhiteSpace(authorizationCode))
            throw new ArgumentException("Redirect URL is empty", nameof(authorizationCode));

        var (accessToken, refreshToken) = ParseImplicitTokens(authorizationCode);

        log.WriteInfo(
            $"Xbox: implicit redirect parsed — access={(string.IsNullOrEmpty(accessToken) ? "no" : "yes")}, " +
            $"refresh={(string.IsNullOrEmpty(refreshToken) ? "no" : "yes")}, redirectLen={authorizationCode.Length}, " +
            $"refreshInRedirect={authorizationCode.Contains("refresh_token", StringComparison.OrdinalIgnoreCase)}");

        if (string.IsNullOrEmpty(accessToken))
        {
            log.WriteError("Xbox: redirect has no access_token — sign in again.");
            return false;
        }

        _refreshToken = refreshToken;
        if (!await AuthenticateCoreAsync(accessToken))
            return false;

        SaveSession(accessToken, refreshToken);
        log.WriteInfo(
            $"Xbox: session persisted — readback={(string.IsNullOrEmpty(secrets.Load(SecretKey)) ? "FAILED" : "ok")}");
        return true;
    }

    private static (string? AccessToken, string? RefreshToken) ParseImplicitTokens(string text)
    {
        text = text.Trim();

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

    private async Task<string?> RequestMsaTokenAsync(Dictionary<string, string> form)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, TokenUrl)
        {
            Content = new FormUrlEncodedContent(form)
        };

        using var response = await Http.SendAsync(request);
        var body = await response.Content.ReadAsStringAsync();

        if (!response.IsSuccessStatusCode)
        {
            log.WriteError($"Xbox MSA token request failed: {response.StatusCode}, {body}");

            // A refresh-token grant rejected with a client error means the cached token is dead.
            if (form.TryGetValue("grant_type", out var grant) && grant == "refresh_token" &&
                (int)response.StatusCode is >= 400 and < 500)
            {
                log.WriteWarning("Xbox: cached refresh token rejected — clearing token cache.");
                secrets.Delete(SecretKey);
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
            log.WriteError("Xbox MSA token response has no access_token.");
            return null;
        }

        return accessToken;
    }

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
            log.WriteError("Xbox: user.auth returned no Token.");
            return false;
        }

        var xsts = await RequestXstsAsync(XboxLiveRelyingParty);
        if (xsts == null) return false;

        _xstsToken = xsts.Value.Token;
        _userHash = xsts.Value.UserHash;
        _xuid = xsts.Value.Xuid;

        log.WriteInfo($"Xbox: authenticated as XUID {_xuid}.");
        return true;
    }

    internal async Task<(string Token, string UserHash, string? Xuid)?> RequestXstsAsync(string relyingParty)
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
            log.WriteError($"Xbox: XSTS denied ({relyingParty}): {DescribeXErr(xErr.Value)}");
            return null;
        }

        var token = json["Token"]?.Value<string>();
        var xui = json["DisplayClaims"]?["xui"]?.FirstOrDefault();
        var uhs = xui?["uhs"]?.Value<string>();
        var xid = xui?["xid"]?.Value<string>();

        if (string.IsNullOrEmpty(token) || string.IsNullOrEmpty(uhs))
        {
            log.WriteError($"Xbox: XSTS response missing Token/uhs ({relyingParty}).");
            return null;
        }

        return (token, uhs, xid);
    }

    // On a 401 rebuilds the session once and retries. The caller disposes the response.
    internal async Task<HttpResponseMessage> SendAuthorizedAsync(
        Func<HttpRequestMessage> requestFactory, CancellationToken ct = default)
    {
        var staleToken = _xstsToken;

        var request = requestFactory();
        request.Headers.Authorization = new AuthenticationHeaderValue("XBL3.0", $"x={_userHash};{_xstsToken}");
        var response = await Http.SendAsync(request, ct);

        if (response.StatusCode == HttpStatusCode.Unauthorized && await TryReauthenticateAsync(staleToken))
        {
            response.Dispose();
            request = requestFactory();
            request.Headers.Authorization = new AuthenticationHeaderValue("XBL3.0", $"x={_userHash};{_xstsToken}");
            response = await Http.SendAsync(request, ct);
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
                log.WriteWarning($"Xbox: re-authentication after 401 failed. {ex.Message}");
                return false;
            }
        }
        finally
        {
            _refreshLock.Release();
        }
    }

    internal static HttpRequestMessage BuildXboxApiRequest(HttpMethod method, string url)
    {
        var request = new HttpRequestMessage(method, url);
        request.Headers.Add("x-xbl-contract-version", "2");
        request.Headers.AcceptLanguage.ParseAdd("en-US");
        return request;
    }

    // allowErrorBody: parse a non-success body too (XSTS denials carry an XErr code).
    private async Task<JObject?> PostXboxJsonAsync(string url, JObject body, bool allowErrorBody = false)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new StringContent(body.ToString(Formatting.None), Encoding.UTF8, "application/json")
        };
        request.Headers.Add("x-xbl-contract-version", "1");
        request.Headers.Accept.ParseAdd("application/json");

        using var response = await Http.SendAsync(request);
        var responseBody = await response.Content.ReadAsStringAsync();

        if (!response.IsSuccessStatusCode && !allowErrorBody)
        {
            log.WriteError($"Xbox auth request to {url} failed: {response.StatusCode}, {responseBody}");
            return null;
        }

        try
        {
            return JObject.Parse(responseBody);
        }
        catch (Exception ex)
        {
            log.WriteError($"Xbox: failed to parse response from {url}: {ex.Message}");
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

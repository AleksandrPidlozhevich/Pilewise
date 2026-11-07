using Avalonia.Input.Platform;
using Pilewise.Models;
using Pilewise.Services;
using System;
using System.Threading.Tasks;

namespace Pilewise.ViewModels;

public sealed class StoreConnector
{
    private readonly string _name;
    private readonly Func<IStoreClient> _clientFactory;
    private readonly Func<string?, string?> _extractCode;
    private readonly Action<bool> _setConnected;
    private readonly Action<bool> _setCodeInputVisible;
    private readonly Action<string?> _setCode;
    private readonly Func<bool> _isConnected;
    private readonly Func<IClipboard?> _getClipboard;
    private readonly Func<string, Exception, Task> _showError;
    private readonly ILogService _log;
    private readonly IWebAuthenticator? _webAuth;

    private bool _authInProgress;

    public StoreConnector(
        string name,
        Func<IStoreClient> clientFactory,
        Func<string?, string?> extractCode,
        Action<bool> setConnected,
        Action<bool> setCodeInputVisible,
        Action<string?> setCode,
        Func<bool> isConnected,
        Func<IClipboard?> getClipboard,
        Func<string, Exception, Task> showError,
        ILogService log,
        IWebAuthenticator? webAuth = null)
    {
        _name = name;
        _clientFactory = clientFactory;
        _extractCode = extractCode;
        _setConnected = setConnected;
        _setCodeInputVisible = setCodeInputVisible;
        _setCode = setCode;
        _isConnected = isConnected;
        _getClipboard = getClipboard;
        _showError = showError;
        _log = log;
        _webAuth = webAuth;
    }

    public async Task InitializeFromCacheAsync()
    {
        try
        {
            _setConnected(await _clientFactory().TryAuthenticateFromCacheAsync()
                          == StoreAuthFromCacheStatus.Authenticated);
        }
        catch (Exception ex)
        {
            _setConnected(false);
            _log.WriteWarning($"{_name}: failed to restore cached session. {ex.Message}");
        }
    }

    public async Task StartConnectAsync()
    {
        try
        {
            var client = _clientFactory();

            if (await client.TryAuthenticateFromCacheAsync() == StoreAuthFromCacheStatus.Authenticated)
            {
                _setConnected(true);
                return;
            }

            if (_webAuth != null)
            {
                var payload = await _webAuth.AuthenticateAsync(client.LoginRequestUri, client.LoginRedirectUri);
                if (payload == null) return;

                var webCode = _extractCode(payload);
                if (string.IsNullOrEmpty(webCode))
                    throw new Exception($"{_name}: couldn't read the authorization data from the login redirect.");

                await SubmitCodeAsync(webCode);
                return;
            }

            client.OpenLoginPageInBrowser();
            _setCodeInputVisible(true);

            // Convenience: a copied code is picked up automatically; setting the field
            // triggers OnCodeChanged below.
            var clipboard = _getClipboard();
            if (clipboard != null)
            {
                var code = await WaitForCodeFromClipboardAsync(clipboard, _extractCode);
                if (code != null && !_isConnected())
                    _setCode(code);
            }
        }
        catch (Exception ex)
        {
            _log.WriteException(ex, $"{_name} connect failed");
            await _showError($"{_name} connect error", ex);
        }
    }

    public void SignOut()
    {
        try
        {
            _clientFactory().SignOut();
        }
        catch (Exception ex)
        {
            _log.WriteWarning($"{_name}: sign-out failed. {ex.Message}");
        }

        _setConnected(false);
        _setCodeInputVisible(false);
        _setCode(null);
    }

    public void OnCodeChanged(string? value)
    {
        if (_isConnected() || _authInProgress) return;

        var code = _extractCode(value);
        if (string.IsNullOrEmpty(code)) return;

        _ = SubmitCodeAsync(code);
    }

    private async Task SubmitCodeAsync(string code)
    {
        if (_authInProgress) return;
        _authInProgress = true;
        try
        {
            var connected = await _clientFactory().AuthenticateWithAuthorizationCodeAsync(code);
            _setConnected(connected);
            if (!connected)
                throw new Exception(
                    $"{_name} authorization failed. The code is one-time — log in again and use a fresh code.");

            _setCode(null);
            _setCodeInputVisible(false);
        }
        catch (Exception ex)
        {
            _setConnected(false);
            _log.WriteException(ex, $"{_name} connect failed");
            await _showError($"{_name} connect error", ex);
        }
        finally
        {
            _authInProgress = false;
        }
    }

    private static async Task<string?> WaitForCodeFromClipboardAsync(
        IClipboard clipboard, Func<string?, string?> extract, int timeoutSeconds = 180)
    {
        var lastText = await clipboard.TryGetTextAsync();
        var deadline = DateTime.UtcNow.AddSeconds(timeoutSeconds);

        while (DateTime.UtcNow < deadline)
        {
            await Task.Delay(800);

            var text = await clipboard.TryGetTextAsync();
            if (string.IsNullOrWhiteSpace(text) || text == lastText)
                continue;

            lastText = text;

            var code = extract(text);
            if (code != null)
                return code;
        }

        return null;
    }
}
using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using System;
using System.Threading.Tasks;

namespace Pilewise.Views;

/// <summary>
/// Hosts the store login in an embedded <see cref="NativeWebView"/> so we can show a loading
/// animation while the page opens. A small HTML/CSS spinner is shown first (dodging the native
/// WebView's "airspace", since it's web content, not an Avalonia overlay); WebView2 keeps it on
/// screen until the real login page paints. The redirect that carries the code/token is captured
/// at <c>NavigationStarted</c> and the navigation is cancelled, so the page that scrubs the URL
/// fragment never even loads.
/// </summary>
public partial class WebLoginWindow : Window
{
    private const string SpinnerHtml =
        "<!doctype html><html><head><meta charset='utf-8'><style>" +
        "html,body{height:100%;margin:0;background:#1e1e1e;display:flex;align-items:center;justify-content:center}" +
        ".s{width:54px;height:54px;border:5px solid #3a3a3a;border-top-color:#4c8dff;border-radius:50%;" +
        "animation:r .9s linear infinite}@keyframes r{to{transform:rotate(360deg)}}" +
        "</style></head><body><div class='s'></div></body></html>";

    private readonly TaskCompletionSource<string?> _result = new();

    private string _redirectPrefix = string.Empty;
    private Uri? _pendingLoginUri;
    private bool _captured;

    private readonly NativeWebView _webView;

    public WebLoginWindow()
    {
        AvaloniaXamlLoader.Load(this);
        _webView = this.Named<NativeWebView>("WebView");
        WebViewSetup.Configure(_webView);
    }

    public Task<string?> AuthenticateAsync(Window owner, Uri requestUri, Uri redirectUri)
    {
        _redirectPrefix = redirectUri.GetLeftPart(UriPartial.Path);
        _pendingLoginUri = requestUri;

        _webView.NavigationStarted += OnNavigationStarted;
        _webView.NavigationCompleted += OnNavigationCompleted;
        Closed += (_, _) => _result.TrySetResult(null);

        // Show the spinner page once the WebView is realized; navigating to the login follows.
        Opened += (_, _) => _webView.NavigateToString(SpinnerHtml, new Uri("about:blank"));

        Show(owner);
        return _result.Task;
    }

    private void OnNavigationStarted(object? sender, WebViewNavigationStartingEventArgs e)
    {
        var url = e.Request?.AbsoluteUri;
        if (string.IsNullOrEmpty(url)) return;

        if (url.StartsWith(_redirectPrefix, StringComparison.OrdinalIgnoreCase))
        {
            // Don't load the redirect page — capture the URL (with its #fragment) as-is.
            e.Cancel = true;
            Capture(url);
        }
    }

    private void OnNavigationCompleted(object? sender, WebViewNavigationCompletedEventArgs e)
    {
        // The spinner page has rendered — now go to the real login. WebView2 keeps the spinner
        // visible until the login page paints over it.
        if (_pendingLoginUri is { } login)
        {
            _pendingLoginUri = null;
            _webView.Source = login;
        }
    }

    private void Capture(string url)
    {
        if (_captured) return;
        _captured = true;
        _result.TrySetResult(url);
        Close();
    }
}

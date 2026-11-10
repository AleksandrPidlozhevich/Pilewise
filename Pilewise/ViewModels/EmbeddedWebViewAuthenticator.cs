using Avalonia.Controls;
using Pilewise.Services;
using Pilewise.Views;
using System;
using System.Threading.Tasks;

namespace Pilewise.ViewModels;

/// <summary>
/// <see cref="IWebAuthenticator"/> that hosts the login in our own <see cref="WebLoginWindow"/>
/// (instead of the broker) so a loading animation can be shown while the page opens. Returns the
/// redirect URL the store's parser reads the code/token from.
/// </summary>
public sealed class EmbeddedWebViewAuthenticator : IWebAuthenticator
{
    private readonly Func<TopLevel?> _topLevel;

    public EmbeddedWebViewAuthenticator(Func<TopLevel?> topLevel)
    {
        _topLevel = topLevel;
    }

    public Task<string?> AuthenticateAsync(Uri requestUri, Uri redirectUri)
    {
        if (_topLevel() is not Window owner)
            throw new InvalidOperationException("No window available for web authentication.");

        return new WebLoginWindow().AuthenticateAsync(owner, requestUri, redirectUri);
    }
}
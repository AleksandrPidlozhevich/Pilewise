using System;
using System.Threading.Tasks;

namespace Pilewise.Models;

public interface IStoreClient
{
    Task<StoreAuthFromCacheStatus> TryAuthenticateFromCacheAsync();

    void SignOut();

    void OpenLoginPageInBrowser();

    Task<bool> AuthenticateWithAuthorizationCodeAsync(string authorizationCode);

    Uri LoginRequestUri { get; }

    Uri LoginRedirectUri { get; }
}
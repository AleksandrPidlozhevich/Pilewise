using System;
using System.Threading;
using System.Threading.Tasks;
using Pilewise.Services;
using Google.Apis.Auth.OAuth2;
using Google.Apis.Drive.v3;
using Google.Apis.Services;
using Google.Apis.Sheets.v4;

namespace Pilewise.Models;

/// <summary>Google OAuth + the Sheets / Drive services. Disposable: each service owns an HttpClient.</summary>
public sealed class GoogleSheetsApiClient : IDisposable
{
    private const string ApplicationName = "Pilewise";
    private const string CLIENT_ID = "400086036961-3uua9rt1oka9r2pahp7a0ljkohd46vhp.apps.googleusercontent.com";

    private readonly string[] _scopes = { DriveService.Scope.DriveFile };

    private readonly ISecretStore _secrets;
    private readonly ILogService _log;
    private readonly string _userId;

    public GoogleSheetsApiClient(ISecretStore secrets, ILogService log, string userId = "user")
    {
        _secrets = secrets;
        _log = log;
        _userId = userId;
    }

    public SheetsService? SheetsService { get; private set; }
    public DriveService? DriveService { get; private set; }

    /// <summary>Authorizes (opening the browser on first use); cancellable while waiting for the login.</summary>
    public async Task<bool> ConnectAsync(CancellationToken ct = default)
    {
        try
        {
            var credential = await GoogleWebAuthorizationBroker.AuthorizeAsync(
                new ClientSecrets
                {
                    ClientId = CLIENT_ID,
                    ClientSecret = null
                },
                _scopes,
                _userId,
                ct,
                new SecretDataStore(_secrets));

            SheetsService = new SheetsService(new BaseClientService.Initializer
            {
                HttpClientInitializer = credential,
                ApplicationName = ApplicationName
            });

            DriveService = new DriveService(new BaseClientService.Initializer
            {
                HttpClientInitializer = credential,
                ApplicationName = ApplicationName
            });

            return true;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _log.WriteError("Google authorization failed: " + ex.Message);
            return false;
        }
    }

    public void Dispose()
    {
        SheetsService?.Dispose();
        DriveService?.Dispose();
    }
}
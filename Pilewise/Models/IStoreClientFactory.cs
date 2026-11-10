using Pilewise.Services;

namespace Pilewise.Models;

public interface IStoreClientFactory
{
    IEpicClient CreateEpic();

    IGogClient CreateGog();

    IXboxClient CreateXbox();

    ISteamClient CreateSteam();
}

public sealed class StoreClientFactory : IStoreClientFactory
{
    private readonly ISecretStore _secrets;
    private readonly ILogService _log;

    public StoreClientFactory(ISecretStore secrets, ILogService log)
    {
        _secrets = secrets;
        _log = log;
    }

    public IEpicClient CreateEpic()
    {
        return new EpicApiClient(_secrets, _log);
    }

    public IGogClient CreateGog()
    {
        return new GogApiClient(_secrets, _log);
    }

    public IXboxClient CreateXbox()
    {
        return new XboxApiClient(_secrets, _log);
    }

    public ISteamClient CreateSteam()
    {
        return new SteamApiClient(_log);
    }
}
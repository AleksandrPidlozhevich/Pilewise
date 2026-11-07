using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Pilewise.Models;

public interface IEpicClient : IStoreClient
{
    Task<List<EpicGame>> GetOwnedGamesAsync(
        IProgress<StoreFetchProgress>? progress = null, CancellationToken ct = default);
}

public interface IGogClient : IStoreClient
{
    Task<List<GogGame>> GetOwnedGamesAsync(
        IProgress<StoreFetchProgress>? progress = null, CancellationToken ct = default);
}

public interface IXboxClient : IStoreClient
{
    Task<List<XboxGame>> GetGamesAsync(
        IProgress<StoreFetchProgress>? progress = null, CancellationToken ct = default);
}

public interface ISteamClient
{
    Task<SteamGamesResponse> GetOwnedGames(string? steamId, string? apiKey, CancellationToken ct = default);
}

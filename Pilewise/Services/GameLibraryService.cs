using Pilewise.Models;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Pilewise.Services;

public sealed record GameLibraryResult(
    List<CsvGameExportRow> Rows,
    bool EpicAuthenticated,
    bool EpicSessionExpired,
    bool GogAuthenticated,
    bool GogSessionExpired,
    bool XboxAuthenticated,
    bool XboxSessionExpired,
    bool SteamFetched = false,
    IReadOnlySet<string>? SkippedStores = null,
    IReadOnlyList<CsvGameExportRow>? IgnoredRows = null)
{
    /// <summary>Fetched games the user hid (<see cref="IgnoredGames"/>): not exported, but their existing
    /// rows in a target count as matched, so they're never reported as no longer owned.</summary>
    public IReadOnlyList<CsvGameExportRow> Ignored => IgnoredRows ?? [];

    /// <summary>Labels of the stores whose library was loaded this run. A game missing from the
    /// fetch only counts as "no longer owned" when all of its stores are in this set.</summary>
    public IReadOnlySet<string> FetchedStores
    {
        get
        {
            var stores = new HashSet<string>();
            if (SteamFetched) stores.Add(Platforms.Steam);
            if (EpicAuthenticated) stores.Add(Platforms.Epic);
            if (GogAuthenticated) stores.Add(Platforms.Gog);
            if (XboxAuthenticated) stores.Add(Platforms.Xbox);
            // Unticked stores are connected but weren't loaded, so their absence proves nothing.
            if (SkippedStores != null) stores.ExceptWith(SkippedStores);
            return stores;
        }
    }
}

public interface IGameLibraryService
{
    Task<GameLibraryResult> FetchAsync(
        string? steamApiKey,
        string? steamId,
        IProgress<StoreFetchProgress>? progress = null,
        Action<string>? onStatus = null,
        CancellationToken ct = default);
}

public sealed class GameLibraryService : IGameLibraryService
{
    private readonly IStoreClientFactory _clients;
    private readonly StoreSelection _selection;
    private readonly IgnoredGames _ignored;
    private readonly FirstSeenGames? _firstSeen;

    public GameLibraryService(
        IStoreClientFactory clients, StoreSelection? selection = null, IgnoredGames? ignored = null,
        FirstSeenGames? firstSeen = null)
    {
        _firstSeen = firstSeen;
        _clients = clients;
        _selection = selection ?? new StoreSelection();
        _ignored = ignored ?? new IgnoredGames();
    }

    public async Task<GameLibraryResult> FetchAsync(
        string? steamApiKey,
        string? steamId,
        IProgress<StoreFetchProgress>? progress = null,
        Action<string>? onStatus = null,
        CancellationToken ct = default)
    {
        var hasSteamCredentials = !string.IsNullOrWhiteSpace(steamApiKey) && !string.IsNullOrWhiteSpace(steamId);
        var includeSteam = hasSteamCredentials && _selection.Includes(Platforms.Steam);
        var skipped = new HashSet<string>(_selection.Excluded, StringComparer.OrdinalIgnoreCase);

        var epicClient = _clients.CreateEpic();
        var gogClient = _clients.CreateGog();
        var xboxClient = _clients.CreateXbox();
        Task<SteamGamesResponse>? steamTask = null;

        if (includeSteam)
        {
            onStatus?.Invoke("Loading Steam library…");
            steamTask = _clients.CreateSteam().GetOwnedGames(steamId, steamApiKey, ct);
        }

        var epicAuthTask = epicClient.TryAuthenticateFromCacheAsync();
        var gogAuthTask = gogClient.TryAuthenticateFromCacheAsync();
        var xboxAuthTask = xboxClient.TryAuthenticateFromCacheAsync();
        await Task.WhenAll(epicAuthTask, gogAuthTask, xboxAuthTask);

        var epicStatus = await epicAuthTask;
        var isEpicAuthenticated = epicStatus == StoreAuthFromCacheStatus.Authenticated;

        var gogStatus = await gogAuthTask;
        var isGogAuthenticated = gogStatus == StoreAuthFromCacheStatus.Authenticated;

        var xboxStatus = await xboxAuthTask;
        var isXboxAuthenticated = xboxStatus == StoreAuthFromCacheStatus.Authenticated;

        List<SteamGame>? steamGames = null;
        if (steamTask != null)
        {
            onStatus?.Invoke("Loading Steam library…");
            var steamGamesResponse = await steamTask;

            if (steamGamesResponse?.Response?.Games == null)
                throw new Exception("Failed to receive a list of games from Steam.");

            steamGames = steamGamesResponse.Response.Games;
        }

        List<EpicGame>? epicGames = null;
        // Sessions are still checked for unticked stores (the connect rows show their status),
        // but their libraries are skipped.
        if (isEpicAuthenticated && _selection.Includes(Platforms.Epic))
        {
            onStatus?.Invoke("Loading Epic library…");
            epicGames = await epicClient.GetOwnedGamesAsync(progress, ct);
        }

        List<GogGame>? gogGames = null;
        if (isGogAuthenticated && _selection.Includes(Platforms.Gog))
        {
            onStatus?.Invoke("Loading GOG library…");
            gogGames = await gogClient.GetOwnedGamesAsync(progress, ct);
        }

        List<XboxGame>? xboxGames = null;
        if (isXboxAuthenticated && _selection.Includes(Platforms.Xbox))
        {
            onStatus?.Invoke("Loading Xbox library…");
            xboxGames = await xboxClient.GetGamesAsync(progress, ct);
        }

        if (!hasSteamCredentials && !isEpicAuthenticated && !isGogAuthenticated && !isXboxAuthenticated)
            throw new Exception("Please enter Steam credentials or connect Epic / GOG / Xbox first.");
        if (steamGames == null && epicGames == null && gogGames == null && xboxGames == null)
            throw new Exception("Every available store is unticked. Tick at least one store to include.");

        var (rows, ignoredRows) = _ignored.Split(
            CsvGameExportService.BuildFromLibraries(steamGames, epicGames, gogGames, xboxGames));

        var result = new GameLibraryResult(
            rows,
            isEpicAuthenticated,
            epicStatus == StoreAuthFromCacheStatus.SessionExpired,
            isGogAuthenticated,
            gogStatus == StoreAuthFromCacheStatus.SessionExpired,
            isXboxAuthenticated,
            xboxStatus == StoreAuthFromCacheStatus.SessionExpired,
            steamGames != null,
            skipped,
            ignoredRows);
        _firstSeen?.Record(rows.Concat(ignoredRows), result.FetchedStores, DateOnly.FromDateTime(DateTime.Now));
        return result;
    }
}
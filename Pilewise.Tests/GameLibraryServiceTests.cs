using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Pilewise.Models;
using Pilewise.Services;
using Xunit;

namespace Pilewise.Tests;

public class GameLibraryServiceTests
{
    private const string Key = "key";
    private const string Id = "id";

    [Fact]
    public async Task FetchAsync_NoSteamCredentials_AndNoStoreConnected_Throws()
    {
        var service = new GameLibraryService(new FakeFactory());

        var ex = await Assert.ThrowsAsync<Exception>(() => service.FetchAsync(null, null));
        Assert.Contains("Steam credentials", ex.Message);
    }

    [Fact]
    public async Task FetchAsync_SteamResponseMissingGames_Throws()
    {
        var factory = new FakeFactory { Steam = new FakeSteamClient(new SteamGamesResponse()) };
        var service = new GameLibraryService(factory);

        var ex = await Assert.ThrowsAsync<Exception>(() => service.FetchAsync(Key, Id));
        Assert.Contains("from Steam", ex.Message);
    }

    [Fact]
    public async Task FetchAsync_SteamOnly_ReturnsSteamRows_WithNoStoreFlags()
    {
        var factory = new FakeFactory { Steam = SteamWith(("Portal", 400)) };
        var service = new GameLibraryService(factory);

        var result = await service.FetchAsync(Key, Id);

        var row = Assert.Single(result.Rows);
        Assert.Equal("Portal", row.Name);
        Assert.Equal(400, row.SteamGameId);
        Assert.False(result.EpicAuthenticated);
        Assert.False(result.GogAuthenticated);
        Assert.False(result.XboxAuthenticated);
        Assert.False(result.EpicSessionExpired);
    }

    [Fact]
    public async Task FetchAsync_AllSourcesAuthenticated_MergesEveryLibrary()
    {
        var factory = new FakeFactory
        {
            Steam = SteamWith(("Steam Game", 1)),
            Epic = new FakeEpicClient(StoreAuthFromCacheStatus.Authenticated)
            {
                Games = { new EpicGame { Title = "Epic Game", CatalogItemId = "cat", Namespace = "ns" } }
            },
            Gog = new FakeGogClient(StoreAuthFromCacheStatus.Authenticated)
            {
                Games = { new GogGame { Title = "Gog Game", Id = 5 } }
            },
            Xbox = new FakeXboxClient(StoreAuthFromCacheStatus.Authenticated)
            {
                Games = { new XboxGame { Name = "Xbox Game", TitleId = "9X", IsGamePass = true } }
            }
        };
        var service = new GameLibraryService(factory);

        var result = await service.FetchAsync(Key, Id);

        Assert.True(result.EpicAuthenticated);
        Assert.True(result.GogAuthenticated);
        Assert.True(result.XboxAuthenticated);
        Assert.Equal(4, result.Rows.Count);
        Assert.True(result.Rows.Single(r => r.Name == "Xbox Game").IsGamePass);

        Assert.True(((FakeEpicClient)factory.Epic).GamesFetched);
        Assert.True(((FakeGogClient)factory.Gog).GamesFetched);
        Assert.True(((FakeXboxClient)factory.Xbox).GamesFetched);
    }

    [Fact]
    public async Task FetchAsync_SessionExpired_FlagsItAndSkipsThatStoresFetch()
    {
        var epic = new FakeEpicClient(StoreAuthFromCacheStatus.SessionExpired)
        {
            Games = { new EpicGame { Title = "Should Not Appear", CatalogItemId = "c", Namespace = "n" } }
        };
        var factory = new FakeFactory { Steam = SteamWith(("Steam Game", 1)), Epic = epic };
        var service = new GameLibraryService(factory);

        var result = await service.FetchAsync(Key, Id);

        Assert.False(result.EpicAuthenticated);
        Assert.True(result.EpicSessionExpired);
        Assert.False(epic.GamesFetched); // expired session is not queried
        Assert.DoesNotContain(result.Rows, r => r.Name == "Should Not Appear");
        Assert.Single(result.Rows); // only the Steam game
    }

    [Fact]
    public async Task FetchAsync_OnlyOneStoreConnected_NoSteam_Succeeds()
    {
        var factory = new FakeFactory
        {
            Xbox = new FakeXboxClient(StoreAuthFromCacheStatus.Authenticated)
            {
                Games = { new XboxGame { Name = "Halo", TitleId = "9H" } }
            }
        };
        var service = new GameLibraryService(factory);

        var result = await service.FetchAsync(null, null);

        Assert.True(result.XboxAuthenticated);
        Assert.Single(result.Rows);
        Assert.Equal("Halo", result.Rows[0].Name);
    }

    [Fact]
    public async Task FetchAsync_UntickedStore_IsSkipped_ButSessionStillChecked()
    {
        var factory = new FakeFactory
        {
            Steam = SteamWith(("Steam Game", 1)),
            Gog = new FakeGogClient(StoreAuthFromCacheStatus.Authenticated)
            {
                Games = { new GogGame { Title = "Gog Game", Id = 5 } }
            }
        };
        var selection = new StoreSelection();
        selection.SetIncluded(Platforms.Gog, false);
        var service = new GameLibraryService(factory, selection);

        var result = await service.FetchAsync(Key, Id);

        Assert.Equal(["Steam Game"], result.Rows.Select(r => r.Name));
        Assert.True(result.GogAuthenticated); // still shown as connected
        Assert.DoesNotContain(Platforms.Gog, result.FetchedStores); // but its games weren't loaded
        Assert.Contains(Platforms.Steam, result.FetchedStores);
    }

    [Fact]
    public async Task FetchAsync_EveryAvailableStoreUnticked_Throws()
    {
        var selection = new StoreSelection();
        selection.SetIncluded(Platforms.Steam, false);
        var service = new GameLibraryService(new FakeFactory { Steam = SteamWith(("Portal", 400)) }, selection);

        var ex = await Assert.ThrowsAsync<Exception>(() => service.FetchAsync(Key, Id));
        Assert.Contains("unticked", ex.Message);
    }

    [Fact]
    public async Task FetchAsync_StoresLoadInParallel_OnOneProgressBar()
    {
        var epicGate = new TaskCompletionSource();
        var epic = new FakeEpicClient(StoreAuthFromCacheStatus.Authenticated)
        {
            Games = { new EpicGame { Title = "Alan Wake", Namespace = "ns", CatalogItemId = "cat" } },
            Gate = epicGate.Task,
            Reports = { new StoreFetchProgress(1, 4) }
        };
        var gog = new FakeGogClient(StoreAuthFromCacheStatus.Authenticated)
        {
            Games = { new GogGame { Title = "Gog Game", Id = 5 } },
            Reports = { new StoreFetchProgress(2, 2) }
        };
        var reports = new List<StoreFetchProgress>();
        var service = new GameLibraryService(new FakeFactory { Epic = epic, Gog = gog });

        var fetch = service.FetchAsync(null, null, new SyncProgress(reports.Add));
        Assert.True(gog.GamesFetched); // GOG didn't wait for Epic
        Assert.False(fetch.IsCompleted);
        epicGate.SetResult();
        var result = await fetch;

        Assert.Equal(2, result.Rows.Count);
        Assert.Equal(new StoreFetchProgress(3, 6), reports[^1] with { Stage = "" });
    }

    [Fact]
    public void StoreSelection_RoundTripsThroughSettings()
    {
        var selection = new StoreSelection();
        selection.SetIncluded(Platforms.Xbox, false);
        selection.SetIncluded(Platforms.Gog, false);
        Assert.Equal("GOG, Xbox", selection.Serialize());

        var settings = AppSettingsService.Parse(AppSettingsService.Serialize(
            new AppSettings("k", "i", "t", "d", ExcludedStores: selection.Serialize())));
        var reloaded = new StoreSelection();
        reloaded.Load(settings.ExcludedStores);

        Assert.False(reloaded.Includes(Platforms.Gog));
        Assert.True(reloaded.Includes(Platforms.Steam));

        reloaded.SetIncluded(Platforms.Gog, true);
        reloaded.SetIncluded(Platforms.Xbox, true);
        Assert.Null(reloaded.Serialize());
    }

    private static ISteamClient SteamWith(params (string Name, int Id)[] games)
    {
        return new FakeSteamClient(new SteamGamesResponse
        {
            Response = new SteamGamesInnerResponse
            {
                Games = games.Select(g => new SteamGame { Name = g.Name, GameID = g.Id }).ToList()
            }
        });
    }
}

file sealed class FakeFactory : IStoreClientFactory
{
    public IEpicClient Epic { get; init; } = new FakeEpicClient(StoreAuthFromCacheStatus.NoCachedSession);
    public IGogClient Gog { get; init; } = new FakeGogClient(StoreAuthFromCacheStatus.NoCachedSession);
    public IXboxClient Xbox { get; init; } = new FakeXboxClient(StoreAuthFromCacheStatus.NoCachedSession);
    public ISteamClient Steam { get; init; } = new FakeSteamClient(new SteamGamesResponse());

    public IEpicClient CreateEpic()
    {
        return Epic;
    }

    public IGogClient CreateGog()
    {
        return Gog;
    }

    public IXboxClient CreateXbox()
    {
        return Xbox;
    }

    public ISteamClient CreateSteam()
    {
        return Steam;
    }
}

file abstract class FakeStoreClient
{
    public void SignOut()
    {
    }

    public void OpenLoginPageInBrowser()
    {
    }

    public Task<bool> AuthenticateWithAuthorizationCodeAsync(string authorizationCode)
    {
        return Task.FromResult(true);
    }

    public Uri LoginRequestUri => new("https://example.test");
    public Uri LoginRedirectUri => new("https://example.test");
}

file sealed class FakeEpicClient(StoreAuthFromCacheStatus status) : FakeStoreClient, IEpicClient
{
    public List<EpicGame> Games { get; init; } = [];
    public bool GamesFetched { get; private set; }
    public Task Gate { get; init; } = Task.CompletedTask;
    public List<StoreFetchProgress> Reports { get; } = [];

    public Task<StoreAuthFromCacheStatus> TryAuthenticateFromCacheAsync()
    {
        return Task.FromResult(status);
    }

    public async Task<List<EpicGame>> GetOwnedGamesAsync(
        IProgress<StoreFetchProgress>? progress = null, CancellationToken ct = default)
    {
        GamesFetched = true;
        Reports.ForEach(r => progress?.Report(r));
        await Gate;
        return Games;
    }
}

file sealed class FakeGogClient(StoreAuthFromCacheStatus status) : FakeStoreClient, IGogClient
{
    public List<GogGame> Games { get; init; } = [];
    public bool GamesFetched { get; private set; }
    public List<StoreFetchProgress> Reports { get; } = [];

    public Task<StoreAuthFromCacheStatus> TryAuthenticateFromCacheAsync()
    {
        return Task.FromResult(status);
    }

    public Task<List<GogGame>> GetOwnedGamesAsync(
        IProgress<StoreFetchProgress>? progress = null, CancellationToken ct = default)
    {
        GamesFetched = true;
        Reports.ForEach(r => progress?.Report(r));
        return Task.FromResult(Games);
    }
}

file sealed class FakeXboxClient(StoreAuthFromCacheStatus status) : FakeStoreClient, IXboxClient
{
    public List<XboxGame> Games { get; init; } = [];
    public bool GamesFetched { get; private set; }

    public Task<StoreAuthFromCacheStatus> TryAuthenticateFromCacheAsync()
    {
        return Task.FromResult(status);
    }

    public Task<List<XboxGame>> GetGamesAsync(
        IProgress<StoreFetchProgress>? progress = null, CancellationToken ct = default)
    {
        GamesFetched = true;
        return Task.FromResult(Games);
    }
}

file sealed class FakeSteamClient(SteamGamesResponse response) : ISteamClient
{
    public Task<SteamGamesResponse> GetOwnedGames(string? steamId, string? apiKey, CancellationToken ct = default)
    {
        return Task.FromResult(response);
    }
}

// Reports synchronously (Progress<T> would post to a sync context).
file sealed class SyncProgress(Action<StoreFetchProgress> report) : IProgress<StoreFetchProgress>
{
    public void Report(StoreFetchProgress value)
    {
        report(value);
    }
}
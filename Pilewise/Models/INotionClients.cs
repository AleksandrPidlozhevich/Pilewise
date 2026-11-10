using Newtonsoft.Json.Linq;
using Pilewise.Services;
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Pilewise.Models;

public interface INotionApiClient : IDisposable
{
    Task<List<JObject>> QueryAllPagesAsync(CancellationToken ct = default);

    Task<IReadOnlyDictionary<string, string>> GetPropertyTypesAsync(CancellationToken ct = default);

    Task AddPropertiesAsync(IReadOnlyDictionary<string, string> properties, CancellationToken ct = default);

    // The client wraps each body with the data-source parent.
    Task AddPagesToDatabaseParallel(
        IEnumerable<NotionPageBody> pages, Action? onPageDone = null, CancellationToken ct = default);

    Task UpdatePagesParallel(
        IEnumerable<(string PageId, NotionPageBody Body)> updates,
        Action? onPageDone = null,
        CancellationToken ct = default);

    Task TrashPagesParallel(IEnumerable<string> pageIds, Action? onPageDone = null, CancellationToken ct = default);
}

public sealed record NotionPageBody(object Properties, string? CoverUrl = null)
{
    public Dictionary<string, object> ToJson()
    {
        var json = new Dictionary<string, object> { ["properties"] = Properties };
        if (CoverUrl != null)
            json["cover"] = new { type = "external", external = new { url = CoverUrl } };
        return json;
    }
}

public interface INotionDataFetcher
{
    Task<List<NotionGameRow>> FetchRowsAsync(CancellationToken ct = default);
}

public interface INotionGateway
{
    INotionApiClient CreateClient(string? token, string? dbId);

    INotionDataFetcher CreateFetcher(INotionApiClient client);
}

public sealed class NotionGateway : INotionGateway
{
    private readonly ILogService _log;

    public NotionGateway(ILogService log)
    {
        _log = log;
    }

    public INotionApiClient CreateClient(string? token, string? dbId)
    {
        return new NotionApiClient(token, dbId);
    }

    public INotionDataFetcher CreateFetcher(INotionApiClient client)
    {
        return new NotionDataFetcher(client, _log);
    }
}
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Pilewise.Models;

public class NotionApiClient : INotionApiClient
{
    // Notion allows about 3 requests per second on average: every request (reads, writes, retries)
    // waits for its own slot, at least this far after the previous one, across all parallel writes.
    private static readonly TimeSpan DefaultDelayBetweenRequests = TimeSpan.FromMilliseconds(334);
    private const int MaxConcurrentRequests = 3;
    private const int MaxRetries = 3;

    // Notion API version. As of 2025-09-03 a database and its data source(s) are distinct: rows are
    // queried and pages are created against a data source, not the database directly.
    private const string NotionVersion = "2025-09-03";
    private const string ApiBase = "https://api.notion.com/v1";

    private readonly string _databaseId;
    private readonly HttpClient _httpClient;
    private readonly TimeSpan _delayBetweenRequests;
    private readonly SemaphoreSlim _semaphore = new(MaxConcurrentRequests, MaxConcurrentRequests);

    private readonly object _paceGate = new();
    private DateTime _nextRequestAt = DateTime.MinValue;

    // The database's (first) data source id, resolved lazily from the database id and cached.
    private string? _dataSourceId;

    public NotionApiClient(string? apiKey, string? databaseId)
        : this(apiKey, databaseId, new HttpClientHandler(), DefaultDelayBetweenRequests)
    {
    }

    /// <summary>With a custom transport and pause between requests (tests use a fake handler and no pause).</summary>
    public NotionApiClient(string? apiKey, string? databaseId, HttpMessageHandler handler, TimeSpan delayBetweenRequests)
    {
        _databaseId = databaseId?.Trim() ?? string.Empty;
        _delayBetweenRequests = delayBetweenRequests;
        _httpClient = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(30) };
        _httpClient.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", apiKey?.Trim() ?? string.Empty);
        _httpClient.DefaultRequestHeaders.Add("Notion-Version", NotionVersion);
    }

    // Resolves the data source id behind the configured database id (single-source databases expose
    // exactly one). Cached after the first call so the export only makes this round-trip once.
    private async Task<string> ResolveDataSourceIdAsync(CancellationToken ct)
    {
        if (!string.IsNullOrEmpty(_dataSourceId)) return _dataSourceId;

        var database = await SendAsync(HttpMethod.Get, $"{ApiBase}/databases/{_databaseId}", null, ct);
        var id = (database["data_sources"] as JArray)?.FirstOrDefault()?["id"]?.Value<string>();
        if (string.IsNullOrEmpty(id))
            throw new InvalidOperationException($"Notion database {_databaseId} exposes no data source.");

        _dataSourceId = id;
        return id;
    }

    public async Task<IReadOnlyDictionary<string, string>> GetPropertyTypesAsync(CancellationToken ct = default)
    {
        var dataSourceId = await ResolveDataSourceIdAsync(ct);
        var dataSource = await SendAsync(HttpMethod.Get, $"{ApiBase}/data_sources/{dataSourceId}", null, ct);

        var types = new Dictionary<string, string>();
        if (dataSource["properties"] is JObject properties)
            foreach (var property in properties.Properties())
                types[property.Name] = property.Value["type"]?.Value<string>() ?? string.Empty;
        return types;
    }

    public async Task AddPropertiesAsync(IReadOnlyDictionary<string, string> properties, CancellationToken ct = default)
    {
        var dataSourceId = await ResolveDataSourceIdAsync(ct);

        // Schema shape: { "properties": { "<name>": { "<type>": {} } } } — an empty config object.
        var schema = new JObject();
        foreach (var (name, type) in properties)
            schema[name] = new JObject { [type] = new JObject() };

        await SendAsync(HttpMethod.Patch, $"{ApiBase}/data_sources/{dataSourceId}",
            new JObject { ["properties"] = schema }, ct);
    }

    public async Task<List<JObject>> QueryAllPagesAsync(CancellationToken ct = default)
    {
        var dataSourceId = await ResolveDataSourceIdAsync(ct);
        var allPages = new List<JObject>();
        string? nextCursor = null;

        do
        {
            var query = nextCursor != null ? new { start_cursor = nextCursor } : (object)new { };
            var result = await SendAsync(HttpMethod.Post, $"{ApiBase}/data_sources/{dataSourceId}/query", query, ct);

            if (result["results"] is JArray pages)
                allPages.AddRange(pages.OfType<JObject>());

            var hasMore = result["has_more"]?.Value<bool>() ?? false;
            nextCursor = hasMore ? result["next_cursor"]?.Value<string>() : null;
        } while (nextCursor != null);

        return allPages;
    }

    public async Task AddPagesToDatabaseParallel(
        IEnumerable<NotionPageBody> pages, Action? onPageDone = null, CancellationToken ct = default)
    {
        var dataSourceId = await ResolveDataSourceIdAsync(ct);
        await WriteAllAsync(pages, page =>
        {
            var body = page.ToJson();
            body["parent"] = new { type = "data_source_id", data_source_id = dataSourceId };
            return SendAsync(HttpMethod.Post, $"{ApiBase}/pages", body, ct);
        }, onPageDone, ct);
    }

    public Task UpdatePagesParallel(
        IEnumerable<(string PageId, NotionPageBody Body)> updates,
        Action? onPageDone = null,
        CancellationToken ct = default) =>
        WriteAllAsync(updates, u => SendAsync(HttpMethod.Patch, $"{ApiBase}/pages/{u.PageId}", u.Body.ToJson(), ct),
            onPageDone, ct);

    public Task TrashPagesParallel(
        IEnumerable<string> pageIds, Action? onPageDone = null, CancellationToken ct = default)
    {
        var body = new Dictionary<string, object> { ["in_trash"] = true };
        return WriteAllAsync(pageIds, id => SendAsync(HttpMethod.Patch, $"{ApiBase}/pages/{id}", body, ct),
            onPageDone, ct);
    }

    // Runs the writes with at most MaxConcurrentRequests in flight. The first failure stops the
    // writes still waiting (so a bad token doesn't run through hundreds of pages); WhenAll then
    // rethrows that failure (the stopped writes are only cancelled, not faulted).
    private async Task WriteAllAsync<T>(
        IEnumerable<T> items, Func<T, Task> write, Action? onDone, CancellationToken ct)
    {
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var tasks = items.Select(async item =>
        {
            await _semaphore.WaitAsync(stop.Token);
            try
            {
                await write(item);
            }
            catch
            {
                stop.Cancel();
                throw;
            }
            finally
            {
                _semaphore.Release();
            }

            onDone?.Invoke();
        }).ToList();

        await Task.WhenAll(tasks);
    }

    /// <summary>
    /// Sends one request (paced, see <see cref="DefaultDelayBetweenRequests"/>) and returns the response
    /// JSON. Rate limits (429), conflicts (409), server errors and network failures are retried with
    /// backoff; anything else throws with Notion's own message. A timeout isn't retried: a page
    /// create that timed out may still have gone through.
    /// </summary>
    private async Task<JObject> SendAsync(HttpMethod method, string url, object? body, CancellationToken ct)
    {
        var json = body == null ? null : JsonConvert.SerializeObject(body);
        for (var attempt = 0;; attempt++)
        {
            await WaitForTurnAsync(ct);

            using var request = new HttpRequestMessage(method, url);
            if (json != null) request.Content = new StringContent(json, Encoding.UTF8, "application/json");

            HttpResponseMessage response;
            try
            {
                response = await _httpClient.SendAsync(request, ct);
            }
            catch (HttpRequestException) when (attempt < MaxRetries)
            {
                await Task.Delay(Backoff(attempt), ct);
                continue;
            }

            using (response)
            {
                var content = await response.Content.ReadAsStringAsync(ct);
                if (response.IsSuccessStatusCode)
                    return string.IsNullOrWhiteSpace(content) ? new JObject() : JObject.Parse(content);

                if (attempt < MaxRetries && IsTransient(response.StatusCode))
                {
                    await Task.Delay(response.Headers.RetryAfter?.Delta ?? Backoff(attempt), ct);
                    continue;
                }

                throw NotionError(response.StatusCode, content);
            }
        }
    }

    private static bool IsTransient(HttpStatusCode status) =>
        status is HttpStatusCode.TooManyRequests or HttpStatusCode.Conflict ||
        (int)status >= 500;

    private static TimeSpan Backoff(int attempt) => TimeSpan.FromSeconds(Math.Pow(2, attempt));

    // Waits until this request's slot: each caller reserves the next one under the lock, so parallel
    // writers line up _delayBetweenRequests apart instead of bursting together.
    private Task WaitForTurnAsync(CancellationToken ct)
    {
        TimeSpan wait;
        lock (_paceGate)
        {
            var now = DateTime.UtcNow;
            var slot = _nextRequestAt > now ? _nextRequestAt : now;
            _nextRequestAt = slot + _delayBetweenRequests;
            wait = slot - now;
        }

        return wait > TimeSpan.Zero ? Task.Delay(wait, ct) : Task.CompletedTask;
    }

    // Notion's response body carries the actual reason (e.g. an invalid token, an unshared database or
    // a bad property value), which a bare status code would hide.
    private static HttpRequestException NotionError(HttpStatusCode status, string body)
    {
        string? notionMessage = null;
        try
        {
            notionMessage = JObject.Parse(body)["message"]?.Value<string>();
        }
        catch (JsonReaderException)
        {
            // Non-JSON body (e.g. a gateway error page) — fall back to the raw content below.
        }

        var detail = notionMessage ?? body;
        if (status == HttpStatusCode.Unauthorized)
            detail = "Notion rejected the integration token (401). " +
                     $"Check the token in settings and that the database is shared with the integration. {detail}";

        return new HttpRequestException($"Notion API {(int)status} {status}: {detail}", null, status);
    }

    public void Dispose()
    {
        _httpClient.Dispose();
        _semaphore.Dispose();
        GC.SuppressFinalize(this);
    }
}

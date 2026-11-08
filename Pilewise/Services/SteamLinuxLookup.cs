using Newtonsoft.Json;
using Pilewise.Models;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace Pilewise.Services;

/// <summary>
/// Resolves Linux ratings for Steam app ids (native from the store, else ProtonDB).
/// Successful results are cached a week. Network failures become <see cref="LinuxRating.Unavailable"/>
/// for this run only (not cached as Unknown), and never erase a durable rating already on a row.
/// </summary>
public sealed class SteamLinuxLookup
{
    public const string StateKey = "linux-ratings";

    // Sequential 1 req/s — Steam appdetails rate-limits hard; parallelise if a bulk API appears.
    private static readonly TimeSpan DefaultSpacing = TimeSpan.FromSeconds(1);
    private const int DefaultMaxFailures = 3;
    private static readonly HttpClient SharedHttp = CreateClient(null);

    private readonly IStateStore _state;
    private readonly ILogService _log;
    private readonly HttpClient _http;
    private readonly TimeSpan _spacing;
    private readonly int _maxFailures;
    private readonly Func<DateOnly> _today;

    public bool Enabled { get; set; }

    public SteamLinuxLookup(IStateStore state, ILogService log)
        : this(state, log, handler: null)
    {
    }

    /// <summary>Test seam: custom handler, spacing, failure budget and clock.</summary>
    internal SteamLinuxLookup(
        IStateStore state, ILogService log, HttpMessageHandler? handler,
        TimeSpan? spacing = null, int? maxFailures = null, Func<DateOnly>? today = null)
    {
        _state = state;
        _log = log;
        _http = handler == null ? SharedHttp : CreateClient(handler);
        _spacing = spacing ?? DefaultSpacing;
        _maxFailures = maxFailures ?? DefaultMaxFailures;
        _today = today ?? (() => DateOnly.FromDateTime(DateTime.UtcNow));
    }

    public async Task StampAsync(
        IEnumerable<CsvGameExportRow> rows, IProgress<StoreFetchProgress>? progress = null, CancellationToken ct = default)
    {
        if (!Enabled) return;
        var list = rows as IList<CsvGameExportRow> ?? rows.ToList();
        LinuxRating.Apply(list, await LookupAsync(list.Select(r => r.SteamGameId).OfType<int>(), progress, ct));
    }

    public async Task<IReadOnlyDictionary<int, string>> LookupAsync(
        IEnumerable<int> appIds, IProgress<StoreFetchProgress>? progress = null, CancellationToken ct = default)
    {
        var ids = appIds.Where(id => id > 0).Distinct().ToList();
        var cache = Load();
        var result = new Dictionary<int, string>();
        var today = _today();
        var fetched = 0;
        var failures = 0;
        var stoppedEarly = false;

        void Flush()
        {
            if (fetched > 0) Save(cache);
        }

        try
        {
            for (var i = 0; i < ids.Count; i++)
            {
                ct.ThrowIfCancellationRequested();
                var id = ids[i];
                progress?.Report(new StoreFetchProgress(i + 1, ids.Count, "Checking Linux support"));

                if (cache.TryGetValue(id.ToString(), out var hit) && Fresh(hit, today))
                {
                    result[id] = LinuxRating.Label(hit.Native, hit.Tier, hit.Confidence);
                    continue;
                }

                try
                {
                    var native = await FetchNativeAsync(id, ct);
                    // Ask ProtonDB whenever Steam did not confirm a native Linux build: Windows-only
                    // (false) or inconclusive store data (null). Native builds skip ProtonDB.
                    string? tier = null;
                    string? confidence = null;
                    if (native != true)
                        (tier, confidence) = await FetchSummaryAsync(id, ct);

                    cache[id.ToString()] = new Cached
                    {
                        Native = native,
                        Tier = tier,
                        Confidence = confidence,
                        At = today.ToString("yyyy-MM-dd")
                    };
                    result[id] = LinuxRating.Label(native, tier, confidence);
                    fetched++;
                    failures = 0;
                }
                catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
                {
                    // Transient: visible for this run, not written into the week-long cache as Unknown.
                    result[id] = LinuxRating.Unavailable;
                    _log.WriteWarning($"Linux: Steam app {id}: {ex.Message}");
                    if (++failures >= _maxFailures)
                    {
                        stoppedEarly = true;
                        _log.WriteWarning(
                            $"Linux: stopping after {_maxFailures} consecutive failures; remaining games marked Unavailable.");
                        for (var j = i + 1; j < ids.Count; j++)
                            if (!result.ContainsKey(ids[j]))
                                result[ids[j]] = LinuxRating.Unavailable;
                        break;
                    }
                }

                if (i + 1 < ids.Count && !stoppedEarly)
                    await Task.Delay(_spacing, ct);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            Flush();
            throw;
        }

        Flush();
        if (fetched > 0 || result.Values.Any(LinuxRating.IsTransient))
            _log.WriteInfo(
                $"Linux: fetched {fetched} of {ids.Count} Steam games" +
                (stoppedEarly ? " (stopped early; rest Unavailable)." : "."));

        return result;
    }

    private async Task<bool?> FetchNativeAsync(int appId, CancellationToken ct)
    {
        using var response = await SendAsync(
            "https://store.steampowered.com/api/appdetails?filters=platforms&appids=" + appId, ct);
        response.EnsureSuccessStatusCode();
        return LinuxRating.ReadNative(await response.Content.ReadAsStringAsync(ct), appId);
    }

    private async Task<(string? Tier, string? Confidence)> FetchSummaryAsync(int appId, CancellationToken ct)
    {
        using var response = await SendAsync(
            "https://www.protondb.com/api/v1/reports/summaries/" + appId + ".json", ct);
        if (response.StatusCode == HttpStatusCode.NotFound) return (null, null);
        response.EnsureSuccessStatusCode();
        return LinuxRating.ReadSummary(await response.Content.ReadAsStringAsync(ct));
    }

    private async Task<HttpResponseMessage> SendAsync(string url, CancellationToken ct)
    {
        for (var attempt = 0; ; attempt++)
        {
            var response = await _http.GetAsync(url, ct);
            if (response.StatusCode != HttpStatusCode.TooManyRequests || attempt == 3)
                return response;

            var delay = response.Headers.RetryAfter?.Delta ?? TimeSpan.FromSeconds(2 * (attempt + 1));
            response.Dispose();
            await Task.Delay(delay, ct);
        }
    }

    private Dictionary<string, Cached> Load()
    {
        try
        {
            var json = _state.Load(StateKey);
            return string.IsNullOrWhiteSpace(json)
                ? new()
                : JsonConvert.DeserializeObject<Dictionary<string, Cached>>(json) ?? new();
        }
        catch (JsonException)
        {
            return new();
        }
    }

    private void Save(Dictionary<string, Cached> cache) =>
        _state.Save(StateKey, JsonConvert.SerializeObject(cache));

    private static bool Fresh(Cached cached, DateOnly today) =>
        DateOnly.TryParse(cached.At, out var day) && day <= today && today <= day.AddDays(7);

    private static HttpClient CreateClient(HttpMessageHandler? handler)
    {
        var client = handler == null
            ? new HttpClient { Timeout = TimeSpan.FromSeconds(10) }
            : new HttpClient(handler, disposeHandler: false) { Timeout = TimeSpan.FromSeconds(10) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("Pilewise");
        return client;
    }

    private sealed class Cached
    {
        public bool? Native { get; set; }
        public string? Tier { get; set; }
        public string? Confidence { get; set; }
        public string? At { get; set; }
    }
}

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
/// Cached a week. Failures are omitted — callers keep any value already on the row.
/// </summary>
public sealed class SteamLinuxLookup
{
    public const string StateKey = "linux-ratings";

    // shortcut: sequential 1 req/s — Steam appdetails rate-limits hard; parallelise if a bulk API appears.
    private static readonly TimeSpan Spacing = TimeSpan.FromSeconds(1);
    private const int MaxFailures = 3;
    private static readonly HttpClient Http = CreateClient();

    private readonly IStateStore _state;
    private readonly ILogService _log;

    public bool Enabled { get; set; }

    public SteamLinuxLookup(IStateStore state, ILogService log)
    {
        _state = state;
        _log = log;
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
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var fetched = 0;
        var failures = 0;

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
                    result[id] = LinuxRating.Label(hit.Native, hit.Tier);
                    continue;
                }

                try
                {
                    var native = await FetchNativeAsync(id, ct);
                    // Native games still have Proton reports (e.g. CS2); only ask ProtonDB for Windows-only.
                    var tier = native == false ? await FetchTierAsync(id, ct) : null;
                    cache[id.ToString()] = new Cached { Native = native, Tier = tier, At = today.ToString("yyyy-MM-dd") };
                    result[id] = LinuxRating.Label(native, tier);
                    fetched++;
                    failures = 0;
                }
                catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
                {
                    _log.WriteWarning($"Linux: Steam app {id}: {ex.Message}");
                    if (++failures >= MaxFailures)
                    {
                        _log.WriteWarning($"Linux: stopping after {MaxFailures} consecutive failures.");
                        break;
                    }
                }

                if (i + 1 < ids.Count)
                    await Task.Delay(Spacing, ct);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            Flush();
            throw;
        }

        Flush();
        if (fetched > 0)
            _log.WriteInfo($"Linux: fetched {fetched} of {ids.Count} Steam games.");

        return result;
    }

    private async Task<bool?> FetchNativeAsync(int appId, CancellationToken ct)
    {
        using var response = await SendAsync(
            "https://store.steampowered.com/api/appdetails?filters=platforms&appids=" + appId, ct);
        response.EnsureSuccessStatusCode();
        return LinuxRating.ReadNative(await response.Content.ReadAsStringAsync(ct), appId);
    }

    private async Task<string?> FetchTierAsync(int appId, CancellationToken ct)
    {
        using var response = await SendAsync(
            "https://www.protondb.com/api/v1/reports/summaries/" + appId + ".json", ct);
        if (response.StatusCode == HttpStatusCode.NotFound) return null;
        response.EnsureSuccessStatusCode();
        return LinuxRating.ReadTier(await response.Content.ReadAsStringAsync(ct));
    }

    private static async Task<HttpResponseMessage> SendAsync(string url, CancellationToken ct)
    {
        for (var attempt = 0; ; attempt++)
        {
            var response = await Http.GetAsync(url, ct);
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

    private static HttpClient CreateClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("Pilewise");
        return client;
    }

    private sealed class Cached
    {
        public bool? Native { get; set; }
        public string? Tier { get; set; }
        public string? At { get; set; }
    }
}

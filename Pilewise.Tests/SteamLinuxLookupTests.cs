using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Pilewise.Services;
using Xunit;

using Pilewise.Tests.Fakes;

namespace Pilewise.Tests;

public class SteamLinuxLookupTests
{
    [Fact]
    public async Task Lookup_FreshCache_SkipsHttp()
    {
        var state = new MemoryStateStore();
        state.Save(SteamLinuxLookup.StateKey, JsonConvert.SerializeObject(new Dictionary<string, object>
        {
            ["400"] = new { Native = true, Tier = (string?)null, At = DateOnly.FromDateTime(DateTime.UtcNow).ToString("yyyy-MM-dd") },
        }));
        var handler = new FakeLinuxHttp();
        var lookup = new SteamLinuxLookup(state, new SilentLog(), handler, TimeSpan.Zero);

        var result = await lookup.LookupAsync([400]);

        Assert.Equal("Native", result[400]);
        Assert.Equal(0, handler.Calls);
    }

    [Fact]
    public async Task Lookup_StaleCache_Refetches()
    {
        var state = new MemoryStateStore();
        state.Save(SteamLinuxLookup.StateKey, JsonConvert.SerializeObject(new Dictionary<string, object>
        {
            ["400"] = new { Native = true, Tier = (string?)null, At = "2020-01-01" },
        }));
        var handler = new FakeLinuxHttp();
        handler.Steam(400, native: true);
        var lookup = new SteamLinuxLookup(state, new SilentLog(), handler, TimeSpan.Zero);

        var result = await lookup.LookupAsync([400]);

        Assert.Equal("Native", result[400]);
        Assert.Equal(1, handler.Calls);
    }

    [Fact]
    public async Task Lookup_SteamInconclusive_StillAsksProtonDb()
    {
        var handler = new FakeLinuxHttp();
        handler.Steam(400, success: false);
        handler.Proton(400, """{"tier":"gold","confidence":"strong","total":12}""");
        var lookup = new SteamLinuxLookup(new MemoryStateStore(), new SilentLog(), handler, TimeSpan.Zero);

        var result = await lookup.LookupAsync([400]);

        Assert.Equal("Gold", result[400]);
        Assert.Equal(2, handler.Calls);
        Assert.Contains("protondb.com", handler.Urls[1]);
    }

    [Fact]
    public async Task Lookup_NativeTrue_SkipsProtonDb()
    {
        var handler = new FakeLinuxHttp();
        handler.Steam(730, native: true);
        var lookup = new SteamLinuxLookup(new MemoryStateStore(), new SilentLog(), handler, TimeSpan.Zero);

        var result = await lookup.LookupAsync([730]);

        Assert.Equal("Native", result[730]);
        Assert.Equal(1, handler.Calls);
    }

    [Fact]
    public async Task Lookup_LowConfidence_WritesSoftMedal_AndDoesNotClaimThinBorked()
    {
        var handler = new FakeLinuxHttp();
        handler.Steam(1, native: false);
        handler.Proton(1, """{"tier":"gold","confidence":"low","total":2}""");
        handler.Steam(2, native: false);
        handler.Proton(2, """{"tier":"borked","confidence":"inadequate","total":1}""");
        var lookup = new SteamLinuxLookup(new MemoryStateStore(), new SilentLog(), handler, TimeSpan.Zero);

        var result = await lookup.LookupAsync([1, 2]);

        Assert.Equal("Gold?", result[1]);
        Assert.Equal(LinuxRating.Unknown, result[2]);
    }

    [Fact]
    public async Task Lookup_HttpFailure_IsUnavailable_AndNotWrittenToWeekCache()
    {
        var state = new MemoryStateStore();
        var handler = new FakeLinuxHttp { FailSteam = true };
        var lookup = new SteamLinuxLookup(state, new SilentLog(), handler, TimeSpan.Zero);

        var result = await lookup.LookupAsync([400]);

        Assert.Equal(LinuxRating.Unavailable, result[400]);
        Assert.True(string.IsNullOrEmpty(state.Load(SteamLinuxLookup.StateKey)));
    }

    [Fact]
    public async Task Lookup_CircuitBreaker_MarksRemainderUnavailable()
    {
        var handler = new FakeLinuxHttp { FailSteam = true };
        var lookup = new SteamLinuxLookup(new MemoryStateStore(), new SilentLog(), handler, TimeSpan.Zero);

        var result = await lookup.LookupAsync([1, 2, 3, 4, 5]);

        Assert.Equal(3, handler.Calls);
        Assert.Equal(LinuxRating.Unavailable, result[1]);
        Assert.Equal(LinuxRating.Unavailable, result[2]);
        Assert.Equal(LinuxRating.Unavailable, result[3]);
        Assert.Equal(LinuxRating.Unavailable, result[4]);
        Assert.Equal(LinuxRating.Unavailable, result[5]);
    }

    [Fact]
    public async Task Lookup_RateLimited_RetriesAndSucceeds()
    {
        var handler = new FakeLinuxHttp();
        handler.Steam(400, native: true, rateLimitOnce: true);
        var lookup = new SteamLinuxLookup(new MemoryStateStore(), new SilentLog(), handler, TimeSpan.Zero);

        var result = await lookup.LookupAsync([400]);

        Assert.Equal("Native", result[400]);
        Assert.Equal(2, handler.Calls);
    }

    [Fact]
    public async Task Lookup_Cancel_FlushesSuccessfulFetchesToCache()
    {
        var state = new MemoryStateStore();
        var handler = new FakeLinuxHttp();
        handler.Steam(400, native: true);
        handler.Steam(500, native: true);
        using var cts = new CancellationTokenSource();
        var lookup = new SteamLinuxLookup(state, new SilentLog(), handler, TimeSpan.Zero);
        var calls = 0;
        handler.OnCall = () =>
        {
            if (++calls == 2)
                cts.Cancel();
        };

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            lookup.LookupAsync([400, 500], ct: cts.Token));

        var cache = JsonConvert.DeserializeObject<Dictionary<string, Cached>>(state.Load(SteamLinuxLookup.StateKey)!);
        Assert.True(cache!.ContainsKey("400"));
        Assert.False(cache.ContainsKey("500"));
    }

    [Fact]
    public async Task Stamp_Unavailable_DoesNotOverwriteDurableLinux()
    {
        var row = CsvFormat.CreateRow("Steam", "Portal", "Steam:400");
        row.Linux = "Platinum";
        var handler = new FakeLinuxHttp { FailSteam = true };
        var lookup = new SteamLinuxLookup(new MemoryStateStore(), new SilentLog(), handler, TimeSpan.Zero) { Enabled = true };

        await lookup.StampAsync([row]);

        Assert.Equal("Platinum", row.Linux);
    }

    private sealed class FakeLinuxHttp : HttpMessageHandler
    {
        private readonly Dictionary<int, Queue<HttpResponseMessage>> _steam = new();
        private readonly Dictionary<int, Queue<HttpResponseMessage>> _proton = new();

        public int Calls { get; private set; }
        public List<string> Urls { get; } = [];
        public bool FailSteam { get; set; }
        public Action? OnCall { get; set; }

        public void Steam(int appId, bool native = false, bool success = true, bool rateLimitOnce = false)
        {
            if (!_steam.TryGetValue(appId, out var queue))
                _steam[appId] = queue = new Queue<HttpResponseMessage>();
            if (rateLimitOnce)
            {
                var limited = Json(HttpStatusCode.TooManyRequests, "{}");
                limited.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.Zero);
                queue.Enqueue(limited);
            }

            var body = success
                ? $"{{\"{appId}\":{{\"success\":true,\"data\":{{\"platforms\":{{\"windows\":true,\"linux\":{(native ? "true" : "false")}}}}}}}}}"
                : $"{{\"{appId}\":{{\"success\":false}}}}";
            queue.Enqueue(Json(HttpStatusCode.OK, body));
        }

        public void Proton(int appId, string body)
        {
            if (!_proton.TryGetValue(appId, out var queue))
                _proton[appId] = queue = new Queue<HttpResponseMessage>();
            queue.Enqueue(Json(HttpStatusCode.OK, body));
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Calls++;
            Urls.Add(request.RequestUri!.AbsoluteUri);
            OnCall?.Invoke();
            ct.ThrowIfCancellationRequested();

            var url = request.RequestUri.AbsoluteUri;
            if (url.Contains("store.steampowered.com", StringComparison.Ordinal))
            {
                if (FailSteam)
                    return Task.FromResult(Json(HttpStatusCode.BadGateway, "nope"));
                var id = int.Parse(url.Split("appids=")[1]);
                return Task.FromResult(Dequeue(_steam, id, Json(HttpStatusCode.OK, $"{{\"{id}\":{{\"success\":false}}}}")));
            }

            if (url.Contains("protondb.com", StringComparison.Ordinal))
            {
                var id = int.Parse(url.Split('/')[^1].Replace(".json", "", StringComparison.Ordinal));
                return Task.FromResult(Dequeue(_proton, id, Json(HttpStatusCode.NotFound, "")));
            }

            return Task.FromResult(Json(HttpStatusCode.NotFound, ""));
        }

        private static HttpResponseMessage Dequeue(
            Dictionary<int, Queue<HttpResponseMessage>> map, int id, HttpResponseMessage fallback) =>
            map.TryGetValue(id, out var queue) && queue.Count > 0 ? queue.Dequeue() : fallback;

        private static HttpResponseMessage Json(HttpStatusCode status, string body) =>
            new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
    }



    private sealed class Cached
    {
        public bool? Native { get; set; }
        public string? Tier { get; set; }
        public string? At { get; set; }
    }
}

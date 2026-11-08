using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using Pilewise.Services;
using Xunit;

namespace Pilewise.Tests;

public class SteamLinuxLookupTests
{
    private sealed class MemoryStateStore : IStateStore
    {
        private readonly Dictionary<string, string> _values = new(StringComparer.Ordinal);

        public string? Load(string key) => _values.TryGetValue(key, out var value) ? value : null;

        public void Save(string key, string value) => _values[key] = value;

        public void Delete(string key) => _values.Remove(key);
    }

    private sealed class SilentLog : ILogService
    {
        public void WriteLog(string message, LogLevel level = LogLevel.Info) { }
        public void WriteInfo(string message) { }
        public void WriteWarning(string message) { }
        public void WriteError(string message) { }
        public void WriteException(Exception ex, string additionalMessage = "") { }
    }

    private sealed class ScriptedHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, int, HttpResponseMessage> _respond;
        private int _calls;

        public ScriptedHandler(Func<HttpRequestMessage, int, HttpResponseMessage> respond) => _respond = respond;

        public int Calls => _calls;

        public List<Uri> Uris { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Uris.Add(request.RequestUri!);
            var n = Interlocked.Increment(ref _calls);
            return Task.FromResult(_respond(request, n));
        }
    }

    private static HttpResponseMessage Json(HttpStatusCode status, string body) =>
        new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private static HttpResponseMessage RateLimited(TimeSpan? retryAfter = null)
    {
        var response = Json(HttpStatusCode.TooManyRequests, """{"message":"slow down"}""");
        response.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(retryAfter ?? TimeSpan.Zero);
        return response;
    }

    private static string SteamPlatforms(int appId, bool? linux, bool success = true)
    {
        if (!success) return $"{{\"{appId}\":{{\"success\":false}}}}";
        var linuxJson = linux is null ? "null" : linux.Value ? "true" : "false";
        return $"{{\"{appId}\":{{\"success\":true,\"data\":{{\"platforms\":{{\"windows\":true,\"mac\":false,\"linux\":{linuxJson}}}}}}}}}";
    }

    private static SteamLinuxLookup Lookup(
        ScriptedHandler handler, MemoryStateStore? state = null, Func<DateOnly>? today = null, int maxFailures = 3) =>
        new(state ?? new MemoryStateStore(), new SilentLog(), handler, spacing: TimeSpan.Zero, maxFailures: maxFailures, today: today);

    [Fact]
    public async Task Lookup_SteamInconclusive_StillQueriesProtonDB()
    {
        var handler = new ScriptedHandler((req, _) =>
        {
            if (req.RequestUri!.Host.Contains("steampowered"))
                return Json(HttpStatusCode.OK, SteamPlatforms(400, linux: null, success: false));
            return Json(HttpStatusCode.OK, """{"tier":"gold","confidence":"strong","total":10}""");
        });
        var lookup = Lookup(handler);

        var result = await lookup.LookupAsync([400]);

        Assert.Equal("Gold", result[400]);
        Assert.Equal(2, handler.Calls);
        Assert.Contains(handler.Uris, u => u.Host.Contains("protondb"));
    }

    [Fact]
    public async Task Lookup_LowConfidenceMedal_IsMarkedProvisional()
    {
        var handler = new ScriptedHandler((req, _) =>
        {
            if (req.RequestUri!.Host.Contains("steampowered"))
                return Json(HttpStatusCode.OK, SteamPlatforms(20, linux: false));
            return Json(HttpStatusCode.OK, """{"tier":"gold","confidence":"low","total":22}""");
        });

        var result = await Lookup(handler).LookupAsync([20]);

        Assert.Equal("Gold?", result[20]);
    }

    [Fact]
    public async Task Lookup_CachesSuccessfulResultForAWeek_NotTransientFailures()
    {
        var state = new MemoryStateStore();
        var day = new DateOnly(2026, 11, 8);
        var steamCalls = 0;
        var handler = new ScriptedHandler((req, _) =>
        {
            if (req.RequestUri!.Host.Contains("steampowered"))
            {
                steamCalls++;
                return steamCalls == 1
                    ? Json(HttpStatusCode.OK, SteamPlatforms(400, linux: true))
                    : throw new HttpRequestException("boom");
            }

            return Json(HttpStatusCode.NotFound, "missing");
        });
        var lookup = Lookup(handler, state, () => day);

        Assert.Equal("Native", (await lookup.LookupAsync([400]))[400]);

        // Same day, still fresh — no second Steam call.
        Assert.Equal("Native", (await lookup.LookupAsync([400]))[400]);
        Assert.Equal(1, steamCalls);

        // After the TTL, a network failure is Unavailable and must not rewrite the cache to Unknown.
        var expired = Lookup(handler, state, () => day.AddDays(8));
        Assert.Equal(LinuxRating.Unavailable, (await expired.LookupAsync([400]))[400]);

        var cached = JObject.Parse(state.Load(SteamLinuxLookup.StateKey)!);
        Assert.True(cached["400"]!["Native"]!.Value<bool>());
        Assert.Equal(day.ToString("yyyy-MM-dd"), cached["400"]!["At"]!.Value<string>());
    }

    [Fact]
    public async Task Lookup_RateLimitedOnce_RetriesAndSucceeds()
    {
        var attempts = 0;
        var handler = new ScriptedHandler((req, _) =>
        {
            if (!req.RequestUri!.Host.Contains("steampowered"))
                return Json(HttpStatusCode.NotFound, "missing");
            attempts++;
            return attempts == 1
                ? RateLimited()
                : Json(HttpStatusCode.OK, SteamPlatforms(400, linux: true));
        });

        var result = await Lookup(handler).LookupAsync([400]);

        Assert.Equal("Native", result[400]);
        Assert.Equal(2, attempts);
    }

    [Fact]
    public async Task Lookup_ConsecutiveFailures_MarkRestUnavailable_WithoutCachingThem()
    {
        var state = new MemoryStateStore();
        var handler = new ScriptedHandler((_, _) =>
            throw new HttpRequestException("down"));
        var lookup = Lookup(handler, state, maxFailures: 3);

        var result = await lookup.LookupAsync([1, 2, 3, 4, 5]);

        Assert.Equal(5, result.Count);
        Assert.All(result.Values, v => Assert.Equal(LinuxRating.Unavailable, v));
        Assert.Null(state.Load(SteamLinuxLookup.StateKey));
        Assert.Equal(3, handler.Calls); // stopped after the failure budget
    }

    [Fact]
    public async Task StampAsync_Unavailable_PreservesExistingMedal()
    {
        var handler = new ScriptedHandler((_, _) => throw new HttpRequestException("down"));
        var lookup = Lookup(handler, maxFailures: 1);
        lookup.Enabled = true;
        var row = CsvGameExportService.CreateRow("Steam", "Portal", "Steam:400");
        row.Linux = "Platinum";

        await lookup.StampAsync([row]);

        Assert.Equal("Platinum", row.Linux);
    }

    [Fact]
    public async Task Lookup_CancelAfterFirstFetch_FlushesCache()
    {
        var state = new MemoryStateStore();
        using var cts = new CancellationTokenSource();
        var steamCalls = 0;
        var handler = new ScriptedHandler((req, _) =>
        {
            if (!req.RequestUri!.Host.Contains("steampowered"))
                return Json(HttpStatusCode.NotFound, "missing");

            steamCalls++;
            if (steamCalls == 1)
                return Json(HttpStatusCode.OK, SteamPlatforms(400, linux: true));

            cts.Cancel();
            throw new OperationCanceledException(cts.Token);
        });
        var lookup = Lookup(handler, state);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            lookup.LookupAsync([400, 500], ct: cts.Token));

        var cached = JObject.Parse(state.Load(SteamLinuxLookup.StateKey)!);
        Assert.NotNull(cached["400"]);
        Assert.Null(cached["500"]);
    }

    [Fact]
    public async Task Lookup_NativeSkipsProtonDB_WindowsOnlyDoesNot()
    {
        var handler = new ScriptedHandler((req, _) =>
        {
            if (req.RequestUri!.AbsolutePath.Contains("appdetails"))
            {
                var id = int.Parse(req.RequestUri.Query.Split('=').Last());
                return Json(HttpStatusCode.OK, SteamPlatforms(id, linux: id == 400));
            }

            return Json(HttpStatusCode.OK, """{"tier":"silver","confidence":"moderate","total":8}""");
        });

        var result = await Lookup(handler).LookupAsync([400, 70]);

        Assert.Equal("Native", result[400]);
        Assert.Equal("Silver", result[70]);
        Assert.Equal(1, handler.Uris.Count(u => u.Host.Contains("protondb")));
    }
}

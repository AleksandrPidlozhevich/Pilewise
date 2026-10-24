using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Pilewise.Models;
using Pilewise.Services;
using Xunit;

namespace Pilewise.Tests;

// Failure paths: Notion retries / errors, the log file, and CSV edge cases.
public class ReliabilityTests
{
    // ---- Notion: retries and errors ----------------------------------------------------------

    private const string DatabaseJson = """{"data_sources":[{"id":"ds-1"}]}""";

    private sealed class FakeNotion : HttpMessageHandler
    {
        private readonly Func<int, HttpResponseMessage> _pageResponse;
        private int _pageCalls;

        public FakeNotion(Func<int, HttpResponseMessage> pageResponse)
        {
            _pageResponse = pageResponse;
        }

        public int PageCalls => _pageCalls;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            if (request.RequestUri!.AbsolutePath.StartsWith("/v1/databases/", StringComparison.Ordinal))
                return Task.FromResult(Json(HttpStatusCode.OK, DatabaseJson));
            return Task.FromResult(_pageResponse(Interlocked.Increment(ref _pageCalls)));
        }
    }

    private static HttpResponseMessage Json(HttpStatusCode status, string body) =>
        new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private static HttpResponseMessage RateLimited()
    {
        var response = Json(HttpStatusCode.TooManyRequests, """{"message":"slow down"}""");
        response.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.Zero);
        return response;
    }

    private static NotionApiClient Client(FakeNotion handler) => new("token", "db", handler, TimeSpan.Zero);

    private static NotionPageBody Page() => new(new { });

    [Fact]
    public async Task NotionCreate_RateLimitedOnEveryTry_Throws()
    {
        var handler = new FakeNotion(_ => RateLimited());
        using var client = Client(handler);
        var done = 0;

        var error = await Assert.ThrowsAsync<HttpRequestException>(() =>
            client.AddPagesToDatabaseParallel([Page()], () => done++));

        Assert.Equal(HttpStatusCode.TooManyRequests, error.StatusCode);
        Assert.Equal(0, done); // never reported as written
        Assert.Equal(4, handler.PageCalls); // the first try + 3 retries
    }

    [Fact]
    public async Task NotionCreate_RateLimitedOnce_RetriesAndSucceeds()
    {
        var handler = new FakeNotion(call => call == 1 ? RateLimited() : Json(HttpStatusCode.OK, "{}"));
        using var client = Client(handler);
        var done = 0;

        await client.AddPagesToDatabaseParallel([Page()], () => done++);

        Assert.Equal(1, done);
        Assert.Equal(2, handler.PageCalls);
    }

    [Fact]
    public async Task NotionUpdate_ValidationError_IsNotRetriedAndCarriesNotionsMessage()
    {
        var handler = new FakeNotion(_ =>
            Json(HttpStatusCode.BadRequest, """{"message":"Playtime is expected to be number."}"""));
        using var client = Client(handler);

        var error = await Assert.ThrowsAsync<HttpRequestException>(() =>
            client.UpdatePagesParallel([("page-1", Page())]));

        Assert.Contains("Playtime is expected to be number.", error.Message);
        Assert.Equal(1, handler.PageCalls);
    }

    [Fact]
    public async Task NotionCreate_FirstFailure_StopsTheQueuedWrites()
    {
        var handler = new FakeNotion(_ => Json(HttpStatusCode.BadRequest, """{"message":"bad"}"""));
        using var client = Client(handler);

        await Assert.ThrowsAsync<HttpRequestException>(() =>
            client.AddPagesToDatabaseParallel(Enumerable.Range(0, 30).Select(_ => Page()).ToList()));

        Assert.True(handler.PageCalls < 30, $"{handler.PageCalls} writes were sent after the first failure");
    }

    [Fact]
    public async Task NotionQuery_ServerErrorOnce_IsRetried()
    {
        var handler = new FakeNotion(call => call == 1
            ? Json(HttpStatusCode.ServiceUnavailable, "<html>busy</html>")
            : Json(HttpStatusCode.OK, """{"results":[{"id":"p1"}],"has_more":false}"""));
        using var client = Client(handler);

        // The first retry waits a second (backoff), so this test takes about that long.
        var pages = await client.QueryAllPagesAsync();

        Assert.Single(pages);
    }

    // ---- log file ----------------------------------------------------------------------------

    private static string TempFolder()
    {
        var folder = Path.Combine(Path.GetTempPath(), "pilewise-log-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        return folder;
    }

    [Fact]
    public void Log_WritesTodaysFile_WithInnerExceptionDetails()
    {
        var folder = TempFolder();
        try
        {
            var log = new FileLogService(folder);
            log.WriteException(new InvalidOperationException("outer", new IOException("disk is full")), "Export failed");

            var text = File.ReadAllText(log.CurrentFilePath);
            Assert.Contains(DateTime.Now.ToString("yyyy-MM-dd"), Path.GetFileName(log.CurrentFilePath));
            Assert.Contains("Export failed", text);
            Assert.Contains("disk is full", text);
        }
        finally
        {
            Directory.Delete(folder, true);
        }
    }

    [Fact]
    public void Log_FileHeldBySharedWriter_StillAppends()
    {
        var folder = TempFolder();
        try
        {
            var log = new FileLogService(folder);
            // Another process (a `--sync` run) appending at the same time.
            using (new FileStream(log.CurrentFilePath, FileMode.Append, FileAccess.Write, FileShare.ReadWrite))
                log.WriteInfo("while shared");

            Assert.Contains("while shared", File.ReadAllText(log.CurrentFilePath));
        }
        finally
        {
            Directory.Delete(folder, true);
        }
    }

    [Fact]
    public void Log_FileLockedExclusively_DoesNotThrow()
    {
        var folder = TempFolder();
        try
        {
            var log = new FileLogService(folder);
            using (new FileStream(log.CurrentFilePath, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
                log.WriteError("dropped, not thrown");
        }
        finally
        {
            Directory.Delete(folder, true);
        }
    }

    // ---- CSV edge cases ----------------------------------------------------------------------

    [Fact]
    public void Csv_NameWithCarriageReturn_IsQuoted()
    {
        var row = new CsvGameExportRow { Name = "Odd\rName", HasSteam = true, SteamGameId = 1 };

        Assert.Equal("Steam,\"Odd\rName\",Steam:1", row.ToCsvLine());
    }

    [Fact]
    public void LegacyBareId_TooLargeForSteam_IsNotWrappedIntoAnId()
    {
        var row = CsvGameExportService.CreateRow("Steam", "Big", null);

        CsvGameExportService.ApplyLegacyBareId(row, "99999999999");

        Assert.Null(row.SteamGameId);
    }
}

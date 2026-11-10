using System;
using System.Collections.Generic;
using System.Linq;
using Pilewise.Services;
using Xunit;
using Pilewise.Tests.Fakes;

namespace Pilewise.Tests;

public class SyncLogTests
{
    private static SyncLogEntry Entry(int minutesAgo, string target = "Notion", bool ok = true, string summary = "Done")
    {
        return new SyncLogEntry(DateTimeOffset.Now.AddMinutes(-minutesAgo), target, ok, summary, "Auto-sync");
    }

    [Fact]
    public void Entries_AreNewestFirst_AndSurviveAReload()
    {
        var state = new MemoryStateStore();
        var log = new SyncLog(state);
        log.Add(Entry(10, "Notion"));
        log.Add(Entry(5, "Google Sheets", false, "Google authorization failed."));

        var reloaded = new SyncLog(state).Entries;

        Assert.Equal(["Google Sheets", "Notion"], reloaded.Select(e => e.Target));
        Assert.False(reloaded[0].Ok);
        Assert.Equal("⚠", reloaded[0].StatusIcon);
        Assert.Equal("Google authorization failed.", reloaded[0].Summary);
    }

    [Fact]
    public void OnlyTheLatestRunsAreKept()
    {
        var log = new SyncLog(new MemoryStateStore());
        for (var i = 0; i < SyncLog.MaxEntries + 10; i++) log.Add(Entry(1000 - i));

        Assert.Equal(SyncLog.MaxEntries, log.Entries.Count);
        Assert.True(log.Entries[0].When > log.Entries[^1].When);
    }

    [Fact]
    public void LongSummaries_AreShortened()
    {
        var log = new SyncLog(new MemoryStateStore());
        log.Add(Entry(0, summary: new string('x', 2000)));

        Assert.True(log.Entries[0].Summary.Length <= 500);
        Assert.EndsWith("…", log.Entries[0].Summary);
    }

    [Fact]
    public void Clear_EmptiesIt_AndRaisesChanged()
    {
        var log = new SyncLog(new MemoryStateStore());
        var changes = 0;
        log.Changed += () => changes++;
        log.Add(Entry(0));

        log.Clear();

        Assert.Empty(log.Entries);
        Assert.Equal(2, changes);
    }

    [Fact]
    public void BrokenJson_IsIgnored()
    {
        Assert.Empty(SyncLog.Parse("[{oops"));
    }

    [Fact]
    public void AutoSync_CsvTarget_RoundTrips()
    {
        var settings = new AutoSyncSettings(24, [SyncTarget.Notion, SyncTarget.Csv]);

        Assert.Equal("24h Notion, CSV", settings.Serialize());
        Assert.Equal([SyncTarget.Notion, SyncTarget.Csv], AutoSyncSettings.Parse("24h Notion, CSV").Targets);
    }
}
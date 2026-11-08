using System;
using System.Collections.Generic;
using System.IO;
using Pilewise.Services;
using Xunit;

using Pilewise.Tests.Fakes;

namespace Pilewise.Tests;

public class AutoSyncTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 17, 12, 0, 0, TimeSpan.Zero);
    private static readonly Dictionary<SyncTarget, DateTimeOffset> NoFailures = [];

    [Fact]
    public void Settings_RoundTrip()
    {
        var settings = new AutoSyncSettings(24, [SyncTarget.Notion, SyncTarget.Sheets]);
        Assert.Equal("24h Notion, Sheets", settings.Serialize());

        var parsed = AutoSyncSettings.Parse(settings.Serialize());
        Assert.Equal(24, parsed.IntervalHours);
        Assert.Equal([SyncTarget.Notion, SyncTarget.Sheets], parsed.Targets);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("24h")]
    [InlineData("5h Notion")]          // not an offered interval
    [InlineData("0h Notion")]
    [InlineData("24 Notion")]
    [InlineData("24h steam")]          // not a sync target
    [InlineData("daily Notion")]
    public void Parse_Invalid_IsOff(string? value) => Assert.False(AutoSyncSettings.Parse(value).IsOn);

    [Fact]
    public void Serialize_OffOrNoTargets_IsNull()
    {
        Assert.Null(new AutoSyncSettings(0, [SyncTarget.Notion]).Serialize());
        Assert.Null(new AutoSyncSettings(6, []).Serialize());
    }

    [Fact]
    public void Settings_SurviveTheSettingsFile()
    {
        var file = AppSettingsService.Serialize(new AppSettings("k", "i", null, null, AutoSync: "6h Sheets"));
        Assert.Equal("6h Sheets", AppSettingsService.Parse(file).AutoSync);
        Assert.Equal(DroppedFileKind.Settings, DroppedFile.Classify("Auto Sync: 6h Sheets"));
    }

    [Fact]
    public void Due_NeverSynced_IsDue()
    {
        var due = AutoSyncPlanner.Due(new AutoSyncSettings(24, [SyncTarget.Notion]),
            _ => null, NoFailures, _ => true, Now);
        Assert.Equal([SyncTarget.Notion], due);
    }

    [Fact]
    public void Due_OnlyTargetsPastTheInterval()
    {
        var last = new Dictionary<SyncTarget, DateTimeOffset>
        {
            [SyncTarget.Notion] = Now.AddHours(-5),   // not yet (6h)
            [SyncTarget.Sheets] = Now.AddHours(-6)    // exactly due
        };
        var due = AutoSyncPlanner.Due(new AutoSyncSettings(6, [SyncTarget.Notion, SyncTarget.Sheets]),
            t => last[t], NoFailures, _ => true, Now);
        Assert.Equal([SyncTarget.Sheets], due);
    }

    [Fact]
    public void Due_SkipsTargetsThatCantRun()
    {
        var due = AutoSyncPlanner.Due(new AutoSyncSettings(1, [SyncTarget.Notion, SyncTarget.Sheets]),
            _ => null, NoFailures, t => t == SyncTarget.Sheets, Now);
        Assert.Equal([SyncTarget.Sheets], due);
    }

    [Fact]
    public void Due_RecentFailure_WaitsAnHour()
    {
        var settings = new AutoSyncSettings(1, [SyncTarget.Notion]);
        var failures = new Dictionary<SyncTarget, DateTimeOffset> { [SyncTarget.Notion] = Now.AddMinutes(-30) };
        Assert.Empty(AutoSyncPlanner.Due(settings, _ => null, failures, _ => true, Now));

        failures[SyncTarget.Notion] = Now.AddMinutes(-61);
        Assert.Single(AutoSyncPlanner.Due(settings, _ => null, failures, _ => true, Now));
    }

    [Fact]
    public void Due_Off_IsEmpty() =>
        Assert.Empty(AutoSyncPlanner.Due(AutoSyncSettings.Off, _ => null, NoFailures, _ => true, Now));

    [Fact]
    public void SyncHistory_SeesRecordsMadeThroughAnotherInstance()
    {
        var state = new MemoryStateStore();
        var app = new SyncHistory(state);
        app.Record(SyncHistory.Csv, Now.AddDays(-1));

        // e.g. the auto-sync's HeadlessSync records Notion through its own instance…
        new SyncHistory(state).Record(SyncHistory.Notion, Now);

        // …the app's instance sees it, and a later Record doesn't drop it.
        Assert.Equal(Now, app.Get(SyncHistory.Notion));
        app.Record(SyncHistory.Csv, Now);
        Assert.Equal(Now, new SyncHistory(state).Get(SyncHistory.Notion));
    }

    [Fact]
    public void CallbackTextWriter_ReportsEachLine()
    {
        var lines = new List<string>();
        using var writer = new CallbackTextWriter(lines.Add);
        writer.WriteLine("== Notion ==");
        writer.Write("Done: 1\r\nnext");
        writer.Flush();
        Assert.Equal(["== Notion ==", "Done: 1", "next"], lines);
    }

}

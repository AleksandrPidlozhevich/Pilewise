using Pilewise.Services;
using Xunit;

namespace Pilewise.Tests;

public class WindowAndAutoStartTests
{
    private static readonly ScreenArea Primary = new(0, 0, 1920, 1040);

    [Fact]
    public void Placement_RoundTrips()
    {
        var placement = new WindowPlacement(-1500, 120, 1024, 700, true);
        Assert.Equal("-1500,120,1024,700,max", placement.Serialize());
        Assert.Equal(placement, WindowPlacement.Parse(placement.Serialize()));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("1,2,3")]
    [InlineData("a,2,800,600,normal")]
    [InlineData("1,2,0,600,normal")]
    public void Placement_Invalid_IsNull(string? value) => Assert.Null(WindowPlacement.Parse(value));

    [Fact]
    public void Fit_OnScreen_KeepsPositionAndSize()
    {
        var (width, height, keep) = new WindowPlacement(100, 100, 900, 700, false).Fit([Primary], 1, 780, 420);
        Assert.Equal((900d, 700d, true), (width, height, keep));
    }

    [Fact]
    public void Fit_OnAnUnpluggedMonitor_IsCentredInstead()
    {
        var (_, _, keep) = new WindowPlacement(2500, 100, 900, 700, false).Fit([Primary], 1, 780, 420);
        Assert.False(keep);
    }

    [Fact]
    public void Fit_OnASecondMonitorLeftOfThePrimary_IsKept()
    {
        var (_, _, keep) = new WindowPlacement(-1700, 50, 900, 700, false)
            .Fit([Primary, new ScreenArea(-1920, 0, 1920, 1040)], 1, 780, 420);
        Assert.True(keep);
    }

    [Fact]
    public void Fit_ClampsTheSize()
    {
        var (width, height, _) = new WindowPlacement(0, 0, 500, 5000, false).Fit([Primary], 2, 780, 420);
        Assert.Equal(780, width);      // at least the minimum
        Assert.Equal(520, height);     // at most the screen (1040 px at 200 % = 520 DIPs)
    }

    [Fact]
    public void WindowsCommand_QuotesThePath()
    {
        Assert.Equal("\"C:\\Users\\a b\\Pilewise\\current\\Pilewise.exe\" --minimized",
            AutoStart.WindowsCommand(@"C:\Users\a b\Pilewise\current\Pilewise.exe"));
    }

    [Fact]
    public void DesktopEntry_QuotesAndEscapesExec()
    {
        var entry = AutoStart.DesktopEntry("/home/me/Apps/Steam$Db.AppImage");
        Assert.Contains("Exec=\"/home/me/Apps/Steam\\$Db.AppImage\" --minimized\n", entry);
        Assert.StartsWith("[Desktop Entry]\n", entry);
    }

    [Fact]
    public void LaunchAgent_RunsAtLoadWithTheMinimizedFlag()
    {
        var plist = AutoStart.LaunchAgentPlist("com.example.pilewise", "/Applications/Pile & Wise.app/Contents/MacOS/Pilewise");
        Assert.Contains("<string>/Applications/Pile &amp; Wise.app/Contents/MacOS/Pilewise</string>", plist);
        Assert.Contains("<string>--minimized</string>", plist);
        Assert.Contains("<key>RunAtLoad</key>", plist);
        Assert.StartsWith("<?xml", plist);
    }

    [Fact]
    public void TrayTooltip_ShowsANewVersionBeforeTheResult()
    {
        Assert.Equal("Pilewise", TraySupport.Tooltip(null, null));
        Assert.Equal("Pilewise — ✓ Notion: 12 added", TraySupport.Tooltip("✓ Notion: 12 added", null));
        Assert.Equal("Pilewise — new version v2.1.0 is available", TraySupport.Tooltip(null, "2.1.0"));
        Assert.Equal("Pilewise — new version v2.1.0 is available · ✓ Notion: 12 added",
            TraySupport.Tooltip("✓ Notion: 12 added", "2.1.0"));
    }

    [Fact]
    public void TrayTooltip_LongResultKeepsTheVersion()
    {
        var tooltip = TraySupport.Tooltip(new string('x', 300), "2.1.0");
        Assert.Equal(TraySupport.MaxTooltipLength, tooltip.Length);
        Assert.StartsWith("Pilewise — new version v2.1.0 is available · x", tooltip);
        Assert.EndsWith("…", tooltip);
    }

    [Fact]
    public void CloseToTray_IsSavedOnlyWhenOn()
    {
        Assert.DoesNotContain("Close To Tray",
            AppSettingsService.Serialize(new AppSettings("k", "i", null, null, CloseToTray: false)));

        var on = AppSettingsService.Serialize(new AppSettings("k", "i", null, null, CloseToTray: true));
        Assert.True(AppSettingsService.Parse(on).CloseToTray);
        Assert.Null(AppSettingsService.Parse("Steam ID: 1").CloseToTray);
    }
}

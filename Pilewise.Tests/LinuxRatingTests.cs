using System.Collections.Generic;
using Pilewise.Services;
using Xunit;

namespace Pilewise.Tests;

public class LinuxRatingTests
{
    [Theory]
    [InlineData(true, "gold", "Native")]
    [InlineData(false, "gold", "Gold")]
    [InlineData(false, "borked", "Borked")]
    [InlineData(false, "pending", "Unknown")]
    [InlineData(false, null, "Unknown")]
    [InlineData(null, "platinum", "Unknown")]
    public void Label_NativeWins_AndAMedalNeedsAConfirmedWindowsBuild(bool? native, string? tier, string expected) =>
        Assert.Equal(expected, LinuxRating.Label(native, tier));

    [Fact]
    public void For_NoSteamId_IsUnknown_EvenWhenTheLookupKnowsOtherGames()
    {
        var xbox = CsvGameExportService.CreateRow("Xbox", "Halo", "Xbox:123");

        Assert.True(LinuxRating.TryGet(xbox, new Dictionary<int, string> { [400] = "Native" }, out var rating));
        Assert.Equal(LinuxRating.Unknown, rating);
    }

    [Fact]
    public void ReadNative_AndReadTier_ParseTheStoreResponses()
    {
        const string steam = """{"730":{"success":true,"data":{"platforms":{"windows":true,"mac":true,"linux":true}}}}""";
        const string proton = """{"tier":"gold","confidence":"strong","total":10}""";

        Assert.True(LinuxRating.ReadNative(steam, 730));
        Assert.Null(LinuxRating.ReadNative("""{"730":{"success":false}}""", 730));
        Assert.Equal("Gold", LinuxRating.ReadTier(proton));
        Assert.Null(LinuxRating.ReadTier("""{"tier":"pending"}"""));
    }

    [Fact]
    public void Serialize_IncludeLinux_AddsTheColumn_AndParseStillReadsTheGame()
    {
        var row = CsvGameExportService.CreateRow("Steam", "Portal", "Steam:400");
        row.Linux = "Native";
        var xbox = CsvGameExportService.CreateRow("Xbox", "Halo", "Xbox:1");

        var csv = CsvGameExportService.Serialize([row, xbox], includeLinux: true);

        Assert.StartsWith("Platform,Name,ID,Completed,Linux", csv);
        Assert.Contains("Steam,Portal,Steam:400,,Native", csv);
        Assert.Contains("Xbox,Halo,Xbox:1,,Unknown", csv);

        var parsed = CsvGameExportService.Parse(csv);
        Assert.Equal(2, parsed.Count);
        Assert.Equal(400, parsed[0].SteamGameId ?? parsed[1].SteamGameId);
        Assert.DoesNotContain("Linux", CsvGameExportService.Serialize([row]));
    }

    [Fact]
    public void ParseAndMerge_ExistingLinuxColumn_KeepsTheSavedRating()
    {
        const string csv = "Platform,Name,ID,Linux\nSteam,Portal,Steam:400,Native\n";

        var existing = CsvGameExportService.Parse(csv);
        var (merged, _, _) = CsvGameExportService.Merge(existing, [CsvGameExportService.CreateRow("Steam", "Portal", "Steam:400")]);

        Assert.True(CsvGameExportService.HasLinuxColumn(csv));
        Assert.Equal("Native", Assert.Single(merged).Linux);
        Assert.Contains("Steam,Portal,Steam:400,,Native", CsvGameExportService.Serialize(merged, includeLinux: true));
    }

    [Fact]
    public void Parse_ExtendedCurrentCsv_UsesTheIdAndLinuxColumns()
    {
        const string csv = "Platform,Name,ID,Notes,Linux\nSteam,Portal,Steam:400,classic,Native\n";

        var game = Assert.Single(CsvGameExportService.Parse(csv));

        Assert.Equal(400, game.SteamGameId);
        Assert.Equal("Native", game.Linux);
    }

    [Fact]
    public void Apply_MissingSteamLookup_KeepsExistingRating()
    {
        var steam = CsvGameExportService.CreateRow("Steam", "Portal", "Steam:400");
        steam.Linux = "Native";
        var xbox = CsvGameExportService.CreateRow("Xbox", "Halo", "Xbox:1");

        LinuxRating.Apply([steam, xbox], new Dictionary<int, string>());

        Assert.Equal("Native", steam.Linux);
        Assert.Equal(LinuxRating.Unknown, xbox.Linux);
    }

    [Fact]
    public void Settings_LinuxColumn_RoundTripsOnlyWhenOn()
    {
        var on = AppSettingsService.Serialize(new AppSettings("k", "i", "t", "d", LinuxColumn: true));
        Assert.Contains("Linux Column: yes", on);
        Assert.True(AppSettingsService.Parse(on).LinuxColumn);

        var off = AppSettingsService.Serialize(new AppSettings("k", "i", "t", "d"));
        Assert.DoesNotContain("Linux Column", off);
        Assert.Null(AppSettingsService.Parse(off).LinuxColumn);
    }
}

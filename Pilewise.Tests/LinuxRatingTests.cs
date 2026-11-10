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
    [InlineData(null, "platinum", "Platinum")]
    [InlineData(null, "Gold?", "Gold?")]
    [InlineData(false, "Silver?", "Silver?")]
    public void Label_NativeWins_ElseMedal_IncludingInconclusiveSteam(bool? native, string? tier, string expected)
    {
        Assert.Equal(expected, LinuxRating.Label(native, tier));
    }

    [Fact]
    public void For_NoSteamId_IsUnknown_EvenWhenTheLookupKnowsOtherGames()
    {
        var xbox = CsvFormat.CreateRow("Xbox", "Halo", "Xbox:123");

        Assert.True(LinuxRating.TryGet(xbox, new Dictionary<int, string> { [400] = "Native" }, out var rating));
        Assert.Equal(LinuxRating.Unknown, rating);
    }

    [Fact]
    public void TryGet_Unavailable_KeepsADurableRating_ButFillsAnEmptyRow()
    {
        var kept = CsvFormat.CreateRow("Steam", "Portal", "Steam:400");
        kept.Linux = "Gold";
        var empty = CsvFormat.CreateRow("Steam", "Hades", "Steam:1145360");

        Assert.False(LinuxRating.TryGet(kept, new Dictionary<int, string> { [400] = LinuxRating.Unavailable }, out _));
        Assert.True(LinuxRating.TryGet(empty, new Dictionary<int, string> { [1145360] = LinuxRating.Unavailable },
            out var rating));
        Assert.Equal(LinuxRating.Unavailable, rating);

        LinuxRating.Apply([kept, empty], new Dictionary<int, string>
        {
            [400] = LinuxRating.Unavailable,
            [1145360] = LinuxRating.Unavailable
        });
        Assert.Equal("Gold", kept.Linux);
        Assert.Equal(LinuxRating.Unavailable, empty.Linux);
    }

    [Fact]
    public void ReadNative_AndReadTier_ParseTheStoreResponses()
    {
        const string steam =
            """{"730":{"success":true,"data":{"platforms":{"windows":true,"mac":true,"linux":true}}}}""";
        const string proton = """{"tier":"gold","confidence":"strong","total":10}""";

        Assert.True(LinuxRating.ReadNative(steam, 730));
        Assert.Null(LinuxRating.ReadNative("""{"730":{"success":false}}""", 730));
        Assert.Null(LinuxRating.ReadNative("""{"730":{"success":true,"data":{"platforms":{"windows":true}}}}""", 730));
        Assert.Equal("Gold", LinuxRating.ReadTier(proton));
        Assert.Null(LinuxRating.ReadTier("""{"tier":"pending"}"""));
    }

    [Theory]
    [InlineData("""{"tier":"pending","provisionalTier":"gold","confidence":"inadequate"}""", "Gold?")]
    [InlineData("""{"tier":"gold","confidence":"low","total":3}""", "Gold?")]
    [InlineData("""{"tier":"silver","confidence":"moderate","total":20}""", "Silver")]
    [InlineData("""{"tier":"borked","confidence":"low","total":2}""", null)]
    [InlineData("""{"tier":"borked","confidence":"strong","total":40}""", "Borked")]
    [InlineData("""{"tier":"pending","provisionalTier":"borked","confidence":"inadequate"}""", null)]
    public void ReadTier_UsesConfidence_AndPendingFallsBackToProvisional(string json, string? expected)
    {
        Assert.Equal(expected, LinuxRating.ReadTier(json));
    }

    [Fact]
    public void Serialize_IncludeLinux_AddsTheColumn_AndParseStillReadsTheGame()
    {
        var row = CsvFormat.CreateRow("Steam", "Portal", "Steam:400");
        row.Linux = "Native";
        var xbox = CsvFormat.CreateRow("Xbox", "Halo", "Xbox:1");

        var csv = CsvFormat.Serialize([row, xbox], true);

        Assert.StartsWith("Platform,Name,ID,Completed,Linux", csv);
        Assert.Contains("Steam,Portal,Steam:400,,Native", csv);
        Assert.Contains("Xbox,Halo,Xbox:1,,Unknown", csv);

        var parsed = CsvFormat.Parse(csv);
        Assert.Equal(2, parsed.Count);
        Assert.Equal(400, parsed[0].SteamGameId ?? parsed[1].SteamGameId);
        Assert.DoesNotContain("Linux", CsvFormat.Serialize([row]));
    }

    [Fact]
    public void ParseAndMerge_ExistingLinuxColumn_KeepsTheSavedRating()
    {
        const string csv = "Platform,Name,ID,Linux\nSteam,Portal,Steam:400,Native\n";

        var existing = CsvFormat.Parse(csv);
        var (merged, _, _) =
            CsvGameExportService.Merge(existing, [CsvFormat.CreateRow("Steam", "Portal", "Steam:400")]);

        Assert.True(CsvFormat.HasLinuxColumn(csv));
        Assert.Equal("Native", Assert.Single(merged).Linux);
        Assert.Contains("Steam,Portal,Steam:400,,Native", CsvFormat.Serialize(merged, true));
    }

    [Fact]
    public void Parse_ExtendedCurrentCsv_UsesTheIdAndLinuxColumns()
    {
        const string csv = "Platform,Name,ID,Notes,Linux\nSteam,Portal,Steam:400,classic,Native\n";

        var game = Assert.Single(CsvFormat.Parse(csv));

        Assert.Equal(400, game.SteamGameId);
        Assert.Equal("Native", game.Linux);
    }

    [Fact]
    public void Apply_MissingSteamLookup_KeepsExistingRating()
    {
        var steam = CsvFormat.CreateRow("Steam", "Portal", "Steam:400");
        steam.Linux = "Native";
        var xbox = CsvFormat.CreateRow("Xbox", "Halo", "Xbox:1");

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
using System.Collections.Generic;
using Pilewise.Services;
using Xunit;

namespace Pilewise.Tests;

public class LinuxRatingTests
{
    [Theory]
    [InlineData(true, "gold", null, "Native")]
    [InlineData(false, "gold", null, "Gold")]
    [InlineData(false, "gold", "strong", "Gold")]
    [InlineData(false, "gold", "moderate", "Gold")]
    [InlineData(false, "gold", "good", "Gold")]
    [InlineData(false, "gold", "low", "Gold?")]
    [InlineData(false, "gold", "inadequate", "Gold?")]
    [InlineData(false, "borked", "strong", "Borked")]
    [InlineData(false, "borked", "low", "Unknown")]
    [InlineData(false, "borked", "inadequate", "Unknown")]
    [InlineData(false, "pending", null, "Unknown")]
    [InlineData(false, null, null, "Unknown")]
    [InlineData(null, "platinum", "strong", "Platinum")]
    [InlineData(null, "platinum", "low", "Platinum?")]
    public void Label_NativeWins_ProtonUsedWhenSteamInconclusive_LowConfidenceSoftened(
        bool? native, string? tier, string? confidence, string expected) =>
        Assert.Equal(expected, LinuxRating.Label(native, tier, confidence));

    [Fact]
    public void For_NoSteamId_IsUnknown_EvenWhenTheLookupKnowsOtherGames()
    {
        var xbox = CsvGameExportService.CreateRow("Xbox", "Halo", "Xbox:123");

        Assert.True(LinuxRating.TryGet(xbox, new Dictionary<int, string> { [400] = "Native" }, out var rating));
        Assert.Equal(LinuxRating.Unknown, rating);
    }

    [Fact]
    public void TryGet_TransientDoesNotEraseDurableRating()
    {
        var steam = CsvGameExportService.CreateRow("Steam", "Portal", "Steam:400");
        steam.Linux = "Gold";

        Assert.True(LinuxRating.TryGet(steam, new Dictionary<int, string> { [400] = LinuxRating.Unavailable }, out var value));
        Assert.Equal("Gold", value);
    }

    [Fact]
    public void TryGet_TransientFillsEmptyRow()
    {
        var steam = CsvGameExportService.CreateRow("Steam", "Portal", "Steam:400");

        Assert.True(LinuxRating.TryGet(steam, new Dictionary<int, string> { [400] = LinuxRating.Unavailable }, out var value));
        Assert.Equal(LinuxRating.Unavailable, value);
    }

    [Fact]
    public void ReadNative_AndReadSummary_ParseTheStoreResponses()
    {
        const string steam = """{"730":{"success":true,"data":{"platforms":{"windows":true,"mac":true,"linux":true}}}}""";
        const string proton = """{"tier":"gold","confidence":"strong","total":10}""";
        const string pending = """{"tier":"pending","provisionalTier":"silver","confidence":"inadequate","total":2}""";

        Assert.True(LinuxRating.ReadNative(steam, 730));
        Assert.Null(LinuxRating.ReadNative("""{"730":{"success":false}}""", 730));
        Assert.Equal(("gold", "strong"), LinuxRating.ReadSummary(proton));
        Assert.Equal(("silver", "inadequate"), LinuxRating.ReadSummary(pending));
        Assert.Equal("Gold", LinuxRating.ReadTier(proton));
        Assert.Equal("Silver", LinuxRating.ReadTier(pending));
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
    public void Apply_Unavailable_DoesNotEraseKnownMedal()
    {
        var steam = CsvGameExportService.CreateRow("Steam", "Portal", "Steam:400");
        steam.Linux = "Gold?";
        var fresh = CsvGameExportService.CreateRow("Steam", "Half-Life", "Steam:70");

        LinuxRating.Apply([steam, fresh], new Dictionary<int, string>
        {
            [400] = LinuxRating.Unavailable,
            [70] = LinuxRating.Unavailable
        });

        Assert.Equal("Gold?", steam.Linux);
        Assert.Equal(LinuxRating.Unavailable, fresh.Linux);
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

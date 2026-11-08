using Pilewise.Services;
using Xunit;

namespace Pilewise.Tests;

public class DroppedFileTests
{
    [Fact]
    public void Classify_GameList_IsGameCsv()
    {
        var csv = CsvFormat.Header + "\nSteam,Portal,Steam:400\n";
        Assert.Equal(DroppedFileKind.GameCsv, DroppedFile.Classify(csv));
    }

    [Fact]
    public void Classify_SettingsFile_IsSettings()
    {
        var content = AppSettingsService.Serialize(new AppSettings("key", "7656", null, null));
        Assert.Equal(DroppedFileKind.Settings, DroppedFile.Classify(content));
    }

    [Fact]
    public void Classify_SettingsWithOnlyTableName_IsSettings() =>
        Assert.Equal(DroppedFileKind.Settings, DroppedFile.Classify("Google Sheets Table: My games"));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   \n ")]
    [InlineData("Title,Year\nHalf-Life,1998")]
    [InlineData("just some text")]
    public void Classify_Other_IsUnknown(string? content) =>
        Assert.Equal(DroppedFileKind.Unknown, DroppedFile.Classify(content));

    [Theory]
    [InlineData("games.csv", true)]
    [InlineData("GAMES.CSV", true)]
    [InlineData("settings.txt", true)]
    [InlineData("library.xlsx", false)]
    [InlineData("noextension", false)]
    public void HasSupportedExtension(string name, bool expected) =>
        Assert.Equal(expected, DroppedFile.HasSupportedExtension(name));
}

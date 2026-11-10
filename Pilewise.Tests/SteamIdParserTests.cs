using Pilewise.Services;
using Xunit;

namespace Pilewise.Tests;

public class SteamIdParserTests
{
    [Theory]
    [InlineData("76561197960287930")]
    [InlineData("  76561197960287930  ")]
    [InlineData("https://steamcommunity.com/profiles/76561197960287930")]
    [InlineData("https://steamcommunity.com/profiles/76561197960287930/")]
    [InlineData("steamcommunity.com/profiles/76561197960287930/games/?tab=all")]
    [InlineData("[U:1:22202]")]
    [InlineData("STEAM_0:0:11101")]
    [InlineData("STEAM_1:0:11101")]
    public void Parse_NumericForms_GiveSteamId64(string input)
    {
        var result = SteamIdParser.Parse(input);

        Assert.Equal("76561197960287930", result.SteamId64);
        Assert.Null(result.VanityName);
    }

    [Theory]
    [InlineData("https://steamcommunity.com/id/gabelogannewell", "gabelogannewell")]
    [InlineData("https://steamcommunity.com/id/gabelogannewell/", "gabelogannewell")]
    [InlineData("http://www.steamcommunity.com/id/some_name-1/games", "some_name-1")]
    [InlineData("gabelogannewell", "gabelogannewell")]
    public void Parse_CustomUrls_GiveVanityName(string input, string expected)
    {
        var result = SteamIdParser.Parse(input);

        Assert.Null(result.SteamId64);
        Assert.Equal(expected, result.VanityName);
    }

    [Fact]
    public void Parse_LegacyOddAccount_UsesAuthServerBit()
    {
        // STEAM_0:Y:Z → base + Z*2 + Y
        Assert.Equal("76561197960287931", SteamIdParser.Parse("STEAM_0:1:11101").SteamId64);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    [InlineData("not a valid name!")]
    public void Parse_Garbage_IsInvalid(string? input)
    {
        Assert.False(SteamIdParser.Parse(input).IsValid);
    }
}
using System;
using Pilewise.Services;
using Xunit;

namespace Pilewise.Tests;

public class AppSettingsServiceTests
{
    // Pins the exact on-disk format (a round-trip alone can't — a symmetric bug in both
    // Serialize and Parse would still round-trip cleanly).
    [Fact]
    public void Serialize_WritesLabeledLinesInExpectedOrder()
    {
        var text = AppSettingsService.Serialize(new AppSettings("k", "i", "t", "d"));

        var nl = Environment.NewLine;
        Assert.Equal($"Steam API Key: k{nl}Steam ID: i{nl}NotionToken: t{nl}DbId: d{nl}", text);
    }

    [Fact]
    public void SerializeThenParse_RoundTripsAllFields()
    {
        var original = new AppSettings("steam-key", "7656119", "ntn_token", "db-123");

        var parsed = AppSettingsService.Parse(AppSettingsService.Serialize(original));

        Assert.Equal(original.SteamApiKey, parsed.SteamApiKey);
        Assert.Equal(original.SteamId, parsed.SteamId);
        Assert.Equal(original.NotionToken, parsed.NotionToken);
        Assert.Equal(original.DbId, parsed.DbId);
    }

    // Documents a real asymmetry worth knowing: a null field is written blank and read back as
    // "" (not null). So saving then importing a file leaves blanked credentials as empty strings,
    // which the import's "?? keep existing" guard will NOT treat as "absent".
    [Fact]
    public void NullField_SerializesBlank_ThenParsesAsEmptyString()
    {
        var serialized = AppSettingsService.Serialize(new AppSettings(null, "id", null, null));
        var parsed = AppSettingsService.Parse(serialized);

        Assert.Equal(string.Empty, parsed.SteamApiKey);
        Assert.Equal("id", parsed.SteamId);
    }

    [Fact]
    public void Parse_AbsentFields_AreNull()
    {
        var parsed = AppSettingsService.Parse("Steam API Key: only-this");

        Assert.Equal("only-this", parsed.SteamApiKey);
        Assert.Null(parsed.SteamId);
        Assert.Null(parsed.NotionToken);
        Assert.Null(parsed.DbId);
    }

    [Fact]
    public void Parse_IgnoresUnknownAndMalformedLines()
    {
        var parsed = AppSettingsService.Parse("Unknown: value\nno-colon-here\nSteam ID: 42");

        Assert.Equal("42", parsed.SteamId);
        Assert.Null(parsed.SteamApiKey);
    }

    [Fact]
    public void Parse_ValueContainingColonSpace_KeepsRemainderIntact()
    {
        // Split is limited to 2 parts, so a ": " inside the value survives.
        var parsed = AppSettingsService.Parse("DbId: a: b: c");
        Assert.Equal("a: b: c", parsed.DbId);
    }
}
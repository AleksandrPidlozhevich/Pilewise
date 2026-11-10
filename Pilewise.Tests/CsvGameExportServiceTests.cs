using System;
using System.Linq;
using Pilewise.Models;
using Pilewise.Services;
using Xunit;

namespace Pilewise.Tests;

public class CsvGameExportServiceTests
{
    [Fact]
    public void Parse_CurrentFormat_ReadsPlatformPrefixedIds()
    {
        var content =
            "Platform,Name,ID\n" +
            "Steam/GOG,Portal 2,Steam:620; GOG:123";

        var rows = CsvFormat.Parse(content);

        var row = Assert.Single(rows);
        Assert.Equal("Portal 2", row.Name);
        Assert.True(row.HasSteam);
        Assert.True(row.HasGog);
        Assert.Equal(620, row.SteamGameId);
        Assert.Equal(123, row.GogId);
        Assert.False(row.HasEpic);
    }

    [Fact]
    public void Parse_CurrentFormat_ReadsEpicNamespaceAndCatalogId()
    {
        var rows = CsvFormat.Parse("Platform,Name,ID\nEpic,Fortnite,Epic:fn-ns/abc123");

        var row = Assert.Single(rows);
        Assert.True(row.HasEpic);
        Assert.Equal("fn-ns", row.Namespace);
        Assert.Equal("abc123", row.CatalogItemId);
    }

    [Fact]
    public void Parse_CurrentFormat_ReadsXboxAndGamePassTag()
    {
        var rows = CsvFormat.Parse("Platform,Name,ID\nXbox/Game Pass,Halo,Xbox:9PXYZ");

        var row = Assert.Single(rows);
        Assert.True(row.HasXbox);
        Assert.True(row.IsGamePass);
        Assert.Equal("9PXYZ", row.XboxTitleId);
    }

    [Fact]
    public void Parse_LegacyWideFormat_ReadsSteamAndEpicColumns()
    {
        // Legacy wide: Platform, Name, Game ID, Catalog Item Id, Namespace.
        var rows = CsvFormat.Parse("Steam,Half-Life,70,,\nEpic,Control,,catItem,ctrl-ns");

        Assert.Equal(2, rows.Count);

        var hl = rows.Single(r => r.Name == "Half-Life");
        Assert.True(hl.HasSteam);
        Assert.Equal(70, hl.SteamGameId);

        var control = rows.Single(r => r.Name == "Control");
        Assert.True(control.HasEpic);
        Assert.Equal("catItem", control.CatalogItemId);
        Assert.Equal("ctrl-ns", control.Namespace);
    }

    [Fact]
    public void Parse_OldestTwoColumnFormat_AssumesSteam()
    {
        var rows = CsvFormat.Parse("Team Fortress 2,440");

        var row = Assert.Single(rows);
        Assert.Equal("Team Fortress 2", row.Name);
        Assert.True(row.HasSteam);
        Assert.Equal(440, row.SteamGameId);
    }

    [Fact]
    public void Parse_SkipsHeaderRow()
    {
        var rows = CsvFormat.Parse("Platform,Name,ID");
        Assert.Empty(rows);
    }

    [Fact]
    public void Parse_HandlesQuotedFieldWithCommaAndEscapedQuotes()
    {
        var rows = CsvFormat.Parse("Platform,Name,ID\nSteam,\"Portal 2, the \"\"sequel\"\"\",Steam:620");

        var row = Assert.Single(rows);
        Assert.Equal("Portal 2, the \"sequel\"", row.Name);
        Assert.Equal(620, row.SteamGameId);
    }

    [Fact]
    public void Serialize_QuotesNameContainingComma_AndRoundTrips()
    {
        var original = CsvFormat.CreateRow("Steam", "Sid Meier's Civilization, Beyond Earth", "Steam:65980");

        var csv = CsvFormat.Serialize(new[] { original });

        // The name actually gets CSV-quoted in the output (not just recovered on parse).
        Assert.Contains("\"Sid Meier's Civilization, Beyond Earth\"", csv);

        var roundTripped = Assert.Single(CsvFormat.Parse(csv));
        Assert.Equal(original.Name, roundTripped.Name);
        Assert.Equal(original.SteamGameId, roundTripped.SteamGameId);
    }

    [Fact]
    public void Serialize_WritesHeaderAndOrdersRowsByName()
    {
        var rows = new[]
        {
            CsvFormat.CreateRow("Steam", "Zelda", "Steam:2"),
            CsvFormat.CreateRow("Steam", "Amnesia", "Steam:1")
        };

        var lines = CsvFormat.Serialize(rows)
            .Split(["\r\n", "\n"], StringSplitOptions.RemoveEmptyEntries);

        Assert.Equal("Platform,Name,ID,Completed", lines[0]);
        Assert.StartsWith("Steam,Amnesia,", lines[1]);
        Assert.StartsWith("Steam,Zelda,", lines[2]);
    }

    [Fact]
    public void SerializeThenParse_MultiPlatformRow_PreservesEveryId()
    {
        var original = CsvFormat.CreateRow(
            "Steam/Epic/GOG/Xbox/Game Pass", "Everything",
            "Steam:1; Epic:ns/cat; GOG:2; Xbox:9P3");

        var csv = CsvFormat.Serialize(new[] { original });
        var row = Assert.Single(CsvFormat.Parse(csv));

        Assert.Equal(1, row.SteamGameId);
        Assert.Equal("ns", row.Namespace);
        Assert.Equal("cat", row.CatalogItemId);
        Assert.Equal(2, row.GogId);
        Assert.Equal("9P3", row.XboxTitleId);
        Assert.True(row.HasSteam && row.HasEpic && row.HasGog && row.HasXbox);
        Assert.True(row.IsGamePass);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Parse_EmptyOrWhitespace_ReturnsEmpty(string content)
    {
        Assert.Empty(CsvFormat.Parse(content));
    }

    [Fact]
    public void CreateRow_ParsesAllPlatformIdsFromCombinedField()
    {
        var row = CsvFormat.CreateRow(
            "Steam/Epic/GOG/Xbox",
            "Everything",
            "Steam:1; Epic:ns/cat; GOG:2; Xbox:9P3");

        Assert.Equal(1, row.SteamGameId);
        Assert.Equal("ns", row.Namespace);
        Assert.Equal("cat", row.CatalogItemId);
        Assert.Equal(2, row.GogId);
        Assert.Equal("9P3", row.XboxTitleId);
        Assert.True(row.HasSteam && row.HasEpic && row.HasGog && row.HasXbox);
    }

    [Fact]
    public void CreateRow_InfersPlatformFromIdEvenWithoutPlatformLabel()
    {
        var row = CsvFormat.CreateRow("", "Doom", "Steam:379720");

        Assert.True(row.HasSteam);
        Assert.Equal(379720, row.SteamGameId);
    }

    [Fact]
    public void IdText_FormatsPlatformPrefixedIds()
    {
        var row = CsvFormat.CreateRow("Steam/GOG", "X", "Steam:10; GOG:20");
        Assert.Equal("Steam:10; GOG:20", row.IdText);
    }

    [Fact]
    public void Merge_SameSteamId_MergesIntoOneRow()
    {
        // Distinct names so the match can only happen via Steam id, not the name fallback.
        var existing = new[] { CsvFormat.CreateRow("Steam", "Portal", "Steam:400") };
        var incoming = new[] { CsvFormat.CreateRow("Steam", "Portal (2007)", "Steam:400") };

        var (rows, added, updated) = CsvGameExportService.Merge(existing, incoming);

        Assert.Single(rows);
        Assert.Equal(0, added);
        Assert.Equal(1, updated);
    }

    [Fact]
    public void Merge_SameGameDifferentPlatforms_CombinesPlatformsByName()
    {
        // Steam row already present; incoming GOG row for the same title (matched on normalized name).
        var existing = new[] { CsvFormat.CreateRow("Steam", "The Witcher 3", "Steam:292030") };
        var incoming = new[] { CsvFormat.CreateRow("GOG", "The Witcher 3", "GOG:1207664663") };

        var (rows, added, updated) = CsvGameExportService.Merge(existing, incoming);

        var row = Assert.Single(rows);
        Assert.Equal(0, added);
        Assert.Equal(1, updated);
        Assert.True(row.HasSteam);
        Assert.True(row.HasGog);
        Assert.Equal(292030, row.SteamGameId);
        Assert.Equal(1207664663, row.GogId);
    }

    [Fact]
    public void Merge_DistinctGames_AreAddedNotMerged()
    {
        var existing = new[] { CsvFormat.CreateRow("Steam", "Game A", "Steam:1") };
        var incoming = new[] { CsvFormat.CreateRow("Steam", "Game B", "Steam:2") };

        var (rows, added, updated) = CsvGameExportService.Merge(existing, incoming);

        Assert.Equal(2, rows.Count);
        Assert.Equal(1, added);
        Assert.Equal(0, updated);
    }

    [Fact]
    public void Merge_SameXboxTitleId_MergesIntoOneRow()
    {
        // Distinct names so the merge is driven by the Xbox title id, not the name fallback.
        var existing = new[] { CsvFormat.CreateRow("Xbox", "Forza", "Xbox:9NXYZ") };
        var incoming = new[] { CsvFormat.CreateRow("Xbox/Game Pass", "Forza Horizon", "Xbox:9NXYZ") };

        var (rows, added, updated) = CsvGameExportService.Merge(existing, incoming);

        var row = Assert.Single(rows);
        Assert.Equal(0, added);
        Assert.Equal(1, updated);
        Assert.True(row.IsGamePass);
    }

    [Fact]
    public void Merge_XboxTitleId_MatchesCaseInsensitively()
    {
        // Distinct names: the only thing that can match these rows is the (case-folded) Xbox id.
        // If the id key weren't lower-cased, these would not merge.
        var existing = new[] { CsvFormat.CreateRow("Xbox", "Sea of Thieves", "Xbox:9pgw") };
        var incoming = new[] { CsvFormat.CreateRow("Xbox", "SoT Anniversary", "Xbox:9PGW") };

        var (rows, _, updated) = CsvGameExportService.Merge(existing, incoming);

        Assert.Single(rows);
        Assert.Equal(1, updated);
    }

    [Fact]
    public void Merge_DoesNotMutateInputRows()
    {
        var existing = CsvFormat.CreateRow("Steam", "Portal", "Steam:400");
        var incoming = CsvFormat.CreateRow("GOG", "Portal", "GOG:99");

        CsvGameExportService.Merge(new[] { existing }, new[] { incoming });

        // Merge clones first, so the originals are untouched.
        Assert.False(existing.HasGog);
        Assert.Null(existing.GogId);
    }

    [Fact]
    public void BuildFromLibraries_MergesSameTitleAcrossStores()
    {
        var steam = new[] { new SteamGame { Name = "Cyberpunk 2077", GameID = 1091500 } };
        var gog = new[] { new GogGame { Title = "Cyberpunk 2077", Id = 1423049311, IsGame = true } };

        var rows = CsvGameExportService.BuildFromLibraries(steam, null, gog);

        var row = Assert.Single(rows);
        Assert.True(row.HasSteam);
        Assert.True(row.HasGog);
        Assert.Equal(1091500, row.SteamGameId);
        Assert.Equal(1423049311, row.GogId);
    }

    [Fact]
    public void RowMatcher_MatchesByIdAndOnlyAfterRegister()
    {
        var matcher = new CsvGameExportService.RowMatcher(new[]
        {
            CsvFormat.CreateRow("Steam", "Portal", "Steam:400")
        });

        // An incoming row with the same Steam id (but a different name) matches the seeded row,
        // so the match is by id rather than the name fallback.
        Assert.NotNull(matcher.Match(CsvFormat.CreateRow("Steam", "Portal (2007)", "Steam:400")));

        var newGame = CsvFormat.CreateRow("GOG", "Bastion", "GOG:99");
        Assert.Null(matcher.Match(newGame));

        matcher.Register(newGame);
        Assert.NotNull(matcher.Match(CsvFormat.CreateRow("GOG", "Bastion", "GOG:99")));
    }

    [Theory]
    [InlineData("The Witcher 3: Wild Hunt™", "the witcher 3 wild hunt")]
    [InlineData("DOOM®", "doom")]
    [InlineData("  Half-Life:  Alyx  ", "half life alyx")]
    [InlineData("Tom Clancy's Rainbow Six® Siege", "tom clancy s rainbow six siege")]
    public void NormalizeGameName_StripsTrademarksPunctuationAndCollapsesSpace(string input, string expected)
    {
        Assert.Equal(expected, CsvGameExportService.NormalizeGameName(input));
    }

    [Fact]
    public void GetMissingColumns_ValidHeader_ReturnsEmpty()
    {
        Assert.Empty(CsvFormat.GetMissingColumns("Platform,Name,ID\nSteam,X,Steam:1"));
    }

    [Fact]
    public void GetMissingColumns_AcceptsLegacyGameIdColumn()
    {
        Assert.Empty(CsvFormat.GetMissingColumns("Platform,Name,Game ID"));
    }

    [Fact]
    public void GetMissingColumns_MissingColumns_AreReported()
    {
        var missing = CsvFormat.GetMissingColumns("Foo,Bar");
        Assert.Contains("Platform", missing);
        Assert.Contains("Name", missing);
        Assert.Contains("ID", missing);
    }

    [Fact]
    public void Row_CachedTexts_FollowEveryChange()
    {
        var row = CsvFormat.CreateRow("Steam", "Portal™", "Steam:400");
        Assert.Equal(("Steam", "Steam:400", "portal"), (row.PlatformText, row.IdText, row.NameKey));
        Assert.Equal(["Steam:400"], row.Keys);

        row.GogId = 7;
        row.HasGog = true;
        row.IsGamePass = true;
        row.Name = "Portal 2";
        Assert.Equal(("Steam/GOG/Game Pass", "Steam:400; GOG:7", "portal 2"),
            (row.PlatformText, row.IdText, row.NameKey));
        Assert.Equal(["Steam:400", "GOG:7"], row.Keys);

        var clone = row.Clone();
        clone.SteamGameId = null;
        Assert.Equal(["GOG:7"], clone.Keys);
        Assert.Equal(["Steam:400", "GOG:7"], row.Keys); // the original keeps its own cache

        var unnamed = CsvFormat.CreateRow("Epic", "Alan Wake", null);
        Assert.Equal(["Name:alan wake"], unnamed.Keys);
    }
}
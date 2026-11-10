using System.Collections.Generic;
using System.Linq;
using Pilewise.Services;
using Xunit;

namespace Pilewise.Tests;

public class NotionSchemaCheckTests
{
    [Fact]
    public void Check_CorrectSchema_NothingToDo()
    {
        var result = NotionSchemaCheck.Check(Schema(("Name", "title"), ("Platform's", "multi_select"),
            ("GameID", "rich_text"), ("Store link", "url"), ("Status", "status")));

        Assert.True(result.IsUsable);
        Assert.Empty(result.ToCreate);
    }

    [Fact]
    public void Check_FreshDatabase_PlansEveryMissingProperty()
    {
        // A new Notion database has only the "Name" title column.
        var result = NotionSchemaCheck.Check(Schema(("Name", "title")));

        Assert.True(result.IsUsable);
        Assert.Equal(
            [("Platform's", "multi_select"), ("GameID", "rich_text"), ("Store link", "url"), ("Completed", "checkbox")],
            result.ToCreate.Select(p => (p.Name, p.Type)));
    }

    [Fact]
    public void Check_IncludeLinux_PlansASelect_AndRejectsTheWrongType()
    {
        var missing = NotionSchemaCheck.Check(Schema(("Name", "title"), ("Platform's", "multi_select"),
            ("GameID", "rich_text"), ("Store link", "url"), ("Done", "checkbox")), true);

        Assert.True(missing.IsUsable);
        Assert.Equal(("Linux", "select"), Assert.Single(missing.ToCreate.Select(p => (p.Name, p.Type))));
        Assert.True(missing.Optional.Linux);

        var wrong = NotionSchemaCheck.Check(Schema(("Name", "title"), ("Platform's", "multi_select"),
            ("GameID", "rich_text"), ("Store link", "url"), ("Linux", "number")), true);

        Assert.False(wrong.IsUsable);
        Assert.Contains("Change the type of \"Linux\" to Select (it is number).", wrong.Problems);
    }

    [Fact]
    public void Check_LegacyNumberGameId_IsAProblem()
    {
        var result = NotionSchemaCheck.Check(Schema(("Name", "title"), ("Platform's", "multi_select"),
            ("GameID", "number")));

        Assert.False(result.IsUsable);
        Assert.Contains("Change the type of \"GameID\" to Text (it is number).", result.Problems);
    }

    [Fact]
    public void Check_TitleNamedDifferently_AsksToRename()
    {
        var result = NotionSchemaCheck.Check(Schema(("Game", "title")));

        Assert.Equal("Rename the title column \"Game\" to \"Name\".", Assert.Single(result.Problems));
    }

    [Fact]
    public void Check_NameIsNotTheTitle_IsAProblem()
    {
        var result = NotionSchemaCheck.Check(Schema(("Title", "title"), ("Name", "rich_text")));

        Assert.Contains("\"Name\" must be the title column (it is rich_text).", result.Problems);
    }

    private static Dictionary<string, string> Schema(params (string Name, string Type)[] properties)
    {
        return properties.ToDictionary(p => p.Name, p => p.Type);
    }
}
using Pilewise.Models;
using System.Collections.Generic;
using System.Linq;

namespace Pilewise.Services;

/// <summary>A database property the export would add (name + Notion type, e.g. "multi_select").</summary>
public sealed record NotionPropertyToCreate(string Name, string Type)
{
    /// <summary>Readable type for the preview, e.g. "multi-select".</summary>
    public string DisplayType => Type switch
    {
        "multi_select" => "multi-select",
        "rich_text" => "text",
        _ => Type
    };
}

/// <summary>Which opt-in columns the database has (and the export therefore fills).</summary>
public sealed record NotionOptionalColumns(bool Playtime, bool LastPlayed, bool Owned = false, bool Linux = false);

/// <summary>Outcome of <see cref="NotionSchemaCheck.Check"/>.</summary>
public sealed record NotionSchemaResult(
    IReadOnlyList<NotionPropertyToCreate> ToCreate,
    IReadOnlyList<string> Problems,
    NotionOptionalColumns Optional)
{
    public bool IsUsable => Problems.Count == 0;
}

/// <summary>
/// Validates a Notion database's properties against what the export reads and writes —
/// <c>Name</c> (title), <c>Platform's</c> (multi-select), <c>GameID</c> (text) — before anything is
/// written. Missing non-title properties are safe to add, so they're planned for creation (shown in
/// the preview); a misnamed title or a wrong type needs the user's hand in Notion, so it's reported.
/// </summary>
public static class NotionSchemaCheck
{
    public static NotionSchemaResult Check(IReadOnlyDictionary<string, string> propertyTypes, bool includeLinux = false)
    {
        var toCreate = new List<NotionPropertyToCreate>();
        var problems = new List<string>();

        // Every database has exactly one title property; the export expects it to be called "Name".
        if (!propertyTypes.TryGetValue(NotionDataFetcher.NameProperty, out var nameType))
        {
            var title = propertyTypes.FirstOrDefault(p => p.Value == "title").Key;
            problems.Add(title != null
                ? $"Rename the title column \"{title}\" to \"{NotionDataFetcher.NameProperty}\"."
                : $"Add a title column named \"{NotionDataFetcher.NameProperty}\".");
        }
        else if (nameType != "title")
        {
            problems.Add($"\"{NotionDataFetcher.NameProperty}\" must be the title column (it is {nameType}).");
        }

        Require(NotionDataFetcher.PlatformProperty, "multi_select", "Multi-select");
        Require(NotionDataFetcher.GameIdProperty, "rich_text", "Text");
        Require(NotionDataFetcher.StoreLinkProperty, "url", "URL");

        // Created and filled only while the Linux column tick is on. Off: leave a column the user
        // already has exactly as it is.
        var linux = includeLinux && Require(NotionDataFetcher.LinuxProperty, "select", "Select");

        var optional = new NotionOptionalColumns(
            Optional(NotionDataFetcher.PlaytimeProperty, "number", "Number"),
            Optional(NotionDataFetcher.LastPlayedProperty, "date", "Date"),
            Optional(NotionDataFetcher.OwnedProperty, "checkbox", "Checkbox"),
            linux);

        return new NotionSchemaResult(toCreate, problems, optional);

        // Opt-in columns are never created; they're used when present with the right type.
        bool Optional(string name, string type, string typeLabel)
        {
            if (!propertyTypes.TryGetValue(name, out var actual)) return false;
            if (actual == type) return true;
            problems.Add($"Change the type of \"{name}\" to {typeLabel} (it is {actual}), or remove it.");
            return false;
        }

        // True when the property is the right type, or missing (it will be created before the writes).
        bool Require(string name, string type, string typeLabel)
        {
            if (!propertyTypes.TryGetValue(name, out var actual))
            {
                toCreate.Add(new NotionPropertyToCreate(name, type));
                return true;
            }

            if (actual == type) return true;
            problems.Add($"Change the type of \"{name}\" to {typeLabel} (it is {actual}).");
            return false;
        }
    }
}

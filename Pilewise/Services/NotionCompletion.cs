using Newtonsoft.Json.Linq;
using System;
using System.Linq;

namespace Pilewise.Services;

/// <summary>
/// Reads whether a Notion page is marked as completed. A few common shapes are recognised: a checkbox
/// named Completed / Done / Finished / Beaten (which the export also writes, see
/// <see cref="NotionSchemaCheck"/>), or a Status / select property (named Status or one of those) set
/// to a "done" option — those are only read, never written.
/// </summary>
public static class NotionCompletion
{
    public static readonly string[] PropertyNames = ["Completed", "Complete", "Done", "Finished", "Beaten"];

    public static readonly string[] DoneValues =
        ["Completed", "Complete", "Done", "Finished", "Beaten", "Played", "100%"];

    /// <summary>True / false when the page has a completion property; null when the database has none.</summary>
    public static bool? Read(JToken? properties)
    {
        if (properties is not JObject all) return null;

        foreach (var property in all.Properties())
            if (IsCompletionName(property.Name) && Type(property) == "checkbox")
                return property.Value["checkbox"]?.Value<bool?>() ?? false;

        foreach (var property in all.Properties())
        {
            var type = Type(property);
            if (type is not ("status" or "select")) continue;
            if (!IsCompletionName(property.Name) && !property.Name.Equals("Status", StringComparison.OrdinalIgnoreCase))
                continue;

            // An empty select / status comes back as JSON null.
            var value = (property.Value[type] as JObject)?["name"]?.Value<string>()?.Trim();
            return value != null && DoneValues.Contains(value, StringComparer.OrdinalIgnoreCase);
        }

        return null;
    }

    private static bool IsCompletionName(string name) =>
        PropertyNames.Contains(name.Trim(), StringComparer.OrdinalIgnoreCase);

    private static string? Type(JProperty property) =>
        property.Value is JObject value ? value["type"]?.Value<string>() : null;
}

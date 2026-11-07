using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System.Text.RegularExpressions;

namespace Pilewise.Services;

/// <summary>
/// Extracts an Epic <c>authorizationCode</c> from arbitrary clipboard / pasted text:
/// the whole redirect JSON page, just the <c>"authorizationCode":"…"</c> fragment, or a
/// bare 32-character hex code.
/// </summary>
public static class EpicAuthCodeParser
{
    public static string? Extract(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return null;

        try
        {
            var code = JObject.Parse(text.Trim())["authorizationCode"]?.Value<string>();
            if (!string.IsNullOrWhiteSpace(code))
                return code.Trim();
        }
        catch (JsonReaderException)
        {
        }

        var match = Regex.Match(
            text,
            "\"authorizationCode\"\\s*:\\s*\"([0-9a-fA-F]{32})\"",
            RegexOptions.IgnoreCase);
        if (match.Success)
            return match.Groups[1].Value;

        match = Regex.Match(text, "\\b[0-9a-fA-F]{32}\\b");
        return match.Success ? match.Value : null;
    }
}
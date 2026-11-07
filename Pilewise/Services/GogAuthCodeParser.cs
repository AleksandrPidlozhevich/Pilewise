using System.Text.RegularExpressions;

namespace Pilewise.Services;

/// <summary>
/// Extracts a GOG authorization <c>code</c> from arbitrary clipboard / pasted text:
/// the whole <c>https://embed.gog.com/on_login_success?...&amp;code=…</c> redirect URL,
/// or a bare code token.
/// </summary>
public static class GogAuthCodeParser
{
    public static string? Extract(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return null;

        text = text.Trim();

        var match = Regex.Match(text, "[?&]code=([A-Za-z0-9_\\-]+)");
        if (match.Success)
            return match.Groups[1].Value;

        if (!text.Contains(' ') && text.Length >= 16 && Regex.IsMatch(text, "^[A-Za-z0-9_\\-]+$"))
            return text;

        return null;
    }
}
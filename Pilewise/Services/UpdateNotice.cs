using System;

namespace Pilewise.Services;

public static class UpdateNotice
{
    public const string StateKey = "last-version";

    /// <summary>The banner text when <paramref name="current"/> is newer than the version that last
    /// ran; null on a first install, the same version, a downgrade or an unparsable version.</summary>
    public static string? Message(string? lastRun, string current)
    {
        if (Parse(lastRun) is not { } previous || Parse(current) is not { } now || now <= previous) return null;
        return $"✓ Updated to v{current}";
    }

    public static string ReleaseUrl(string version) =>
        $"https://github.com/AleksandrPidlozhevich/Pilewise/releases/tag/v{version}";

    // "1.2.0-beta.1" → 1.2.0 (pre-release suffixes don't change the order for this purpose).
    private static Version? Parse(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var core = value.Trim().TrimStart('v').Split('-', '+')[0];
        return Version.TryParse(core, out var version) ? version : null;
    }
}

using System;
using System.IO;

namespace Pilewise.Services;

/// <summary>
/// Where the app keeps its per-user data (logs, encrypted secrets). This is deliberately not the
/// install folder: Velopack replaces that folder on every update, and inside a macOS .app bundle it
/// may not be writable at all.
/// </summary>
public static class AppPaths
{
    public static string DataDirectory { get; } = ResolveDataDirectory();

    public static string ExeDirectory => AppContext.BaseDirectory;

    private static string ResolveDataDirectory()
    {
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var current = Path.Combine(appData, "Pilewise");
        var legacy = Path.Combine(appData, "SteamDb");
        try
        {
            if (!Directory.Exists(current) && Directory.Exists(legacy))
                Directory.Move(legacy, current);
        }
        catch (Exception)
        {
            // Another instance moved it first, or a file is locked: start with what's there.
        }

        return current;
    }
}

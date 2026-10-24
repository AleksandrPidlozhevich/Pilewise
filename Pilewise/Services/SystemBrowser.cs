using System;
using System.ComponentModel;
using System.Diagnostics;

namespace Pilewise.Services;

/// <summary>Opens a URL, file or folder with its default app (browser, Excel, file manager), cross-platform.</summary>
public static class SystemBrowser
{
    /// <summary>Throws <see cref="InvalidOperationException"/> when nothing could open it (e.g. no app
    /// is associated with .csv files, or xdg-open is missing).</summary>
    public static void Open(string url)
    {
        try
        {
            using var _ = Process.Start(StartInfo(url));
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or PlatformNotSupportedException)
        {
            throw new InvalidOperationException($"Couldn't open {url}: {ex.Message}", ex);
        }
    }

    // The target goes in ArgumentList, not a joined argument string, so a path with spaces
    // ("/home/me/My Games/games.csv") reaches xdg-open / open as one argument.
    private static ProcessStartInfo StartInfo(string url)
    {
        if (OperatingSystem.IsWindows()) return new ProcessStartInfo { FileName = url, UseShellExecute = true };

        var info = new ProcessStartInfo(OperatingSystem.IsMacOS() ? "open" : "xdg-open") { UseShellExecute = false };
        info.ArgumentList.Add(url);
        return info;
    }
}

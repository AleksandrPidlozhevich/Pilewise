using System;
using System.Diagnostics;
using System.IO;
using System.Linq;

namespace Pilewise.Services;

/// <summary>
/// Whether a tray icon will actually be visible. Windows and macOS always have one; on Linux the
/// tray needs a StatusNotifier host (KDE, most panels, GNOME only with the AppIndicator extension),
/// which is asked over D-Bus. Without one, "Close to tray" would hide the window with no way back.
/// </summary>
public static class TraySupport
{
    public const string WatcherName = "org.kde.StatusNotifierWatcher";

    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(2);

    public static bool IsAvailable()
    {
        if (!OperatingSystem.IsLinux()) return true;

        var output = RunQuiet("gdbus",
                         "call --session --dest org.freedesktop.DBus --object-path /org/freedesktop/DBus " +
                         $"--method org.freedesktop.DBus.NameHasOwner {WatcherName}")
                     ?? RunQuiet("dbus-send",
                         "--session --print-reply --dest=org.freedesktop.DBus /org/freedesktop/DBus " +
                         $"org.freedesktop.DBus.NameHasOwner string:{WatcherName}");

        // Can't ask (no D-Bus tools): assume a tray rather than take the feature away.
        return ParseNameHasOwner(output) ?? true;
    }

    /// <summary>Some platforms cap tray tooltips (Windows: 127 chars).</summary>
    public const int MaxTooltipLength = 120;

    /// <summary>
    /// The tray tooltip: "Pilewise", then a newer version when one is out, then the last result
    /// ("Pilewise — new version v2.1.0 is available · ✓ Notion: 12 added"). The version comes first
    /// so a long result can't cut it off.
    /// </summary>
    public static string Tooltip(string? resultMessage, string? availableUpdateVersion)
    {
        var parts = new[]
            {
                availableUpdateVersion is { } version ? $"new version v{version} is available" : null,
                resultMessage
            }
            .Where(part => !string.IsNullOrWhiteSpace(part))
            .ToArray();
        var text = parts.Length == 0 ? "Pilewise" : $"Pilewise — {string.Join(" · ", parts)}";
        return text.Length <= MaxTooltipLength ? text : text[..(MaxTooltipLength - 1)] + "…";
    }

    /// <summary>
    /// Reads a NameHasOwner reply: gdbus prints "(true,)", dbus-send "boolean true".
    /// Null when the output is neither (tool failed or unknown format).
    /// </summary>
    public static bool? ParseNameHasOwner(string? output)
    {
        if (string.IsNullOrWhiteSpace(output)) return null;
        if (output.Contains("true", StringComparison.OrdinalIgnoreCase)) return true;
        if (output.Contains("false", StringComparison.OrdinalIgnoreCase)) return false;
        return null;
    }

    private static string? RunQuiet(string file, string arguments)
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo(file, arguments)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            });
            if (process == null) return null;

            var output = process.StandardOutput.ReadToEndAsync();
            if (!process.WaitForExit(Timeout))
            {
                process.Kill();
                return null;
            }

            return process.ExitCode == 0 ? output.GetAwaiter().GetResult() : null;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or IOException or InvalidOperationException)
        {
            return null;
        }
    }
}

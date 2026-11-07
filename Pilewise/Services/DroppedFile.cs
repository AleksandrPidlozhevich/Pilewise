using System;
using System.IO;

namespace Pilewise.Services;

/// <summary>What a file dropped onto the main window is.</summary>
public enum DroppedFileKind
{
    /// <summary>A Pilewise game list (<c>Platform,Name,ID</c> or a legacy layout): run Update .csv on it.</summary>
    GameCsv,

    /// <summary>A Save settings file (<c>Key: value</c> lines): import it.</summary>
    Settings,

    /// <summary>Anything else: nothing to do.</summary>
    Unknown
}

/// <summary>
/// Decides what to do with a file dropped onto the window from its name and contents. Pure, so
/// it's unit-tested; the view model reads the file and acts on the result.
/// </summary>
public static class DroppedFile
{
    /// <summary>Files larger than this aren't read (a game list is a few hundred KB at most).</summary>
    public const long MaxBytes = 20 * 1024 * 1024;

    public static bool HasSupportedExtension(string name)
    {
        var extension = Path.GetExtension(name);
        return extension.Equals(".csv", StringComparison.OrdinalIgnoreCase) ||
               extension.Equals(".txt", StringComparison.OrdinalIgnoreCase);
    }

    public static DroppedFileKind Classify(string? content)
    {
        if (string.IsNullOrWhiteSpace(content)) return DroppedFileKind.Unknown;

        if (CsvGameExportService.GetMissingColumns(content).Count == 0)
            return DroppedFileKind.GameCsv;

        var settings = AppSettingsService.Parse(content);
        var anyField = settings.SteamApiKey != null || settings.SteamId != null ||
                       settings.NotionToken != null || settings.DbId != null ||
                       settings.GoogleSheetsTableName != null || settings.ExcludedStores != null ||
                       settings.AutoSync != null || settings.CloseToTray != null ||
                       settings.LinuxColumn != null;
        return anyField ? DroppedFileKind.Settings : DroppedFileKind.Unknown;
    }
}

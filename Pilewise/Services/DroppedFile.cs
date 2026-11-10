using System;
using System.IO;

namespace Pilewise.Services;

public enum DroppedFileKind
{
    GameCsv,

    Settings,

    Unknown
}

public static class DroppedFile
{
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

        if (CsvFormat.GetMissingColumns(content).Count == 0)
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
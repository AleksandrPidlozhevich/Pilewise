using System;

namespace Pilewise.Services;

/// <summary>Credentials persisted to / loaded from the plaintext settings file.</summary>
public sealed record AppSettings(
    string? SteamApiKey, string? SteamId, string? NotionToken, string? DbId, string? GoogleSheetsTableName = null,
    string? ExcludedStores = null, string? AutoSync = null, bool? CloseToTray = null, string? CsvFile = null,
    string? Theme = null, bool? SidebarCollapsed = null, bool? LinuxColumn = null);

public static class AppSettingsService
{
    /// <summary>Secret-store key of the remembered settings (shared by the app and <c>--sync</c>).</summary>
    public const string SecretKey = "app-settings";

    public static string Serialize(AppSettings settings)
    {
        return $"Steam API Key: {settings.SteamApiKey}" + Environment.NewLine +
               $"Steam ID: {settings.SteamId}" + Environment.NewLine +
               $"NotionToken: {settings.NotionToken}" + Environment.NewLine +
               $"DbId: {settings.DbId}" + Environment.NewLine +
               // Optional line, only when set, so files stay readable by older versions unchanged.
               (string.IsNullOrEmpty(settings.GoogleSheetsTableName)
                   ? string.Empty
                   : $"Google Sheets Table: {settings.GoogleSheetsTableName}" + Environment.NewLine) +
               (string.IsNullOrEmpty(settings.ExcludedStores)
                   ? string.Empty
                   : $"Excluded Stores: {settings.ExcludedStores}" + Environment.NewLine) +
               (string.IsNullOrEmpty(settings.AutoSync)
                   ? string.Empty
                   : $"Auto Sync: {settings.AutoSync}" + Environment.NewLine) +
               (settings.CloseToTray == true ? "Close To Tray: yes" + Environment.NewLine : string.Empty) +
               (string.IsNullOrEmpty(settings.CsvFile)
                   ? string.Empty
                   : $"CSV File: {settings.CsvFile}" + Environment.NewLine) +
               (string.IsNullOrEmpty(settings.Theme) ? string.Empty : $"Theme: {settings.Theme}" + Environment.NewLine) +
               (settings.SidebarCollapsed == true ? "Sidebar: collapsed" + Environment.NewLine : string.Empty) +
               (settings.LinuxColumn == true ? "Linux Column: yes" + Environment.NewLine : string.Empty);
    }

    /// <summary>
    /// Parses the settings file. Fields absent from the file are returned as null so the
    /// caller can leave the corresponding values untouched.
    /// </summary>
    public static AppSettings Parse(string content)
    {
        string? steamApiKey = null, steamId = null, notionToken = null, dbId = null, sheetsTable = null,
            excludedStores = null, autoSync = null, csvFile = null, theme = null;
        bool? closeToTray = null, sidebarCollapsed = null, linuxColumn = null;

        foreach (var line in content.Split(["\r\n", "\r", "\n"], StringSplitOptions.RemoveEmptyEntries))
        {
            var keyValue = line.Split(": ", 2);
            if (keyValue.Length != 2) continue;

            switch (keyValue[0])
            {
                case "Steam API Key": steamApiKey = keyValue[1]; break;
                case "Steam ID": steamId = keyValue[1]; break;
                case "NotionToken": notionToken = keyValue[1]; break;
                case "DbId": dbId = keyValue[1]; break;
                case "Google Sheets Table": sheetsTable = keyValue[1]; break;
                case "Excluded Stores": excludedStores = keyValue[1]; break;
                case "Auto Sync": autoSync = keyValue[1]; break;
                case "CSV File": csvFile = keyValue[1].Trim(); break;
                case "Theme": theme = keyValue[1].Trim(); break;
                case "Sidebar": sidebarCollapsed = keyValue[1].Trim() == "collapsed"; break;
                case "Close To Tray":
                    closeToTray = keyValue[1].Trim() is "yes" or "true";
                    break;
                case "Linux Column":
                    linuxColumn = keyValue[1].Trim() is "yes" or "true";
                    break;
            }
        }

        return new AppSettings(steamApiKey, steamId, notionToken, dbId, sheetsTable, excludedStores, autoSync,
            closeToTray, csvFile, theme, sidebarCollapsed, linuxColumn);
    }
}
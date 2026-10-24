using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Pilewise.Services;
using System;
using System.IO;
using System.Threading.Tasks;

namespace Pilewise.ViewModels;

// Remembered settings (encrypted secret store), the Save / Import settings file and the store ticks.
public partial class MainWindowViewModel
{
    /// <summary>"Include in export" ticks per store (saved; also honoured by <c>--sync</c>).</summary>
    [ObservableProperty] private bool includeSteam = true;

    [ObservableProperty] private bool includeEpic = true;

    [ObservableProperty] private bool includeGog = true;

    [ObservableProperty] private bool includeXbox = true;

    [RelayCommand]
    private async Task SaveSettings()
    {
        var file = await _dialogs.PickSaveCsvAsync("pilewise-settings.csv");
        if (file == null) return;

        try
        {
            await _csv.WriteAsync(file, AppSettingsService.Serialize(CurrentSettings()));
            PersistSettings();
            ShowResult($"✓ Saved settings to {file.Name}");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _log.WriteException(ex, "Error saving the settings file");
            await _dialogs.ShowErrorAsync("Save settings", ex);
        }
    }

    [RelayCommand]
    private async Task ImportSettings()
    {
        // Importing changes the store ticks and credentials, which a running export is still reading.
        if (Progress.IsBusy) return;
        var file = await _dialogs.PickOpenCsvAsync("Import CSV File");
        if (file == null) return;

        try
        {
            var content = await _csv.ReadAsync(file);
            if (string.IsNullOrEmpty(content)) return;

            ApplyImportedSettings(content);
            ShowResult($"✓ Imported settings from {file.Name}");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _log.WriteException(ex, "Error reading the settings file");
            await _dialogs.ShowErrorAsync("Import settings", ex);
        }
    }

    /// <summary>Called on shutdown, so values typed into the fields survive a restart.</summary>
    public void SaveOnExit() => PersistSettings();

    /// <summary>Persists the current settings to the encrypted secret store (also read by the
    /// command-line <c>--sync</c>). Runs on Save/Import, when an export starts, and on exit.</summary>
    private void PersistSettings() =>
        _secrets.Save(AppSettingsService.SecretKey, AppSettingsService.Serialize(CurrentSettings()));

    /// <summary>Loads the settings remembered (encrypted) from a previous session.</summary>
    private void LoadPersistedSettings()
    {
        var content = _secrets.Load(AppSettingsService.SecretKey);
        ChooseStartTab(firstRun: string.IsNullOrEmpty(content));
        if (string.IsNullOrEmpty(content)) return;

        var settings = AppSettingsService.Parse(content);
        ApplyCredentials(settings);
        ApplyExcludedStores(settings.ExcludedStores);
        ApplyAutoSync(settings.AutoSync);
        CloseToTray = settings.CloseToTray ?? false;
        CsvFilePath = settings.CsvFile;
        ThemeIndex = ThemeIndexFor(settings.Theme);
        IsSidebarCollapsed = settings.SidebarCollapsed ?? false;
    }

    // Only overwrites the fields actually present in the settings file, then remembers them.
    private void ApplyImportedSettings(string content)
    {
        var settings = AppSettingsService.Parse(content);
        ApplyCredentials(settings);
        if (settings.ExcludedStores != null) ApplyExcludedStores(settings.ExcludedStores);
        if (settings.AutoSync != null) ApplyAutoSync(settings.AutoSync);
        if (settings.CloseToTray != null) CloseToTray = settings.CloseToTray.Value;
        if (settings.CsvFile != null) CsvFilePath = settings.CsvFile;
        if (settings.Theme != null) ThemeIndex = ThemeIndexFor(settings.Theme);
        PersistSettings();
    }

    // Fields missing from the settings keep their current value.
    private void ApplyCredentials(AppSettings settings)
    {
        SteamApiKey = settings.SteamApiKey ?? SteamApiKey;
        SteamId = settings.SteamId ?? SteamId;
        NotionToken = settings.NotionToken ?? NotionToken;
        DbId = settings.DbId ?? DbId;
        GoogleSheetsTableName = settings.GoogleSheetsTableName ?? GoogleSheetsTableName;
    }

    private AppSettings CurrentSettings() =>
        new(SteamApiKey, SteamId, NotionToken, DbId, GoogleSheetsTableName, _selection.Serialize(),
            CurrentAutoSync().Serialize(), CloseToTray, CsvFilePath, ThemeSetting, IsSidebarCollapsed);

    private void ApplyExcludedStores(string? excluded)
    {
        _selection.Load(excluded);
        IncludeSteam = _selection.Includes(Platforms.Steam);
        IncludeEpic = _selection.Includes(Platforms.Epic);
        IncludeGog = _selection.Includes(Platforms.Gog);
        IncludeXbox = _selection.Includes(Platforms.Xbox);
    }

    partial void OnIncludeSteamChanged(bool value) => OnIncludeChanged(Platforms.Steam, value);

    partial void OnIncludeEpicChanged(bool value) => OnIncludeChanged(Platforms.Epic, value);

    partial void OnIncludeGogChanged(bool value) => OnIncludeChanged(Platforms.Gog, value);

    partial void OnIncludeXboxChanged(bool value) => OnIncludeChanged(Platforms.Xbox, value);

    private void OnIncludeChanged(string store, bool included)
    {
        _selection.SetIncluded(store, included);
        RefreshCanExport();
    }
}

using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Pilewise.Services;
using System;
using System.Threading.Tasks;

namespace Pilewise.ViewModels;

// What the window reports: the result banner, the "Last sync" / backlog lines under the cards, the
// sync history, and app updates.
public partial class MainWindowViewModel
{
    // ---- result banner -------------------------------------------------------------------------

    /// <summary>Outcome of the last export ("✓ Notion: 12 added, 3 updated"), shown as a banner.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasResult))]
    private string? resultMessage;

    public bool HasResult => ResultMessage is not null;

    /// <summary>Label of the banner's follow-up action ("Open file", "Open database"), or null.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasResultAction))]
    private string? resultActionLabel;

    public bool HasResultAction => ResultActionLabel is not null;

    private Action? _resultAction;

    /// <summary>A second banner action (e.g. "Undo" after Update .csv), or null.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasResultSecondaryAction))]
    private string? resultSecondaryLabel;

    public bool HasResultSecondaryAction => ResultSecondaryLabel is not null;

    private Func<Task>? _resultSecondaryAction;

    private void ShowResult(
        string message, string? actionLabel = null, Action? action = null,
        string? secondaryLabel = null, Func<Task>? secondaryAction = null)
    {
        ResultMessage = message;
        ResultActionLabel = action != null ? actionLabel : null;
        _resultAction = action;
        ResultSecondaryLabel = secondaryAction != null ? secondaryLabel : null;
        _resultSecondaryAction = secondaryAction;
    }

    [RelayCommand]
    private void RunResultAction() => _resultAction?.Invoke();

    [RelayCommand]
    private async Task RunResultSecondaryAction()
    {
        if (_resultSecondaryAction != null) await _resultSecondaryAction();
    }

    [RelayCommand]
    private void DismissResult()
    {
        ResultMessage = null;
        ResultActionLabel = null;
        _resultAction = null;
        ResultSecondaryLabel = null;
        _resultSecondaryAction = null;
    }

    // ---- last sync, backlog progress, history --------------------------------------------------

    /// <summary>"Last sync: today 14:20" under each export button; null when never synced.</summary>
    [ObservableProperty] private string? csvLastSync;

    [ObservableProperty] private string? notionLastSync;

    [ObservableProperty] private string? sheetsLastSync;

    /// <summary>"Completed 120 of 640 (19%)" from the last Notion / Sheets export; null before one.</summary>
    [ObservableProperty] private string? notionProgress;

    [ObservableProperty] private string? sheetsProgress;

    private void RefreshLastSync()
    {
        var now = DateTimeOffset.Now;
        CsvLastSync = SyncHistory.Describe(_syncHistory.Get(SyncHistory.Csv), now);
        NotionLastSync = SyncHistory.Describe(_syncHistory.Get(SyncHistory.Notion), now);
        SheetsLastSync = SyncHistory.Describe(_syncHistory.Get(SyncHistory.GoogleSheets), now);
        NotionProgress = _backlog.Get(SyncHistory.Notion)?.Text;
        SheetsProgress = _backlog.Get(SyncHistory.GoogleSheets)?.Text;
    }

    // A manual export succeeded: timestamp it and list it in the history with the banner's text.
    private void RecordSync(string target)
    {
        _syncHistory.Record(target, DateTimeOffset.Now);
        _syncLog.Add(new SyncLogEntry(DateTimeOffset.Now, target, true,
            (ResultMessage ?? "Done").TrimStart('✓', ' '), SyncLog.Manual));
        RefreshLastSync();
    }

    private void LogFailure(string target, Exception ex) =>
        _syncLog.Add(new SyncLogEntry(DateTimeOffset.Now, target, false, ex.Message, SyncLog.Manual));

    /// <summary>Opens the list of recent sync runs (manual, auto-sync, tray and command line).</summary>
    [RelayCommand]
    public void ShowSyncHistory() => _dialogs.ShowSyncLog(new SyncLogViewModel(_syncLog));

    // ---- app updates ---------------------------------------------------------------------------

    /// <summary>The running version, shown in the window corner (e.g. <c>v1.1.0</c>).</summary>
    public string AppVersion => $"v{_updates.CurrentVersion}";

    /// <summary>A newer version found on GitHub Releases at startup, or null.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsUpdateAvailable), nameof(UpdateStatus), nameof(UpdateButtonText))]
    private string? availableUpdateVersion;

    public bool IsUpdateAvailable => AvailableUpdateVersion is not null;

    /// <summary>"Update to v2.1.0" on the sidebar button.</summary>
    public string UpdateButtonText => $"Update to v{AvailableUpdateVersion}";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(UpdateStatus), nameof(CheckUpdatesLabel), nameof(CheckUpdatesTip))]
    private bool isCheckingForUpdate;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(UpdateStatus))]
    private DateTimeOffset? lastUpdateCheck;

    /// <summary>Only the installed app can update itself (not a development build).</summary>
    public bool CanCheckForUpdates => _updates.IsInstalled;

    public string CheckUpdatesLabel => IsCheckingForUpdate ? "Checking…" : "Check for updates";

    /// <summary>The ⟳ button's tooltip (it stays visible, disabled, in a development build).</summary>
    public string CheckUpdatesTip => CanCheckForUpdates
        ? CheckUpdatesLabel
        : "Only the installed app can check for updates";

    /// <summary>The Updates panel's line: up to date / a version is available / why it can't tell.</summary>
    public string UpdateStatus =>
        !_updates.IsInstalled ? $"Pilewise {AppVersion}. Updates work in the installed app (from the GitHub release), not in a development build."
        : IsCheckingForUpdate ? "Checking GitHub for a newer version…"
        : AvailableUpdateVersion is { } version ? $"Version {version} is available (you have {AppVersion})."
        : _updates.LastCheckError is { } error ? $"Couldn't check for updates: {error}"
        : LastUpdateCheck is { } checkedAt ? $"You're up to date ({AppVersion}), checked at {checkedAt:HH:mm}."
        : $"You have {AppVersion}.";

    /// <summary>How often a long-running app (in the tray) looks for a new version.</summary>
    private static readonly TimeSpan UpdateCheckInterval = TimeSpan.FromHours(12);

    /// <summary>A check started from the ⟳ button spins at least this long, so a quick answer from
    /// GitHub still reads as "checked".</summary>
    private static readonly TimeSpan MinManualCheckDuration = TimeSpan.FromMilliseconds(800);

    private async Task CheckForUpdateAsync(TimeSpan minDuration = default)
    {
        IsCheckingForUpdate = true;
        try
        {
            var check = _updates.CheckForUpdateAsync();
            await Task.WhenAll(check, Task.Delay(minDuration));
            AvailableUpdateVersion = await check;
            LastUpdateCheck = DateTimeOffset.Now;
        }
        finally
        {
            IsCheckingForUpdate = false;
        }
    }

    /// <summary>Settings → Updates → ⟳.</summary>
    [RelayCommand]
    private Task CheckForUpdates() =>
        IsCheckingForUpdate ? Task.CompletedTask : CheckForUpdateAsync(MinManualCheckDuration);

    // Called from the minute timer: re-check when the last check is older than UpdateCheckInterval.
    private async Task CheckForUpdatesIfStaleAsync()
    {
        if (!_updates.IsInstalled || IsCheckingForUpdate || IsUpdateAvailable) return;
        if (LastUpdateCheck is { } last && DateTimeOffset.Now - last < UpdateCheckInterval) return;
        await CheckForUpdateAsync();
    }

    // Once after an update: "Updated to v1.2.0" with a link to its release notes.
    private void ShowWhatsNew()
    {
        var current = _updates.CurrentVersion;
        var message = UpdateNotice.Message(_state.Load(UpdateNotice.StateKey), current);
        _state.Save(UpdateNotice.StateKey, current);
        if (message == null) return;

        _log.WriteInfo($"Updated to {current}.");
        ShowResult(message, "What's new", () => _dialogs.OpenLink(UpdateNotice.ReleaseUrl(current)));
    }

    [RelayCommand]
    private async Task InstallUpdate()
    {
        if (Progress.IsBusy) return;
        Progress.Begin($"Downloading update {AvailableUpdateVersion}…");
        try
        {
            // Constructed on the UI thread, so callbacks marshal back to it automatically.
            var reporter = new Progress<int>(percent =>
            {
                Progress.IsIndeterminate = false;
                Progress.Maximum = 100;
                Progress.Value = percent;
                Progress.Status = $"Downloading update {AvailableUpdateVersion}… {percent}%";
            });
            // Restarts the app into the new version on success.
            await _updates.DownloadAndRestartAsync(reporter, Progress.Token);
        }
        catch (OperationCanceledException) when (Progress.Token.IsCancellationRequested)
        {
            _log.WriteInfo("Update download cancelled by user.");
        }
        catch (Exception ex)
        {
            _log.WriteException(ex, "Error installing update");
            await _dialogs.ShowErrorAsync("Update error", ex);
        }
        finally
        {
            Progress.End();
        }
    }
}

using Avalonia.Input.Platform;
using Avalonia.Platform.Storage;
using CommunityToolkit.Mvvm.Input;
using Pilewise.Services;
using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Pilewise.ViewModels;

// The export commands: CSV, Notion (preview first), Google Sheets, the library viewer, Notion
// duplicates and files dropped on the window.
public partial class MainWindowViewModel
{
    private const string FetchingGames = "Fetching games…";

    /// <summary>
    /// The frame every export shares: one at a time, busy bar on, previous result cleared, settings
    /// remembered; cancel is logged quietly, any other error is logged, listed in the sync history
    /// (when <paramref name="historyTarget"/> is set) and shown.
    /// </summary>
    /// <param name="operation">For the log ("CSV export cancelled by user.").</param>
    /// <param name="catchSteamAuth">Show a 401 from Steam as "Steam authorization error" (exports that
    /// fetch the library directly).</param>
    private async Task RunOperationAsync(
        string busyStatus, string operation, string errorTitle, Func<Task> work,
        string? historyTarget = null, bool catchSteamAuth = false)
    {
        if (Progress.IsBusy) return;
        Progress.Begin(busyStatus);
        DismissResult();
        PersistSettings();
        try
        {
            await work();
        }
        catch (UnauthorizedAccessException ex) when (catchSteamAuth)
        {
            _log.WriteException(ex, "Steam API authorization error");
            await _dialogs.ShowErrorAsync("Steam authorization error", ex);
        }
        // Only the Cancel button counts as a cancel: an HTTP timeout is an OperationCanceledException
        // too, and must be reported like any other error.
        catch (OperationCanceledException) when (Progress.Token.IsCancellationRequested)
        {
            _log.WriteInfo($"{operation} cancelled by user.");
        }
        catch (Exception ex)
        {
            _log.WriteException(ex, $"{operation} failed");
            if (historyTarget != null) LogFailure(historyTarget, ex);
            await _dialogs.ShowErrorAsync(errorTitle, ex);
        }
        finally
        {
            Progress.End();
        }
    }

    private Task<GameLibraryResult> FetchLibraryAsync() =>
        _library.FetchAsync(SteamApiKey, SteamId, Progress.CreateReporter(), Progress.SetIndeterminate, Progress.Token);

    // ---- CSV -----------------------------------------------------------------------------------

    [RelayCommand(CanExecute = nameof(CanFetchLibrary))]
    private Task ExportToCsv() =>
        RunOperationAsync(FetchingGames, "CSV export", "CSV export error", async () =>
        {
            var result = await FetchLibraryAsync();
            if (!await HandleFetchedLibraryAsync(result, "Export to CSV")) return;

            var file = await _dialogs.PickSaveCsvAsync("dbgame.csv");
            if (file == null) return;

            // Export just writes a fresh file with the fetched games — no merging.
            await _csv.WriteAsync(file, CsvGameExportService.Serialize(result.Rows));
            _log.WriteInfo($"CSV created with {result.Rows.Count} rows.");
            ShowResult($"✓ Saved {result.Rows.Count} games to {file.Name}", "Open file", OpenFileAction(file));
            RememberCsvFile(file);
            RecordSync(SyncHistory.Csv);
        }, SyncHistory.Csv, catchSteamAuth: true);

    [RelayCommand(CanExecute = nameof(CanFetchLibrary))]
    private async Task UpdateCsv()
    {
        if (Progress.IsBusy) return;
        var file = await _dialogs.PickOpenCsvAsync("Select CSV File to Update");
        if (file != null) await UpdateCsvFileAsync(file);
    }

    private Task UpdateCsvFileAsync(IStorageFile file) =>
        RunOperationAsync(FetchingGames, "CSV update", "CSV update error", async () =>
        {
            // Validate the chosen file is a Pilewise CSV before touching it.
            var existingContent = await _csv.ReadAsync(file);
            var missingColumns = CsvGameExportService.GetMissingColumns(existingContent);
            if (missingColumns.Count > 0)
            {
                _log.WriteWarning($"Update CSV aborted: missing columns {string.Join(", ", missingColumns)}.");
                await _dialogs.ShowErrorAsync("Update CSV", new Exception(
                    "The selected file is not a Pilewise CSV. Missing columns: " +
                    string.Join(", ", missingColumns) +
                    $".{Environment.NewLine}Expected header: {CsvGameExportService.Header}"));
                return;
            }

            var result = await FetchLibraryAsync();
            if (!await HandleFetchedLibraryAsync(result, "Update CSV")) return;

            var (added, total, backup) = await _csv.WriteMergedAsync(file, existingContent, result.Rows);
            var backupNote = backup != null ? $" · previous version saved as {Path.GetFileName(backup)}" : "";
            ShowResult(
                (added == 0
                    ? $"✓ {file.Name} is already up to date ({total} games)"
                    : $"✓ Added {added} new games to {file.Name} ({total} total)") + backupNote,
                "Open file", OpenFileAction(file),
                "Undo", added > 0 ? UndoCsvUpdateAction(file, backup) : null);
            RememberCsvFile(file);
            RecordSync(SyncHistory.Csv);
        }, SyncHistory.Csv, catchSteamAuth: true);

    // Undo for Update .csv: the .bak written just before goes back over the file.
    private Func<Task>? UndoCsvUpdateAction(IStorageFile file, string? backup)
    {
        var path = file.TryGetLocalPath();
        if (path == null || backup == null) return null;

        return async () =>
        {
            try
            {
                if (await CsvBackup.RestoreAsync(path))
                {
                    _log.WriteInfo($"Update CSV undone: restored {path} from its backup.");
                    ShowResult($"↶ Restored the previous version of {file.Name}", "Open file", OpenFileAction(file));
                }
                else
                {
                    ShowResult($"⚠ The backup of {file.Name} is gone, so the update can't be undone");
                }
            }
            catch (Exception ex)
            {
                _log.WriteException(ex, "Error restoring the CSV backup");
                await _dialogs.ShowErrorAsync("Undo CSV update", ex);
            }
        };
    }

    // The CSV just written becomes the one CSV auto-sync (and `--sync csv` without --file) updates.
    private void RememberCsvFile(IStorageFile file)
    {
        if (file.TryGetLocalPath() is not { } path) return;
        CsvFilePath = path;
        PersistSettings();
    }

    /// <summary>
    /// Handles a file dropped onto the window: a Pilewise game list is updated like
    /// <c>Update .csv</c>, a settings file is imported, anything else is explained.
    /// </summary>
    public async Task OpenDroppedFileAsync(IStorageFile file)
    {
        if (Progress.IsBusy) return;
        DismissResult();
        try
        {
            if (!DroppedFile.HasSupportedExtension(file.Name))
            {
                await _dialogs.ShowErrorAsync("Unsupported file", new Exception(
                    $"\"{file.Name}\" isn't a .csv file. Drop a Pilewise game list to update it, " +
                    "or a saved settings file to import it."));
                return;
            }

            var size = (await file.GetBasicPropertiesAsync()).Size;
            var content = size > DroppedFile.MaxBytes ? null : await _csv.ReadAsync(file);
            switch (DroppedFile.Classify(content))
            {
                case DroppedFileKind.GameCsv:
                    if (!CanFetchLibrary)
                    {
                        await _dialogs.ShowErrorAsync("Update CSV", new Exception(LibraryHint ?? NoSourceHint));
                        return;
                    }

                    _log.WriteInfo($"Dropped game list {file.Name}: updating it.");
                    await UpdateCsvFileAsync(file);
                    break;
                case DroppedFileKind.Settings:
                    _log.WriteInfo($"Dropped settings file {file.Name}: importing it.");
                    ApplyImportedSettings(content!);
                    ShowResult($"✓ Imported settings from {file.Name}");
                    break;
                default:
                    await _dialogs.ShowErrorAsync("Unsupported file", new Exception(
                        $"\"{file.Name}\" is neither a Pilewise game list (expected header: " +
                        $"{CsvGameExportService.Header}) nor a saved settings file."));
                    break;
            }
        }
        catch (Exception ex)
        {
            _log.WriteException(ex, $"Error opening dropped file {file.Name}");
            await _dialogs.ShowErrorAsync("Couldn't open the file", ex);
        }
    }

    // ---- library viewer ------------------------------------------------------------------------

    [RelayCommand(CanExecute = nameof(CanFetchLibrary))]
    private Task ViewLibrary() =>
        RunOperationAsync(FetchingGames, "Library fetch", "Library error", async () =>
        {
            var result = await FetchLibraryAsync();
            if (!await HandleFetchedLibraryAsync(result, "Game library")) return;

            _dialogs.ShowLibrary(new LibraryViewModel(
                result.Rows, result.Ignored, _ignored, _backlog.Latest(), LibraryActions(),
                addedOn: _firstSeen.AddedOn));
        }, catchSteamAuth: true);

    // Store pages, clipboard, and re-reading the ✓ marks from the tracker they came from (Notion or
    // Google Sheets: the one the latest snapshot is from, else whichever is set up, Notion first).
    private LibraryActions LibraryActions()
    {
        var notion = CanFindNotionDuplicates;
        var sheets = CanFindSheetsDuplicates;
        var useSheets = sheets && (!notion || _backlog.Latest()?.Target == SyncHistory.GoogleSheets);

        Func<CancellationToken, Task<BacklogSnapshot?>>? refresh =
            useSheets ? ct => _sheetsExporter.ReadBacklogAsync(GoogleSheetsTableName, _ignored.IsIgnored, ct)
            : notion ? ct => _notionExporter.ReadBacklogAsync(NotionToken, DbId, _ignored.IsIgnored, ct)
            : null;

        return new LibraryActions(
            _dialogs.OpenLink,
            async text =>
            {
                if (_dialogs.Clipboard is { } clipboard) await clipboard.SetTextAsync(text);
            },
            refresh == null
                ? null
                : async ct =>
                {
                    var snapshot = await refresh(ct);
                    RefreshLastSync(); // the card's "Completed …" line
                    return snapshot;
                },
            useSheets ? SyncHistory.GoogleSheets : "Notion");
    }

    // ---- Notion --------------------------------------------------------------------------------

    [RelayCommand(CanExecute = nameof(CanExportNotion))]
    private Task ExportToNotion() =>
        RunOperationAsync(FetchingGames, "Notion export", "Export error in Notion", async () =>
        {
            // Dry run first: nothing is written until the user confirms the preview.
            var plan = await _notionExporter.PlanAsync(
                SteamApiKey, SteamId, NotionToken, DbId, Progress.CreateReporter(), Progress.SetIndeterminate,
                Progress.Token);
            if (!await HandleFetchedLibraryAsync(plan.Library, "Export to Notion")) return;

            if (plan.IsEmpty)
            {
                // Nothing to write, but this still records the backlog progress read by the plan.
                await _notionExporter.ApplyAsync(NotionToken, DbId, plan, ct: Progress.Token);
                ShowResult($"✓ Notion is already up to date ({plan.Unchanged} games)" +
                           GoneSuffix(plan.NoLongerOwned.Count),
                    "Open database", NotionDatabaseAction());
                RecordSync(SyncHistory.Notion);
                return;
            }

            Progress.SetIndeterminate("Waiting for confirmation…");
            if (!await _dialogs.ConfirmNotionExportAsync(new NotionExportPreviewViewModel(plan)))
            {
                _log.WriteInfo("Notion export not confirmed; nothing written.");
                return;
            }

            await _notionExporter.ApplyAsync(
                NotionToken, DbId, plan, Progress.CreateReporter(), Progress.SetIndeterminate, Progress.Token);
            ShowResult(
                plan.ToCreate.Count + plan.ToUpdate.Count == 0
                    ? "✓ Notion is up to date"
                    : $"✓ Notion: {plan.ToCreate.Count} added, {plan.ToUpdate.Count} updated" +
                      GoneSuffix(plan.NoLongerOwned.Count),
                "Open database", NotionDatabaseAction());
            RecordSync(SyncHistory.Notion);
        }, SyncHistory.Notion);

    /// <summary>Finds pages of the same game in Notion and, after confirmation, merges them.</summary>
    [RelayCommand(CanExecute = nameof(CanFindNotionDuplicates))]
    private Task FindNotionDuplicates() =>
        RunOperationAsync("Reading the Notion database…", "Notion duplicate search", "Notion duplicates error",
            async () =>
            {
                var groups = await _notionExporter.FindDuplicatesAsync(NotionToken, DbId, Progress.Token);
                if (groups.Count == 0)
                {
                    ShowResult("✓ No duplicate pages in Notion", "Open database", NotionDatabaseAction());
                    return;
                }

                Progress.SetIndeterminate("Waiting for confirmation…");
                if (!await _dialogs.ConfirmDuplicatesAsync(DuplicatesViewModel.ForNotion(groups)))
                {
                    _log.WriteInfo("Notion duplicate merge not confirmed; nothing changed.");
                    return;
                }

                await _notionExporter.MergeDuplicatesAsync(
                    NotionToken, DbId, groups, Progress.CreateReporter(), Progress.Token);
                var trashed = groups.Sum(g => g.Remove.Count);
                ShowResult($"✓ Merged {groups.Count} duplicate games · {trashed} pages moved to Notion's trash",
                    "Open database", NotionDatabaseAction());
            });

    /// <summary>Finds rows of the same game in the Google Sheet and, after confirmation, merges them.</summary>
    [RelayCommand(CanExecute = nameof(CanFindSheetsDuplicates))]
    private Task FindSheetsDuplicates() =>
        RunOperationAsync("Reading the Google Sheet…", "Google Sheets duplicate search", "Google Sheets duplicates error",
            async () =>
            {
                var groups = await _sheetsExporter.FindDuplicatesAsync(GoogleSheetsTableName, Progress.Token);
                if (groups.Count == 0)
                {
                    ShowResult("✓ No duplicate rows in Google Sheets");
                    return;
                }

                Progress.SetIndeterminate("Waiting for confirmation…");
                if (!await _dialogs.ConfirmDuplicatesAsync(DuplicatesViewModel.ForSheets(groups)))
                {
                    _log.WriteInfo("Google Sheets duplicate merge not confirmed; nothing changed.");
                    return;
                }

                Progress.SetIndeterminate("Merging duplicate rows…");
                await _sheetsExporter.MergeDuplicatesAsync(GoogleSheetsTableName, groups, Progress.Token);
                ShowResult($"✓ Merged {groups.Count} duplicate games · {groups.Sum(g => g.Remove.Count)} rows deleted");
            });

    private Action? NotionDatabaseAction()
    {
        var id = DbId?.Trim().Replace("-", "");
        return string.IsNullOrEmpty(id) ? null : () => _dialogs.OpenLink($"https://www.notion.so/{id}");
    }

    // ---- Google Sheets -------------------------------------------------------------------------

    [RelayCommand(CanExecute = nameof(CanExportSheets))]
    private Task ExportToGoogleSheets() =>
        RunOperationAsync(FetchingGames, "Google Sheets export", "Google Sheets export error", async () =>
        {
            var result = await _sheetsExporter.ExportAsync(
                GoogleSheetsTableName, SteamApiKey, SteamId, Progress.CreateReporter(), Progress.SetIndeterminate,
                Progress.Token);
            if (!await HandleFetchedLibraryAsync(result.Library, "Export to Google Sheets")) return;

            var url = result.SpreadsheetUrl;
            ShowResult(
                result.Added + result.Updated == 0
                    ? "✓ Google Sheets is already up to date"
                    : $"✓ Google Sheets: {result.Added} added, {result.Updated} updated" +
                      GoneSuffix(result.NoLongerOwned),
                url != null ? "Open sheet" : null, url != null ? () => _dialogs.OpenLink(url) : null);
            RecordSync(SyncHistory.GoogleSheets);
        }, SyncHistory.GoogleSheets);

    // ---- shared post-fetch handling ------------------------------------------------------------

    private static string GoneSuffix(int count) => count > 0 ? $" · {count} no longer owned" : string.Empty;

    // Opens a just-written file in its default app (Excel etc.); null when it has no local path.
    private Action? OpenFileAction(IStorageFile file)
    {
        var path = file.TryGetLocalPath();
        return path != null ? () => _dialogs.OpenLink(path) : null;
    }

    // Syncs the connect rows, warns about expired sessions and, when nothing was fetched, explains
    // why. Returns false when there is nothing to export.
    private async Task<bool> HandleFetchedLibraryAsync(GameLibraryResult result, string title)
    {
        ApplyStoreStatuses(result);
        await NotifyExpiredSessionsAsync(result);

        if (result.Rows.Count > 0) return true;

        _log.WriteInfo("No games found to export.");
        if (!result.EpicSessionExpired && !result.GogSessionExpired && !result.XboxSessionExpired)
            await _dialogs.ShowErrorAsync(title, new Exception(
                "No games found to export. Check your Steam credentials or Epic / GOG / Xbox connection."));
        return false;
    }

    // Reflects the latest cached-session outcome from a library fetch back onto the connect buttons.
    private void ApplyStoreStatuses(GameLibraryResult result)
    {
        Epic.IsConnected = result.EpicAuthenticated;
        Gog.IsConnected = result.GogAuthenticated;
        Xbox.IsConnected = result.XboxAuthenticated;
    }

    // Surfaces a clear message when a previously connected store session has expired,
    // so the export isn't silently missing that store's games.
    private Task NotifyExpiredSessionsAsync(GameLibraryResult result)
    {
        var expired = new[]
        {
            (Store: "Epic", Expired: result.EpicSessionExpired),
            (Store: "GOG", Expired: result.GogSessionExpired),
            (Store: "Xbox", Expired: result.XboxSessionExpired)
        }.Where(s => s.Expired).Select(s => s.Store).ToList();
        if (expired.Count == 0) return Task.CompletedTask;

        foreach (var store in expired)
            _log.WriteWarning($"{store}: session expired — {store} games were skipped.");

        // One dialog for all of them, rather than one per store.
        var stores = expired.Count == 1 ? expired[0] : string.Join(", ", expired[..^1]) + " and " + expired[^1];
        var connect = string.Join(" / ", expired.Select(s => $"\"Connect {s}\""));
        return _dialogs.ShowErrorAsync(expired.Count == 1 ? $"{stores} session expired" : "Store sessions expired",
            new Exception($"Your {stores} {(expired.Count == 1 ? "session has" : "sessions have")} expired, " +
                          $"so those games were skipped. Click {connect} to log in again."));
    }
}

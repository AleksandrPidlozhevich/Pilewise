using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Input.Platform;
using Avalonia.Platform.Storage;
using Pilewise.ViewModels;
using Pilewise.Views;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace Pilewise.Services;

/// <summary>
/// <see cref="IDialogService"/> backed by the application's main window. Resolves the window
/// lazily (it may not exist yet when the service is constructed) and no-ops gracefully if it isn't.
/// </summary>
public sealed class WindowDialogService : IDialogService
{
    private static readonly FilePickerFileType CsvFileType =
        new("CSV files") { Patterns = ["*.csv"] };

    private static Window? MainWindow =>
        (Application.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.MainWindow;

    public IClipboard? Clipboard => MainWindow?.Clipboard;

    public async Task<IStorageFile?> PickSaveCsvAsync(string suggestedName)
    {
        var window = MainWindow;
        if (window == null) return null;

        return await window.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            SuggestedFileName = suggestedName,
            Title = "Save CSV File",
            FileTypeChoices = [CsvFileType]
        });
    }

    public async Task<IStorageFile?> PickOpenCsvAsync(string title)
    {
        var window = MainWindow;
        if (window == null) return null;

        var files = await window.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = title,
            AllowMultiple = false,
            FileTypeFilter = [CsvFileType]
        });

        return files.FirstOrDefault();
    }

    public Task ShowErrorAsync(string title, Exception exception) =>
        ShowDialogAsync(new ErrorWindow(title, BuildErrorMessage(exception)));

    public void ShowLibrary(LibraryViewModel library)
    {
        if (MainWindow is { } owner) new LibraryWindow { DataContext = library }.Show(owner);
    }

    public Task<bool> ConfirmNotionExportAsync(NotionExportPreviewViewModel preview) =>
        ShowDialogAsync(new NotionExportPreviewWindow { DataContext = preview });

    public Task<bool> ConfirmDuplicatesAsync(DuplicatesViewModel duplicates) =>
        ShowDialogAsync(new DuplicatesWindow { DataContext = duplicates });

    private SyncLogWindow? _syncLogWindow;

    public void ShowSyncLog(SyncLogViewModel log)
    {
        // One history window: a second click brings the open one forward.
        if (_syncLogWindow != null)
        {
            _syncLogWindow.Activate();
            return;
        }

        var window = new SyncLogWindow { DataContext = log };
        window.Closed += (_, _) =>
        {
            log.Dispose();
            _syncLogWindow = null;
        };
        _syncLogWindow = window;
        if (MainWindow is { IsVisible: true } owner) window.Show(owner);
        else window.Show();
    }

    // Called from plain (non-async) commands, so a failure is shown here rather than thrown.
    public void OpenLink(string url)
    {
        try
        {
            SystemBrowser.Open(url);
        }
        catch (InvalidOperationException ex)
        {
            _ = ShowErrorAsync("Couldn't open the link", ex);
        }
    }

    // Modal over the main window; false (nothing confirmed) when there's no window to own it, or it's
    // hidden in the tray (Avalonia refuses a dialog over a hidden owner).
    private static async Task<bool> ShowDialogAsync(DialogWindow dialog)
    {
        if (MainWindow is not { IsVisible: true } owner) return false;
        return await dialog.ShowDialog<bool>(owner);
    }

    private static string BuildErrorMessage(Exception exception)
    {
        var messages = new List<string>();
        var current = exception;
        while (current != null)
        {
            if (!string.IsNullOrWhiteSpace(current.Message))
                messages.Add(current.Message);
            current = current.InnerException;
        }

        return messages.Count == 0 ? "Unknown error" : string.Join(Environment.NewLine, messages.Distinct());
    }
}

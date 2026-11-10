using Avalonia.Input.Platform;
using Avalonia.Platform.Storage;
using Pilewise.ViewModels;
using System;
using System.Threading.Tasks;

namespace Pilewise.Services;

public interface IDialogService
{
    Task<IStorageFile?> PickSaveCsvAsync(string suggestedName);

    Task<IStorageFile?> PickOpenCsvAsync(string title);

    Task ShowErrorAsync(string title, Exception exception);

    void ShowLibrary(LibraryViewModel library);

    /// <summary>Shows the Notion dry run; true when the user confirms the export.</summary>
    Task<bool> ConfirmNotionExportAsync(NotionExportPreviewViewModel preview);

    void ShowSyncLog(SyncLogViewModel log);

    Task<bool> ConfirmDuplicatesAsync(DuplicatesViewModel duplicates);

    void OpenLink(string url);

    IClipboard? Clipboard { get; }
}
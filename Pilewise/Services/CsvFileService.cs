using Avalonia.Platform.Storage;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading.Tasks;

namespace Pilewise.Services;

/// <summary>Reads/writes Pilewise CSV files chosen through the storage pickers.</summary>
public sealed class CsvFileService
{
    private readonly ILogService _log;

    public CsvFileService(ILogService log)
    {
        _log = log;
    }

    public async Task<string?> ReadAsync(IStorageFile file)
    {
        await using var stream = await file.OpenReadAsync();
        using var reader = new StreamReader(stream);
        return await reader.ReadToEndAsync();
    }

    public async Task WriteAsync(IStorageFile file, string content)
    {
        await using var stream = await file.OpenWriteAsync();
        // Write a UTF-8 BOM so Excel opens the file as UTF-8 (otherwise it assumes the
        // system ANSI codepage and garbles non-ASCII game names). Notepad is fine either way.
        using var writer = new StreamWriter(stream, new UTF8Encoding(true));
        await writer.WriteAsync(content);
    }

    /// <summary>Merges incoming games into the existing CSV content and writes the result. The file's
    /// previous content is first saved next to it as <c>&lt;name&gt;.bak</c>, so a bad update can be undone.
    /// Returns how many rows were added, the file's total row count and the backup path (null when the
    /// file isn't a local file or was empty).</summary>
    public async Task<(int Added, int Total, string? BackupPath)> WriteMergedAsync(
        IStorageFile file,
        string? existingContent,
        List<CsvGameExportRow> incomingRows,
        bool includeLinux = false)
    {
        var existingRows = CsvGameExportService.Parse(existingContent ?? string.Empty);
        var (mergedRows, added, updated) = CsvGameExportService.Merge(existingRows, incomingRows);
        var writeLinux = includeLinux || CsvGameExportService.HasLinuxColumn(existingContent);

        var backup = await CsvBackup.WriteAsync(file.TryGetLocalPath(), existingContent);
        if (backup != null) _log.WriteInfo($"CSV backup written to {backup}.");

        await WriteAsync(file, CsvGameExportService.Serialize(mergedRows, writeLinux));

        _log.WriteInfo($"CSV updated: {added} new, {updated} changed, {mergedRows.Count} total rows.");
        return (added, mergedRows.Count, backup);
    }
}
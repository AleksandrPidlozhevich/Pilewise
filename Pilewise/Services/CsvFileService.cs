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

        using var writer = new StreamWriter(stream, new UTF8Encoding(true));
        await writer.WriteAsync(content);
    }


    public async Task<(int Added, int Total, string? BackupPath)> WriteMergedAsync(
        IStorageFile file,
        string? existingContent,
        List<CsvGameExportRow> incomingRows,
        bool includeLinux = false)
    {
        var (content, added, updated, total) =
            CsvGameExportService.MergeToCsv(existingContent, incomingRows, includeLinux);

        var backup = await CsvBackup.WriteAsync(file.TryGetLocalPath(), existingContent);
        if (backup != null) _log.WriteInfo($"CSV backup written to {backup}.");

        await WriteAsync(file, content);

        _log.WriteInfo($"CSV updated: {added} new, {updated} changed, {total} total rows.");
        return (added, total, backup);
    }
}

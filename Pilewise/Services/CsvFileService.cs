using Avalonia.Platform.Storage;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading.Tasks;

namespace Pilewise.Services;

public sealed class CsvFileService(ILogService log, CompletedGames? completed = null)
{
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

    public async Task<(CsvMergeResult Result, string? BackupPath)> WriteMergedAsync(
        IStorageFile file, string? existingContent, GameLibraryResult library, bool includeLinux = false)
    {
        var path = file.TryGetLocalPath();
        var result = CsvGameExportService.MergeToCsv(existingContent, library.Rows, new CsvMergeOptions(
            includeLinux, library.FetchedStores, library.Ignored, completed,
            path != null ? CsvGameExportService.TargetOf(path) : $"CSV:{file.Name}"));

        var backup = await CsvBackup.WriteAsync(path, existingContent);
        if (backup != null) log.WriteInfo($"CSV backup written to {backup}.");

        await WriteAsync(file, result.Content);
        result.Commit();

        log.WriteInfo($"CSV written: {result.Added} new, {result.Updated} matched, {result.Total} total rows.");
        return (result, backup);
    }
}

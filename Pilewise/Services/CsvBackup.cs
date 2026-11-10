using System.IO;
using System.Text;
using System.Threading.Tasks;

namespace Pilewise.Services;

public static class CsvBackup
{
    public static string PathFor(string csvPath)
    {
        return csvPath + ".bak";
    }

    public static async Task<string?> WriteAsync(string? csvPath, string? previousContent)
    {
        if (string.IsNullOrEmpty(csvPath) || string.IsNullOrWhiteSpace(previousContent)) return null;

        var backup = PathFor(csvPath);
        // Same encoding as the CSV itself (UTF-8 with BOM, for Excel).
        await File.WriteAllTextAsync(backup, previousContent, new UTF8Encoding(true));
        return backup;
    }

    /// <summary>Puts the backup back over the CSV and removes it (so a second undo can't apply a
    /// stale copy). False when there's no backup.</summary>
    public static async Task<bool> RestoreAsync(string csvPath)
    {
        var backup = PathFor(csvPath);
        if (!File.Exists(backup)) return false;

        var content = await File.ReadAllTextAsync(backup);
        await File.WriteAllTextAsync(csvPath, content, new UTF8Encoding(true));
        File.Delete(backup);
        return true;
    }
}
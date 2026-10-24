using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Pilewise.Services;

/// <summary>
/// Runs <c>Pilewise --sync …</c> without a window (e.g. from Task Scheduler): loads the credentials
/// and store sessions the app saved, then syncs each target. Notion is applied without the preview
/// (use <c>--dry-run</c> to see the changes first). Every run is also written to the log.
/// </summary>
public sealed class HeadlessSync
{
    private readonly ISecretStore _secrets;
    private readonly IStateStore _state;
    private readonly IGameLibraryService _library;
    private readonly NotionGameExporter _notion;
    private readonly GoogleSheetsGameExporter _sheets;
    private readonly ILogService _log;
    private readonly TextWriter _out;
    private readonly StoreSelection? _selection;
    private readonly SyncLog? _syncLog;
    private readonly string _source;

    /// <param name="syncLog">Where each run is listed for the Sync history window (null: not listed).</param>
    /// <param name="source">What started the run, as shown in the history ("Auto-sync", "Command line"…).</param>
    public HeadlessSync(
        ISecretStore secrets, IStateStore state, IGameLibraryService library, NotionGameExporter notion,
        GoogleSheetsGameExporter sheets, ILogService log, TextWriter output, StoreSelection? selection = null,
        SyncLog? syncLog = null, string source = SyncLog.CommandLine)
    {
        _syncLog = syncLog;
        _source = source;
        _selection = selection;
        _secrets = secrets;
        _state = state;
        _library = library;
        _notion = notion;
        _sheets = sheets;
        _log = log;
        _out = output;
    }

    /// <summary>Runs every target; returns the process exit code (0 = all succeeded, 1 = any failed).</summary>
    public async Task<int> RunAsync(SyncOptions options, CancellationToken ct = default)
    {
        var settings = AppSettingsService.Parse(_secrets.Load(AppSettingsService.SecretKey) ?? string.Empty);
        var history = new SyncHistory(_state);
        // Same stores as the app: the ones unticked there are skipped here too.
        _selection?.Load(settings.ExcludedStores);
        var failed = false;

        foreach (var target in options.Targets)
        {
            Write($"== {target} ==");
            try
            {
                var summary = target switch
                {
                    SyncTarget.Csv => await SyncCsvAsync(settings, options, ct),
                    SyncTarget.Notion => await SyncNotionAsync(settings, options, ct),
                    _ => await SyncSheetsAsync(settings, options, ct)
                };
                Write(summary);
                if (!options.DryRun)
                {
                    history.Record(SyncHistory.KeyOf(target), DateTimeOffset.Now);
                    _syncLog?.Add(new SyncLogEntry(DateTimeOffset.Now, SyncHistory.KeyOf(target), true, summary, _source));
                }
            }
            // A timeout is an OperationCanceledException too, but only a real cancel stops the run.
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                Write("Cancelled.");
                return 1;
            }
            catch (Exception ex)
            {
                failed = true;
                _log.WriteException(ex, $"Headless sync of {target} failed");
                Write($"FAILED: {Flatten(ex)}");
                if (!options.DryRun)
                    _syncLog?.Add(new SyncLogEntry(DateTimeOffset.Now, SyncHistory.KeyOf(target), false, Flatten(ex), _source));
            }
        }

        return failed ? 1 : 0;
    }

    private async Task<string> SyncCsvAsync(AppSettings settings, SyncOptions options, CancellationToken ct)
    {
        var file = options.CsvFile ?? settings.CsvFile;
        if (string.IsNullOrWhiteSpace(file))
            throw new InvalidOperationException(
                "No CSV file: pass --file <path>, or export / update a CSV once in the app so it's remembered.");

        var library = await FetchAsync(settings, ct);
        var path = Path.GetFullPath(file);

        int added, total;
        string content;
        var existing = File.Exists(path) ? await File.ReadAllTextAsync(path, ct) : null;
        if (existing != null)
        {
            var missing = CsvGameExportService.GetMissingColumns(existing);
            if (missing.Count > 0)
                throw new InvalidOperationException(
                    $"{path} is not a Pilewise CSV (missing columns: {string.Join(", ", missing)}).");

            var (merged, mergedAdded, _) = CsvGameExportService.Merge(CsvGameExportService.Parse(existing), library.Rows);
            (added, total, content) = (mergedAdded, merged.Count, CsvGameExportService.Serialize(merged));
        }
        else
        {
            (added, total, content) = (library.Rows.Count, library.Rows.Count, CsvGameExportService.Serialize(library.Rows));
        }

        if (options.DryRun) return $"Dry run: would add {added} games to {path} ({total} total).";

        // The content the merge was based on, not a second read that could differ from it.
        var backup = await CsvBackup.WriteAsync(path, existing);

        // UTF-8 with BOM, like the app, so Excel reads non-ASCII names correctly.
        await File.WriteAllTextAsync(path, content, new UTF8Encoding(true), ct);
        return $"Added {added} games to {path} ({total} total)." +
               (backup != null ? $" Previous version: {backup}" : "");
    }

    private async Task<string> SyncNotionAsync(AppSettings settings, SyncOptions options, CancellationToken ct)
    {
        var plan = await _notion.PlanAsync(
            settings.SteamApiKey, settings.SteamId, settings.NotionToken, settings.DbId, onStatus: Write, ct: ct);
        WarnExpiredSessions(plan.Library);
        if (plan.Library.Rows.Count == 0) throw NoGames();

        var gone = plan.NoLongerOwned.Count > 0 ? $", {plan.NoLongerOwned.Count} no longer owned" : "";
        var properties = plan.PropertiesToCreate.Count > 0
            ? $"; adds properties {string.Join(", ", plan.PropertiesToCreate.Select(p => p.Name))}"
            : "";
        var counts = $"{plan.ToCreate.Count} to add, {plan.ToUpdate.Count} to update, " +
                     $"{plan.Unchanged} unchanged{gone}{properties}";

        if (options.DryRun)
        {
            foreach (var create in plan.ToCreate) Write($"  + {create.Name} ({create.Platforms})");
            foreach (var update in plan.ToUpdate) Write($"  ~ {update.Name}: {update.Before} -> {update.After}");
            foreach (var game in plan.NoLongerOwned) Write($"  - {game.Name} (no longer owned)");
            return $"Dry run: {counts}.";
        }

        await _notion.ApplyAsync(settings.NotionToken, settings.DbId, plan, onStatus: Write, ct: ct);
        return $"Done: {counts}." + (plan.Backlog is { } backlog ? $" {backlog.Text}." : "");
    }

    private async Task<string> SyncSheetsAsync(AppSettings settings, SyncOptions options, CancellationToken ct)
    {
        var sheet = options.SheetName ?? settings.GoogleSheetsTableName;
        var result = await _sheets.ExportAsync(
            sheet, settings.SteamApiKey, settings.SteamId, onStatus: Write, ct: ct, dryRun: options.DryRun);
        WarnExpiredSessions(result.Library);
        if (result.Library.Rows.Count == 0) throw NoGames();

        var gone = result.NoLongerOwned > 0 ? $", {result.NoLongerOwned} no longer owned" : "";
        if (result.DryRun)
            return $"Dry run: would add {result.Added}, update {result.Updated}{gone}" +
                   (result.SpreadsheetUrl == null ? $" (\"{sheet?.Trim()}\" would be created)." : ".");
        return $"Done: {result.Added} added, {result.Updated} updated{gone}. {result.SpreadsheetUrl}";
    }

    private async Task<GameLibraryResult> FetchAsync(AppSettings settings, CancellationToken ct)
    {
        var library = await _library.FetchAsync(settings.SteamApiKey, settings.SteamId, onStatus: Write, ct: ct);
        WarnExpiredSessions(library);
        if (library.Rows.Count == 0) throw NoGames();
        return library;
    }

    private void WarnExpiredSessions(GameLibraryResult library)
    {
        if (library.EpicSessionExpired) Write("WARNING: Epic session expired, Epic games skipped. Reconnect in the app.");
        if (library.GogSessionExpired) Write("WARNING: GOG session expired, GOG games skipped. Reconnect in the app.");
        if (library.XboxSessionExpired) Write("WARNING: Xbox session expired, Xbox games skipped. Reconnect in the app.");
    }

    private static InvalidOperationException NoGames() => new(
        "No games found. Check the Steam credentials and store connections in the app.");

    private static string Flatten(Exception ex)
    {
        var messages = new StringBuilder(ex.Message);
        for (var inner = ex.InnerException; inner != null; inner = inner.InnerException)
            messages.Append(" — ").Append(inner.Message);
        return messages.ToString();
    }

    private void Write(string line)
    {
        _out.WriteLine(line);
        _log.WriteInfo($"[sync] {line}");
    }
}

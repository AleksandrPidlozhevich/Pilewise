using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using Pilewise.Services;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace Pilewise.ViewModels;

// Running in the background: auto-sync on a schedule, the tray's "Sync now", close to tray and
// start at login.
public partial class MainWindowViewModel
{
    /// <summary>In-app auto-sync: interval picker + targets (saved).</summary>
    public IReadOnlyList<string> AutoSyncIntervalLabels => AutoSyncSettings.IntervalLabels;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsAutoSyncOn))]
    private int autoSyncIntervalIndex;

    [ObservableProperty] private bool autoSyncNotion;

    [ObservableProperty] private bool autoSyncSheets;

    [ObservableProperty] private bool autoSyncCsv;

    public bool IsAutoSyncOn => AutoSyncIntervalIndex > 0;

    /// <summary>The CSV last written by Export / Update .csv (saved): what CSV auto-sync updates.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(AutoSyncCsvHint))]
    private string? csvFilePath;

    public string AutoSyncCsvHint => CsvFilePath is { } path
        ? $"Update {path} on the schedule (a .bak of the previous version is kept)"
        : "Export or update a CSV once and it's remembered here for auto-sync";

    /// <summary>Closing the window keeps Pilewise running in the tray (saved).</summary>
    [ObservableProperty] private bool closeToTray;

    /// <summary>False on a Linux desktop without a tray: closing then really closes (set by <c>App</c>).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CloseToTrayHint), nameof(StartAtLoginHint))]
    private bool isTrayAvailable = true;

    /// <summary>Close to tray only works when there's a tray to come back from.</summary>
    public bool KeepsRunningOnClose => CloseToTray && IsTrayAvailable;

    public string CloseToTrayHint => IsTrayAvailable
        ? "Closing the window keeps Pilewise running in the tray (so auto-sync keeps going). Quit from the tray icon's menu."
        : "This desktop has no system tray (on GNOME, install the AppIndicator extension), so closing the window quits Pilewise.";

    public string StartAtLoginHint => IsTrayAvailable
        ? "Start Pilewise hidden in the tray when you log in (turns on Close to tray as well)."
        : "Start Pilewise when you log in (the window opens, since this desktop has no tray).";

    /// <summary>Start hidden in the tray at login (a system login item, not a saved setting).</summary>
    [ObservableProperty] private bool startAtLogin;

    public bool CanStartAtLogin => _autoStart.IsSupported;

    // True while StartAtLogin is set from the OS state, so that doesn't write the login item back.
    private bool _syncingStartAtLogin;

    /// <summary>Raised when the window should be brought up (e.g. Sync now has nothing to do).</summary>
    public event Action? ShowWindowRequested;

    private readonly DispatcherTimer _autoSyncTimer = new() { Interval = TimeSpan.FromMinutes(1) };
    private readonly Dictionary<SyncTarget, DateTimeOffset> _autoSyncFailures = [];

    // ---- start at login ------------------------------------------------------------------------

    private void LoadStartAtLogin() => SetStartAtLoginSilently(_autoStart.IsSupported && _autoStart.IsEnabled);

    private void SetStartAtLoginSilently(bool value)
    {
        _syncingStartAtLogin = true;
        StartAtLogin = value;
        _syncingStartAtLogin = false;
    }

    partial void OnStartAtLoginChanged(bool value)
    {
        if (_syncingStartAtLogin) return;
        try
        {
            _autoStart.SetEnabled(value);
            // Starting hidden only makes sense if closing keeps it in the tray too.
            if (value && IsTrayAvailable) CloseToTray = true;
        }
        catch (Exception ex)
        {
            _log.WriteException(ex, "Couldn't change the login item");
            ShowResult($"⚠ Couldn't change \"Start at login\": {ex.Message}");
            SetStartAtLoginSilently(!value);
        }
    }

    // ---- auto-sync -----------------------------------------------------------------------------

    // Checked once a minute, so the first auto-sync starts a minute after launch (after the cached
    // store sessions have been restored). The same tick re-checks for app updates every 12 hours.
    private void StartAutoSyncTimer()
    {
        _autoSyncTimer.Tick += async (_, _) =>
        {
            await RunDueAutoSyncAsync();
            await CheckForUpdatesIfStaleAsync();
        };
        _autoSyncTimer.Start();
    }

    private AutoSyncSettings CurrentAutoSync()
    {
        var targets = new List<SyncTarget>();
        if (AutoSyncNotion) targets.Add(SyncTarget.Notion);
        if (AutoSyncSheets) targets.Add(SyncTarget.Sheets);
        if (AutoSyncCsv) targets.Add(SyncTarget.Csv);
        var index = Math.Clamp(AutoSyncIntervalIndex, 0, AutoSyncSettings.Intervals.Count - 1);
        return new AutoSyncSettings(AutoSyncSettings.Intervals[index], targets);
    }

    private void ApplyAutoSync(string? value)
    {
        var settings = AutoSyncSettings.Parse(value);
        var index = AutoSyncSettings.Intervals.ToList().IndexOf(settings.IntervalHours);
        AutoSyncIntervalIndex = Math.Max(index, 0);
        AutoSyncNotion = settings.Targets.Contains(SyncTarget.Notion);
        AutoSyncSheets = settings.Targets.Contains(SyncTarget.Sheets);
        AutoSyncCsv = settings.Targets.Contains(SyncTarget.Csv);
    }

    private bool CanAutoSync(SyncTarget target) => target switch
    {
        SyncTarget.Notion => CanExportNotion,
        SyncTarget.Sheets => CanExportSheets,
        _ => CanFetchLibrary && CsvFilePath != null
    };

    /// <summary>
    /// Runs the auto-sync targets that are due, like <c>--sync</c> does (Notion is applied without the
    /// preview), and reports the outcome in the result banner instead of a dialog. Skipped while
    /// anything else runs.
    /// </summary>
    private async Task RunDueAutoSyncAsync()
    {
        if (Progress.IsBusy) return;

        var due = AutoSyncPlanner.Due(
            CurrentAutoSync(),
            t => _syncHistory.Get(SyncHistory.KeyOf(t)),
            _autoSyncFailures,
            CanAutoSync,
            DateTimeOffset.Now);
        if (due.Count == 0) return;

        await RunSyncTargetsAsync(due, "Auto-sync");
    }

    /// <summary>
    /// Tray "Sync now": runs the targets ticked under Auto-sync right away (ignoring the schedule).
    /// With none ticked or ready, brings the window up with a hint instead.
    /// </summary>
    public async Task SyncNowAsync()
    {
        if (Progress.IsBusy) return;

        var targets = CurrentAutoSync().Targets.Where(CanAutoSync).ToList();
        if (targets.Count == 0)
        {
            ShowResult("Pick an Auto-sync interval and tick Notion / Sheets / CSV (with their fields filled in) to use Sync now.");
            ShowWindowRequested?.Invoke();
            return;
        }

        await RunSyncTargetsAsync(targets, "Sync");
    }

    // Each target runs through HeadlessSync (the same path as `--sync`); its last output line is the
    // outcome shown in the banner. A failed or cancelled target waits an hour before auto-sync retries.
    private async Task RunSyncTargetsAsync(IReadOnlyList<SyncTarget> targets, string label)
    {
        Progress.Begin($"{label}…");
        PersistSettings(); // HeadlessSync reads the remembered settings
        var started = DateTimeOffset.Now;
        var outcomes = new List<string>();
        var failed = false;
        try
        {
            foreach (var target in targets)
            {
                var name = SyncHistory.KeyOf(target);
                string? lastLine = null;
                var output = new CallbackTextWriter(line =>
                {
                    lastLine = line;
                    Dispatcher.UIThread.Post(() => Progress.Status = $"{label} {name}: {line}");
                });
                var sync = new HeadlessSync(
                    _secrets, _state, _library, _notionExporter, _sheetsExporter, _log, output, _selection, _syncLog,
                    label, _linux);
                var code = await sync.RunAsync(new SyncOptions([target], null, null, false), Progress.Token);

                if (Progress.Token.IsCancellationRequested)
                {
                    foreach (var t in targets) _autoSyncFailures[t] = started;
                    outcomes.Add("cancelled");
                    failed = true;
                    break;
                }

                if (code == 0)
                {
                    _autoSyncFailures.Remove(target);
                    outcomes.Add($"{name}: {lastLine}");
                }
                else
                {
                    _autoSyncFailures[target] = started;
                    outcomes.Add($"{name}: {lastLine ?? "failed"}");
                    failed = true;
                }
            }
        }
        catch (Exception ex)
        {
            _log.WriteException(ex, $"{label} failed");
            foreach (var t in targets) _autoSyncFailures[t] = started;
            outcomes.Add(ex.Message);
            failed = true;
        }
        finally
        {
            Progress.End();
            RefreshLastSync();
        }

        var message = $"{(failed ? "⚠" : "✓")} {label} {started:HH:mm} — {string.Join(" · ", outcomes)}";
        if (failed) ShowResult(message, "Open logs", OpenLogsFolder);
        else ShowResult(message);
    }
}

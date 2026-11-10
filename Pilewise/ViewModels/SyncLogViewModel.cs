using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Pilewise.Services;
using System;
using System.Collections.Generic;
using System.Linq;

namespace Pilewise.ViewModels;

public sealed partial class SyncLogViewModel : ViewModelBase, IDisposable
{
    private readonly SyncLog _log;

    [ObservableProperty] [NotifyPropertyChangedFor(nameof(IsEmpty), nameof(Summary))]
    private IReadOnlyList<SyncLogEntry> entries = [];

    public SyncLogViewModel(SyncLog log)
    {
        _log = log;
        _log.Changed += OnChanged;
        Refresh();
    }

    public SyncLogViewModel()
        : this(new SyncLog())
    {
        var now = DateTimeOffset.Now;
        _log.Add(new SyncLogEntry(now.AddHours(-25), "Notion", true,
            "Done: 1 to add, 170 to update. Completed 137 of 386 (35%).", "Auto-sync"));
        _log.Add(
            new SyncLogEntry(now.AddHours(-1), "Google Sheets", false, "Google authorization failed.", "Auto-sync"));
        _log.Add(new SyncLogEntry(now, "CSV", true, "Added 2 new games to games.csv (640 total)", SyncLog.Manual));
    }

    public bool IsEmpty => Entries.Count == 0;

    public string Summary
    {
        get
        {
            var failed = Entries.Count(e => !e.Ok);
            return $"{Entries.Count} runs" + (failed > 0 ? $" · {failed} failed" : "");
        }
    }

    [RelayCommand]
    private void Clear()
    {
        _log.Clear();
    }

    // Runs may finish on a background thread (auto-sync, command line in another process isn't seen).
    private void OnChanged()
    {
        Dispatcher.UIThread.Post(Refresh);
    }

    private void Refresh()
    {
        Entries = _log.Entries;
    }

    public void Dispose()
    {
        _log.Changed -= OnChanged;
    }
}
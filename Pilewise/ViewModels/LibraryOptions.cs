using Pilewise.Services;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace Pilewise.ViewModels;

/// <summary>
/// What the library viewer can do outside itself; each one is optional (tests and the previewer
/// pass none, which hides the matching buttons).
/// </summary>
/// <param name="OpenLink">Opens a store page.</param>
/// <param name="CopyText">Puts text on the clipboard ("Copy list").</param>
/// <param name="RefreshBacklog">Re-reads the completed marks from the tracker (read-only).</param>
/// <param name="BacklogSource">The tracker <paramref name="RefreshBacklog"/> reads ("Notion").</param>
public sealed record LibraryActions(
    Action<string>? OpenLink = null,
    Func<string, Task>? CopyText = null,
    Func<CancellationToken, Task<BacklogSnapshot?>>? RefreshBacklog = null,
    string? BacklogSource = null);

public sealed record LibrarySortOption(string Label, LibrarySort Sort)
{
    public override string ToString()
    {
        return Label;
    }
}

public enum LibrarySort
{
    Name,
    MostPlayed,
    RecentlyPlayed,
    RecentlyAdded
}

public sealed record LibraryFilterOption(string Label, string Key, int Count)
{
    public override string ToString()
    {
        return $"{Label} ({Count})";
    }
}
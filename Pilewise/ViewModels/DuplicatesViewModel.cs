using Pilewise.Services;
using System.Collections.Generic;
using System.Linq;

namespace Pilewise.ViewModels;

public sealed class DuplicatesViewModel : ViewModelBase
{
    private readonly string _target;
    private readonly string _entries;
    private readonly string _whatHappens;

    public DuplicatesViewModel(string target, string entries, IReadOnlyList<IDuplicateGroupView> groups, string whatHappens)
    {
        _target = target;
        _entries = entries;
        _whatHappens = whatHappens;
        Groups = groups;
    }

    public static DuplicatesViewModel ForNotion(IReadOnlyList<NotionDuplicateGroup> groups)
    {
        var extra = groups.Sum(g => g.Remove.Count);
        return new("Notion", "pages", groups,
            $"For each, the oldest page is kept and gets all store tags and ids; {Count(extra, "other page")} " +
            $"{(extra == 1 ? "is" : "are")} moved to Notion's trash, where pages can be restored for 30 days. Notes, " +
            "status and other columns of the kept page are not changed — copy anything you need from the other pages first.");
    }

    public static DuplicatesViewModel ForSheets(IReadOnlyList<SheetDuplicateGroup> groups)
    {
        var extra = groups.Sum(g => g.Remove.Count);
        return new("Google Sheets", "rows", groups,
            "For each, the top row is kept and gets all store tags and ids (and the Completed tick, if any copy had it); " +
            $"{Count(extra, "other row")} {(extra == 1 ? "is" : "are")} deleted. Other cells of the kept row are not " +
            "changed. File → Version history in Google Sheets brings deleted rows back if needed.");
    }

    public static string Count(int count, string noun) => $"{count} {noun}{(count == 1 ? "" : "s")}";

    public DuplicatesViewModel() : this("Notion", "pages", [], "")
    {
    }

    public IReadOnlyList<IDuplicateGroupView> Groups { get; }

    public bool HasGroups => Groups.Count > 0;

    public string Title => $"{_target}: duplicate {_entries}";

    public string Summary => HasGroups
        ? $"{Count(Groups.Count, "game")} {(Groups.Count == 1 ? "has" : "have")} more than one {_entries.TrimEnd('s')}. {_whatHappens}"
        : $"No duplicate {_entries} found: every game has a single {_entries.TrimEnd('s')}.";

    public string ConfirmLabel => $"Merge {Count(Groups.Count, "game")}";

    public string CancelLabel => HasGroups ? "Cancel" : "Close";
}

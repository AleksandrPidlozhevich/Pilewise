using Pilewise.Services;
using System.Collections.Generic;
using System.Linq;

namespace Pilewise.ViewModels;

public sealed class NotionExportPreviewViewModel : ViewModelBase
{
    public NotionExportPreviewViewModel(NotionExportPlan plan)
    {
        PropertiesToCreate = plan.PropertiesToCreate;
        ToCreate = plan.ToCreate;
        ToUpdate = plan.ToUpdate;
        Unchanged = plan.Unchanged;
        NoLongerOwned = plan.NoLongerOwned;
        HasOwnedColumn = plan.HasOwnedColumn;
    }

    public NotionExportPreviewViewModel()
        : this(new NotionExportPlan(
            new GameLibraryResult([], false, false, false, false, false, false),
            [new NotionPropertyToCreate("GameID", "rich_text")],
            [new NotionPageCreate("Alan Wake", "Epic", new object())],
            [new NotionPageUpdate("p1", "The Witcher 3", "Steam · Steam:292030",
                "Steam/GOG · Steam:292030; GOG:1207664643", new object())],
            120,
            [new NotionGoneGame("Refunded Game", "Steam")],
            false))
    {
    }

    public IReadOnlyList<NotionPropertyToCreate> PropertiesToCreate { get; }

    public IReadOnlyList<NotionPageCreate> ToCreate { get; }

    public IReadOnlyList<NotionPageUpdate> ToUpdate { get; }

    public int Unchanged { get; }

    public IReadOnlyList<NotionGoneGame> NoLongerOwned { get; }

    public bool HasOwnedColumn { get; }

    public bool HasGone => NoLongerOwned.Count > 0;

    public string GoneHeader => $"No longer owned ({NoLongerOwned.Count})";

    public string GoneNote => HasOwnedColumn
        ? "These pages are kept; their \"Owned\" checkbox is unticked."
        : "These pages are kept as they are. Add an \"Owned\" checkbox property to the database to mark them.";

    public bool HasChanges => HasNewProperties || ToCreate.Count > 0 || ToUpdate.Count > 0;

    public bool HasNewProperties => PropertiesToCreate.Count > 0;

    public string NewPropertiesText =>
        $"The database is missing {string.Join(", ", PropertiesToCreate.Select(p => $"{p.Name} ({p.DisplayType})"))}. " +
        (PropertiesToCreate.Count == 1 ? "It will be added." : "They will be added.");

    public bool HasCreates => ToCreate.Count > 0;

    public bool HasUpdates => ToUpdate.Count > 0;

    public string Summary => HasChanges
        ? $"{ToCreate.Count} to add · {ToUpdate.Count} to update · {Unchanged} unchanged"
        : $"Your Notion database is already up to date ({Unchanged} games).";

    public string CreateHeader => $"New games ({ToCreate.Count})";

    public string UpdateHeader => $"Updated games ({ToUpdate.Count})";

    public string ConfirmLabel
    {
        get
        {
            var count = ToCreate.Count + ToUpdate.Count;
            if (count == 0) return "Add properties";
            return count == 1 ? "Export 1 change" : $"Export {count} changes";
        }
    }
}

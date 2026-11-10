using System.Collections.Generic;

namespace Pilewise.Services;

public sealed record NotionPageCreate(string Name, string Platforms, object Properties, string? CoverUrl = null);

public sealed record NotionGoneGame(string Name, string Platforms);

public sealed record NotionPageUpdate(
    string PageId,
    string Name,
    string Before,
    string After,
    object Properties,
    string? CoverUrl = null);

public sealed record NotionExportPlan(
    GameLibraryResult Library,
    IReadOnlyList<NotionPropertyToCreate> PropertiesToCreate,
    IReadOnlyList<NotionPageCreate> ToCreate,
    IReadOnlyList<NotionPageUpdate> ToUpdate,
    int Unchanged,
    IReadOnlyList<NotionGoneGame> NoLongerOwned,
    bool HasOwnedColumn,
    BacklogSnapshot? Backlog = null,
    CompletionSync? Completion = null,
    IReadOnlyList<(CsvGameExportRow Row, bool Completed)>? CompletionAfter = null)
{
    public bool IsEmpty => PropertiesToCreate.Count == 0 && ToCreate.Count == 0 && ToUpdate.Count == 0;
}
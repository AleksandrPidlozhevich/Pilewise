using System;

namespace Pilewise.Services;

public static class Platforms
{
    public const string Steam = "Steam";
    public const string Epic = "Epic";
    public const string Gog = "GOG";
    public const string Xbox = "Xbox";

    /// <summary>A tag (not a store): an Xbox title played via Game Pass rather than owned.</summary>
    public const string GamePass = "Game Pass";
}

internal sealed record PlatformDescriptor(
    string Label,
    Func<CsvGameExportRow, bool> Has,
    Action<CsvGameExportRow, bool> SetHas,
    Func<CsvGameExportRow, bool> HasId,
    Func<CsvGameExportRow, string?> FormatId,
    Action<CsvGameExportRow, string> ApplyId,
    Action<CsvGameExportRow, CsvGameExportRow> MergeId,
    Func<CsvGameExportRow, string?> MatchKey);

internal static class PlatformCatalog
{
    public static readonly PlatformDescriptor[] All =
    [
        new(Platforms.Steam,
            r => r.HasSteam,
            (r, v) => r.HasSteam = v,
            r => r.SteamGameId.HasValue,
            r => r.SteamGameId?.ToString(),
            (r, id) => { if (int.TryParse(id.Trim(), out var n)) r.SteamGameId = n; },
            (r, o) => { if (o.SteamGameId.HasValue) r.SteamGameId = o.SteamGameId; },
            r => r.SteamGameId?.ToString()),

        new(Platforms.Epic,
            r => r.HasEpic,
            (r, v) => r.HasEpic = v,
            r => !string.IsNullOrEmpty(r.CatalogItemId) || !string.IsNullOrEmpty(r.Namespace),
            r => !string.IsNullOrEmpty(r.Namespace) && !string.IsNullOrEmpty(r.CatalogItemId)
                ? $"{r.Namespace}/{r.CatalogItemId}"
                : null,
            ApplyEpicId,
            (r, o) =>
            {
                if (!string.IsNullOrEmpty(o.CatalogItemId)) r.CatalogItemId = o.CatalogItemId;
                if (!string.IsNullOrEmpty(o.Namespace)) r.Namespace = o.Namespace;
            },
            r => BuildEpicKey(r.Namespace, r.CatalogItemId)),

        new(Platforms.Gog,
            r => r.HasGog,
            (r, v) => r.HasGog = v,
            r => r.GogId.HasValue,
            r => r.GogId?.ToString(),
            (r, id) => { if (long.TryParse(id.Trim(), out var n)) r.GogId = n; },
            (r, o) => { if (o.GogId.HasValue) r.GogId = o.GogId; },
            r => r.GogId?.ToString()),

        new(Platforms.Xbox,
            r => r.HasXbox,
            (r, v) => r.HasXbox = v,
            r => !string.IsNullOrEmpty(r.XboxTitleId),
            r => string.IsNullOrEmpty(r.XboxTitleId) ? null : r.XboxTitleId,
            (r, id) => r.XboxTitleId = NullIfEmpty(id),
            (r, o) => { if (!string.IsNullOrEmpty(o.XboxTitleId)) r.XboxTitleId = o.XboxTitleId; },
            // Xbox title ids were matched case-insensitively; lowercase the key to preserve that.
            r => string.IsNullOrEmpty(r.XboxTitleId) ? null : r.XboxTitleId.ToLowerInvariant()),
    ];

    public static string? BuildEpicKey(string? ns, string? catalogItemId)
    {
        if (string.IsNullOrWhiteSpace(ns) || string.IsNullOrWhiteSpace(catalogItemId))
            return null;

        return $"{ns.Trim().ToLowerInvariant()}|{catalogItemId.Trim().ToLowerInvariant()}";
    }

    private static void ApplyEpicId(CsvGameExportRow row, string epic)
    {
        var slash = epic.IndexOf('/');
        if (slash > 0 && slash < epic.Length - 1)
        {
            row.Namespace = NullIfEmpty(epic[..slash]);
            row.CatalogItemId = NullIfEmpty(epic[(slash + 1)..]);
        }
    }

    private static string? NullIfEmpty(string value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}

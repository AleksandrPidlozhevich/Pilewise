using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace Pilewise.Services;

public sealed class CsvGameExportRow
{
    public string Name { get; set; } = string.Empty;

    public bool HasSteam { get; set; }

    public bool HasEpic { get; set; }

    public bool HasGog { get; set; }

    public int? SteamGameId { get; set; }

    public string? CatalogItemId { get; set; }

    public string? Namespace { get; set; }

    public long? GogId { get; set; }

    public bool HasXbox { get; set; }

    public string? XboxTitleId { get; set; }

    /// <summary>Game Pass play (not ownership) — exported as a "Game Pass" platform tag.</summary>
    public bool IsGamePass { get; set; }

    public int? PlaytimeMinutes { get; set; }

    public DateTimeOffset? LastPlayed { get; set; }

    public string? FetchedCoverUrl { get; set; }

    public string? Linux { get; set; }

    public string? FetchedStoreUrl { get; set; }

    /// <summary>Extra CSV cells (Completed, opt-in and user columns), keyed by header.</summary>
    public IReadOnlyDictionary<string, string>? Cells { get; set; }

    public CsvGameExportRow Clone() => (CsvGameExportRow)MemberwiseClone();

    public string? CoverUrl => SteamGameId is { } id ? StoreLinks.SteamCover(id) : FetchedCoverUrl;

    public string? StoreUrl => SteamGameId is { } id ? StoreLinks.SteamStore(id) : FetchedStoreUrl;

    public double? PlaytimeHours => PlaytimeMinutes is { } m ? Math.Round(m / 60.0, 1) : null;

    public string? LastPlayedDate => LastPlayed?.UtcDateTime.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    public bool HasContent =>
        !string.IsNullOrWhiteSpace(Name) || PlatformCatalog.All.Any(d => d.HasId(this));

    public string IdText => BuildIdField();

    public string PlatformText => BuildPlatformField();

    public string ToCsvLine() =>
        string.Join(",",
            EscapeCsvField(BuildPlatformField()),
            EscapeCsvField(Name),
            EscapeCsvField(BuildIdField()));

    private string BuildPlatformField()
    {
        var parts = new List<string>();
        foreach (var d in PlatformCatalog.All)
            if (d.Has(this))
                parts.Add(d.Label);
        if (IsGamePass) parts.Add(Platforms.GamePass);
        return string.Join("/", parts);
    }

    private string BuildIdField()
    {
        var parts = new List<string>();
        foreach (var d in PlatformCatalog.All)
        {
            var id = d.FormatId(this);
            if (id != null) parts.Add($"{d.Label}:{id}");
        }

        return string.Join("; ", parts);
    }

    public void MergeFrom(CsvGameExportRow other)
    {
        foreach (var d in PlatformCatalog.All)
        {
            if (d.Has(other)) d.SetHas(this, true);
            d.MergeId(this, other);
        }

        IsGamePass |= other.IsGamePass;

        // Steam playtime + Steam/Xbox last-played: keep the max / newest.
        if (other.PlaytimeMinutes is { } minutes && minutes > (PlaytimeMinutes ?? -1))
            PlaytimeMinutes = minutes;
        if (other.LastPlayed is { } lastPlayed && (LastPlayed == null || lastPlayed > LastPlayed))
            LastPlayed = lastPlayed;

        FetchedCoverUrl ??= other.FetchedCoverUrl;
        FetchedStoreUrl ??= other.FetchedStoreUrl;
        if (!string.IsNullOrEmpty(other.Linux)) Linux = other.Linux;
        Cells ??= other.Cells;

        if (!string.IsNullOrEmpty(other.Name))
            Name = other.Name;
    }

    internal static string EscapeCsvField(string value)
    {
        if (value.AsSpan().IndexOfAny(",\"\r\n") < 0)
            return value;

        return $"\"{value.Replace("\"", "\"\"")}\"";
    }
}

public sealed record CsvMergeOptions(
    bool IncludeLinux = false,
    IReadOnlySet<string>? FetchedStores = null,
    IEnumerable<CsvGameExportRow>? Ignored = null,
    CompletedGames? Completed = null,
    string? Target = null);

public sealed record CsvMergeResult(
    string Content, int Added, int Updated, int Total, bool Changed,
    CompletionSync? Sync, IReadOnlyList<(CsvGameExportRow Row, bool Completed)> Written)
{
    public void Commit() => Sync?.Commit(Written);
}

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace Pilewise.Services;

public sealed class CsvGameExportRow
{
    public string Name
    {
        get;
        set
        {
            field = value;
            Changed();
        }
    } = string.Empty;

    public bool HasSteam
    {
        get;
        set
        {
            field = value;
            Changed();
        }
    }

    public bool HasEpic
    {
        get;
        set
        {
            field = value;
            Changed();
        }
    }

    public bool HasGog
    {
        get;
        set
        {
            field = value;
            Changed();
        }
    }

    public int? SteamGameId
    {
        get;
        set
        {
            field = value;
            Changed();
        }
    }

    public string? CatalogItemId
    {
        get;
        set
        {
            field = value;
            Changed();
        }
    }

    public string? Namespace
    {
        get;
        set
        {
            field = value;
            Changed();
        }
    }

    public long? GogId
    {
        get;
        set
        {
            field = value;
            Changed();
        }
    }

    public bool HasXbox
    {
        get;
        set
        {
            field = value;
            Changed();
        }
    }

    public string? XboxTitleId
    {
        get;
        set
        {
            field = value;
            Changed();
        }
    }

    public bool IsGamePass
    {
        get;
        set
        {
            field = value;
            Changed();
        }
    }

    public int? PlaytimeMinutes { get; set; }

    public DateTimeOffset? LastPlayed { get; set; }

    public string? FetchedCoverUrl { get; set; }

    public string? Linux { get; set; }

    public string? FetchedStoreUrl { get; set; }

    public IReadOnlyDictionary<string, string>? Cells { get; set; }

    public CsvGameExportRow Clone()
    {
        return (CsvGameExportRow)MemberwiseClone();
    }

    public string? CoverUrl => SteamGameId is { } id ? StoreLinks.SteamCover(id) : FetchedCoverUrl;

    public string? StoreUrl => SteamGameId is { } id ? StoreLinks.SteamStore(id) : FetchedStoreUrl;

    public double? PlaytimeHours => PlaytimeMinutes is { } m ? Math.Round(m / 60.0, 1) : null;

    public string? LastPlayedDate => LastPlayed?.UtcDateTime.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    public bool HasContent =>
        !string.IsNullOrWhiteSpace(Name) || PlatformCatalog.All.Any(d => d.HasId(this));

    // Setters that feed these must call Changed().
    private string? _idText, _platformText, _nameKey;
    private string[]? _keys;

    private void Changed()
    {
        _idText = _platformText = _nameKey = null;
        _keys = null;
    }

    public string IdText => _idText ??= BuildIdField();

    public string PlatformText => _platformText ??= BuildPlatformField();

    public string NameKey => _nameKey ??= CsvGameExportService.NormalizeGameName(Name);

    public IReadOnlyList<string> Keys => _keys ??= BuildKeys();

    private string[] BuildKeys()
    {
        var keys = PlatformCatalog.All
            .Select(d => d.FormatId(this) is { } id ? $"{d.Label}:{id}" : null)
            .OfType<string>()
            .ToArray();
        return keys.Length > 0 || NameKey.Length == 0 ? keys : [$"Name:{NameKey}"];
    }

    public string ToCsvLine()
    {
        return string.Join(",", EscapeCsvField(PlatformText), EscapeCsvField(Name), EscapeCsvField(IdText));
    }

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
    string Content,
    int Added,
    int Updated,
    int Total,
    bool Changed,
    CompletionSync? Sync,
    IReadOnlyList<(CsvGameExportRow Row, bool Completed)> Written)
{
    public void Commit()
    {
        Sync?.Commit(Written);
    }
}
using System;

namespace Pilewise.Models;

/// <summary>
/// An owned title from the user's Xbox library (the purchased/owned set). The fetch only returns
/// owned games, so <see cref="IsGamePass"/> is no longer set here; the flag is kept for the CSV /
/// Notion "Game Pass" tag (manually-tagged or legacy rows) and stays false for fetched titles.
/// </summary>
public sealed class XboxGame
{
    public string TitleId { get; set; } = string.Empty;

    public string Name { get; set; } = string.Empty;

    public bool IsGamePass { get; set; }

    public DateTimeOffset? LastPlayed { get; set; }

    public string? ImageUrl { get; set; }

    public string? ProductId { get; set; }
}
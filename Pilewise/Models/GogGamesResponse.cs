using Newtonsoft.Json;
using System.Collections.Generic;

namespace Pilewise.Models;

public class GogGame
{
    [JsonProperty("id")] public long Id { get; set; }

    [JsonProperty("title")] public string Title { get; set; } = string.Empty;

    [JsonProperty("isGame")] public bool IsGame { get; set; }

    [JsonProperty("isMovie")] public bool IsMovie { get; set; }

    /// <summary>Cover hash URL without extension, e.g. <c>//images-4.gog-statics.com/abc</c>.</summary>
    [JsonProperty("image")] public string? Image { get; set; }

    [JsonProperty("url")] public string? Url { get; set; }

    [JsonProperty("slug")] public string? Slug { get; set; }
}

public class GogFilteredProductsResponse
{
    [JsonProperty("page")] public int Page { get; set; }

    [JsonProperty("totalPages")] public int TotalPages { get; set; }

    [JsonProperty("products")] public List<GogGame> Products { get; set; } = new();
}
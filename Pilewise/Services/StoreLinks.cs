using Newtonsoft.Json.Linq;
using System;
using System.Linq;

namespace Pilewise.Services;

public static class StoreLinks
{
    public static string SteamCover(int appId)
    {
        return $"https://cdn.akamai.steamstatic.com/steam/apps/{appId}/header.jpg";
    }

    public static string SteamStore(int appId)
    {
        return $"https://store.steampowered.com/app/{appId}/";
    }

    /// <summary>GOG's <c>image</c> is a protocol-relative hash without extension, e.g.
    /// <c>//images-4.gog-statics.com/abc123</c>; appending <c>.jpg</c> gives the cover.</summary>
    public static string? GogCover(string? image)
    {
        if (string.IsNullOrWhiteSpace(image)) return null;
        var url = Absolute(image.Trim());
        if (url == null) return null;
        return HasImageExtension(url) ? url : url + ".jpg";
    }

    /// <summary>GOG's <c>url</c> is a site path like <c>/en/game/the_witcher</c>; the slug is a fallback.</summary>
    public static string? GogStore(string? path, string? slug)
    {
        if (!string.IsNullOrWhiteSpace(path))
        {
            path = path.Trim();
            if (path.StartsWith("http", StringComparison.OrdinalIgnoreCase)) return path;
            if (path.StartsWith('/')) return "https://www.gog.com" + path;
        }

        return string.IsNullOrWhiteSpace(slug) ? null : $"https://www.gog.com/game/{slug.Trim()}";
    }

    // Wide images first (they suit a Notion page cover), then the tall box art.
    private static readonly string[] EpicImageTypes =
        ["DieselGameBox", "DieselStoreFrontWide", "OfferImageWide", "Thumbnail", "DieselGameBoxTall"];

    public static string? EpicCover(JToken? catalogItem)
    {
        if (catalogItem?["keyImages"] is not JArray images) return null;
        foreach (var type in EpicImageTypes)
        {
            var url = images.FirstOrDefault(i => i["type"]?.Value<string>() == type)?["url"]?.Value<string>();
            if (Absolute(url) is { } absolute) return absolute;
        }

        return null;
    }

    public static string? EpicStore(JToken? catalogItem)
    {
        var slug = catalogItem?["customAttributes"]?["com.epicgames.app.productSlug"]?["value"]?.Value<string>();
        if (string.IsNullOrWhiteSpace(slug)) return null;
        // Some slugs carry a "/home" suffix; the product page is the part before it.
        slug = slug.Trim().Split('/')[0];
        return string.IsNullOrEmpty(slug) ? null : $"https://store.epicgames.com/p/{slug}";
    }

    public static string? XboxCover(string? displayImage)
    {
        return Absolute(displayImage);
    }

    public static string? XboxStore(string? productId)
    {
        return string.IsNullOrWhiteSpace(productId) ? null : $"https://www.xbox.com/games/store/_/{productId.Trim()}";
    }

    // https for protocol-relative / plain-http URLs; null for anything that isn't a web URL.
    private static string? Absolute(string? url)
    {
        if (string.IsNullOrWhiteSpace(url)) return null;
        url = url.Trim();
        if (url.StartsWith("//", StringComparison.Ordinal)) return "https:" + url;
        if (url.StartsWith("http://", StringComparison.OrdinalIgnoreCase)) return "https://" + url[7..];
        return url.StartsWith("https://", StringComparison.OrdinalIgnoreCase) ? url : null;
    }

    private static bool HasImageExtension(string url)
    {
        var path = url.Split('?')[0];
        return path.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase) ||
               path.EndsWith(".jpeg", StringComparison.OrdinalIgnoreCase) ||
               path.EndsWith(".png", StringComparison.OrdinalIgnoreCase) ||
               path.EndsWith(".webp", StringComparison.OrdinalIgnoreCase);
    }
}
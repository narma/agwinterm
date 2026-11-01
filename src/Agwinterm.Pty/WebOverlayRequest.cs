using System.Text.Json;

namespace Agwinterm.Pty;

public sealed record WebOverlayRequest(string? Html, string? Url, bool JavaScript,
    int Percent, int? Width, int? Height, bool Follow, bool Navigation = false, bool Chromeless = false, string? Bridge = null, bool Browse = false, string? Cwd = null, string? Pane = null, bool Persistent = false, bool Block = false, bool Companion = false, string? Image = null, string? Background = null, bool ExplicitSize = false)
{
    public static bool IsWeb(JsonElement args) => args.ValueKind == JsonValueKind.Object &&
        (args.TryGetProperty("html", out _) || args.TryGetProperty("url", out _) || args.TryGetProperty("image", out _));

    public static WebOverlayRequest Parse(JsonElement args)
    {
        var seen = new HashSet<string>();
        foreach (var property in args.EnumerateObject())
        {
            if (!seen.Add(property.Name)) throw new ArgumentException("duplicate web overlay argument: " + property.Name);
            if (property.Name is not ("action" or "html" or "url" or "js" or "size-percent" or "width" or "height" or "follow" or "navigation" or "chromeless" or "bridge" or "browse" or "cwd" or "pane" or "persistent" or "block" or "companion" or "image" or "background-color"))
                throw new ArgumentException("web overlay: unsupported argument " + property.Name);
        }
        string? Text(string key)
        {
            if (!args.TryGetProperty(key, out var value)) return null;
            if (value.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(value.GetString()))
                throw new ArgumentException(key + " must be a nonempty string");
            return value.GetString();
        }
        bool Flag(string key)
        {
            if (!args.TryGetProperty(key, out var value)) return false;
            if (value.ValueKind is not (JsonValueKind.True or JsonValueKind.False)) throw new ArgumentException(key + " must be boolean");
            return value.GetBoolean();
        }
        int? Size(string key, int maximum)
        {
            if (!args.TryGetProperty(key, out var value)) return null;
            if (value.ValueKind != JsonValueKind.Number || !value.TryGetInt32(out int number) || number < 1 || number > maximum) throw new ArgumentException(key + " is outside its allowed range");
            return number;
        }
        if (Text("action") is not (null or "open")) throw new ArgumentException("web sources require overlay open");
        string? html = Text("html"), url = Text("url"), image = Text("image");
        if (new[] { html, url, image }.Count(value => value is not null) != 1) throw new ArgumentException("exactly one of html, url or image is required");
        if (image is not null && new[] { "js", "navigation", "chromeless", "bridge", "browse", "cwd", "persistent", "block", "companion" }.Any(seen.Contains))
            throw new ArgumentException("image refuses page flags, cwd, block and companion");
        string? background = Text("background-color");
        if (background is not null && !System.Text.RegularExpressions.Regex.IsMatch(background, "^#[0-9a-fA-F]{6}$")) throw new ArgumentException("background-color must be #rrggbb");
        if (url is not null && (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https") || uri.UserInfo.Length != 0))
            throw new ArgumentException("url must be HTTP(S) without embedded credentials");
        bool js = Flag("js"), navigation = Flag("navigation"), chromeless = Flag("chromeless");
        string? bridge = Text("bridge");
        string? cwd = Text("cwd");
        string? pane = Text("pane");
        if (!OverlayPanes.TryParse(pane, out _, out var paneError)) throw new ArgumentException(paneError);
        if (pane is not null && (args.TryGetProperty("size-percent", out _) || args.TryGetProperty("width", out _) || args.TryGetProperty("height", out _)))
            throw new ArgumentException("pane overlays are always full-pane; floating dimensions are unsupported");
        if (cwd is not null && html is null) throw new ArgumentException("cwd requires local HTML");
        bool browse = Flag("browse");
        bool persistent = Flag("persistent");
        bool block = Flag("block");
        bool companion = Flag("companion");
        if (companion && (pane is not null || args.TryGetProperty("size-percent", out _) || args.TryGetProperty("width", out _) || args.TryGetProperty("height", out _)))
            throw new ArgumentException("companion refuses pane and floating dimensions");
        if (block && html is null) throw new ArgumentException("block requires local HTML");
        if (persistent && url is null) throw new ArgumentException("persistent requires url");
        if (browse && (url is null || chromeless)) throw new ArgumentException("browse requires url and cannot be combined with chromeless");
        if (chromeless && url is not null && !WebLoopback(url)) throw new ArgumentException("chromeless URL requires localhost, 127.0.0.1 or ::1");
        if (navigation && chromeless) throw new ArgumentException("navigation and chromeless are mutually exclusive");
        if (bridge is not null && (bridge != "review" || html is null || !js)) throw new ArgumentException("bridge review requires local HTML and explicit js");
        return new(html, url, js, Size("size-percent", 100) ?? 100, Size("width", 16384), Size("height", 16384), Flag("follow"), navigation, chromeless, bridge, browse, cwd, pane, persistent, block, companion, image, background,
            seen.Contains("size-percent") || seen.Contains("width") || seen.Contains("height"));
    }
    private static bool WebLoopback(string url) => new Uri(url).Host.ToLowerInvariant() is "localhost" or "127.0.0.1" or "[::1]" or "::1";
}

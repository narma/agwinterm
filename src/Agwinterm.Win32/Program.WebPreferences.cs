using System.Globalization;
using System.Text.Json;

namespace Agwinterm.Win32;

internal partial class Program
{
    private sealed record WebPreferences(string LinkOpenMode = "browser", double Zoom = 1, bool UndoClose = true);
    private static WebPreferences? _webPreferences;
    private static bool _webStorageClearing;
    private static readonly object WebStoreGate = new();
    private static void EnsurePersistentWebStore()
    {
        lock (WebStoreGate)
        {
            string directory = Path.Combine(AppDir, "webview2-persistent");
            Directory.CreateDirectory(directory);
            string path = Path.Combine(directory, "store-id");
            if (!File.Exists(path)) File.WriteAllText(path, Guid.NewGuid().ToString("N"));
            if (!Guid.TryParseExact(File.ReadAllText(path).Trim(), "N", out _))
                throw new IOException("Persistent browser store identity is unreadable; nothing opened");
        }
    }
    private static WebPreferences BrowserPreferences => _webPreferences ??= LoadWebPreferences();
    private static string BrowserSettingsValue()
    {
        try { return BrowserPreferences.LinkOpenMode; }
        catch (Exception ex) when (ex is IOException or JsonException or InvalidOperationException or UnauthorizedAccessException)
        { return "Unavailable"; }
    }
    private static WebPreferences LoadWebPreferences()
    {
        string path = Path.Combine(AppDir, "browser.json");
        if (!File.Exists(path)) return new();
        var value = JsonSerializer.Deserialize<WebPreferences>(File.ReadAllText(path)) ?? new();
        if (value.LinkOpenMode is not ("browser" or "overlay" or "overlay95" or "overlay75" or "overlay50") ||
            !double.IsFinite(value.Zoom) || value.Zoom < .25 || value.Zoom > 5)
            throw new InvalidOperationException("Invalid browser preferences");
        return value;
    }
    private static void SaveWebPreferences(WebPreferences value)
    {
        string path = Path.Combine(AppDir, "browser.json");
        Directory.CreateDirectory(AppDir);
        File.WriteAllText(path + ".tmp", JsonSerializer.Serialize(value));
        File.Move(path + ".tmp", path, true);
        _webPreferences = value;
    }

    public string? BrowserLinkOpenMode() => InvokeOnUiQueued(() => BrowserPreferences.LinkOpenMode);

    public object BrowserCommand(string action, string? mode)
    {
        if (action == "browser.links") return InvokeOnUiQueued<object>(() =>
        {
            if (mode is not null)
            {
                if (mode is not ("browser" or "overlay" or "overlay95" or "overlay75" or "overlay50")) throw new ArgumentException("invalid browser links mode");
                SaveWebPreferences(BrowserPreferences with { LinkOpenMode = mode });
            }
            return BrowserPreferences.LinkOpenMode;
        });
        var clearing = InvokeOnUiQueued(() =>
        {
            if (_webStorageClearing) throw new ArgumentException("browser storage is being cleared");
            int count = _registry.Values.Distinct().Sum(window => window._webPages.Count(pair => pair.Value.Request.Persistent));
            if (count != 0) throw new ArgumentException($"browser.clear: {count} persistent page(s) still open");
            string path = Path.Combine(AppDir, "webview2-persistent");
            if (!Directory.Exists(path)) return Task.CompletedTask;
            _webStorageClearing = true;
            var host = NewWebHost();
            return host.RunAsync(async () =>
            {
                try
                {
                    await host.CreateAsync(_hwnd, path, Guid.NewGuid().ToString("N"), _uiGone.Token, persistent: true);
                    await host.Page.Profile.ClearBrowsingDataAsync();
                    File.Delete(Path.Combine(path, "store-id"));
                }
                finally { host.Dispose(); _webStorageClearing = false; }
            });
        });
        clearing.GetAwaiter().GetResult(); // Control-pipe thread; browser work stays asynchronous on STA.
        return "cleared";
    }

    private void ChangeWebZoom(int delta)
    {
        double[] steps = [.25, .33, .5, .67, .75, .8, .9, 1, 1.1, 1.25, 1.5, 1.75, 2, 2.5, 3, 4, 5];
        double current = BrowserPreferences.Zoom;
        double zoom = delta == 0 ? 1 : delta > 0 ? steps.FirstOrDefault(n => n > current + .001, 5) : steps.LastOrDefault(n => n < current - .001, .25);
        SetWebZoom(zoom);
    }

    private static bool _settingWebZoom;
    private void SetWebZoom(double zoom)
    {
        if (_settingWebZoom) return;
        zoom = Math.Clamp(zoom, .25, 5);
        _settingWebZoom = true;
        try
        {
            SaveWebPreferences(BrowserPreferences with { Zoom = zoom });
            foreach (var window in _registry.Values.Distinct())
                foreach (var (_, page) in window._webPages) { page.Host.SetZoom(zoom); page.Zoom = zoom; }
            RequestRedraw();
        }
        finally { _settingWebZoom = false; }
    }

    private bool TryOpenWebLink(string url)
    {
        var owner = _active;
        string mode;
        try { mode = BrowserPreferences.LinkOpenMode; }
        catch (Exception ex) when (ex is IOException or JsonException or InvalidOperationException or UnauthorizedAccessException)
        { ShowToast("Browser preferences could not be read; opening the external browser"); return false; }
        if (mode == "browser" || owner is null || _isQuickWindow || _cover is not null || owner.Hud is not null ||
            owner.ActivePane.Overlay.Term is not null || owner.Overlay.Term is not null || _webPages.ContainsKey(owner) ||
            (Win32.GetKeyState(0x10) & 0x8000) != 0) return false;
        var uri = new Uri(url);
        if (uri.Scheme == "http" && uri.Host.Contains('.') && !uri.Host.EndsWith(".local", StringComparison.OrdinalIgnoreCase) &&
            !System.Net.IPAddress.TryParse(uri.Host, out _)) return false;
        int percent = mode == "overlay" ? 100 : int.Parse(mode[7..], CultureInfo.InvariantCulture);
        _ = Task.Run(() =>
        {
            try { OpenWebOverlay(owner.Id, new(null, url, true, percent, null, null, false, Navigation: true, Browse: true, Persistent: true)); }
            catch { PostVerb(() => Win32.ShellExecuteW(_hwnd, "open", url, null, null, Win32.SW_SHOW)); }
        });
        return true;
    }
}

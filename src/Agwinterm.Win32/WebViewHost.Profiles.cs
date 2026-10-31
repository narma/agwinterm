using Microsoft.Web.WebView2.Core;

namespace Agwinterm.Win32;

internal sealed partial class WebViewHost
{
    private static readonly Dictionary<string, HashSet<string>> PendingProfiles = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<string, int> ActiveProfiles = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<string, Task> ProfileCleanups = new(StringComparer.OrdinalIgnoreCase);
    private string? _profilePath;
    private string? _profileDirectory;

    private async Task PrepareProfileAsync(string directory, string pageId)
    {
        if (!Guid.TryParseExact(pageId, "N", out _)) throw new ArgumentException("invalid temporary profile identity");
        directory = Path.GetFullPath(directory);
        _profileDirectory = directory;
        _profilePath = Path.Combine(directory, "EBWebView", "WV2Profile_page-" + pageId);
        ActiveProfiles[_profilePath] = ActiveProfiles.GetValueOrDefault(_profilePath) + 1;
        if (!PendingProfiles.TryGetValue(directory, out var pending))
        {
            PendingProfiles[directory] = pending = new(StringComparer.OrdinalIgnoreCase);
            // Only the first web request performs orphan IO; terminal startup never does.
            var active = ActiveProfiles.Keys.ToHashSet(StringComparer.OrdinalIgnoreCase);
            ProfileCleanups[directory] = Task.Run(() =>
            {
                string root = Path.Combine(directory, "EBWebView");
                if (!Directory.Exists(root)) return;
                foreach (string path in Directory.EnumerateDirectories(root, "WV2Profile_page-*"))
                    if (!active.Contains(path)) DeleteProfileDirectory(directory, path);
            });
        }
        pending.Remove(_profilePath);
        if (ProfileCleanups.TryGetValue(directory, out var cleanup)) await cleanup;
    }

    private void ReleaseProfile()
    {
        if (_profilePath is not { } path) return;
        _profilePath = null;
        int remaining = ActiveProfiles.GetValueOrDefault(path) - 1;
        if (remaining > 0) ActiveProfiles[path] = remaining;
        else
        {
            ActiveProfiles.Remove(path);
            PendingProfiles[_profileDirectory!].Add(path);
        }
    }

    private static async Task<CoreWebView2Environment> CreateEnvironmentAsync(string directory)
    {
        var environment = await CoreWebView2Environment.CreateAsync(userDataFolder: directory);
        environment.BrowserProcessExited += (_, _) =>
        {
            string key = Path.GetFullPath(directory);
            if (!PendingProfiles.TryGetValue(key, out var pending)) return;
            string[] closed = pending.Where(path => !ActiveProfiles.ContainsKey(path)).ToArray();
            pending.ExceptWith(closed);
            Task previous = ProfileCleanups.GetValueOrDefault(key) ?? Task.CompletedTask;
            ProfileCleanups[key] = Task.Run(async () =>
            {
                await previous;
                foreach (string path in closed) DeleteProfileDirectory(key, path);
            });
        };
        return environment;
    }

    private static void DeleteProfileDirectory(string directory, string path)
    {
        string root = Path.GetFullPath(Path.Combine(directory, "EBWebView"));
        string full = Path.GetFullPath(path), name = Path.GetFileName(full);
        const string prefix = "WV2Profile_page-";
        if (!string.Equals(Path.GetDirectoryName(full), root, StringComparison.OrdinalIgnoreCase) ||
            !name.StartsWith(prefix, StringComparison.Ordinal) || !Guid.TryParseExact(name[prefix.Length..], "N", out _))
            throw new InvalidOperationException("temporary profile cleanup escaped its directory");
        try
        {
            if (Directory.Exists(full) && (File.GetAttributes(full) & FileAttributes.ReparsePoint) == 0)
                Directory.Delete(full, recursive: true);
        }
        catch (IOException) { } // A delayed runtime handle is retried on the next web startup.
        catch (UnauthorizedAccessException) { }
    }
}

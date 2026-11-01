using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace Agwinterm.Pty;

/// <summary>A document-directory grant. Reads validate the opened handle, not a lexical path alone.</summary>
public sealed class LocalWebSource
{
    private readonly string _root;
    private readonly ShellProfile? _wsl;
    private readonly bool _directoryGrant;
    public string EntryName { get; }
    public string ExternalPath { get; }
    public string EntryUrlPath => string.Join('/', EntryName.Split('/').Select(Uri.EscapeDataString));
    private LocalWebSource(string root, string entry, string external, ShellProfile? wsl, bool directoryGrant)
    { _root = root; EntryName = entry; ExternalPath = external; _wsl = wsl; _directoryGrant = directoryGrant; }

    public static LocalWebSource Open(string file, string? cwd, ShellProfile? profile, string? grant = null, int maxBytes = int.MaxValue)
    {
        if (WslIntegration.IsWsl(profile?.Command) && !Path.IsPathFullyQualified(file))
        {
            string baseDirectory = WslIntegration.ValidateDirectory(profile!, cwd ?? WslIntegration.ProfileDirectory(profile) ?? "~");
            string path = file.StartsWith('/') ? file : baseDirectory.TrimEnd('/') + "/" + file;
            path = Encoding.UTF8.GetString(RunWsl(profile!, ["readlink", "-e", "--", path])).TrimEnd('\r', '\n');
            int slash = path.LastIndexOf('/');
            string root = grant is null ? path[..slash] : WslIntegration.ValidateDirectory(profile!, grant.StartsWith('/') || grant.StartsWith('~') ? grant : baseDirectory.TrimEnd('/') + "/" + grant);
            root = Encoding.UTF8.GetString(RunWsl(profile!, ["readlink", "-e", "--", root.Length == 0 ? "/" : root])).TrimEnd('\r', '\n');
            if (root.Length == 0) throw new IOException("WSL document directory is unavailable");
            if (grant is not null)
            {
                string home = WslIntegration.ValidateDirectory(profile!, "~");
                if (root == "/" || root == home) throw new ArgumentException("cwd cannot grant the filesystem root or home directory");
            }
            if (!path.StartsWith(root.TrimEnd('/') + "/", StringComparison.Ordinal)) throw new ArgumentException("HTML file must be inside cwd");
            var source = new LocalWebSource(root, path[(root.TrimEnd('/').Length + 1)..], WslIntegration.WindowsPath(profile!, path), profile, grant is not null);
            source.Read("/" + source.EntryUrlPath, maxBytes);
            return source;
        }
        string full = Path.IsPathFullyQualified(file) ? Path.GetFullPath(file) : Path.GetFullPath(file,
            cwd ?? throw new ArgumentException("target cwd unavailable; use an absolute HTML path"));
        using (var entry = File.OpenHandle(full, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete)) full = FinalPath(entry);
        string grantPath = grant is null ? Path.GetDirectoryName(full)! : Path.GetFullPath(grant, cwd ?? Path.GetDirectoryName(full)!);
        using var directory = CreateFileW(grantPath, 0, 7, IntPtr.Zero, 3, 0x02000000, IntPtr.Zero);
        if (directory.IsInvalid) throw new IOException("document directory is unavailable", new System.ComponentModel.Win32Exception());
        string canonicalRoot = FinalPath(directory).TrimEnd('\\');
        if (grant is not null)
        {
            using var home = CreateFileW(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), 0, 7, IntPtr.Zero, 3, 0x02000000, IntPtr.Zero);
            if (canonicalRoot.Equals(Path.GetPathRoot(full)?.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase) ||
                !home.IsInvalid && canonicalRoot.Equals(FinalPath(home).TrimEnd('\\'), StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException("cwd cannot grant the filesystem root or home directory");
        }
        if (!full.StartsWith(canonicalRoot + "\\", StringComparison.OrdinalIgnoreCase)) throw new ArgumentException("HTML file must be inside cwd");
        string external = full.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase) ? @"\\" + full[8..] : full.StartsWith(@"\\?\") ? full[4..] : full;
        var result = new LocalWebSource(canonicalRoot, full[(canonicalRoot.Length + 1)..].Replace('\\', '/'), external, null, grant is not null);
        result.Read("/" + result.EntryUrlPath, maxBytes);
        return result;
    }

    private string RelativePath(string uriPath)
    {
        string relative = Uri.UnescapeDataString(uriPath).TrimStart('/');
        if (relative.Length == 0) relative = EntryName;
        if (!_directoryGrant && !relative.Equals(EntryName, _wsl is null ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
            throw new UnauthorizedAccessException("resource requires an explicit cwd grant");
        if (relative.Contains('\\') || relative.Contains(':') || relative.Contains('\0') || relative.Split('/').Any(s => s is "." or ".."))
            throw new UnauthorizedAccessException("resource escapes the document directory");
        return relative;
    }

    public string ResolveExternalPath(string uriPath)
    {
        string relative = RelativePath(uriPath);
        if (_wsl is not null)
        {
            string actual = Encoding.UTF8.GetString(RunWsl(_wsl, ["readlink", "-e", "--", _root + "/" + relative])).TrimEnd('\r', '\n');
            if (!actual.StartsWith(_root.TrimEnd('/') + "/", StringComparison.Ordinal))
                throw new UnauthorizedAccessException("resource link escapes the document directory");
            return WslIntegration.WindowsPath(_wsl, actual);
        }
        using var handle = File.OpenHandle(Path.Combine(_root, relative.Replace('/', '\\')), FileMode.Open,
            FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        string path = FinalPath(handle);
        if (!path.StartsWith(_root + "\\", StringComparison.OrdinalIgnoreCase))
            throw new UnauthorizedAccessException("resource link escapes the document directory");
        return path.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase) ? @"\\" + path[8..] : path.StartsWith(@"\\?\") ? path[4..] : path;
    }

    public byte[] Read(string uriPath, int maxBytes = int.MaxValue)
    {
        string relative = RelativePath(uriPath);
        if (_wsl is not null)
        {
            // Open first, then verify the actual fd target; replacement of the pathname
            // after this check cannot redirect the bytes read through fd 3.
            const string script = "root=${1%/}; test -f \"$root/$2\" || exit 3; exec 3<\"$root/$2\" || exit 3; actual=$(readlink -f /proc/$$/fd/3) || exit 3; case \"$actual\" in \"$root\"/*) test -f /proc/$$/fd/3 && head -c \"$3\" <&3;; *) exit 4;; esac";
            var bytes = RunWsl(_wsl, ["/bin/sh", "-c", script, "web-resource", _root, relative, ((long)maxBytes + 1).ToString(System.Globalization.CultureInfo.InvariantCulture)]);
            if (bytes.Length > maxBytes) throw new ArgumentException("image too large");
            return bytes;
        }
        string path = Path.GetFullPath(Path.Combine(_root, relative.Replace('/', '\\')));
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        if (!FinalPath(stream.SafeFileHandle).StartsWith(_root + "\\", StringComparison.OrdinalIgnoreCase))
            throw new UnauthorizedAccessException("resource link escapes the document directory");
        if (stream.Length > maxBytes) throw new ArgumentException("image too large");
        using var output = new MemoryStream();
        byte[] buffer = new byte[81920];
        int count;
        while ((count = stream.Read(buffer)) > 0)
        {
            if (output.Length + count > maxBytes) throw new ArgumentException("image too large");
            output.Write(buffer, 0, count);
        }
        return output.ToArray();
    }

    private static byte[] RunWsl(ShellProfile profile, string[] command)
    {
        var start = new ProcessStartInfo(profile.Command) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var arg in WslIntegration.Selectors(profile.Args).Concat(new[] { "--exec" }).Concat(command)) start.ArgumentList.Add(arg);
        using var process = Process.Start(start) ?? throw new IOException("WSL could not start");
        using var bytes = new MemoryStream();
        Task output = process.StandardOutput.BaseStream.CopyToAsync(bytes);
        Task<string> error = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit(10000)) { process.Kill(true); process.WaitForExit(); output.GetAwaiter().GetResult(); throw new IOException("WSL resource request timed out"); }
        output.GetAwaiter().GetResult(); string detail = error.GetAwaiter().GetResult();
        if (process.ExitCode != 0) throw new IOException("WSL resource unavailable or outside the document grant: " + detail.Trim());
        return bytes.ToArray();
    }

    public static string ContentType(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".html" or ".htm" => "text/html; charset=utf-8", ".css" => "text/css; charset=utf-8",
        ".js" or ".mjs" => "text/javascript; charset=utf-8", ".json" => "application/json",
        ".svg" => "image/svg+xml", ".png" => "image/png", ".jpg" or ".jpeg" => "image/jpeg",
        ".gif" => "image/gif", ".webp" => "image/webp", ".ico" => "image/x-icon",
        ".woff2" => "font/woff2", ".woff" => "font/woff", ".ttf" => "font/ttf", ".txt" or ".md" => "text/plain; charset=utf-8",
        _ => "application/octet-stream"
    };
    private static string FinalPath(SafeFileHandle handle)
    {
        var buffer = new StringBuilder(32768);
        uint length = GetFinalPathNameByHandleW(handle, buffer, (uint)buffer.Capacity, 0);
        if (length == 0 || length >= buffer.Capacity) throw new IOException("Cannot resolve canonical resource path");
        return buffer.ToString();
    }
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFileW(string name, uint access, uint share, IntPtr security, uint creation, uint flags, IntPtr template);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetFinalPathNameByHandleW(SafeFileHandle handle, StringBuilder path, uint size, uint flags);
}

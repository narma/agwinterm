using System.Text;
using Xunit;

namespace Agwinterm.Pty.Tests;

public class LocalWebSourceTests
{
    [Fact]
    public void NoImplicitDirectoryGrantAndNestedExplicitGrant()
    {
        string root = Path.Combine(Path.GetTempPath(), "agwinterm-web-grant-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "nested"));
        try
        {
            File.WriteAllText(Path.Combine(root, "nested", "page.html"), "document");
            File.WriteAllText(Path.Combine(root, "nested", "asset.css"), "style");
            var standalone = LocalWebSource.Open("nested/page.html", root, null);
            Assert.Equal("document", Encoding.UTF8.GetString(standalone.Read("/page.html")));
            Assert.Throws<UnauthorizedAccessException>(() => standalone.Read("/asset.css"));
            var granted = LocalWebSource.Open("nested/page.html", root, null, root);
            Assert.Equal("nested/page.html", granted.EntryUrlPath);
            Assert.Equal("style", Encoding.UTF8.GetString(granted.Read("/nested/asset.css")));
            Assert.Equal(Path.Combine(root, "nested", "asset.css"), granted.ResolveExternalPath("/nested/asset.css"));
            Assert.Throws<UnauthorizedAccessException>(() => standalone.ResolveExternalPath("/asset.css"));
            Assert.Throws<UnauthorizedAccessException>(() => granted.ResolveExternalPath("/../secret.html"));
            Assert.Throws<ArgumentException>(() => LocalWebSource.Open("nested/page.html", root, null, Path.GetPathRoot(root)));
            Assert.Throws<ArgumentException>(() => granted.Read("/nested/page.html", 3));
        }
        finally { Directory.Delete(root, true); }
    }
    [Fact]
    public void WslResourcesUseTheSelectedDistributionAndRejectEscapingLinks()
    {
        // Explicit opt-in keeps ordinary unit runs independent of installed distributions.
        string? distro = Environment.GetEnvironmentVariable("AGWINTERM_TEST_WEB_WSL");
        if (string.IsNullOrWhiteSpace(distro)) return;
        var profile = new ShellProfile { Command = "wsl.exe", Args = ["-d", distro] };
        string root = "/tmp/agwinterm-web-test-" + Guid.NewGuid().ToString("N");
        void Run(string script)
        {
            var info = new System.Diagnostics.ProcessStartInfo("wsl.exe") { UseShellExecute = false, CreateNoWindow = true };
            foreach (string arg in new[] { "-d", distro, "--exec", "/bin/sh", "-c", script, "web-test", root }) info.ArgumentList.Add(arg);
            using var process = System.Diagnostics.Process.Start(info)!;
            Assert.True(process.WaitForExit(15000)); Assert.Equal(0, process.ExitCode);
        }
        Run("mkdir -p \"$1/grant\"; printf document >\"$1/grant/отчёт 1.html\"; printf asset >\"$1/grant/стиль 1.css\"; printf secret >\"$1/secret\"; ln -s ../secret \"$1/grant/escape\"");
        try
        {
            var source = LocalWebSource.Open("отчёт 1.html", root + "/grant", profile, root + "/grant");
            Assert.Equal(source.ExternalPath, source.ResolveExternalPath("/" + source.EntryUrlPath));
            Assert.Throws<UnauthorizedAccessException>(() => source.ResolveExternalPath("/escape"));
            Assert.Equal("asset", Encoding.UTF8.GetString(source.Read("/" + Uri.EscapeDataString("стиль 1.css"))));
            Assert.Throws<IOException>(() => source.Read("/escape"));
            Assert.Throws<UnauthorizedAccessException>(() => source.Read("/%2e%2e/secret"));
            var missing = new ShellProfile { Command = "wsl.exe", Args = ["-d", "agwinterm-absent-" + Guid.NewGuid().ToString("N")] };
            Assert.ThrowsAny<Exception>(() => LocalWebSource.Open("/tmp/document.html", "/tmp", missing));
        }
        finally { Run("case \"$1\" in /tmp/agwinterm-web-test-*) rm -rf -- \"$1\";; *) exit 1;; esac"); }
    }

    [Fact]
    public void SiblingUnicodeAssetsAreReadableButTraversalAndStreamsRefuse()
    {
        string root = Path.Combine(Path.GetTempPath(), "agwinterm-web-source-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            File.WriteAllText(Path.Combine(root, "отчёт 1.html"), "document");
            File.WriteAllText(Path.Combine(root, "стиль 1.css"), "body{}");
            var source = LocalWebSource.Open("отчёт 1.html", root, null, root);
            Assert.Equal("body{}", Encoding.UTF8.GetString(source.Read("/" + Uri.EscapeDataString("стиль 1.css"))));
            Assert.Throws<UnauthorizedAccessException>(() => source.Read("/../secret"));
            Assert.Throws<UnauthorizedAccessException>(() => source.Read("/%2e%2e/secret"));
            Assert.Throws<UnauthorizedAccessException>(() => source.Read("/file.html:secret"));
            Assert.Throws<UnauthorizedAccessException>(() => source.Read("/dir%5c..%5csecret"));
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void SymlinkEscapeChecksTheOpenedFile()
    {
        string root = Path.Combine(Path.GetTempPath(), "agwinterm-web-links-" + Guid.NewGuid().ToString("N"));
        string grant = Path.Combine(root, "grant"); Directory.CreateDirectory(grant);
        try
        {
            File.WriteAllText(Path.Combine(grant, "index.html"), "document");
            File.WriteAllText(Path.Combine(root, "secret.txt"), "secret");
            string link = Path.Combine(grant, "escape.txt");
            File.CreateSymbolicLink(link, Path.Combine(root, "secret.txt"));
            var source = LocalWebSource.Open("index.html", grant, null, grant);
            Assert.Throws<UnauthorizedAccessException>(() => source.Read("/escape.txt"));
            File.Delete(link);
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void JunctionEscapeChecksTheOpenedFile()
    {
        string root = Path.Combine(Path.GetTempPath(), "agwinterm-web-junction-" + Guid.NewGuid().ToString("N"));
        string grant = Path.Combine(root, "grant"), outside = Path.Combine(root, "outside"), link = Path.Combine(grant, "escape");
        Directory.CreateDirectory(grant); Directory.CreateDirectory(outside);
        try
        {
            File.WriteAllText(Path.Combine(grant, "index.html"), "document");
            File.WriteAllText(Path.Combine(outside, "secret.txt"), "secret");
            // Both paths are newly created, quoted test paths; no destructive shell operation.
            var info = new System.Diagnostics.ProcessStartInfo("cmd.exe", $"/d /c mklink /J \"{link}\" \"{outside}\"")
                { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
            using var process = System.Diagnostics.Process.Start(info)!;
            Assert.True(process.WaitForExit(5000)); Assert.Equal(0, process.ExitCode);
            var source = LocalWebSource.Open("index.html", grant, null, grant);
            Assert.Throws<UnauthorizedAccessException>(() => source.Read("/escape/secret.txt"));
        }
        finally
        {
            if (Directory.Exists(link)) Directory.Delete(link);
            Directory.Delete(root, true);
        }
    }
}
